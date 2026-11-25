using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace UI.Services;

internal sealed class DesktopBackgroundDoubleClickMonitor : IDisposable
{
    internal const string LaunchArgument = "--desktop-background-monitor";
    internal const string ActivationArgument = "--desktop-background-launch";
    internal const string WindowTitle = "ExplorerPlusPlus Desktop Monitor";

    private const int WhMouseLowLevel = 14;
    private const int WmLeftButtonDown = 0x0201;
    private const int WmClose = 0x0010;
    private const int SwHide = 0;
    private const int SwRestore = 9;
    private const int SmCxDoubleClick = 36;
    private const int SmCyDoubleClick = 37;
    private const int AccessibleRoleList = 0x21;
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private const string StartupKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "ExplorerPlusPlusDesktopBackgroundMonitor";
    private const string MutexName = @"Local\ExplorerPlusPlusDesktopBackgroundMonitor";

    private readonly string _executablePath;
    private readonly HookProcedure _hookProcedure;
    private Mutex? _instanceMutex;
    private IntPtr _hookHandle;
    private bool _ownsMutex;
    private int _isDisposed;
    private long _lastClickTimestamp;
    private NativePoint _lastClickPoint;
    private int _launchQueued;

    private DesktopBackgroundDoubleClickMonitor(string executablePath)
    {
        _executablePath = executablePath;
        _hookProcedure = MouseHookCallback;
    }

    internal static string? ApplySetting(bool enabled)
    {
        try
        {
            if (enabled)
                Enable();
            else
                Disable();
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           SecurityException or Win32Exception or InvalidOperationException or
                                           ArgumentException)
        {
            return $"Could not update the desktop background double-click launcher: {exception.Message}";
        }
    }

    internal static DesktopBackgroundDoubleClickMonitor? Start(string executablePath)
    {
        var monitor = new DesktopBackgroundDoubleClickMonitor(executablePath);
        monitor._instanceMutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            monitor._instanceMutex.Dispose();
            monitor._instanceMutex = null;
            return null;
        }

        monitor._ownsMutex = true;
        monitor._hookHandle = SetWindowsHookEx(
            WhMouseLowLevel, monitor._hookProcedure, GetModuleHandle(null), 0);
        if (monitor._hookHandle == IntPtr.Zero)
        {
            var exception = new Win32Exception(Marshal.GetLastWin32Error());
            monitor.Dispose();
            throw exception;
        }

        return monitor;
    }

    internal static void HideWindow(IntPtr windowHandle) => ShowWindow(windowHandle, SwHide);

    internal static bool ActivateExistingApplicationWindow()
    {
        var existingWindow = FindWindow(null, "Explorer++");
        if (existingWindow == IntPtr.Zero)
            return false;

        ShowWindow(existingWindow, SwRestore);
        SetForegroundWindow(existingWindow);
        return true;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _isDisposed, 1);
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }

        if (_ownsMutex)
        {
            _instanceMutex?.ReleaseMutex();
            _ownsMutex = false;
        }

        _instanceMutex?.Dispose();
        _instanceMutex = null;
    }

    private static void Enable()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new InvalidOperationException("The application executable path is unavailable.");

        using var runKey = Registry.CurrentUser.CreateSubKey(StartupKeyPath, writable: true)
            ?? throw new InvalidOperationException("The current-user startup key could not be opened.");
        runKey.SetValue(StartupValueName, GetStartupCommand(executablePath), RegistryValueKind.String);

        if (FindWindow(null, WindowTitle) != IntPtr.Zero)
            return;

        using var process = Process.Start(CreateMonitorStartInfo(executablePath))
            ?? throw new InvalidOperationException("Windows did not start the desktop background monitor.");
    }

    private static void Disable()
    {
        using (var runKey = Registry.CurrentUser.OpenSubKey(StartupKeyPath, writable: true))
            runKey?.DeleteValue(StartupValueName, throwOnMissingValue: false);

        var monitorWindow = FindWindow(null, WindowTitle);
        if (monitorWindow != IntPtr.Zero && !PostMessage(monitorWindow, WmClose, IntPtr.Zero, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static ProcessStartInfo CreateMonitorStartInfo(string executablePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory,
            UseShellExecute = false
        };
        if (Path.GetFileNameWithoutExtension(executablePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assembly = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(assembly))
                throw new InvalidOperationException("The application assembly path is unavailable.");
            startInfo.ArgumentList.Add(assembly);
        }
        startInfo.ArgumentList.Add(LaunchArgument);
        return startInfo;
    }

    private static string GetStartupCommand(string executablePath)
    {
        var arguments = new List<string>();
        if (Path.GetFileNameWithoutExtension(executablePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assembly = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(assembly))
                throw new InvalidOperationException("The application assembly path is unavailable.");
            arguments.Add(QuoteArgument(assembly));
        }
        arguments.Add(LaunchArgument);
        return $"{QuoteArgument(executablePath)} {string.Join(" ", arguments)}";
    }

    private static string QuoteArgument(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private IntPtr MouseHookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && message.ToInt32() == WmLeftButtonDown)
        {
            try
            {
                var hookData = Marshal.PtrToStructure<MouseHookData>(data);
                HandleLeftButtonDown(hookData.Point);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                               AccessViolationException)
            {
                Debug.WriteLine($"Desktop double-click monitor ignored mouse input: {exception.Message}");
            }
        }

        return CallNextHookEx(_hookHandle, code, message, data);
    }

    private void HandleLeftButtonDown(NativePoint point)
    {
        if (!IsDesktopListViewPoint(point))
        {
            _lastClickTimestamp = 0;
            return;
        }

        var timestamp = Environment.TickCount64;
        var isDoubleClick = _lastClickTimestamp != 0 &&
            timestamp - _lastClickTimestamp <= GetDoubleClickTime() &&
            Math.Abs(point.X - _lastClickPoint.X) <= Math.Max(1, GetSystemMetrics(SmCxDoubleClick) / 2) &&
            Math.Abs(point.Y - _lastClickPoint.Y) <= Math.Max(1, GetSystemMetrics(SmCyDoubleClick) / 2);

        if (!isDoubleClick)
        {
            _lastClickTimestamp = timestamp;
            _lastClickPoint = point;
            return;
        }

        _lastClickTimestamp = 0;
        if (Interlocked.Exchange(ref _launchQueued, 1) != 0)
            return;

        ThreadPool.QueueUserWorkItem(_ => HandleDesktopDoubleClick(_lastClickPoint, point));
    }

    private void HandleDesktopDoubleClick(NativePoint firstPoint, NativePoint secondPoint)
    {
        try
        {
            if (Volatile.Read(ref _isDisposed) != 0)
                return;
            if (!IsEmptyDesktopPoint(firstPoint) || !IsEmptyDesktopPoint(secondPoint))
                return;

            if (ActivateExistingApplicationWindow())
                return;

            var startInfo = new ProcessStartInfo
            {
                FileName = _executablePath,
                WorkingDirectory = Path.GetDirectoryName(_executablePath) ?? Environment.CurrentDirectory,
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add(ActivationArgument);
            using var process = Process.Start(startInfo);
            if (process is null)
                throw new InvalidOperationException("Windows did not start ExplorerPlusPlus.");
        }
        catch (Exception exception) when (exception is COMException or Win32Exception or
                                           InvalidOperationException or UnauthorizedAccessException or
                                           ArgumentException or InvalidCastException or
                                           Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            Debug.WriteLine($"Could not launch ExplorerPlusPlus from the desktop: {exception.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _launchQueued, 0);
        }
    }

    private static bool IsEmptyDesktopPoint(NativePoint point)
    {
        if (!IsDesktopListViewPoint(point))
            return false;

        var initializeResult = CoInitializeEx(IntPtr.Zero, 0);
        var uninitialize = initializeResult >= 0;
        if (initializeResult < 0 && initializeResult != RpcEChangedMode)
            Marshal.ThrowExceptionForHR(initializeResult);

        try
        {
            var result = AccessibleObjectFromPoint(point, out var accessibleObject, out var childId);
            if (result < 0)
                Marshal.ThrowExceptionForHR(result);

            try
            {
                if (childId is not int id || id != 0)
                    return false;

                dynamic accessible = accessibleObject;
                var role = Convert.ToInt32(accessible.accRole(id), CultureInfo.InvariantCulture);
                return role == AccessibleRoleList;
            }
            finally
            {
                if (Marshal.IsComObject(accessibleObject))
                    Marshal.ReleaseComObject(accessibleObject);
            }
        }
        finally
        {
            if (uninitialize)
                CoUninitialize();
        }
    }

    private static bool IsDesktopListViewPoint(NativePoint point)
    {
        var window = WindowFromPoint(point);
        var foundListView = false;
        var foundDesktopView = false;
        for (var depth = 0; window != IntPtr.Zero && depth < 8; depth++, window = GetParent(window))
        {
            var className = GetWindowClassName(window);
            foundListView |= string.Equals(className, "SysListView32", StringComparison.Ordinal);
            foundDesktopView |= string.Equals(className, "SHELLDLL_DefView", StringComparison.Ordinal);
            if (foundListView && foundDesktopView)
                return true;
        }

        return false;
    }

    private static string GetWindowClassName(IntPtr window)
    {
        var className = new StringBuilder(128);
        var length = GetClassName(window, className, className.Capacity);
        return length == 0 ? "" : className.ToString();
    }

    private delegate IntPtr HookProcedure(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        internal NativePoint Point;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, HookProcedure callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

    [DllImport("oleacc.dll", PreserveSig = true)]
    private static extern int AccessibleObjectFromPoint(
        NativePoint point,
        [MarshalAs(UnmanagedType.Interface)] out object accessibleObject,
        [MarshalAs(UnmanagedType.Struct)] out object childId);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
