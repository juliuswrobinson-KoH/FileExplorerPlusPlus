using System.Runtime.InteropServices;
using UI.Models;

namespace UI.Services;

public static class RecycleBinService
{
    public const string Path = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    private const uint ShellFolders = 0x20;
    private const uint ShellNonFolders = 0x40;
    private const uint AttributeFolder = 0x20000000;
    private const uint NameNormalDisplay = 0;
    private const uint NameDesktopAbsoluteParsing = 0x80028000;
    private const uint CoInitMultithreaded = 0;
    private static readonly Guid ShellFolderId = new("000214E6-0000-0000-C000-000000000046");

    public static bool IsPath(string path) =>
        string.Equals(path, Path, StringComparison.OrdinalIgnoreCase);

    public static List<ExplorerEntry> GetEntries()
    {
        var initializeResult = CoInitializeEx(IntPtr.Zero, CoInitMultithreaded);
        Marshal.ThrowExceptionForHR(initializeResult);
        try
        {
            var result = SHParseDisplayName(Path, IntPtr.Zero, out var rootPidl, 0, IntPtr.Zero);
            Marshal.ThrowExceptionForHR(result);
            try
            {
                Marshal.ThrowExceptionForHR(SHGetDesktopFolder(out var desktopFolder));
                try
                {
                    var folderId = ShellFolderId;
                    Marshal.ThrowExceptionForHR(desktopFolder.BindToObject(
                        rootPidl, IntPtr.Zero, ref folderId, out var recycleFolder));
                    try
                    {
                        Marshal.ThrowExceptionForHR(recycleFolder.EnumObjects(
                            IntPtr.Zero,
                            ShellFolders | ShellNonFolders,
                            out var enumerator));
                        try
                        {
                            return ReadEntries(recycleFolder, enumerator, rootPidl);
                        }
                        finally
                        {
                            ReleaseComObject(enumerator);
                        }
                    }
                    finally
                    {
                        ReleaseComObject(recycleFolder);
                    }
                }
                finally
                {
                    ReleaseComObject(desktopFolder);
                }
            }
            finally
            {
                CoTaskMemFree(rootPidl);
            }
        }
        finally
        {
            CoUninitialize();
        }
    }

    private static List<ExplorerEntry> ReadEntries(IShellFolder folder, IEnumIDList enumerator, IntPtr rootPidl)
    {
        var entries = new List<ExplorerEntry>();
        while (true)
        {
            var nextResult = enumerator.Next(1, out var childPidl, out var fetched);
            if (nextResult == 1 || fetched == 0)
                break;
            Marshal.ThrowExceptionForHR(nextResult);

            try
            {
                var attributes = AttributeFolder;
                Marshal.ThrowExceptionForHR(folder.GetAttributesOf(1, [childPidl], ref attributes));
                var isDirectory = (attributes & AttributeFolder) != 0;
                var absolutePidl = ILCombine(rootPidl, childPidl);
                if (absolutePidl == IntPtr.Zero)
                    throw new InvalidOperationException("Windows could not create the Recycle Bin item path.");

                try
                {
                    var name = GetName(absolutePidl, NameNormalDisplay);
                    var parsingName = GetName(absolutePidl, NameDesktopAbsoluteParsing);
                    var extension = System.IO.Path.GetExtension(name);
                    entries.Add(new ExplorerEntry
                    {
                        Name = name,
                        FullPath = parsingName,
                        Kind = isDirectory ? ExplorerEntryKind.Directory : ExplorerEntryKind.File,
                        Type = isDirectory ? "Folder" : string.IsNullOrEmpty(extension)
                            ? "File"
                            : $"{extension.TrimStart('.').ToUpperInvariant()} file",
                        IsRecycleBinEntry = true
                    });
                }
                finally
                {
                    CoTaskMemFree(absolutePidl);
                }
            }
            finally
            {
                CoTaskMemFree(childPidl);
            }
        }

        return entries
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string GetName(IntPtr itemIdList, uint nameType)
    {
        Marshal.ThrowExceptionForHR(SHGetNameFromIDList(itemIdList, nameType, out var namePointer));
        try
        {
            return Marshal.PtrToStringUni(namePointer)
                ?? throw new InvalidOperationException("Windows returned an empty Recycle Bin item name.");
        }
        finally
        {
            CoTaskMemFree(namePointer);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.ReleaseComObject(value);
    }

    [ComImport]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr bindContext, string displayName, ref uint eaten, out IntPtr itemIdList, ref uint attributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, uint flags, [MarshalAs(UnmanagedType.Interface)] out IEnumIDList enumerator);
        [PreserveSig] int BindToObject(IntPtr itemIdList, IntPtr bindContext, ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IShellFolder result);
        [PreserveSig] int BindToStorage(IntPtr itemIdList, IntPtr bindContext, ref Guid interfaceId, out IntPtr result);
        [PreserveSig] int CompareIds(IntPtr parameter, IntPtr first, IntPtr second);
        [PreserveSig] int CreateViewObject(IntPtr hwnd, ref Guid interfaceId, out IntPtr result);
        [PreserveSig] int GetAttributesOf(uint count,
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] itemIdLists, ref uint attributes);
        [PreserveSig] int GetUIObjectOf(IntPtr hwnd, uint count, IntPtr itemIdLists, ref Guid interfaceId,
            IntPtr reserved, out IntPtr result);
        [PreserveSig] int GetDisplayNameOf(IntPtr itemIdList, uint flags, out IntPtr name);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr itemIdList, string name, uint flags, out IntPtr renamedItemIdList);
    }

    [ComImport]
    [Guid("000214F2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumIDList
    {
        [PreserveSig] int Next(uint count, out IntPtr itemIdList, out uint fetched);
        [PreserveSig] int Skip(uint count);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumIDList enumerator);
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr itemIdList, uint attributesIn, IntPtr attributesOut);

    [DllImport("shell32.dll")]
    private static extern int SHGetDesktopFolder([MarshalAs(UnmanagedType.Interface)] out IShellFolder folder);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILCombine(IntPtr parent, IntPtr child);

    [DllImport("shell32.dll")]
    private static extern int SHGetNameFromIDList(IntPtr itemIdList, uint nameType, out IntPtr name);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr memory);
}
