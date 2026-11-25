using System.Runtime.InteropServices;

namespace UI.Services;

public static class ShellContextMenuService
{
    public const uint PreviewCommand = 0x7000;
    public const uint HexCommand = 0x7001;
    public const uint AppPropertiesCommand = 0x7002;
    public const uint PasteCommand = 0x7003;
    public const uint DeleteCommand = 0x7004;
    public const uint AdminCommandPromptCommand = 0x7005;
    public const uint RemoveFavoriteCommand = 0x7006;
    public const uint ConvertImagesToPngCommand = 0x7007;
    private const uint FirstShellCommand = 1;
    private const uint LastShellCommand = 0x6FFF;
    private const uint MenuFlagString = 0x00000000;
    private const uint MenuFlagSeparator = 0x00000800;
    private const uint QueryContextMenuNormal = 0x00000000;
    private const uint TrackPopupMenuReturnCommand = 0x0100;
    private const uint TrackPopupMenuRightButton = 0x0002;
    private static readonly Guid ShellFolderId = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid ContextMenuId = new("000214E4-0000-0000-C000-000000000046");

    public static uint Show(
        IntPtr owner,
        IReadOnlyList<string> paths,
        bool showPaste,
        bool showRemoveFavorite = false,
        bool includeExplorerPlusPlusCommands = true)
    {
        if (paths.Count == 0 || paths.Any(path =>
                !File.Exists(path) && !Directory.Exists(path) &&
                !path.StartsWith("::", StringComparison.Ordinal)))
            return 0;

        if (paths.Count > 1)
        {
            var parentPath = Path.GetDirectoryName(paths[0]);
            if (string.IsNullOrEmpty(parentPath) ||
                paths.Any(path => !string.Equals(Path.GetDirectoryName(path), parentPath, StringComparison.OrdinalIgnoreCase)))
                return 0;
        }

        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
            throw new InvalidOperationException("Windows could not create the file context menu.");

        var absolutePidls = new List<IntPtr>(paths.Count);
        var childPidls = new IntPtr[paths.Count];
        var parentFolders = new List<IShellFolder>(paths.Count);
        IShellFolder? shellFolder = null;
        IContextMenu? contextMenu = null;
        try
        {
            foreach (var path in paths)
            {
                var result = SHParseDisplayName(path, IntPtr.Zero, out var absolutePidl, 0, IntPtr.Zero);
                Marshal.ThrowExceptionForHR(result);
                absolutePidls.Add(absolutePidl);

                var shellFolderId = ShellFolderId;
                result = SHBindToParent(absolutePidl, ref shellFolderId, out var folder, out var childPidl);
                Marshal.ThrowExceptionForHR(result);
                parentFolders.Add(folder);
                if (shellFolder is null)
                    shellFolder = folder;
                else if (!SameComObject(shellFolder, folder))
                    return 0;
                childPidls[absolutePidls.Count - 1] = childPidl;
            }

            var iid = ContextMenuId;
            Marshal.ThrowExceptionForHR(shellFolder!.GetUIObjectOf(
                owner, (uint)childPidls.Length, childPidls, ref iid, IntPtr.Zero, out contextMenu));
            Marshal.ThrowExceptionForHR(contextMenu.QueryContextMenu(
                menu, 0, FirstShellCommand, LastShellCommand, QueryContextMenuNormal));

            if (includeExplorerPlusPlusCommands || showRemoveFavorite)
                AppendMenuW(menu, MenuFlagSeparator, UIntPtr.Zero, null);
            if (includeExplorerPlusPlusCommands)
            {
                AppendMenuW(menu, MenuFlagString, new UIntPtr(PreviewCommand), "Preview");
                AppendMenuW(menu, MenuFlagString, new UIntPtr(HexCommand), "Hex / entry");
                AppendMenuW(menu, MenuFlagString, new UIntPtr(AppPropertiesCommand), "Explorer++ properties");
                if (showPaste)
                    AppendMenuW(menu, MenuFlagString, new UIntPtr(PasteCommand), "Paste");
                AppendMenuW(menu, MenuFlagString, new UIntPtr(DeleteCommand), "Delete Permanently");
                AppendMenuW(menu, MenuFlagString, new UIntPtr(AdminCommandPromptCommand),
                    "Open command prompt as administrator");
                if (paths.All(path => File.Exists(path) && PreviewFormatRegistry.IsImage(path)) &&
                    paths.Any(path => !Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)))
                    AppendMenuW(menu, MenuFlagString, new UIntPtr(ConvertImagesToPngCommand),
                        "Convert selected images to PNG");
            }
            if (showRemoveFavorite)
                AppendMenuW(menu, MenuFlagString, new UIntPtr(RemoveFavoriteCommand), "Remove favorite");

            if (!GetCursorPos(out var cursor))
                throw new COMException("Windows could not determine the context-menu position.", Marshal.GetLastWin32Error());
            var command = TrackPopupMenuEx(menu,
                TrackPopupMenuReturnCommand | TrackPopupMenuRightButton, cursor.X, cursor.Y, owner, IntPtr.Zero);
            if (command >= FirstShellCommand && command <= LastShellCommand)
            {
                var invokeInfo = new CommandInfo
                {
                    Size = Marshal.SizeOf<CommandInfo>(),
                    Window = owner,
                    Verb = new IntPtr(unchecked((int)(command - FirstShellCommand))),
                    Show = 1
                };
                Marshal.ThrowExceptionForHR(contextMenu.InvokeCommand(ref invokeInfo));
                return 0xFFFFFFFF;
            }
            return command;
        }
        finally
        {
            if (contextMenu is not null && Marshal.IsComObject(contextMenu))
                Marshal.ReleaseComObject(contextMenu);
            foreach (var folder in parentFolders)
            {
                if (Marshal.IsComObject(folder))
                    Marshal.ReleaseComObject(folder);
            }
            foreach (var pidl in absolutePidls)
                CoTaskMemFree(pidl);
            DestroyMenu(menu);
        }
    }

    private static bool SameComObject(object first, object second)
    {
        var firstUnknown = Marshal.GetIUnknownForObject(first);
        var secondUnknown = Marshal.GetIUnknownForObject(second);
        try
        {
            return firstUnknown == secondUnknown;
        }
        finally
        {
            Marshal.Release(firstUnknown);
            Marshal.Release(secondUnknown);
        }
    }

    [ComImport]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr bindContext, string displayName, ref uint eaten, out IntPtr itemIdList, ref uint attributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, uint flags, out IntPtr enumerator);
        [PreserveSig] int BindToObject(IntPtr itemIdList, IntPtr bindContext, ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IShellFolder result);
        [PreserveSig] int BindToStorage(IntPtr itemIdList, IntPtr bindContext, ref Guid interfaceId, out IntPtr result);
        [PreserveSig] int CompareIds(IntPtr parameter, IntPtr first, IntPtr second);
        [PreserveSig] int CreateViewObject(IntPtr hwnd, ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IContextMenu result);
        [PreserveSig] int GetAttributesOf(uint count,
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] itemIdLists, ref uint attributes);
        [PreserveSig] int GetUIObjectOf(IntPtr hwnd, uint count,
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] itemIdLists, ref Guid interfaceId, IntPtr reserved,
            [MarshalAs(UnmanagedType.Interface)] out IContextMenu result);
        [PreserveSig] int GetDisplayNameOf(IntPtr itemIdList, uint flags, out IntPtr name);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr itemIdList, string name, uint flags, out IntPtr renamedItemIdList);
    }

    [ComImport]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint indexMenu, uint firstCommand, uint lastCommand, uint flags);
        [PreserveSig] int InvokeCommand(ref CommandInfo commandInfo);
        [PreserveSig] int GetCommandString(UIntPtr command, uint flags, IntPtr reserved, IntPtr name, uint maximumNameLength);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommandInfo
    {
        public int Size;
        public uint Mask;
        public IntPtr Window;
        public IntPtr Verb;
        public IntPtr Parameters;
        public IntPtr Directory;
        public int Show;
        public uint HotKey;
        public IntPtr Icon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr itemIdList, uint attributesIn, IntPtr attributesOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr itemIdList, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IShellFolder parent, out IntPtr childItemIdList);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr itemId, string? itemText);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr memory);
}
