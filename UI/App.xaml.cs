using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.UI.Xaml;
using UI.Services;
using WinRT.Interop;

namespace UI;

public partial class App : Application
{
    private const int ShowWindowRestore = 9;
    private Window? _window;
    private DesktopBackgroundDoubleClickMonitor? _desktopBackgroundMonitor;
    internal static string? StartupNotice { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLineArguments = Environment.GetCommandLineArgs();
        if (commandLineArguments.Any(argument =>
                argument.Equals(DesktopBackgroundDoubleClickMonitor.LaunchArgument, StringComparison.OrdinalIgnoreCase)))
        {
            StartDesktopBackgroundMonitor();
            return;
        }

        var desktopLaunch = commandLineArguments.Any(argument =>
            argument.Equals(DesktopBackgroundDoubleClickMonitor.ActivationArgument, StringComparison.OrdinalIgnoreCase));
        if (desktopLaunch && DesktopBackgroundDoubleClickMonitor.ActivateExistingApplicationWindow())
        {
            Environment.Exit(0);
            return;
        }

        var settings = ExplorerSettingsService.Load(out var settingsError);
        var monitorError = DesktopBackgroundDoubleClickMonitor.ApplySetting(
            settings.DesktopBackgroundDoubleClickLauncherEnabled);
        StartupNotice = monitorError ?? settingsError;

        if (TryRelaunchElevated(desktopLaunch))
            return;

        _window = new MainWindow(GetOpenPathArgument());
        _window.Activate();
        if (desktopLaunch)
        {
            var windowHandle = WindowNative.GetWindowHandle(_window);
            ShowWindow(windowHandle, ShowWindowRestore);
            SetForegroundWindow(windowHandle);
        }
    }

    private void StartDesktopBackgroundMonitor()
    {
        try
        {
            var executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("The application executable path is unavailable.");
            _desktopBackgroundMonitor = DesktopBackgroundDoubleClickMonitor.Start(executablePath);
            if (_desktopBackgroundMonitor is null)
            {
                Environment.Exit(0);
                return;
            }

            _window = new Window { Title = DesktopBackgroundDoubleClickMonitor.WindowTitle };
            _window.Closed += (_, _) => _desktopBackgroundMonitor?.Dispose();
            _window.Activate();
            DesktopBackgroundDoubleClickMonitor.HideWindow(WindowNative.GetWindowHandle(_window));
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
                                           UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or
                                           System.Runtime.InteropServices.COMException)
        {
            MessageBox(IntPtr.Zero, exception.Message, "ExplorerPlusPlus desktop monitor", 0x10);
            Environment.Exit(1);
        }
    }

    private static bool TryRelaunchElevated(bool desktopLaunch)
    {
        //Check for Alt key to bypass Admin Elevate
        if ((GetAsyncKeyState(0x12) & 0x8000) != 0)
            return false;

        if (Environment.GetCommandLineArgs().Any(argument => argument.Equals("--elevated", StringComparison.OrdinalIgnoreCase)))
            return false;

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                return false;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException)
        {
            StartupNotice = $"Could not check administrator access; continuing normally. {exception.Message}";
            return false;
        }

        try
        {
            var startInfo = CreateElevatedStartInfo(desktopLaunch: desktopLaunch);
            if (Process.Start(startInfo) is null)
                throw new InvalidOperationException("Windows did not start the elevated application.");

            Environment.Exit(0);
            return true;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            StartupNotice = "Administrator approval was declined; continuing with your normal access.";
            return false;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or UnauthorizedAccessException or ArgumentException or NotSupportedException or IOException or System.Runtime.InteropServices.COMException)
        {
            StartupNotice = $"Could not start with administrator access; continuing normally. {exception.Message}";
            return false;
        }
    }

    internal static bool RestartAsAdministrator(string path)
    {
        try
        {
            if (Process.Start(CreateElevatedStartInfo(path)) is null)
                throw new InvalidOperationException("Windows did not start the elevated application.");
            Environment.Exit(0);
            return true;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or UnauthorizedAccessException or ArgumentException or NotSupportedException or IOException or System.Runtime.InteropServices.COMException)
        {
            StartupNotice = $"Could not restart with administrator access. {exception.Message}";
            return false;
        }
    }

    private static ProcessStartInfo CreateElevatedStartInfo(string? openPath = null, bool desktopLaunch = false)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException("The application executable path is unavailable.");

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Environment.CurrentDirectory
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assembly = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(assembly))
                throw new InvalidOperationException("The application assembly path is unavailable.");
            startInfo.ArgumentList.Add(assembly);
        }
        startInfo.ArgumentList.Add("--elevated");
        if (desktopLaunch)
            startInfo.ArgumentList.Add(DesktopBackgroundDoubleClickMonitor.ActivationArgument);
        if (!string.IsNullOrWhiteSpace(openPath))
        {
            startInfo.ArgumentList.Add("--open");
            startInfo.ArgumentList.Add(openPath);
        }
        return startInfo;
    }

    private static string? GetOpenPathArgument()
    {
        var arguments = Environment.GetCommandLineArgs();
        for (var index = 0; index < arguments.Length - 1; index++)
        {
            if (arguments[index].Equals("--open", StringComparison.OrdinalIgnoreCase))
                return arguments[index + 1];
        }
        return null;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}
