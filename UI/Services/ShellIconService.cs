using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using UI.Models;

namespace UI.Services;

public static class ShellIconService
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiSmallIcon = 0x000000001;
    private const uint ShgfiPidl = 0x000000008;
    private const uint ShgfiUseFileAttributes = 0x000000010;
    private static readonly SemaphoreSlim IconLimit = new(4);
    private static readonly Dictionary<string, byte[]> IconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheLock = new();

    public static async Task<ImageSource?> LoadFileIconAsync(string path, bool isDirectory)
    {
        try
        {
            await IconLimit.WaitAsync();
            try
            {
                var bytes = await Task.Run(() => GetIconBytes(path, isDirectory));
                if (bytes is null)
                    return null;

                var image = new BitmapImage();
                using var stream = new MemoryStream(bytes);
                await image.SetSourceAsync(stream.AsRandomAccessStream());
                return image;
            }
            finally
            {
                IconLimit.Release();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or ExternalException)
        {
            return null;
        }
    }

    public static async Task<ImageSource?> LoadIconResourceAsync(string modulePath, int iconIndex)
    {
        try
        {
            await IconLimit.WaitAsync();
            try
            {
                var bytes = await Task.Run(() => GetIconResourceBytes(modulePath, iconIndex));
                if (bytes is null)
                    return null;

                var image = new BitmapImage();
                using var stream = new MemoryStream(bytes);
                await image.SetSourceAsync(stream.AsRandomAccessStream());
                return image;
            }
            finally
            {
                IconLimit.Release();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or ExternalException)
        {
            return null;
        }
    }

    public static async Task<ImageSource?> LoadShellItemIconAsync(string parsingName)
    {
        try
        {
            await IconLimit.WaitAsync();
            try
            {
                var bytes = await Task.Run(() => GetShellItemIconBytes(parsingName));
                if (bytes is null)
                    return null;

                var image = new BitmapImage();
                using var stream = new MemoryStream(bytes);
                await image.SetSourceAsync(stream.AsRandomAccessStream());
                return image;
            }
            finally
            {
                IconLimit.Release();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or ExternalException)
        {
            return null;
        }
    }

    private static byte[]? GetShellItemIconBytes(string parsingName)
    {
        var cacheKey = $"{parsingName}|shell-item";
        lock (CacheLock)
        {
            if (IconCache.TryGetValue(cacheKey, out var cached))
                return cached;
        }

        var result = SHParseDisplayName(parsingName, IntPtr.Zero, out var itemIdList, 0, IntPtr.Zero);
        Marshal.ThrowExceptionForHR(result);
        try
        {
            var info = new ShellFileInfo();
            if (SHGetFileInfo(itemIdList, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(),
                    ShgfiIcon | ShgfiSmallIcon | ShgfiPidl) == IntPtr.Zero ||
                info.Icon == IntPtr.Zero)
                return null;

            try
            {
                using var shellIcon = (Icon)Icon.FromHandle(info.Icon).Clone();
                using var bitmap = shellIcon.ToBitmap();
                using var stream = new MemoryStream();
                bitmap.Save(stream, ImageFormat.Png);
                var bytes = stream.ToArray();
                lock (CacheLock)
                    IconCache[cacheKey] = bytes;
                return bytes;
            }
            finally
            {
                DestroyIcon(info.Icon);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(itemIdList);
        }
    }

    private static byte[]? GetIconResourceBytes(string modulePath, int iconIndex)
    {
        var cacheKey = $"{modulePath}|resource:{iconIndex}";
        lock (CacheLock)
        {
            if (IconCache.TryGetValue(cacheKey, out var cached))
                return cached;
        }

        var large = new IntPtr[1];
        var small = new IntPtr[1];
        if (ExtractIconEx(modulePath, iconIndex, large, small, 1) == 0)
            return null;

        var icon = small[0] != IntPtr.Zero ? small[0] : large[0];
        var unusedIcon = small[0] != IntPtr.Zero ? large[0] : IntPtr.Zero;
        if (icon == IntPtr.Zero)
            return null;

        try
        {
            using var shellIcon = (Icon)Icon.FromHandle(icon).Clone();
            using var bitmap = shellIcon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            var bytes = stream.ToArray();
            lock (CacheLock)
                IconCache[cacheKey] = bytes;
            return bytes;
        }
        finally
        {
            DestroyIcon(icon);
            if (unusedIcon != IntPtr.Zero)
                DestroyIcon(unusedIcon);
        }
    }

    private static byte[]? GetIconBytes(string path, bool isDirectory)
    {
        var cacheKey = path + (isDirectory ? "|directory" : "|file");
        lock (CacheLock)
        {
            if (IconCache.TryGetValue(cacheKey, out var cached))
                return cached;
        }

        var icon = isDirectory ? GetDesktopIniIcon(path) : IntPtr.Zero;
        if (icon == IntPtr.Zero)
        {
            var info = new ShellFileInfo();
            var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), ShgfiIcon | ShgfiSmallIcon);
            if (result == IntPtr.Zero && !isDirectory)
            {
                result = SHGetFileInfo(path, 0x80, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(),
                    ShgfiIcon | ShgfiSmallIcon | ShgfiUseFileAttributes);
            }
            icon = info.Icon;
        }

        if (icon == IntPtr.Zero)
            return null;

        try
        {
            using var shellIcon = (Icon)Icon.FromHandle(icon).Clone();
            using var bitmap = shellIcon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            var bytes = stream.ToArray();
            lock (CacheLock)
                IconCache[cacheKey] = bytes;
            return bytes;
        }
        finally
        {
            DestroyIcon(icon);
        }
    }

    private static IntPtr GetDesktopIniIcon(string directory)
    {
        var desktopIni = Path.Combine(directory, "desktop.ini");
        if (!File.Exists(desktopIni))
            return IntPtr.Zero;

        string? iconLocation = null;
        var iconIndex = 0;
        var inShellClassInfo = false;
        foreach (var rawLine in File.ReadLines(desktopIni))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inShellClassInfo = line.Equals("[.ShellClassInfo]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inShellClassInfo)
                continue;

            var separator = line.IndexOf('=');
            if (separator <= 0)
                continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"');
            if (key.Equals("IconResource", StringComparison.OrdinalIgnoreCase))
            {
                var indexSeparator = value.LastIndexOf(',');
                if (indexSeparator > 0 && int.TryParse(value[(indexSeparator + 1)..], out var resourceIndex))
                {
                    iconLocation = value[..indexSeparator].Trim().TrimStart('@');
                    iconIndex = resourceIndex;
                }
                else
                {
                    iconLocation = value.TrimStart('@');
                }
                break;
            }
            if (key.Equals("IconFile", StringComparison.OrdinalIgnoreCase))
            {
                iconLocation = value;
            }
            else if (key.Equals("IconIndex", StringComparison.OrdinalIgnoreCase))
            {
                _ = int.TryParse(value, out iconIndex);
            }
        }

        if (string.IsNullOrWhiteSpace(iconLocation))
            return IntPtr.Zero;

        iconLocation = Environment.ExpandEnvironmentVariables(iconLocation);
        if (!Path.IsPathRooted(iconLocation))
            iconLocation = Path.GetFullPath(Path.Combine(directory, iconLocation));
        if (!File.Exists(iconLocation))
            return IntPtr.Zero;

        var large = new IntPtr[1];
        var small = new IntPtr[1];
        if (ExtractIconEx(iconLocation, iconIndex, large, small, 1) == 0)
            return IntPtr.Zero;

        if (small[0] != IntPtr.Zero)
        {
            if (large[0] != IntPtr.Zero)
                DestroyIcon(large[0]);
            return small[0];
        }
        return large[0];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint fileAttributes, ref ShellFileInfo fileInfo, uint fileInfoSize, uint flags);

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(IntPtr itemIdList, uint fileAttributes, ref ShellFileInfo fileInfo, uint fileInfoSize, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr itemIdList, uint attributesIn, IntPtr attributesOut);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string fileName, int iconIndex, IntPtr[] largeIcons, IntPtr[] smallIcons, uint iconCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
