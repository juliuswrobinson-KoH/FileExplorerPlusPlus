<p align="center">
  <img src="FileExplorer%2B%2B%20Logo.png" alt="FileExplorer++ logo" width="320">
</p>

# ExplorerPlusPlus

Versatile File Explorer with Custom Filesystem Support and extensible features.

A WinUI 3, dual-pane file manager for Windows. The left and right panes have independent tabs and locations, with a folder tree and quick-access shortcuts alongside them.

## Run

Open `ExplorerPlusPlus.slnx` in Visual Studio with the Windows App SDK development components installed, or run:

```powershell
dotnet run --project UI\ExplorerPlusPlus.csproj
```

The repository root also contains `build.bat` (build), `rebuild.bat` (clean and build), and `run.bat` (build and launch).

## Included

- Independent left/right panes with reorderable tabs, duplicate/move/close/reopen actions, double-click-to-add, middle-click-to-close, and pane restore controls.
- Per-tab path, filter, view, thumbnail size, sort order, and selected items are checkpointed with each pane's selected tab and Back/Forward history; recovery retains the one-retry/last-clean-session safeguard.
- A single/dual-pane toggle that keeps the active pane visible.
- A top drive strip grouped by physical disk, with mounted partitions/volumes and capacity, plus mapped network drives; clicking a drive opens it. The Add location menu opens the Windows network-drive mapper.
- The executable, title bar, and taskbar use the custom 3D File Explorer icon in `UI\Assets\FileExplorerIcon.ico` (PNG source is alongside it).
- Windows shell icons for registered file types, executables, `.ico` files, and drive letters, plus folder icon resources configured in `desktop.ini`.
- Clickable breadcrumb paths with editable/autocompleting addresses and recent-location suggestions; independent pane Back/Forward/Up, refresh, and live name filtering. UNC server paths such as `\\192.168.0.1` list available shares, and share paths browse like regular folders.
- FTP and SFTP locations can be opened from the address bar or Add location menu using `ftp://host[:port]/path` and `sftp://host[:port]/path` (the `ftp:\\host` / `sftp:\\host` form is also accepted). FTP uses a saved account when available; otherwise it tries anonymous access and prompts if the server rejects it. SFTP prompts for credentials and asks you to verify the server host key. Remote create, rename, delete, copy, upload, and download actions are supported. Credentials and trusted SFTP host keys are stored in Windows Credential Manager, not the app settings. FTP is unencrypted; use SFTP on untrusted networks.
- Persistent favorites, recent locations, and named left/right folder sets in Quick Access.
- Per-tab sorting by name, type, size, or modified date.
- Multi-selection and copy, cut, paste, rename, delete, and new folder; pasting clipboard images, HTML, rich text, or plain text creates timestamped PNG/HTML/RTF/TXT files in the active folder. Clipboard history listens for Windows clipboard updates while the app is running, saves each available binary clipboard format (unknown formats are retained as blobs), and offers restore, per-entry delete, and clear-all actions. It retains up to 1 GB in a hidden subfolder of the initial active folder. File/folder history keeps source paths and displays item counts and source sizes, but does not copy their contents. Drag items onto folders or between panes, or onto a tab to choose Move or Copy.
- Keyboard shortcuts include F2 rename, F3 filter, F6 pane switch, Ctrl+T/W/Tab, Ctrl+Shift+T/P, Ctrl+L, Alt+Left/Right, Ctrl+C/X/V, Delete, Shift+Delete, and Ctrl+Shift+N. The searchable command palette includes common tab, pane, sort, and selection actions; the tab context menu offers duplicate, move, close, and reopen.
- Elevated Command Prompt launch from item and folder-pane context menus, using the selected folder or current pane location.
- New-item menus create folders, text files, ZIP files, and blank PNG/JPEG/BMP images opened with their registered Windows app; shortcuts and network drives use the Windows system wizards.
- Folder-pane menus provide creation, paste, refresh, parent navigation, view options, and a per-pane selection toggle between native item checkboxes and classic Ctrl/Shift multi-selection; empty-space right-click menus provide common folder actions.
- ZIP and CBZ browsing as read-only virtual folders; files and directories can be copied out.
- List, details, and thumbnail views, with per-pane sizing, a slider, and Ctrl+mouse-wheel resizing.
- Adjustable file-name font, size, and color via the Text category in Settings; the live preview shows the selected style. Installed Windows fonts and `.ttf`, `.otf`, or `.ttc` files in the executable's `Fonts` folder are available.
- Background settings control the main window's acrylic tint, tint opacity, luminosity opacity, and fallback color. Changes preview live and are saved under `%LOCALAPPDATA%\FirmwareExplorer\settings.json`; the acrylic blur itself is managed by Windows and has no separate radius setting.
- Thumbnail previews for images, text, and binary files; 3D model files are marked and can be opened in an orbit/zoom/pan viewer.
- Image preview using ImageSharp, Windows codecs, and game-texture decoders for DDS, KTX1, supported KTX2 formats, Blizzard BLP1/BLP2, Valve VTF, PC DXT RenderWare TXD files, and GTA V legacy RAGE YTD dictionaries. TXD preview reads the first supported texture; KTX2 Basis/supercompressed payloads, GTA V Gen9 YTD files, and GTA IV WTD dictionaries are not currently supported. Images inside ZIP/CBZ archives are also previewable.
- Selected images can be converted to full-resolution PNG with transparency preserved. Use the item context menu or the command palette; outputs go beside the source files and existing files are not overwritten. Converting a YTD dictionary exports each contained texture to a separate PNG.
- A hex/entry action for selected files and folders: ordinary files can be edited in 1 KiB pages, with an explicit confirmation before each write; folders show filesystem metadata.
- A per-partition raw hex view guarded by a warning and kept strictly read-only. Startup requests administrator elevation when needed; declining or failing the UAC prompt continues with normal access.
- When folder or file access is denied, the app offers retry, alternate Windows credentials, or cancel. Credentials are not saved and are used only for the requested retry.
- Other regular files open using their registered Windows application.

The archive backend currently supports ZIP-compatible archives (including CBZ), not formats such as 7z or RAR. This is a functional foundation rather than a full feature-for-feature Directory Opus replacement.

Image and 3D format support depends on the built-in decoders/importers and installed Windows codecs; it is not universal. Additional formats can be added through code-level registrations. 3D files use the Assimp importer, but model thumbnails are not currently rendered; the thumbnail view shows the system thumbnail when available and otherwise a 3D badge.
