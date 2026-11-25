using System.ComponentModel;
using System.Runtime.InteropServices;
using UI.Models;

namespace UI.Services;

public static class NetworkShareService
{
    private const int NoError = 0;
    private const int ErrorMoreData = 234;
    private const int ErrorAccessDenied = 5;
    private const uint DiskTreeShare = 0;
    public static bool IsServerPath(string path)
    {
        if (!path.StartsWith(@"\\", StringComparison.Ordinal))
            return false;
        var server = path.TrimEnd('\\');
        return server.Length > 2 && server[2..].IndexOf('\\') < 0;
    }

    public static List<ExplorerEntry> GetShares(string serverPath)
    {
        if (!IsServerPath(serverPath))
            throw new ArgumentException("The path must contain a server name without a share.", nameof(serverPath));

        var entries = new List<ExplorerEntry>();
        var resumeHandle = 0;
        int status;
        do
        {
            status = NetShareEnum(serverPath, 1, out var buffer, -1, out var entriesRead, out _, ref resumeHandle);
            try
            {
                if (status == ErrorAccessDenied)
                    throw new UnauthorizedAccessException($"Windows denied access while enumerating shares on {serverPath}.");
                if (status != NoError && status != ErrorMoreData)
                    throw new Win32Exception(status, $"Windows could not enumerate network shares on {serverPath}.");

                var entrySize = Marshal.SizeOf<ShareInfo1>();
                for (var index = 0; index < entriesRead; index++)
                {
                    var itemPointer = IntPtr.Add(buffer, index * entrySize);
                    var item = Marshal.PtrToStructure<ShareInfo1>(itemPointer);
                    var name = Marshal.PtrToStringUni(item.Name) ?? "";
                    if (string.IsNullOrWhiteSpace(name) ||
                        item.Type != DiskTreeShare ||
                        name.EndsWith('$'))
                        continue;

                    entries.Add(new ExplorerEntry
                    {
                        Name = name,
                        FullPath = $@"{serverPath.TrimEnd('\\')}\{name}",
                        Kind = ExplorerEntryKind.Directory,
                        Type = "Network share"
                    });
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                    NetApiBufferFree(buffer);
            }
        } while (status == ErrorMoreData);

        return entries.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShareInfo1
    {
        public IntPtr Name;
        public uint Type;
        public IntPtr Remark;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetShareEnum(
        string serverName,
        int level,
        out IntPtr buffer,
        int preferredMaximumLength,
        out int entriesRead,
        out int totalEntries,
        ref int resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
