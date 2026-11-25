using System.Runtime.InteropServices;

namespace UI.Services;

public static class KnownFolderService
{
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string GetDownloadsPath()
    {
        var folderId = DownloadsFolderId;
        var result = SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out var pathPointer);
        Marshal.ThrowExceptionForHR(result);
        try
        {
            return Marshal.PtrToStringUni(pathPointer) ?? "";
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        ref Guid folderId,
        uint flags,
        IntPtr token,
        out IntPtr path);
}
