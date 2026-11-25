using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace UI.Services;

public sealed record WindowsClipboardFormat(uint Id, string Name, byte[] Data)
{
    public IReadOnlyList<string> StoragePaths { get; init; } = [];
}

public sealed class WindowsClipboardMonitor : IDisposable
{
    private const uint ClipboardUpdateMessage = 0x031D;
    private static readonly UIntPtr SubclassId = new(0x46505043);
    private readonly IntPtr _windowHandle;
    private readonly Action<IReadOnlyList<WindowsClipboardFormat>> _clipboardChanged;
    private readonly Action<Exception> _captureFailed;
    private readonly SubclassProcedure _subclassProcedure;
    private bool _listening;
    private bool _disposed;

    public WindowsClipboardMonitor(
        IntPtr windowHandle,
        Action<IReadOnlyList<WindowsClipboardFormat>> clipboardChanged,
        Action<Exception> captureFailed)
    {
        _windowHandle = windowHandle;
        _clipboardChanged = clipboardChanged;
        _captureFailed = captureFailed;
        _subclassProcedure = WindowSubclassProcedure;

        if (!SetWindowSubclass(_windowHandle, _subclassProcedure, SubclassId, UIntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not attach the clipboard listener to the ExplorerPlusPlus window.");

        if (!AddClipboardFormatListener(_windowHandle))
        {
            var error = Marshal.GetLastWin32Error();
            RemoveWindowSubclass(_windowHandle, _subclassProcedure, SubclassId);
            throw new Win32Exception(error, "Could not register for Windows clipboard updates.");
        }

        _listening = true;
        try
        {
            _clipboardChanged(ReadClipboardFormats(_windowHandle));
        }
        catch (Exception exception)
        {
            _captureFailed(exception);
        }
    }

    private IntPtr WindowSubclassProcedure(
        IntPtr windowHandle,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData)
    {
        if (message == ClipboardUpdateMessage)
        {
            try
            {
                var formats = ReadClipboardFormats(windowHandle);
                _clipboardChanged(formats);
            }
            catch (Exception exception)
            {
                _captureFailed(exception);
            }
        }

        return DefSubclassProc(windowHandle, message, wParam, lParam);
    }

    private static IReadOnlyList<WindowsClipboardFormat> ReadClipboardFormats(IntPtr ownerWindow)
    {
        if (!OpenClipboard(ownerWindow))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not open the clipboard for reading.");

        try
        {
            var formats = new List<WindowsClipboardFormat>();
            uint previousFormat = 0;
            while (true)
            {
                var format = EnumClipboardFormats(previousFormat);
                if (format == 0)
                    break;

                previousFormat = format;
                var dataHandle = GetClipboardData(format);
                if (dataHandle == IntPtr.Zero)
                    continue;

                if (format == 2)
                {
                    var dibData = CaptureBitmapAsDib(dataHandle);
                    if (dibData.Length > 0)
                        formats.Add(new WindowsClipboardFormat(8, "CF_DIB", dibData));
                    continue;
                }

                var size = GlobalSize(dataHandle).ToUInt64();
                if (size == 0 || size > ClipboardHistoryService.MaximumHistoryBytes || size > int.MaxValue)
                    continue;

                var lockedData = GlobalLock(dataHandle);
                if (lockedData == IntPtr.Zero)
                    continue;

                try
                {
                    var data = new byte[(int)size];
                    Marshal.Copy(lockedData, data, 0, data.Length);
                    var clipboardFormat = new WindowsClipboardFormat(format, GetFormatName(format), data);
                    formats.Add(format == 15
                        ? clipboardFormat with { StoragePaths = ReadDropPaths(dataHandle) }
                        : clipboardFormat);
                }
                finally
                {
                    GlobalUnlock(dataHandle);
                }
            }

            return formats;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static IReadOnlyList<string> ReadDropPaths(IntPtr dropHandle)
    {
        var count = DragQueryFile(dropHandle, uint.MaxValue, null, 0);
        var paths = new List<string>((int)count);
        for (uint index = 0; index < count; index++)
        {
            var length = DragQueryFile(dropHandle, index, null, 0);
            if (length == 0)
                continue;
            var path = new StringBuilder(checked((int)length + 1));
            if (DragQueryFile(dropHandle, index, path, path.Capacity) > 0)
                paths.Add(path.ToString());
        }
        return paths;
    }

    private static byte[] CaptureBitmapAsDib(IntPtr bitmapHandle)
    {
        if (GetObject(bitmapHandle, Marshal.SizeOf<BitmapObject>(), out var bitmap) == 0 ||
            bitmap.Width <= 0 || bitmap.Height <= 0)
            return [];

        var pixelBytes = checked((long)bitmap.Width * bitmap.Height * 4);
        var headerSize = Marshal.SizeOf<BitmapInfoHeader>();
        if (pixelBytes + headerSize > ClipboardHistoryService.MaximumHistoryBytes ||
            pixelBytes + headerSize > int.MaxValue)
            return [];

        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)headerSize,
                Width = bitmap.Width,
                Height = bitmap.Height,
                Planes = 1,
                BitCount = 32,
                Compression = 0,
                SizeImage = (uint)pixelBytes
            }
        };
        var pixels = Marshal.AllocHGlobal((int)pixelBytes);
        var deviceContext = GetDC(IntPtr.Zero);
        if (deviceContext == IntPtr.Zero)
        {
            Marshal.FreeHGlobal(pixels);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not access the copied bitmap.");
        }

        try
        {
            var scanLines = GetDIBits(deviceContext, bitmapHandle, 0, (uint)bitmap.Height,
                pixels, ref info, 0);
            if (scanLines == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not read the copied bitmap.");

            var result = new byte[headerSize + (int)pixelBytes];
            var headerBuffer = Marshal.AllocHGlobal(headerSize);
            try
            {
                Marshal.StructureToPtr(info.Header, headerBuffer, false);
                Marshal.Copy(headerBuffer, result, 0, headerSize);
            }
            finally
            {
                Marshal.FreeHGlobal(headerBuffer);
            }
            Marshal.Copy(pixels, result, headerSize, (int)pixelBytes);
            return result;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, deviceContext);
            Marshal.FreeHGlobal(pixels);
        }
    }

    private static string GetFormatName(uint format)
    {
        var standardName = format switch
        {
            1 => "CF_TEXT",
            2 => "CF_BITMAP",
            3 => "CF_METAFILEPICT",
            7 => "CF_OEMTEXT",
            8 => "CF_DIB",
            9 => "CF_PALETTE",
            13 => "CF_UNICODETEXT",
            14 => "CF_ENHMETAFILE",
            15 => "CF_HDROP",
            17 => "CF_DIBV5",
            _ => null
        };
        if (standardName is not null)
            return standardName;

        var name = new StringBuilder(256);
        var length = GetClipboardFormatName(format, name, name.Capacity);
        return length > 0 ? name.ToString() : $"Clipboard format {format}";
    }

    public static void SetClipboardFormat(IntPtr ownerWindow, uint format, string formatName, byte[] data)
    {
        var resolvedFormat = format >= 0xC000
            ? RegisterClipboardFormat(formatName)
            : format;
        if (resolvedFormat == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Windows could not register clipboard format '{formatName}'.");
        if (!OpenClipboard(ownerWindow))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not open the clipboard for writing.");

        IntPtr globalMemory = IntPtr.Zero;
        try
        {
            if (!EmptyClipboard())
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not clear the clipboard.");
            globalMemory = GlobalAlloc(0x0002, (UIntPtr)data.Length);
            if (globalMemory == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not allocate clipboard memory.");
            var destination = GlobalLock(globalMemory);
            if (destination == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not lock clipboard memory.");
            try
            {
                Marshal.Copy(data, 0, destination, data.Length);
            }
            finally
            {
                GlobalUnlock(globalMemory);
            }

            if (SetClipboardData(resolvedFormat, globalMemory) == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not set clipboard data.");
            globalMemory = IntPtr.Zero;
        }
        finally
        {
            if (globalMemory != IntPtr.Zero)
                GlobalFree(globalMemory);
            CloseClipboard();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        if (_listening)
        {
            RemoveClipboardFormatListener(_windowHandle);
            _listening = false;
        }
        RemoveWindowSubclass(_windowHandle, _subclassProcedure, SubclassId);
        _disposed = true;
    }

    private delegate IntPtr SubclassProcedure(
        IntPtr windowHandle,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapObject
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint FirstColor;
    }

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        IntPtr windowHandle,
        SubclassProcedure subclassProcedure,
        UIntPtr subclassId,
        UIntPtr referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        IntPtr windowHandle,
        SubclassProcedure subclassProcedure,
        UIntPtr subclassId);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(
        IntPtr windowHandle,
        uint message,
        UIntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr ownerWindow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint EnumClipboardFormats(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClipboardFormatName(uint format, StringBuilder formatName, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string formatName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW", SetLastError = true)]
    private static extern int GetObject(IntPtr handle, int size, out BitmapObject bitmap);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(
        IntPtr deviceContext,
        IntPtr bitmap,
        uint startScan,
        uint scanLines,
        IntPtr bits,
        ref BitmapInfo info,
        uint usage);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr windowHandle, IntPtr deviceContext);

    [DllImport("shell32.dll", EntryPoint = "DragQueryFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint DragQueryFile(
        IntPtr dropHandle,
        uint fileIndex,
        StringBuilder? fileName,
        int characters);
}
