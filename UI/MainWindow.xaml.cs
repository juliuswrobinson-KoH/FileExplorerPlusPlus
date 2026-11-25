using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Compression;
using System.IO.Enumeration;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.UI.Input.DragDrop;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using UI.Models;
using UI.Services;
using UI.Views;
using Windows.Storage.Pickers;
using WinRT;
using WinRT.Interop;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace UI;

public sealed partial class MainWindow : Window
{
    private const string InternalDragFormat = "ExplorerPlusPlus.Paths";
    private readonly ObservableCollection<ExplorerEntry> _leftEntries = [];
    private readonly ObservableCollection<ExplorerEntry> _rightEntries = [];
    private readonly ObservableCollection<PhysicalDrive> _physicalDrives = [];
    private readonly List<string> _leftTabs = [];
    private readonly List<string> _rightTabs = [];
    private readonly List<string> _leftFilters = [];
    private readonly List<string> _rightFilters = [];
    private readonly List<int> _leftTabViews = [];
    private readonly List<int> _rightTabViews = [];
    private readonly List<double> _leftTabIconSizes = [];
    private readonly List<double> _rightTabIconSizes = [];
    private readonly List<string> _leftTabSortFields = [];
    private readonly List<string> _rightTabSortFields = [];
    private readonly List<bool> _leftTabSortDescending = [];
    private readonly List<bool> _rightTabSortDescending = [];
    private readonly List<List<string>> _leftTabSelectedPaths = [];
    private readonly List<List<string>> _rightTabSelectedPaths = [];
    private readonly Stack<(bool Left, ExplorerTabState State)> _closedTabs = [];
    private readonly List<FilePropertiesWindow> _propertyWindows = [];
    private readonly DispatcherQueueTimer _driveRefreshTimer;
    private readonly DispatcherQueueTimer _sessionSaveTimer;
    private readonly SemaphoreSlim _clipboardHistoryCaptureGate = new(1, 1);
    private WindowsClipboardMonitor? _windowsClipboardMonitor;
    private ClipboardHistoryService? _clipboardHistory;
    private Window? _settingsWindow;
    private DispatcherQueue _uiDispatcherQueue = null!;
    private ExplorerSettings _settings = new();
    private string _itemFontFamily = "Segoe UI";
    private string? _settingsLoadError;
    private string? _sessionRecoveryNotice;
    private List<ExplorerEntry> _leftDirectoryEntries = [];
    private List<ExplorerEntry> _rightDirectoryEntries = [];
    private readonly List<string> _leftBack = [];
    private readonly List<string> _leftForward = [];
    private readonly List<string> _rightBack = [];
    private readonly List<string> _rightForward = [];
    private string? _clipboardPath;
    private bool _clipboardIsCut;
    private bool _leftActive = true;
    private ExplorerEntry? _leftLastClickedEntry;
    private ExplorerEntry? _rightLastClickedEntry;
    private int _leftTabIndex;
    private int _rightTabIndex;
    private bool _initializing;
    private bool _updatingViewControls;
    private bool _refreshingDrives;
    private bool _updatingSearchBox;
    private bool _restoringPaneSelection;
    private int _treeRefreshVersion;
    private TreeViewNode? _computerRoot;
    private int _leftLoadGeneration;
    private int _rightLoadGeneration;
    private Border? _activeSplitter;
    private Grid? _splitterGrid;
    private int _splitterColumnIndex;
    private double _splitterMinimumWidth;
    private double _splitterMinimumOtherWidth;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private bool _acrylicTargetAttached;
    private bool _leftPaneOpen = true;
    private bool _rightPaneOpen = true;
    private bool _singlePaneMode;
    private bool _leftSelectionCheckBoxesEnabled;
    private bool _rightSelectionCheckBoxesEnabled;
    private bool _refreshShellChangesOnActivation;
    private bool _initialLoadStarted;
    private bool _sessionPersistenceEnabled;
    private string? _lastPointerClickPath;
    private long _lastPointerClickTimestamp;
    private bool _lastPointerClickLeft;
    private bool _suppressTabClick;
    private ListView? _internalDragList;
    private string? _internalDragPath;
    private string? _internalDragDestination;
    private Button? _internalDragTargetButton;
    private Brush? _internalDragTargetBackground;
    private string[] _internalDragSources = [];
    private Windows.Foundation.Point _internalDragStart;
    private bool _internalDragStarted;
    private bool _tabDragStarted;
    private bool _tabDragLeft;
    private int _tabDragIndex = -1;
    private Windows.Foundation.Point _tabDragStart;
    private GridLength _dualLeftPaneWidth = new(1, GridUnitType.Star);
    private GridLength _dualRightPaneWidth = new(1, GridUnitType.Star);

    public ObservableCollection<ExplorerEntry> LeftEntries => _leftEntries;
    public ObservableCollection<ExplorerEntry> RightEntries => _rightEntries;
    public ObservableCollection<PhysicalDrive> PhysicalDrives => _physicalDrives;

    private string CurrentPath => _leftActive ? _leftTabs[_leftTabIndex] : _rightTabs[_rightTabIndex];
    private ListView ActiveList => _leftActive ? LeftList : RightList;
    private ObservableCollection<ExplorerEntry> ActiveEntries => _leftActive ? _leftEntries : _rightEntries;
    private StackPanel ActiveTabs => _leftActive ? LeftTabs : RightTabs;
    private int ActiveViewIndex => GetViewIndex(_leftActive);
    private double ActiveIconSize => GetIconSize(_leftActive);
    private int GetViewIndex(bool left) =>
        (left ? _leftTabViews : _rightTabViews)[left ? _leftTabIndex : _rightTabIndex];
    private double GetIconSize(bool left) =>
        (left ? _leftTabIconSizes : _rightTabIconSizes)[left ? _leftTabIndex : _rightTabIndex];

    public MainWindow(string? initialPath = null)
    {
        _initializing = true;
        InitializeComponent();
        Root.AddHandler(UIElement.PointerMovedEvent,
            new PointerEventHandler(InternalDrag_PointerMoved), handledEventsToo: true);
        Root.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler(InternalDrag_PointerReleased), handledEventsToo: true);
        _uiDispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _settings = ExplorerSettingsService.Load(out _settingsLoadError);
        _settings.Session ??= new ExplorerSessionState();
        _itemFontFamily = _settings.ItemFontFamily;
        try
        {
            var recoveryMode = ExplorerSettingsService.BeginSession();
            if (recoveryMode == SessionRecoveryMode.UseLastCleanSession)
            {
                var safeSession = ExplorerSettingsService.LoadLastCleanSession(out var recoveryError);
                if (safeSession is not null)
                {
                    _settings.Session = safeSession;
                    _sessionRecoveryNotice = "The recent folder session failed to reopen twice. Restored the last clean folder session.";
                }
                else
                {
                    _settings.Session = new ExplorerSessionState
                    {
                        LeftTabPaths = [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)],
                        RightTabPaths = [Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)]
                    };
                    _sessionRecoveryNotice = "The recent folder session failed to reopen and no clean session was available. Opened the default folders.";
                    if (recoveryError is not null)
                        _settingsLoadError = recoveryError;
                }
            }
            else if (recoveryMode == SessionRecoveryMode.TryRecentCheckpoint)
            {
                _sessionRecoveryNotice = "The previous session ended unexpectedly. Trying the most recently saved folder session.";
            }
            _sessionPersistenceEnabled = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _settingsLoadError = $"Session recovery could not be initialized: {exception.Message}";
        }
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(MainWindow_KeyDown), true);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
        var windowHandle = WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle));
        appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "FileExplorerIcon.ico"));
        appWindow.TitleBar.BackgroundColor = Microsoft.UI.Colors.Transparent;
        appWindow.TitleBar.InactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        appWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        appWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        appWindow.TitleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
        appWindow.TitleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 190, 198, 208);
        InitializeAcrylicBackdrop();
        var defaultFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var documentsFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var initialFolder = string.IsNullOrWhiteSpace(initialPath)
            ? null
            : File.Exists(initialPath) ? Path.GetDirectoryName(initialPath) : initialPath;
        List<ExplorerTabState> leftTabStates = initialFolder is null
            ? GetRestorableTabStates(_settings.Session.LeftTabs, _settings.Session.LeftTabPaths, defaultFolder)
            : [new ExplorerTabState { Path = initialFolder }];
        var rightTabStates = GetRestorableTabStates(
            _settings.Session.RightTabs, _settings.Session.RightTabPaths, documentsFolder);
        _leftTabs.AddRange(leftTabStates.Select(state => state.Path));
        _rightTabs.AddRange(rightTabStates.Select(state => state.Path));
        _leftTabIndex = initialFolder is null
            ? Math.Clamp(_settings.Session.LeftSelectedTab, 0, _leftTabs.Count - 1)
            : 0;
        _rightTabIndex = Math.Clamp(_settings.Session.RightSelectedTab, 0, _rightTabs.Count - 1);
        foreach (var state in leftTabStates)
        {
            _leftFilters.Add(state.Filter);
            _leftTabViews.Add(state.ViewIndex);
            _leftTabIconSizes.Add(state.IconSize);
            _leftTabSortFields.Add(state.SortField);
            _leftTabSortDescending.Add(state.SortDescending);
            _leftTabSelectedPaths.Add(state.SelectedPaths);
        }
        foreach (var state in rightTabStates)
        {
            _rightFilters.Add(state.Filter);
            _rightTabViews.Add(state.ViewIndex);
            _rightTabIconSizes.Add(state.IconSize);
            _rightTabSortFields.Add(state.SortField);
            _rightTabSortDescending.Add(state.SortDescending);
            _rightTabSelectedPaths.Add(state.SelectedPaths);
        }
        _leftBack.AddRange(_settings.Session.LeftBackHistory.Where(IsRestorablePath));
        _leftForward.AddRange(_settings.Session.LeftForwardHistory.Where(IsRestorablePath));
        _rightBack.AddRange(_settings.Session.RightBackHistory.Where(IsRestorablePath));
        _rightForward.AddRange(_settings.Session.RightForwardHistory.Where(IsRestorablePath));
        if (string.IsNullOrWhiteSpace(_settings.ClipboardHistoryDirectory))
        {
            var historyRoot = FileSystemService.IsArchivePath(CurrentPath) || FileSystemService.IsRemotePath(CurrentPath)
                ? documentsFolder
                : CurrentPath;
            _settings.ClipboardHistoryDirectory = Path.Combine(historyRoot, ".ExplorerPlusPlusClipboardHistory");
            try
            {
                ExplorerSettingsService.Save(_settings);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _settingsLoadError = $"The clipboard history location could not be saved: {exception.Message}";
            }
        }
        SetActivePane(true);
        InitializeNavigationTree();
        SetPaneView(true, GetViewIndex(true));
        SetPaneView(false, GetViewIndex(false));
        UpdateViewControls();
        UpdatePaneLayout();
        _initializing = false;
        _driveRefreshTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _driveRefreshTimer.Interval = TimeSpan.FromSeconds(3);
        _driveRefreshTimer.Tick += async (_, _) => await LoadPhysicalDrivesAsync(reportErrors: false);
        _driveRefreshTimer.Start();
        _sessionSaveTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _sessionSaveTimer.Interval = TimeSpan.FromSeconds(2);
        _sessionSaveTimer.Tick += (_, _) => SaveSessionCheckpoint();
        try
        {
            _windowsClipboardMonitor = new WindowsClipboardMonitor(
                windowHandle,
                WindowsClipboardChanged,
                exception =>
                {
                    StatusText.Text = $"Could not capture Windows clipboard data: {exception.Message}";
                    Debug.WriteLine(StatusText.Text);
                });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            _settingsLoadError = $"Windows clipboard monitoring could not start: {exception.Message}";
        }
        Activated += MainWindow_Activated;
        Closed += (_, _) =>
        {
            _windowsClipboardMonitor?.Dispose();
            _settingsWindow?.Close();
            _driveRefreshTimer.Stop();
            _sessionSaveTimer.Stop();
            if (_sessionPersistenceEnabled)
            {
                try
                {
                    CaptureSessionState();
                    ExplorerSettingsService.CompleteSession(_settings);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    StatusText.Text = $"Could not save the folder session: {exception.Message}";
                    Debug.WriteLine(StatusText.Text);
                }
            }
            if (_acrylicController is not null)
            {
                if (_acrylicTargetAttached)
                    _acrylicController.RemoveSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
                _acrylicController.Dispose();
                _acrylicController = null;
                _acrylicTargetAttached = false;
            }
        };
        if (!string.IsNullOrEmpty(App.StartupNotice))
            StatusText.Text = App.StartupNotice;
        else if (_sessionRecoveryNotice is not null)
            StatusText.Text = _sessionRecoveryNotice;
    }

    private void InitializeAcrylicBackdrop()
    {
        if (!DesktopAcrylicController.IsSupported())
        {
            Root.Background = new SolidColorBrush(ParseFontColor(_settings.BackgroundFallbackColor));
            return;
        }
        _backdropConfiguration = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = SystemBackdropTheme.Dark
        };
        ApplyBackgroundMaterial(_settings);
    }

    private void ApplyBackgroundMaterial(ExplorerSettings settings)
    {
        var fallbackColor = ParseFontColor(settings.BackgroundFallbackColor);
        if (!settings.UseAcrylicBackground || !DesktopAcrylicController.IsSupported())
        {
            if (_acrylicController is not null)
            {
                _acrylicController.RemoveSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
                _acrylicController.Dispose();
                _acrylicController = null;
            }
            Root.Background = new SolidColorBrush(fallbackColor);
            return;
        }

        Root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        if (_acrylicController is null)
        {
            _acrylicController = new DesktopAcrylicController();
            _acrylicController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
            _acrylicTargetAttached = true;
        }
        _acrylicController.TintColor = ParseFontColor(settings.BackgroundTintColor);
        _acrylicController.TintOpacity = (float)settings.BackgroundTintOpacity;
        _acrylicController.LuminosityOpacity = (float)settings.BackgroundLuminosityOpacity;
        _acrylicController.FallbackColor = fallbackColor;
        if (_backdropConfiguration is not null)
            _acrylicController.SetSystemBackdropConfiguration(_backdropConfiguration);
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_backdropConfiguration is not null)
            _backdropConfiguration.IsInputActive = args.WindowActivationState != WindowActivationState.Deactivated;
        if (args.WindowActivationState != WindowActivationState.Deactivated && !_initialLoadStarted)
        {
            _initialLoadStarted = true;
            LoadPane(true);
            LoadPane(false);
            _ = LoadPhysicalDrivesAsync();
        }
        if (args.WindowActivationState != WindowActivationState.Deactivated && _settingsLoadError is not null)
        {
            var error = _settingsLoadError;
            _settingsLoadError = null;
            await ShowMessageAsync("Settings could not be loaded", error);
        }
        if (args.WindowActivationState != WindowActivationState.Deactivated && _refreshShellChangesOnActivation)
        {
            _refreshShellChangesOnActivation = false;
            LoadPane(_leftActive);
            _ = LoadPhysicalDrivesAsync(reportErrors: false);
        }
    }

    private List<QuickLocation> BuildQuickAccess()
    {
        var locations = new List<QuickLocation>();
        var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddLocation("Home", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "\uE80F",
            locations, knownPaths, iconResourceIndex: 317);
        AddLocation("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "\uE8FC", locations, knownPaths);
        AddLocation("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "\uE8A5", locations, knownPaths);
        AddLocation("Downloads", KnownFolderService.GetDownloadsPath(), "\uE896", locations, knownPaths);
        AddLocation("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "\uEB9F", locations, knownPaths);
        AddLocation("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "\uE189", locations, knownPaths);
        AddLocation("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "\uE714", locations, knownPaths);
        foreach (var bookmark in _settings.Bookmarks.Where(IsRestorablePath))
        {
            var label = FileSystemService.IsRemotePath(bookmark)
                ? FileSystemService.GetName(bookmark)
                : Path.GetFileName(bookmark.TrimEnd('\\', '/'));
            AddLocation(string.IsNullOrEmpty(label) ? bookmark : label, bookmark, "\uE734", locations, knownPaths, canRemove: true);
        }
        var recentCount = 0;
        foreach (var recent in _settings.RecentLocations.Where(IsRestorablePath))
        {
            var label = FileSystemService.IsRemotePath(recent)
                ? FileSystemService.GetName(recent)
                : Path.GetFileName(recent.TrimEnd('\\', '/'));
            if (AddLocation(string.IsNullOrEmpty(label) ? recent : label, recent, "\uE823", locations, knownPaths) &&
                ++recentCount == 8)
                break;
        }
        foreach (var set in _settings.FolderSets.Where(set => IsRestorablePath(set.LeftPath) && IsRestorablePath(set.RightPath)))
            AddLocation(set.Name, set.LeftPath, "\uE8B9", locations, knownPaths, set.RightPath, canRemove: true);
        return locations;
    }

    private void InitializeNavigationTree()
    {
        FolderTree.RootNodes.Clear();
        _computerRoot = new TreeViewNode
        {
            Content = new TreeItem("This PC", "", false, false, "\uE7F4",
                isComputerRoot: true, shellPath: "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}")
            {
                FileIconSize = _settings.FileIconSize
            },
            IsExpanded = true
        };
        RefreshQuickAccessTree();
        FolderTree.RootNodes.Add(_computerRoot);
        RefreshComputerTree();

        var recycleBinItem = new TreeItem(
            "Recycle Bin",
            RecycleBinService.Path,
            false,
            true,
            "\uE74D",
            shellPath: RecycleBinService.Path,
            isRecycleBin: true)
        {
            FileIconSize = _settings.FileIconSize
        };
        FolderTree.RootNodes.Add(new TreeViewNode { Content = recycleBinItem });
        _ = LoadShellTreeIconAsync(recycleBinItem);
    }

    private void RefreshQuickAccessTree()
    {
        foreach (var node in FolderTree.RootNodes
                     .Where(node => node.Content is TreeItem { IsQuickAccessEntry: true })
                     .ToArray())
            FolderTree.RootNodes.Remove(node);

        var quickAccessNodes = new List<TreeViewNode>();
        foreach (var location in BuildQuickAccess())
        {
            var isDirectory = !FileSystemService.IsRemotePath(location.Path) && Directory.Exists(location.Path);
            var item = new TreeItem(
                location.Name,
                location.Path,
                false,
                isDirectory,
                location.Icon,
                isQuickAccessEntry: true,
                otherPath: location.OtherPath,
                canRemove: location.CanRemove)
            {
                FileIconSize = _settings.FileIconSize
            };
            quickAccessNodes.Add(new TreeViewNode
            {
                Content = item,
                HasUnrealizedChildren = isDirectory
            });
            if (location.IconResourceIndex is int iconResourceIndex)
                _ = LoadQuickLocationIconAsync(item, iconResourceIndex);
            else if (isDirectory)
                _ = LoadTreeIconAsync(item);
        }

        var computerIndex = _computerRoot is null ? -1 : FolderTree.RootNodes.IndexOf(_computerRoot);
        var insertionIndex = computerIndex < 0 ? FolderTree.RootNodes.Count : computerIndex;
        foreach (var node in quickAccessNodes)
            FolderTree.RootNodes.Insert(insertionIndex++, node);
    }

    private void RefreshComputerTree()
    {
        if (_computerRoot is null)
            return;

        var drives = DriveInfo.GetDrives().OrderBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var drivePaths = drives.Select(drive => drive.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentDrivePaths = _computerRoot.Children
            .Select(node => node.Content is TreeItem item ? item.Path : "")
            .Where(drivePaths.Contains)
            .ToArray();
        if (drives.Select(drive => drive.Name).SequenceEqual(currentDrivePaths, StringComparer.OrdinalIgnoreCase))
            return;

        var additionalNodes = _computerRoot.Children
            .Where(node => node.Content is not TreeItem item || !drivePaths.Contains(item.Path))
            .ToArray();
        _computerRoot.Children.Clear();
        foreach (var drive in drives)
        {
            var item = new TreeItem(drive.Name, drive.Name, false, true, "\uE7F4")
            {
                FileIconSize = _settings.FileIconSize
            };
            _computerRoot.Children.Add(new TreeViewNode { Content = item, HasUnrealizedChildren = true });
            _ = LoadDriveIconAsync(item);
        }
        foreach (var node in additionalNodes)
            _computerRoot.Children.Add(node);
        _computerRoot.HasUnrealizedChildren = false;
        _computerRoot.IsExpanded = true;
    }

    private void AddBookmark(string path)
    {
        if (!IsRestorablePath(path))
            return;
        if (!_settings.Bookmarks.Contains(path, StringComparer.OrdinalIgnoreCase))
            _settings.Bookmarks.Add(path);
        RefreshQuickAccessTree();
        ScheduleSessionSave();
    }

    private async Task SaveFolderSetAsync()
    {
        var name = await PromptAsync("Save folder set", "Folder set name");
        if (string.IsNullOrWhiteSpace(name))
            return;
        _settings.FolderSets.RemoveAll(set => string.Equals(set.Name, name.Trim(), StringComparison.CurrentCultureIgnoreCase));
        _settings.FolderSets.Add(new SavedFolderSet
        {
            Name = name.Trim(),
            LeftPath = _leftTabs[_leftTabIndex],
            RightPath = _rightTabs[_rightTabIndex]
        });
        RefreshQuickAccessTree();
        ScheduleSessionSave();
    }

    private static void AddHistory(List<string> history, string path)
    {
        history.RemoveAll(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
        history.Add(path);
        if (history.Count > 100)
            history.RemoveAt(0);
    }

    private static bool TryPopHistory(List<string> history, out string path)
    {
        if (history.Count == 0)
        {
            path = "";
            return false;
        }
        var index = history.Count - 1;
        path = history[index];
        history.RemoveAt(index);
        return true;
    }

    private static bool AddLocation(
        string name,
        string path,
        string icon,
        ICollection<QuickLocation> locations,
        HashSet<string> knownPaths,
        string? otherPath = null,
        bool canRemove = false,
        int? iconResourceIndex = null)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            (!FileSystemService.IsRemotePath(path) &&
             !NetworkShareService.IsServerPath(path) &&
             !Directory.Exists(path)) ||
            !knownPaths.Add(NormalizeQuickAccessPath(path)))
            return false;

        locations.Add(new QuickLocation(name, path, icon, otherPath, canRemove, iconResourceIndex));
        return true;
    }

    private static string NormalizeQuickAccessPath(string path) =>
        FileSystemService.IsRemotePath(path)
            ? RemoteFileSystemService.NormalizeAddress(path).TrimEnd('/')
            : NetworkShareService.IsServerPath(path)
                ? path.TrimEnd('\\')
                : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private async void LoadPane(bool left)
    {
        ScheduleSessionSave();
        var generation = left ? ++_leftLoadGeneration : ++_rightLoadGeneration;
        var path = left ? _leftTabs[_leftTabIndex] : _rightTabs[_rightTabIndex];
        var address = left ? LeftAddress : RightAddress;
        address.Text = path;
        address.Visibility = Visibility.Collapsed;
        (left ? LeftBreadcrumbScroller : RightBreadcrumbScroller).Visibility = Visibility.Visible;
        RenderBreadcrumbs(left, path);
        var filter = (left ? _leftFilters : _rightFilters)[left ? _leftTabIndex : _rightTabIndex].Trim();
        try
        {
            var allEntries = RecycleBinService.IsPath(path)
                ? await Task.Run(RecycleBinService.GetEntries)
                : await AccessRetryService.RunAsync(Root.XamlRoot, path, () => FileSystemService.GetEntries(path).ToList());
            if (!_uiDispatcherQueue.TryEnqueue(() =>
                {
                    if (generation == (left ? _leftLoadGeneration : _rightLoadGeneration))
                        ApplyPaneEntries(left, path, filter, allEntries);
                }))
                Debug.WriteLine($"Unable to display folder contents because the UI dispatcher is unavailable: {path}");
        }
        catch (RemoteAuthenticationException exception)
        {
            if (generation != (left ? _leftLoadGeneration : _rightLoadGeneration))
                return;
            var credentials = await PromptRemoteCredentialsAsync(path, exception.Message);
            if (credentials is null)
            {
                ApplyPaneEntries(left, path, filter, null);
                return;
            }

            RemoteFileSystemService.SetCredentials(
                path, credentials.Value.UserName, credentials.Value.Password, credentials.Value.Persist);
            LoadPane(left);
        }
        catch (RemoteHostKeyException exception)
        {
            if (generation != (left ? _leftLoadGeneration : _rightLoadGeneration))
                return;
            var confirm = new ContentDialog
            {
                Title = exception.KeyChanged ? "SFTP server identity changed" : "Trust SFTP server?",
                Content = $"{exception.Message}\n\nSHA-256 fingerprint:\n{exception.Fingerprint}\n\nOnly trust this key if you verified it with the server administrator.",
                PrimaryButtonText = exception.KeyChanged ? "Trust new key" : "Trust and connect",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Root.XamlRoot
            };
            if (await confirm.ShowAsync() == ContentDialogResult.Primary && exception.Fingerprint is not null)
            {
                RemoteFileSystemService.TrustHostKey(path, exception.Fingerprint);
                LoadPane(left);
            }
            else
            {
                ApplyPaneEntries(left, path, filter, null);
            }
        }
        catch (Exception exception)
        {
            if (!_uiDispatcherQueue.TryEnqueue(() =>
                {
                    if (generation == (left ? _leftLoadGeneration : _rightLoadGeneration))
                        ShowPaneLoadError(left, path, exception);
                }))
                Debug.WriteLine($"Unable to report folder load failure for {path}: {exception}");
        }
    }

    private async Task<(string UserName, string Password, bool Persist)?> PromptRemoteCredentialsAsync(
        string path,
        string reason)
    {
        var userName = new TextBox { Header = "User name" };
        var password = new PasswordBox { Header = "Password" };
        var persist = new CheckBox
        {
            Content = "Save credentials in Windows Credential Manager",
            IsChecked = true
        };
        var ftpWarning = FileSystemService.IsRemotePath(path) &&
                         RemoteFileSystemService.TryParse(path, out var remoteLocation) &&
                         remoteLocation.IsFtp
            ? "\n\nFTP does not encrypt credentials or file contents. Use SFTP on untrusted networks."
            : "";
        var content = new StackPanel { Spacing = 10, Width = 430 };
        content.Children.Add(new TextBlock
        {
            Text = $"{reason}\n\nCredentials are sent only to {path} and are not stored in the application settings file.{ftpWarning}",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(userName);
        content.Children.Add(password);
        content.Children.Add(persist);
        var dialog = new ContentDialog
        {
            Title = "Connect to remote server",
            Content = content,
            PrimaryButtonText = "Connect",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;
        if (string.IsNullOrWhiteSpace(userName.Text) || string.IsNullOrEmpty(password.Password))
        {
            await ShowMessageAsync("Credentials required", "Enter both a user name and password.");
            return null;
        }

        var enteredPassword = password.Password;
        password.Password = string.Empty;
        return (userName.Text.Trim(), enteredPassword, persist.IsChecked == true);
    }

    private void ApplyPaneEntries(bool left, string path, string filter, List<ExplorerEntry>? allEntries)
    {
        var entries = left ? _leftEntries : _rightEntries;
        if (allEntries is null)
        {
            _restoringPaneSelection = true;
            entries.Clear();
            _restoringPaneSelection = false;
            if (left)
                _leftDirectoryEntries = [];
            else
                _rightDirectoryEntries = [];
            StatusText.Text = $"Access to {path} was canceled.";
            return;
        }

        if (left)
            _leftDirectoryEntries = allEntries;
        else
            _rightDirectoryEntries = allEntries;
        var sortFields = left ? _leftTabSortFields : _rightTabSortFields;
        var sortDescending = left ? _leftTabSortDescending : _rightTabSortDescending;
        var tabIndex = left ? _leftTabIndex : _rightTabIndex;
        var result = allEntries
            .Where(entry => string.IsNullOrEmpty(filter) || entry.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry, Comparer<ExplorerEntry>.Create((first, second) =>
            {
                var comparison = sortFields[tabIndex] switch
                {
                    "Type" => StringComparer.CurrentCultureIgnoreCase.Compare(first.Type, second.Type),
                    "Size" => first.SizeBytes.CompareTo(second.SizeBytes),
                    "Modified" => first.ModifiedTime.CompareTo(second.ModifiedTime),
                    _ => StringComparer.CurrentCultureIgnoreCase.Compare(first.Name, second.Name)
                };
                return sortDescending[tabIndex] ? -comparison : comparison;
            }))
            .ToList();
        _restoringPaneSelection = true;
        entries.Clear();
        foreach (var entry in result)
        {
            entry.TileSize = GetIconSize(left);
            ApplyItemTypography(entry);
            entries.Add(entry);
            if (entry.IsRecycleBinEntry)
                _ = LoadShellEntryIconAsync(entry);
            else if (!entry.IsArchiveEntry)
                _ = LoadEntryIconAsync(entry);
        }
        var selectedPaths = (left ? _leftTabSelectedPaths : _rightTabSelectedPaths)[tabIndex];
        foreach (var entry in result.Where(entry => selectedPaths.Contains(entry.FullPath, StringComparer.OrdinalIgnoreCase)))
            (left ? LeftList : RightList).SelectedItems.Add(entry);
        _restoringPaneSelection = false;
        RenderTabs(left, left ? LeftTabs : RightTabs);
        StatusText.Text = $"{result.Count} items  •  {path}";
        if (_leftActive == left)
            RefreshTree(path, allEntries);
    }

    private void RenderBreadcrumbs(bool left, string path)
    {
        var segments = left ? LeftBreadcrumbSegments : RightBreadcrumbSegments;
        segments.Children.Clear();
        if (RecycleBinService.IsPath(path))
        {
            AddBreadcrumbSegment(segments, "Recycle Bin", RecycleBinService.Path);
            return;
        }
        if (FileSystemService.IsArchivePath(path))
        {
            var (archive, entry) = FileSystemService.SplitArchivePath(path);
            AddBreadcrumbSegment(segments, Path.GetPathRoot(archive) ?? archive, archive);
            AddBreadcrumbSegment(segments, Path.GetFileName(archive), FileSystemService.ArchivePath(archive));
            var archivePath = "";
            foreach (var part in entry.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                archivePath = string.IsNullOrEmpty(archivePath) ? part : $"{archivePath}/{part}";
                AddBreadcrumbSegment(segments, part, FileSystemService.ArchivePath(archive, archivePath));
            }
            return;
        }
        if (NetworkShareService.IsServerPath(path))
        {
            AddBreadcrumbSegment(segments, path.TrimEnd('\\'), path);
            return;
        }
        if (RemoteFileSystemService.TryParse(path, out var remoteLocation))
        {
            var remoteRoot = RemoteFileSystemService.GetRootPath(path);
            AddBreadcrumbSegment(segments, $"{remoteLocation.Scheme}://{remoteLocation.Host}", remoteRoot);
            var currentRemotePath = remoteRoot;
            foreach (var part in remoteLocation.Path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                currentRemotePath = FileSystemService.CombinePath(currentRemotePath, part);
                AddBreadcrumbSegment(segments, part, currentRemotePath);
            }
            return;
        }

        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? fullPath;
        AddBreadcrumbSegment(segments, root, root);
        var current = root;
        foreach (var part in fullPath[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            AddBreadcrumbSegment(segments, part, current);
        }
    }

    private void AddBreadcrumbSegment(Panel segments, string label, string path)
    {
        if (segments.Children.Count > 0)
            segments.Children.Add(new TextBlock { Text = "›", Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
        {
            Content = label,
            Tag = path,
            Padding = new Thickness(5, 1, 5, 1),
            MinHeight = 24,
            MinWidth = 0
        };
        ToolTipService.SetToolTip(button, path);
        button.Click += (_, _) =>
        {
            if (button.Tag is string target)
            {
                var paneLeft = ReferenceEquals(segments, LeftBreadcrumbSegments);
                SetActivePane(paneLeft);
                Navigate(target);
            }
        };
        segments.Children.Add(button);
    }

    private void AddressEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string pane })
            return;
        var left = pane == "Left";
        SetActivePane(left);
        var address = left ? LeftAddress : RightAddress;
        address.Text = CurrentPath;
        (left ? LeftBreadcrumbScroller : RightBreadcrumbScroller).Visibility = Visibility.Collapsed;
        address.Visibility = Visibility.Visible;
        address.Focus(FocusState.Programmatic);
    }

    private void CopyPanePathToOther_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string pane })
            return;

        var sourceLeft = pane == "Left";
        var sourcePath = sourceLeft
            ? _leftTabs[_leftTabIndex]
            : _rightTabs[_rightTabIndex];
        NavigatePane(!sourceLeft, sourcePath);
    }

    private void SwapPanePaths_Click(object sender, RoutedEventArgs e)
    {
        var leftPath = _leftTabs[_leftTabIndex];
        var rightPath = _rightTabs[_rightTabIndex];
        if (string.Equals(leftPath, rightPath, StringComparison.OrdinalIgnoreCase))
            return;

        NavigatePane(true, rightPath);
        NavigatePane(false, leftPath);
    }

    private void Address_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            return;

        var text = sender.Text.Trim();
        if (text.Length == 0)
        {
            sender.ItemsSource = Array.Empty<string>();
            return;
        }

        try
        {
            if (FileSystemService.IsRemotePath(text) || text.StartsWith("ftp:", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("sftp:", StringComparison.OrdinalIgnoreCase))
            {
                sender.ItemsSource = _settings.RecentLocations
                    .Where(candidate => candidate.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                    .Take(20)
                    .ToList();
                return;
            }

            var expanded = Environment.ExpandEnvironmentVariables(text);
            var parent = Path.GetDirectoryName(expanded);
            var partial = Path.GetFileName(expanded);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            {
                sender.ItemsSource = Array.Empty<string>();
                return;
            }
            var directoryMatches = Directory.EnumerateDirectories(parent)
                .Where(candidate => Path.GetFileName(candidate).StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                .OrderBy(candidate => candidate, StringComparer.CurrentCultureIgnoreCase)
                .Take(20)
                .ToList();
            var recentMatches = _settings.RecentLocations
                .Where(candidate => candidate.StartsWith(expanded, StringComparison.OrdinalIgnoreCase))
                .Take(10);
            sender.ItemsSource = recentMatches.Concat(directoryMatches)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Debug.WriteLine($"Unable to suggest locations for '{text}': {exception.Message}");
            sender.ItemsSource = Array.Empty<string>();
        }
    }

    private void Address_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var path = args.ChosenSuggestion as string ?? args.QueryText.Trim();
        if (string.IsNullOrWhiteSpace(path))
            return;
        var left = ReferenceEquals(sender, LeftAddress);
        SetActivePane(left);
        if (!path.StartsWith("ftp:", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("sftp:", StringComparison.OrdinalIgnoreCase))
            path = Environment.ExpandEnvironmentVariables(path);
        Navigate(path);
    }

    private void ShowPaneLoadError(bool left, string path, Exception exception)
    {
        (left ? _leftEntries : _rightEntries).Clear();
        if (left)
            _leftDirectoryEntries = [];
        else
            _rightDirectoryEntries = [];
        StatusText.Text = $"Could not read folder: {exception.Message}";
        _ = ShowMessageAsync("Unable to open location", exception.Message);
    }

    private static List<ExplorerTabState> GetRestorableTabStates(
        IEnumerable<ExplorerTabState>? savedStates,
        IEnumerable<string>? savedPaths,
        string fallbackPath)
    {
        var tabs = savedStates?
            .Where(state => IsRestorablePath(state.Path))
            .Select(state => new ExplorerTabState
            {
                Path = state.Path,
                Filter = state.Filter ?? "",
                ViewIndex = state.ViewIndex is >= 0 and <= 2 ? state.ViewIndex : 1,
                IconSize = double.IsFinite(state.IconSize) && state.IconSize is >= 64 and <= 512
                    ? state.IconSize
                    : 120,
                SortField = state.SortField is "Name" or "Type" or "Size" or "Modified" ? state.SortField : "Name",
                SortDescending = state.SortDescending,
                SelectedPaths = state.SelectedPaths ?? []
            })
            .ToList();
        if (tabs is { Count: > 0 })
            return tabs;

        var paths = savedPaths?.Where(IsRestorablePath).ToList();
        return paths is { Count: > 0 }
            ? paths.Select(path => new ExplorerTabState { Path = path }).ToList()
            : [new ExplorerTabState { Path = fallbackPath }];
    }

    private static bool IsRestorablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        if (FileSystemService.IsRemotePath(path))
            return true;
        if (NetworkShareService.IsServerPath(path))
            return true;
        if (RecycleBinService.IsPath(path))
            return true;
        if (!FileSystemService.IsArchivePath(path))
            return Directory.Exists(path);

        try
        {
            var (archivePath, _) = FileSystemService.SplitArchivePath(path);
            return File.Exists(archivePath);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void ScheduleSessionSave()
    {
        if (!_sessionPersistenceEnabled || _sessionSaveTimer is null)
            return;
        _sessionSaveTimer.Stop();
        _sessionSaveTimer.Start();
    }

    private void CaptureSessionState()
    {
        CaptureActiveTabState(true);
        CaptureActiveTabState(false);
        _settings.Session = new ExplorerSessionState
        {
            LeftTabPaths = [.. _leftTabs],
            RightTabPaths = [.. _rightTabs],
            LeftSelectedTab = _leftTabIndex,
            RightSelectedTab = _rightTabIndex,
            LeftTabs = CreateTabStates(true),
            RightTabs = CreateTabStates(false),
            LeftBackHistory = [.. _leftBack],
            LeftForwardHistory = [.. _leftForward],
            RightBackHistory = [.. _rightBack],
            RightForwardHistory = [.. _rightForward]
        };
    }

    private List<ExplorerTabState> CreateTabStates(bool left)
    {
        var paths = left ? _leftTabs : _rightTabs;
        var filters = left ? _leftFilters : _rightFilters;
        var views = left ? _leftTabViews : _rightTabViews;
        var sizes = left ? _leftTabIconSizes : _rightTabIconSizes;
        var sortFields = left ? _leftTabSortFields : _rightTabSortFields;
        var sortDescending = left ? _leftTabSortDescending : _rightTabSortDescending;
        var selectedPaths = left ? _leftTabSelectedPaths : _rightTabSelectedPaths;
        return paths.Select((path, index) => new ExplorerTabState
        {
            Path = path,
            Filter = filters[index],
            ViewIndex = views[index],
            IconSize = sizes[index],
            SortField = sortFields[index],
            SortDescending = sortDescending[index],
            SelectedPaths = [.. selectedPaths[index]]
        }).ToList();
    }

    private void CaptureActiveTabState(bool left)
    {
        var index = left ? _leftTabIndex : _rightTabIndex;
        var list = left ? LeftList : RightList;
        var selectedPaths = left ? _leftTabSelectedPaths : _rightTabSelectedPaths;
        if (index >= 0 && index < selectedPaths.Count)
            selectedPaths[index] = list.SelectedItems.Cast<ExplorerEntry>().Select(entry => entry.FullPath).ToList();
    }

    private async void SaveSessionCheckpoint()
    {
        _sessionSaveTimer.Stop();
        if (!_sessionPersistenceEnabled)
            return;

        try
        {
            CaptureSessionState();
            ExplorerSettingsService.Save(_settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _sessionPersistenceEnabled = false;
            StatusText.Text = $"Could not save the folder session: {exception.Message}";
            await ShowMessageAsync("Unable to save folder session", exception.Message);
        }
    }

    private void RenderTabs(bool left, StackPanel container)
    {
        container.Children.Clear();
        var tabs = left ? _leftTabs : _rightTabs;
        var selected = left ? _leftTabIndex : _rightTabIndex;
        for (var index = 0; index < tabs.Count; index++)
        {
            var tabIndex = index;
            var label = Path.GetFileName(tabs[index].TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(label))
                label = tabs[index];
            var tabHeader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
            var button = new Button
            {
                Content = label,
                Tag = tabIndex,
                Padding = new Thickness(8, 3, 5, 3),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                AllowDrop = true
            };
            var tabPath = tabs[index];
            button.DragOver += (_, args) => Tab_DragOver(tabPath, args);
            button.Drop += async (_, args) => await Tab_DropAsync(tabPath, args);
            var close = new Button
            {
                Content = "\uE8BB",
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 8,
                Width = 17,
                Height = 17,
                MinWidth = 17,
                Padding = new Thickness(0),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(255, 115, 35, 42)),
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = true
            };
            ToolTipService.SetToolTip(close, "Close tab");
            button.Click += (_, _) =>
            {
                CaptureActiveTabState(left);
                if (left)
                    _leftTabIndex = tabIndex;
                else
                    _rightTabIndex = tabIndex;
                SetActivePane(left);
                SetPaneView(left, GetViewIndex(left));
                LoadPane(left);
            };
            button.PointerPressed += (_, args) =>
            {
                if (!args.GetCurrentPoint(button).Properties.IsMiddleButtonPressed)
                {
                    if (args.GetCurrentPoint(button).Properties.IsLeftButtonPressed)
                    {
                        _tabDragLeft = left;
                        _tabDragIndex = tabIndex;
                        _tabDragStart = args.GetCurrentPoint(button).Position;
                        _tabDragStarted = false;
                    }
                    return;
                }
                CloseTab(left, tabIndex);
                args.Handled = true;
            };
            button.PointerMoved += (_, args) => TabButton_PointerMoved(left, tabIndex, container, button, args);
            button.PointerReleased += (_, _) => FinishTabDrag(left, container);
            button.RightTapped += (_, args) =>
            {
                var menu = new MenuFlyout();
                AddMenuAction(menu, "Duplicate tab", () => { DuplicateTab(left, tabIndex); return Task.CompletedTask; });
                AddMenuAction(menu, "Move tab to other pane", () => { MoveTabToOtherPane(left, tabIndex); return Task.CompletedTask; });
                AddMenuAction(menu, "Close tab", () => { CloseTab(left, tabIndex); return Task.CompletedTask; });
                AddMenuAction(menu, "Reopen closed tab", () => { ReopenClosedTab(); return Task.CompletedTask; },
                    _closedTabs.Count > 0);
                menu.ShowAt(button, args.GetPosition(button));
                args.Handled = true;
            };
            close.Click += (_, _) => CloseTab(left, tabIndex);
            tabHeader.Children.Add(button);
            tabHeader.Children.Add(close);
            container.Children.Add(new Border
            {
                Background = new SolidColorBrush(index == selected
                    ? Windows.UI.Color.FromArgb(255, 44, 58, 72)
                    : Windows.UI.Color.FromArgb(255, 31, 35, 42)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 58, 65, 76)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(2, 1, 2, 1),
                Child = tabHeader
            });
        }
        var add = new Button { Content = "+", Padding = new Thickness(9, 3, 9, 3) };
        ToolTipService.SetToolTip(add, "Add tab");
        add.Click += (_, _) => AddTab(left, tabs[left ? _leftTabIndex : _rightTabIndex]);
        container.Children.Add(add);
    }

    private void CloseTab(bool left, int tabIndex)
    {
        var tabs = left ? _leftTabs : _rightTabs;
        if (tabs.Count <= 1)
        {
            _closedTabs.Push((left, GetTabState(left, tabIndex)));
            while (_closedTabs.Count > 20)
                _closedTabs.Pop();
            if (left)
                _leftPaneOpen = false;
            else
                _rightPaneOpen = false;

            if (!_leftPaneOpen && _rightPaneOpen)
                SetActivePane(false);
            else if (!_rightPaneOpen && _leftPaneOpen)
                SetActivePane(true);
            _singlePaneMode = _leftPaneOpen != _rightPaneOpen ||
                (_singlePaneMode && _leftPaneOpen && _rightPaneOpen);
            UpdatePaneLayout();
            UpdateViewControls();
            return;
        }

        var paneWasActive = _leftActive == left;
        CaptureActiveTabState(left);
        _closedTabs.Push((left, GetTabState(left, tabIndex)));
        while (_closedTabs.Count > 20)
            _closedTabs.Pop();
        tabs.RemoveAt(tabIndex);
        RemoveTabState(left, tabIndex);
        if (left)
            _leftTabIndex = Math.Min(_leftTabIndex - (tabIndex < _leftTabIndex ? 1 : 0), tabs.Count - 1);
        else
            _rightTabIndex = Math.Min(_rightTabIndex - (tabIndex < _rightTabIndex ? 1 : 0), tabs.Count - 1);

        if (paneWasActive)
            SetActivePane(left);
        SetPaneView(left, GetViewIndex(left));
        LoadPane(left);
    }

    private void TabButton_PointerMoved(
        bool left, int tabIndex, StackPanel container, Button button, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(button);
        if (!point.Properties.IsLeftButtonPressed)
            return;
        if (!_tabDragStarted &&
            (Math.Abs(point.Position.X - _tabDragStart.X) > 8 || Math.Abs(point.Position.Y - _tabDragStart.Y) > 8))
            _tabDragStarted = true;
        if (!_tabDragStarted || _tabDragLeft != left)
            return;

        var currentIndex = _tabDragIndex;
        var targetPoint = args.GetCurrentPoint(container).Position;
        var targetIndex = -1;
        for (var index = 0; index < container.Children.Count - 1; index++)
        {
            if (container.Children[index] is not FrameworkElement child)
                continue;
            var origin = child.TransformToVisual(container).TransformPoint(new Windows.Foundation.Point(0, 0));
            if (targetPoint.X < origin.X + child.ActualWidth / 2)
            {
                targetIndex = index;
                break;
            }
        }
        if (targetIndex < 0)
            targetIndex = (left ? _leftTabs : _rightTabs).Count - 1;
        if (targetIndex == currentIndex)
            return;
        MoveTab(left, currentIndex, targetIndex);
        _tabDragIndex = targetIndex;
    }

    private void FinishTabDrag(bool left, StackPanel container)
    {
        if (!_tabDragStarted || _tabDragLeft != left)
            return;
        _tabDragStarted = false;
        RenderTabs(left, container);
        SetPaneView(left, GetViewIndex(left));
        LoadPane(left);
    }

    private void MoveTab(bool left, int oldIndex, int newIndex)
    {
        var tabs = left ? _leftTabs : _rightTabs;
        if (oldIndex < 0 || oldIndex >= tabs.Count || newIndex < 0 || newIndex >= tabs.Count)
            return;
        MoveItem(tabs, oldIndex, newIndex);
        MoveItem(left ? _leftFilters : _rightFilters, oldIndex, newIndex);
        MoveItem(left ? _leftTabViews : _rightTabViews, oldIndex, newIndex);
        MoveItem(left ? _leftTabIconSizes : _rightTabIconSizes, oldIndex, newIndex);
        MoveItem(left ? _leftTabSortFields : _rightTabSortFields, oldIndex, newIndex);
        MoveItem(left ? _leftTabSortDescending : _rightTabSortDescending, oldIndex, newIndex);
        MoveItem(left ? _leftTabSelectedPaths : _rightTabSelectedPaths, oldIndex, newIndex);
        var selected = left ? _leftTabIndex : _rightTabIndex;
        if (selected == oldIndex)
            selected = newIndex;
        else if (oldIndex < selected && selected <= newIndex)
            selected--;
        else if (newIndex <= selected && selected < oldIndex)
            selected++;
        if (left)
            _leftTabIndex = selected;
        else
            _rightTabIndex = selected;
        ScheduleSessionSave();
    }

    private static void MoveItem<T>(List<T> items, int oldIndex, int newIndex)
    {
        var item = items[oldIndex];
        items.RemoveAt(oldIndex);
        items.Insert(newIndex, item);
    }

    private ExplorerTabState GetTabState(bool left, int index) => new()
    {
        Path = (left ? _leftTabs : _rightTabs)[index],
        Filter = (left ? _leftFilters : _rightFilters)[index],
        ViewIndex = (left ? _leftTabViews : _rightTabViews)[index],
        IconSize = (left ? _leftTabIconSizes : _rightTabIconSizes)[index],
        SortField = (left ? _leftTabSortFields : _rightTabSortFields)[index],
        SortDescending = (left ? _leftTabSortDescending : _rightTabSortDescending)[index],
        SelectedPaths = [.. (left ? _leftTabSelectedPaths : _rightTabSelectedPaths)[index]]
    };

    private void RemoveTabState(bool left, int index)
    {
        (left ? _leftFilters : _rightFilters).RemoveAt(index);
        (left ? _leftTabViews : _rightTabViews).RemoveAt(index);
        (left ? _leftTabIconSizes : _rightTabIconSizes).RemoveAt(index);
        (left ? _leftTabSortFields : _rightTabSortFields).RemoveAt(index);
        (left ? _leftTabSortDescending : _rightTabSortDescending).RemoveAt(index);
        (left ? _leftTabSelectedPaths : _rightTabSelectedPaths).RemoveAt(index);
    }

    private void AddTabState(bool left, ExplorerTabState state, int index)
    {
        (left ? _leftTabs : _rightTabs).Insert(index, state.Path);
        (left ? _leftFilters : _rightFilters).Insert(index, state.Filter);
        (left ? _leftTabViews : _rightTabViews).Insert(index, state.ViewIndex);
        (left ? _leftTabIconSizes : _rightTabIconSizes).Insert(index, state.IconSize);
        (left ? _leftTabSortFields : _rightTabSortFields).Insert(index, state.SortField);
        (left ? _leftTabSortDescending : _rightTabSortDescending).Insert(index, state.SortDescending);
        (left ? _leftTabSelectedPaths : _rightTabSelectedPaths).Insert(index, state.SelectedPaths);
    }

    private void ReopenClosedTab()
    {
        if (!_closedTabs.TryPop(out var closed))
            return;
        var tabs = closed.Left ? _leftTabs : _rightTabs;
        if (closed.Left ? !_leftPaneOpen : !_rightPaneOpen)
        {
            if (closed.Left)
            {
                _leftPaneOpen = true;
                _leftTabIndex = Math.Max(0, _leftTabs.FindIndex(path => string.Equals(path, closed.State.Path, StringComparison.OrdinalIgnoreCase)));
            }
            else
            {
                _rightPaneOpen = true;
                _rightTabIndex = Math.Max(0, _rightTabs.FindIndex(path => string.Equals(path, closed.State.Path, StringComparison.OrdinalIgnoreCase)));
            }
            SetActivePane(closed.Left);
            UpdatePaneLayout();
            LoadPane(closed.Left);
            return;
        }
        if (closed.Left)
            _leftPaneOpen = true;
        else
            _rightPaneOpen = true;
        AddTabState(closed.Left, closed.State, tabs.Count);
        if (closed.Left)
            _leftTabIndex = tabs.Count - 1;
        else
            _rightTabIndex = tabs.Count - 1;
        SetActivePane(closed.Left);
        SetPaneView(closed.Left, GetViewIndex(closed.Left));
        UpdatePaneLayout();
        LoadPane(closed.Left);
    }

    private void DuplicateTab(bool left, int index)
    {
        CaptureActiveTabState(left);
        var tabs = left ? _leftTabs : _rightTabs;
        AddTabState(left, GetTabState(left, index), index + 1);
        if (left)
            _leftTabIndex = index + 1;
        else
            _rightTabIndex = index + 1;
        SetActivePane(left);
        SetPaneView(left, GetViewIndex(left));
        LoadPane(left);
    }

    private void MoveTabToOtherPane(bool left, int index)
    {
        CaptureActiveTabState(left);
        var sourceTabs = left ? _leftTabs : _rightTabs;
        var state = GetTabState(left, index);
        var targetLeft = !left;
        var targetTabs = targetLeft ? _leftTabs : _rightTabs;
        AddTabState(targetLeft, state, targetTabs.Count);
        RemoveTabState(left, index);
        sourceTabs.RemoveAt(index);
        if (sourceTabs.Count == 0)
        {
            var fallbackPath = targetTabs[targetLeft ? _leftTabIndex : _rightTabIndex];
            AddTabState(left, new ExplorerTabState { Path = fallbackPath }, 0);
        }
        else
        {
            if (left)
                _leftTabIndex = Math.Clamp(_leftTabIndex - (index < _leftTabIndex ? 1 : 0), 0, sourceTabs.Count - 1);
            else
                _rightTabIndex = Math.Clamp(_rightTabIndex - (index < _rightTabIndex ? 1 : 0), 0, sourceTabs.Count - 1);
        }
        if (targetLeft)
            _leftTabIndex = targetTabs.Count - 1;
        else
            _rightTabIndex = targetTabs.Count - 1;
        SetActivePane(targetLeft);
        SetPaneView(targetLeft, GetViewIndex(targetLeft));
        LoadPane(left);
        LoadPane(targetLeft);
    }

    private void TabStrip_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not Grid { Tag: string pane } grid || !ReferenceEquals(e.OriginalSource, grid))
            return;
        var left = pane == "Left";
        if (!(left ? _leftPaneOpen : _rightPaneOpen))
            return;
        AddTab(left, (left ? _leftTabs : _rightTabs)[left ? _leftTabIndex : _rightTabIndex]);
        e.Handled = true;
    }

    private async void RefreshTree(string path, List<ExplorerEntry>? entries = null)
    {
        if (RecycleBinService.IsPath(path) ||
            FileSystemService.IsRemotePath(path) ||
            NetworkShareService.IsServerPath(path))
            return;

        var refreshVersion = ++_treeRefreshVersion;
        try
        {
            var pathItems = BuildTreePath(path);
            if (pathItems.Count == 0)
                return;
            foreach (var item in pathItems)
                item.FileIconSize = _settings.FileIconSize;

            var drivePath = pathItems[0].Path;
            var parent = _computerRoot?.Children.FirstOrDefault(node =>
                node.Content is TreeItem item &&
                string.Equals(item.Path, drivePath, StringComparison.OrdinalIgnoreCase));
            if (parent is null && _computerRoot is not null)
            {
                var driveItem = new TreeItem(drivePath, drivePath, false, true, "\uE7F4")
                {
                    FileIconSize = _settings.FileIconSize
                };
                parent = new TreeViewNode { Content = driveItem, HasUnrealizedChildren = true };
                _computerRoot.Children.Add(parent);
                _ = LoadDriveIconAsync(driveItem);
            }
            if (parent is null)
                return;

            for (var index = 1; index < pathItems.Count; index++)
            {
                var parentItem = (TreeItem)parent.Content;
                if (!await EnsureTreeChildrenAsync(parent, parentItem, refreshVersion))
                    return;

                var nextItem = pathItems[index];
                var nextNode = parent.Children.FirstOrDefault(node =>
                    node.Content is TreeItem child &&
                    string.Equals(child.Path, nextItem.Path, StringComparison.OrdinalIgnoreCase));
                if (nextNode is null)
                    return;

                parent.IsExpanded = true;
                parent = nextNode;
            }

            var currentPath = ((TreeItem)parent.Content).Path;
            var currentEntries = entries is not null &&
                string.Equals(currentPath, path, StringComparison.OrdinalIgnoreCase)
                ? entries
                : await AccessRetryService.RunAsync(Root.XamlRoot, currentPath,
                    () => FileSystemService.GetEntries(currentPath).ToList());
            if (currentEntries is null || refreshVersion != _treeRefreshVersion)
                return;

            parent.IsExpanded = true;
            AddTreeFolders(parent, currentEntries);
            parent.HasUnrealizedChildren = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            if (refreshVersion == _treeRefreshVersion)
                await ShowMessageAsync("Unable to load folder tree", exception.Message);
        }
    }

    private static List<TreeItem> BuildTreePath(string path)
    {
        var pathItems = new List<TreeItem>();
        string? archiveFile = null;
        var archiveEntry = "";
        var filesystemPath = path;
        if (FileSystemService.IsArchivePath(path))
        {
            (archiveFile, archiveEntry) = FileSystemService.SplitArchivePath(path);
            filesystemPath = Path.GetDirectoryName(Path.GetFullPath(archiveFile))
                ?? Path.GetPathRoot(archiveFile)
                ?? archiveFile;
        }

        var directories = new List<DirectoryInfo>();
        var directory = new DirectoryInfo(Path.GetFullPath(filesystemPath));
        while (directory is not null)
        {
            directories.Add(directory);
            directory = directory.Parent;
        }
        directories.Reverse();

        foreach (var item in directories)
        {
            var itemPath = item.FullName;
            var name = string.IsNullOrEmpty(item.Name) ? itemPath : item.Name;
            pathItems.Add(new TreeItem(name, itemPath, false, true));
        }

        if (archiveFile is null)
            return pathItems;

        var archiveRoot = FileSystemService.ArchivePath(archiveFile);
        pathItems.Add(new TreeItem(Path.GetFileName(archiveFile), archiveRoot, true, true));
        var currentArchivePath = "";
        foreach (var segment in archiveEntry.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            currentArchivePath = string.IsNullOrEmpty(currentArchivePath)
                ? segment
                : $"{currentArchivePath}/{segment}";
            pathItems.Add(new TreeItem(segment, FileSystemService.ArchivePath(archiveFile, currentArchivePath), true, true));
        }
        return pathItems;
    }

    private void FolderTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        var node = args.Node;
        if (!node.HasUnrealizedChildren || node.Content is not TreeItem item)
            return;

        node.HasUnrealizedChildren = false;
        _ = LoadTreeChildrenAsync(node, item, _treeRefreshVersion);
    }

    private async Task LoadTreeChildrenAsync(TreeViewNode parent, TreeItem item, int refreshVersion)
    {
        try
        {
            if (item.IsComputerRoot)
            {
                RefreshComputerTree();
                return;
            }
            var children = await AccessRetryService.RunAsync(Root.XamlRoot, item.Path,
                () => FileSystemService.GetEntries(item.Path).ToList());
            if (refreshVersion != _treeRefreshVersion)
                return;
            if (children is null)
            {
                parent.HasUnrealizedChildren = true;
                return;
            }
            AddTreeFolders(parent, children);
            parent.HasUnrealizedChildren = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            if (refreshVersion == _treeRefreshVersion)
            {
                parent.HasUnrealizedChildren = true;
                await ShowMessageAsync("Unable to expand folder tree", exception.Message);
            }
        }
    }

    private async Task<bool> EnsureTreeChildrenAsync(TreeViewNode parent, TreeItem item, int refreshVersion)
    {
        if (!parent.HasUnrealizedChildren)
            return true;

        var children = await AccessRetryService.RunAsync(Root.XamlRoot, item.Path,
            () => FileSystemService.GetEntries(item.Path).ToList());
        if (children is null || refreshVersion != _treeRefreshVersion)
            return false;
        AddTreeFolders(parent, children);
        parent.HasUnrealizedChildren = false;
        parent.IsExpanded = true;
        return true;
    }

    private void AddTreeFolders(TreeViewNode parent, IEnumerable<ExplorerEntry> entries)
    {
        foreach (var entry in entries)
        {
            var isArchiveContainer = !entry.IsArchiveEntry && !entry.IsDirectory && IsZip(entry.Name);
            if (!entry.IsDirectory && !isArchiveContainer)
                continue;

            var path = isArchiveContainer ? FileSystemService.ArchivePath(entry.FullPath) : entry.FullPath;
            if (parent.Children.Any(node =>
                    node.Content is TreeItem child &&
                    string.Equals(child.Path, path, StringComparison.OrdinalIgnoreCase)))
                continue;

            var treeItem = new TreeItem(entry.Name, path, FileSystemService.IsArchivePath(path), true)
            {
                FileIconSize = _settings.FileIconSize
            };
            var node = new TreeViewNode
            {
                Content = treeItem,
                HasUnrealizedChildren = true
            };
            parent.Children.Add(node);
            if (!treeItem.IsArchive)
                _ = LoadTreeIconAsync(treeItem);
        }
    }

    private async Task LoadEntryIconAsync(ExplorerEntry entry)
    {
        if (FileSystemService.IsRemotePath(entry.FullPath))
            return;
        entry.IconImage = await ShellIconService.LoadFileIconAsync(entry.FullPath, entry.IsDirectory);
    }

    private async Task LoadShellEntryIconAsync(ExplorerEntry entry)
    {
        entry.IconImage = await ShellIconService.LoadShellItemIconAsync(entry.FullPath);
    }

    private async Task LoadTreeIconAsync(TreeItem item)
    {
        item.IconImage = await ShellIconService.LoadFileIconAsync(item.Path, item.IsDirectory);
    }

    private async Task LoadDriveIconAsync(TreeItem item)
    {
        item.IconImage = await ShellIconService.LoadFileIconAsync(item.Path, isDirectory: true);
    }

    private async Task LoadQuickLocationIconAsync(TreeItem item, int iconResourceIndex)
    {
        var shell32Path = Path.Combine(Environment.SystemDirectory, "shell32.dll");
        item.IconImage = await ShellIconService.LoadIconResourceAsync(shell32Path, iconResourceIndex);
    }

    private static async Task LoadShellTreeIconAsync(TreeItem item)
    {
        if (item.ShellPath is not null)
            item.IconImage = await ShellIconService.LoadShellItemIconAsync(item.ShellPath);
    }

    private async Task LoadPhysicalDrivesAsync(bool reportErrors = true)
    {
        if (_refreshingDrives)
            return;
        _refreshingDrives = true;
        try
        {
            var drives = await Task.Run(PhysicalDriveService.GetDrives);
            if (string.Equals(GetDriveSnapshot(drives), GetDriveSnapshot(_physicalDrives), StringComparison.Ordinal))
            {
                RefreshComputerTree();
                return;
            }
            _physicalDrives.Clear();
            foreach (var drive in drives)
            {
                _physicalDrives.Add(drive);
                foreach (var partition in drive.Partitions)
                {
                    partition.FileIconSize = _settings.FileIconSize;
                    if (partition.Path is not null)
                        _ = LoadPartitionIconAsync(partition);
                }
            }
            RefreshComputerTree();

            if (reportErrors && drives.Count == 0)
                StatusText.Text = "No physical drives were found.";
        }
        catch (Exception exception) when (exception is System.Management.ManagementException or UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            StatusText.Text = $"Could not enumerate physical drives: {exception.Message}";
            if (reportErrors)
                await ShowMessageAsync("Unable to enumerate physical drives", exception.Message);
        }
        finally
        {
            _refreshingDrives = false;
        }
    }

    private static string GetDriveSnapshot(IEnumerable<PhysicalDrive> drives) =>
        string.Join("|", drives.OrderBy(drive => drive.DiskIndex).Select(drive =>
            $"{drive.DiskIndex}:{drive.Model}:{drive.SizeBytes}:{drive.IsRemovable}:{drive.IsNetwork}:" +
            string.Join(",", drive.Partitions.OrderBy(partition => partition.Name).Select(partition =>
                $"{partition.Name}:{partition.Path}:{partition.Details}:{partition.ByteOffset}:{partition.ByteLength}"))));

    private static async Task LoadPartitionIconAsync(DrivePartition partition)
    {
        if (partition.Path is not null)
            partition.IconImage = await ShellIconService.LoadFileIconAsync(partition.Path, isDirectory: true);
    }

    private async void DriveContext_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement target)
            return;

        var element = e.OriginalSource as DependencyObject;
        object? context = null;
        while (element is not null)
        {
            if (element is FrameworkElement frameworkElement &&
                frameworkElement.DataContext is DrivePartition or PhysicalDrive)
            {
                context = frameworkElement.DataContext;
                break;
            }
            element = VisualTreeHelper.GetParent(element);
        }
        context ??= target.DataContext;

        var menu = new MenuFlyout();
        if (context is DrivePartition partition && !partition.IsNetwork)
        {
            AddContextAction(menu, "View partition as raw bytes (read-only)",
                () => OpenRawRegionAsync(partition.RawDevicePath, partition.ByteOffset,
                    partition.ByteLength, $"{partition.Name} — raw partition"),
                partition.CanReadRaw);
            if (partition.IsRemovable && !string.IsNullOrWhiteSpace(partition.Path))
                AddContextAction(menu, "Eject removable drive", () => EjectDriveAsync(partition.Path, partition.Name));
        }
        else if (context is PhysicalDrive drive && !drive.IsNetwork)
        {
            AddContextAction(menu, "View physical drive as raw bytes (read-only)",
                () => OpenRawRegionAsync($@"\\.\PhysicalDrive{drive.DiskIndex}", 0,
                    drive.SizeBytes, $"{drive.Name} — entire physical device"),
                drive.SizeBytes > 0);
            var mountedVolume = drive.Partitions.FirstOrDefault(item => item.Path is not null);
            if (drive.IsRemovable && mountedVolume?.Path is not null)
                AddContextAction(menu, "Eject removable drive",
                    () => EjectDriveAsync(mountedVolume.Path, drive.Name));
        }

        if (menu.Items.Count > 0)
            menu.ShowAt(target, e.GetPosition(target));
        e.Handled = true;
    }

    private async Task EjectDriveAsync(string? volumePath, string name)
    {
        if (string.IsNullOrWhiteSpace(volumePath))
            return;
        var confirm = new ContentDialog
        {
            Title = $"Eject {name}?",
            Content = "Make sure files on this device are closed before ejecting it.",
            PrimaryButtonText = "Eject",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            return;
        try
        {
            await Task.Run(() => DriveEjectService.EjectVolume(volumePath));
            await Task.Delay(2000);
            await LoadPhysicalDrivesAsync(reportErrors: false);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Unable to eject drive", exception.Message);
        }
    }

    private void DrivePartition_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DrivePartition { Path: not null } partition })
            Navigate(partition.Path);
    }

    private void AddLocation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement target)
            return;
        var menu = new MenuFlyout();
        AddMenuAction(menu, "Map network drive", () => ShowNetworkDriveWizardAsync(_leftActive));
        AddMenuAction(menu, "Connect to FTP/SFTP", () => PromptRemoteLocationAsync(_leftActive));
        menu.ShowAt(target);
    }

    private async Task PromptRemoteLocationAsync(bool left)
    {
        var address = await PromptAsync(
            "Connect to FTP/SFTP",
            "ftp://server or sftp://server",
            "ftp://");
        if (string.IsNullOrWhiteSpace(address))
            return;
        address = FileSystemService.NormalizeLocation(address);
        if (!RemoteFileSystemService.IsRemotePath(address))
        {
            await ShowMessageAsync(
                "Invalid remote address",
                "Enter an FTP or SFTP address such as ftp://192.168.0.1 or sftp://server:22/folder.");
            return;
        }

        SetActivePane(left);
        Navigate(address);
    }

    private void Navigate(string path, bool recordHistory = true)
        => NavigatePane(_leftActive, path, recordHistory);

    private void NavigatePane(bool left, string path, bool recordHistory = true)
    {
        path = FileSystemService.NormalizeLocation(path);
        var tabs = left ? _leftTabs : _rightTabs;
        var tabIndex = left ? _leftTabIndex : _rightTabIndex;
        var currentPath = tabs[tabIndex];
        if (recordHistory && !string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase))
        {
            var back = left ? _leftBack : _rightBack;
            var forward = left ? _leftForward : _rightForward;
            AddHistory(back, currentPath);
            forward.Clear();
        }
        var recentLocations = _settings.RecentLocations;
        if (IsRestorablePath(path))
        {
            recentLocations.RemoveAll(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
            recentLocations.Insert(0, path);
            if (recentLocations.Count > 20)
                recentLocations.RemoveRange(20, recentLocations.Count - 20);
            RefreshQuickAccessTree();
        }
        tabs[tabIndex] = path;
        LoadPane(left);
    }

    private void AddTab(bool left, string path)
    {
        var tabs = left ? _leftTabs : _rightTabs;
        CaptureActiveTabState(left);
        tabs.Add(path);
        (left ? _leftFilters : _rightFilters).Add("");
        (left ? _leftTabViews : _rightTabViews).Add(GetViewIndex(left));
        (left ? _leftTabIconSizes : _rightTabIconSizes).Add(GetIconSize(left));
        (left ? _leftTabSortFields : _rightTabSortFields).Add("Name");
        (left ? _leftTabSortDescending : _rightTabSortDescending).Add(false);
        (left ? _leftTabSelectedPaths : _rightTabSelectedPaths).Add([]);
        if (left)
        {
            _leftTabIndex = tabs.Count - 1;
            SetActivePane(true);
        }
        else
        {
            _rightTabIndex = tabs.Count - 1;
            SetActivePane(false);
        }
        SetPaneView(left, GetViewIndex(left));
        LoadPane(left);
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            Navigate(folder.Path);
    }

    private void NewTabButton_Click(object sender, RoutedEventArgs e) => AddTab(_leftActive, CurrentPath);

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        var back = _leftActive ? _leftBack : _rightBack;
        var forward = _leftActive ? _leftForward : _rightForward;
        if (TryPopHistory(back, out var path))
        {
            AddHistory(forward, CurrentPath);
            Navigate(path, recordHistory: false);
        }
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        var back = _leftActive ? _leftBack : _rightBack;
        var forward = _leftActive ? _leftForward : _rightForward;
        if (TryPopHistory(forward, out var path))
        {
            AddHistory(back, CurrentPath);
            Navigate(path, recordHistory: false);
        }
    }

    private void UpButton_Click(object sender, RoutedEventArgs e) => Navigate(FileSystemService.GetParent(CurrentPath));
    private void RefreshButton_Click(object sender, RoutedEventArgs e) => LoadPane(_leftActive);

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingSearchBox || _leftTabs.Count == 0 || _rightTabs.Count == 0)
            return;
        var filters = _leftActive ? _leftFilters : _rightFilters;
        var index = _leftActive ? _leftTabIndex : _rightTabIndex;
        filters[index] = SearchBox.Text ?? "";
        LoadPane(_leftActive);
    }

    private void SetActivePane(bool left)
    {
        var paneChanged = _leftActive != left;
        if (paneChanged && !_initializing)
            CaptureActiveTabState(_leftActive);
        _leftActive = left;
        if (LeftPaneBorder is not null)
        {
            LeftPaneBorder.BorderBrush = new SolidColorBrush(left
                ? Windows.UI.Color.FromArgb(255, 75, 166, 255)
                : Windows.UI.Color.FromArgb(255, 76, 86, 100));
            RightPaneBorder.BorderBrush = new SolidColorBrush(!left
                ? Windows.UI.Color.FromArgb(255, 75, 166, 255)
                : Windows.UI.Color.FromArgb(255, 76, 86, 100));
        }
        UpdatePaneLayout();
        if (SearchBox is not null && _leftTabs.Count > 0 && _rightTabs.Count > 0)
        {
            var filters = left ? _leftFilters : _rightFilters;
            var index = left ? _leftTabIndex : _rightTabIndex;
            _updatingSearchBox = true;
            SearchBox.Text = filters[index];
            _updatingSearchBox = false;
        }
        UpdateViewControls();
        if (paneChanged)
            RefreshTree(CurrentPath, left ? _leftDirectoryEntries : _rightDirectoryEntries);
    }

    private void PaneBorder_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(sender, LeftPaneBorder))
            SetActivePane(true);
        else if (ReferenceEquals(sender, RightPaneBorder))
            SetActivePane(false);
    }

    private void UpdateViewControls()
    {
        if (_initializing || IconSizeSlider is null)
            return;

        _updatingViewControls = true;
        IconSizeSlider.Value = ActiveIconSize;
        IconSizeText.Text = $"{ActiveIconSize:0}px";
        ThumbnailSizePanel.Visibility = ActiveViewIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        SetViewButtonState(ListViewButton, ActiveViewIndex == 0);
        SetViewButtonState(DetailsViewButton, ActiveViewIndex == 1);
        SetViewButtonState(ThumbnailsViewButton, ActiveViewIndex == 2);
        SetViewButtonState(SinglePaneButton, _singlePaneMode);
        _updatingViewControls = false;
    }

    private void SetPaneView(bool left, int viewIndex)
    {
        var list = left ? LeftList : RightList;
        (left ? _leftTabViews : _rightTabViews)[left ? _leftTabIndex : _rightTabIndex] = viewIndex;
        list.ItemTemplate = (DataTemplate)Root.Resources[viewIndex switch
        {
            0 => "ListEntryTemplate",
            2 => "ThumbnailEntryTemplate",
            _ => "DetailEntryTemplate"
        }];
        list.ItemsPanel = (ItemsPanelTemplate)Root.Resources[viewIndex == 2 ? "WrapEntryPanel" : "StackEntryPanel"];
        list.HorizontalContentAlignment = viewIndex == 2 ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        if (viewIndex == 2)
            SetEntryTileSizes(left ? _leftEntries : _rightEntries, GetIconSize(left));
        if (_leftActive == left)
            UpdateViewControls();
    }

    private static void SetEntryTileSizes(IEnumerable<ExplorerEntry> entries, double size)
    {
        foreach (var entry in entries)
        {
            entry.TileSize = size;
            entry.PreviewSizeLoaded = 0;
        }
    }

    private static void SetViewButtonState(Button button, bool selected)
    {
        button.Background = new SolidColorBrush(selected
            ? Windows.UI.Color.FromArgb(255, 54, 91, 128)
            : Windows.UI.Color.FromArgb(255, 38, 43, 51));
        button.BorderBrush = new SolidColorBrush(selected
            ? Windows.UI.Color.FromArgb(255, 105, 179, 255)
            : Windows.UI.Color.FromArgb(255, 58, 65, 76));
    }

    private void ViewModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;
        if (Equals(button.Tag, "TogglePane"))
        {
            ToggleSinglePaneMode();
            return;
        }
        if (int.TryParse(Convert.ToString(button.Tag), out var viewIndex))
            SetPaneView(_leftActive, viewIndex);
    }

    private void ToggleSinglePaneMode()
    {
        if (!_leftPaneOpen && !_rightPaneOpen)
            return;

        if (_singlePaneMode)
        {
            _singlePaneMode = false;
        }
        else
        {
            if (!_leftPaneOpen || !_rightPaneOpen)
                return;
            var leftColumn = PanesGrid.ColumnDefinitions[0];
            var rightColumn = PanesGrid.ColumnDefinitions[2];
            _dualLeftPaneWidth = new GridLength(Math.Max(1, leftColumn.ActualWidth), GridUnitType.Pixel);
            _dualRightPaneWidth = new GridLength(Math.Max(1, rightColumn.ActualWidth), GridUnitType.Pixel);
            _singlePaneMode = true;
        }
        UpdatePaneLayout();
        UpdateViewControls();
    }

    private void RestorePaneButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string pane })
            return;
        var left = pane == "Left";
        if (left)
            _leftPaneOpen = true;
        else
            _rightPaneOpen = true;
        _singlePaneMode = false;
        SetActivePane(left);
        UpdatePaneLayout();
    }

    private void UpdatePaneLayout()
    {
        if (PanesGrid is null)
            return;

        LeftPaneBorder.Visibility = _leftPaneOpen && (!_singlePaneMode || _leftActive)
            ? Visibility.Visible : Visibility.Collapsed;
        RightPaneBorder.Visibility = _rightPaneOpen && (!_singlePaneMode || !_leftActive)
            ? Visibility.Visible : Visibility.Collapsed;
        LeftRestoreButton.Visibility = !_leftPaneOpen
            ? Visibility.Visible : Visibility.Collapsed;
        RightRestoreButton.Visibility = !_rightPaneOpen
            ? Visibility.Visible : Visibility.Collapsed;

        if (_singlePaneMode)
        {
            PanesGrid.ColumnDefinitions[0].Width = !_leftPaneOpen
                ? new GridLength(36)
                : _leftActive ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            PanesGrid.ColumnDefinitions[1].Width = new GridLength(0);
            PanesGrid.ColumnDefinitions[2].Width = !_rightPaneOpen
                ? new GridLength(36)
                : _leftActive ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            PaneSplitter.Visibility = Visibility.Collapsed;
        }
        else
        {
            if (!_leftPaneOpen)
            {
                PanesGrid.ColumnDefinitions[0].Width = new GridLength(36);
                PanesGrid.ColumnDefinitions[1].Width = new GridLength(0);
                PanesGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
                PaneSplitter.Visibility = Visibility.Collapsed;
            }
            else if (!_rightPaneOpen)
            {
                PanesGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                PanesGrid.ColumnDefinitions[1].Width = new GridLength(0);
                PanesGrid.ColumnDefinitions[2].Width = new GridLength(36);
                PaneSplitter.Visibility = Visibility.Collapsed;
            }
            else
            {
                PanesGrid.ColumnDefinitions[0].Width = _dualLeftPaneWidth;
                PanesGrid.ColumnDefinitions[1].Width = new GridLength(8);
                PanesGrid.ColumnDefinitions[2].Width = _dualRightPaneWidth;
                PaneSplitter.Visibility = Visibility.Visible;
            }
        }
    }

    private void IconSizeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_initializing || _updatingViewControls)
            return;
        var size = Math.Round(e.NewValue / 16) * 16;
        IconSizeText.Text = $"{size:0}px";
        (_leftActive ? _leftTabIconSizes : _rightTabIconSizes)[_leftActive ? _leftTabIndex : _rightTabIndex] = size;
        SetEntryTileSizes(ActiveEntries, size);
    }

    private void ExplorerList_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListView list)
            return;

        SetActivePane(ReferenceEquals(list, LeftList));
        if (!e.GetCurrentPoint(list).Properties.IsLeftButtonPressed)
            return;

        var element = e.OriginalSource as DependencyObject;
        while (element is not null && element is not ListViewItem &&
               element is not Microsoft.UI.Xaml.Controls.Primitives.ScrollBar)
            element = VisualTreeHelper.GetParent(element);

        if (element is Microsoft.UI.Xaml.Controls.Primitives.ScrollBar)
            return;
        if (element is not ListViewItem container)
        {
            if (!list.IsMultiSelectCheckBoxEnabled)
            {
                _lastPointerClickPath = null;
                list.SelectedItems.Clear();
            }
            return;
        }

        var entry = container.DataContext as ExplorerEntry ?? container.Content as ExplorerEntry;
        if (entry is null)
            return;

        _internalDragList = list;
        _internalDragPath = entry.FullPath;
        _internalDragStart = e.GetCurrentPoint(Root).Position;
        _internalDragSources = [];
        _internalDragDestination = null;
        _internalDragStarted = false;

        if (list.IsMultiSelectCheckBoxEnabled)
            return;

        var controlDown = (GetKeyState(0x11) & 0x8000) != 0;
        var shiftDown = (GetKeyState(0x10) & 0x8000) != 0;
        if (controlDown || shiftDown)
        {
            _lastPointerClickPath = null;
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var isRenameClick = _lastPointerClickLeft == ReferenceEquals(list, LeftList) &&
            string.Equals(_lastPointerClickPath, entry.FullPath, StringComparison.OrdinalIgnoreCase) &&
            Stopwatch.GetElapsedTime(_lastPointerClickTimestamp) >=
                TimeSpan.FromMilliseconds(GetDoubleClickTime()) &&
            list.SelectedItems.Count == 1 &&
            list.SelectedItem is ExplorerEntry selected &&
            string.Equals(selected.FullPath, entry.FullPath, StringComparison.OrdinalIgnoreCase);
        _lastPointerClickPath = entry.FullPath;
        _lastPointerClickTimestamp = now;
        _lastPointerClickLeft = ReferenceEquals(list, LeftList);

        if (isRenameClick)
        {
            _lastPointerClickPath = null;
            e.Handled = true;
            _ = RenameEntryAsync(entry);
            return;
        }

        list.SelectedItems.Clear();
        list.SelectedItem = entry;
    }

    private void ExplorerList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not ListView list)
            return;

        _lastPointerClickPath = null;
        var left = ReferenceEquals(list, LeftList);
        SetActivePane(left);
        var element = e.OriginalSource as DependencyObject;
        while (element is not null && element is not ListViewItem)
            element = VisualTreeHelper.GetParent(element);
        var container = element as ListViewItem;
        var entry = container?.DataContext as ExplorerEntry ?? container?.Content as ExplorerEntry;

        if (entry is not null && !list.SelectedItems.Contains(entry))
        {
            list.SelectedItems.Clear();
            list.SelectedItem = entry;
        }

        var selected = list.SelectedItems.Cast<ExplorerEntry>().ToArray();
        if (entry is { IsRecycleBinEntry: true })
        {
            try
            {
                var command = ShellContextMenuService.Show(
                    WindowNative.GetWindowHandle(this),
                    selected.Select(item => item.FullPath).ToArray(),
                    showPaste: false,
                    includeExplorerPlusPlusCommands: false);
                if (command == 0xFFFFFFFF)
                    LoadPane(left);
                e.Handled = true;
                return;
            }
            catch (Exception exception) when (exception is COMException or InvalidOperationException or ArgumentException)
            {
                _ = ShowMessageAsync("Unable to show Windows context menu", exception.Message);
                e.Handled = true;
                return;
            }
        }
        if (entry is not null && selected.Length > 0 &&
            selected.All(item => !item.IsArchiveEntry && !FileSystemService.IsRemotePath(item.FullPath)))
        {
            try
            {
                var command = ShellContextMenuService.Show(
                    WindowNative.GetWindowHandle(this),
                    selected.Select(item => item.FullPath).ToArray(),
                    showPaste: true);
                if (command == ShellContextMenuService.PreviewCommand)
                    _ = PreviewEntryAsync(entry);
                else if (command == ShellContextMenuService.HexCommand)
                    _ = ShowHexEntryAsync(entry);
                else if (command == ShellContextMenuService.AppPropertiesCommand)
                    OpenPropertiesWindow(entry);
                else if (command == ShellContextMenuService.PasteCommand)
                    _ = PasteToAsync(CurrentPath);
                else if (command == ShellContextMenuService.DeleteCommand)
                    _ = DeleteEntriesAsync(selected, permanent: true);
                else if (command == ShellContextMenuService.ConvertImagesToPngCommand)
                    _ = ConvertSelectedImagesToPngAsync(selected);
                else if (command == ShellContextMenuService.AdminCommandPromptCommand)
                {
                    var workingDirectory = selected.Length == 1 && Directory.Exists(selected[0].FullPath)
                        ? selected[0].FullPath
                        : CurrentPath;
                    _ = OpenAdminCommandPromptAsync(workingDirectory);
                }
                else if (command == 0xFFFFFFFF)
                    LoadPane(left);
                e.Handled = true;
                return;
            }
            catch (Exception exception) when (exception is COMException or InvalidOperationException or ArgumentException)
            {
                _ = ShowMessageAsync("Unable to show Windows context menu", exception.Message);
            }
        }

        if (entry is null)
        {
            ShowFolderContextMenu(list, e.GetPosition(list), left);
            e.Handled = true;
            return;
        }

        ShowAppContextMenu(list, entry, e.GetPosition(list));
        e.Handled = true;
    }

    private void FolderPanelMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string pane } button)
            return;
        ShowFolderContextMenu(button, new Windows.Foundation.Point(0, button.ActualHeight), pane == "Left");
    }

    private void NewItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
            ShowNewItemsMenu(button);
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        IReadOnlyList<string> fontFamilies;
        try
        {
            fontFamilies = FontCatalogService.GetFontFamilies();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            await ShowMessageAsync("Unable to load fonts", exception.Message);
            return;
        }

        var fontFamilyInput = new ComboBox
        {
            Header = "Font face",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxDropDownHeight = 360
        };
        foreach (var family in fontFamilies)
        {
            fontFamilyInput.Items.Add(new ComboBoxItem
            {
                Content = new TextBlock
                {
                    Text = family,
                    FontFamily = new FontFamily(family)
                },
                Tag = family
            });
        }
        var selectedFont = fontFamilies.FirstOrDefault(font =>
            string.Equals(font, _settings.ItemFontFamily, StringComparison.OrdinalIgnoreCase));
        if (selectedFont is null && !string.IsNullOrWhiteSpace(_settings.ItemFontFamily))
        {
            selectedFont = _settings.ItemFontFamily;
            fontFamilyInput.Items.Add(new ComboBoxItem
            {
                Content = new TextBlock
                {
                    Text = selectedFont,
                    FontFamily = new FontFamily(selectedFont)
                },
                Tag = selectedFont
            });
        }
        fontFamilyInput.SelectedItem = fontFamilyInput.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, selectedFont, StringComparison.OrdinalIgnoreCase));

        Grid settingsRoot = null!;
        var settingsSaved = false;
        var showingTextCategory = true;
        var showingBackgroundCategory = false;
        var showingDesktopCategory = false;
        var selectedFontColor = ParseFontColor(_settings.ItemFontColor);
        var selectedBackgroundTint = ParseFontColor(_settings.BackgroundTintColor);
        var selectedFallbackColor = ParseFontColor(_settings.BackgroundFallbackColor);
        var useAcrylicBackground = _settings.UseAcrylicBackground;
        var fontSizeInput = new NumberBox
        {
            Header = "Font size",
            Value = _settings.ItemFontSize,
            Minimum = 6,
            Maximum = 48,
            SmallChange = 1,
            LargeChange = 2,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Width = 150
        };
        var fileIconSizeInput = new NumberBox
        {
            Header = "File and folder icon size",
            Value = _settings.FileIconSize,
            Minimum = 8,
            Maximum = 64,
            SmallChange = 1,
            LargeChange = 4,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Width = 190
        };
        var previewText = new TextBlock
        {
            Text = "Aa Bb Cc 0123 4567 89 !@#$%^&*()_+-=[]{}",
            FontFamily = new FontFamily(selectedFont ?? "Segoe UI"),
            FontSize = fontSizeInput.Value,
            Foreground = new SolidColorBrush(selectedFontColor),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var preview = new Border
        {
            Padding = new Thickness(20),
            MinHeight = 104,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 31, 35, 42)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 76, 86, 100)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Child = previewText
        };

        var tintOpacitySlider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            StepFrequency = 0.05,
            Value = _settings.BackgroundTintOpacity,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var luminosityOpacitySlider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            StepFrequency = 0.05,
            Value = _settings.BackgroundLuminosityOpacity,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var tintOpacityLabel = new TextBlock();
        var luminosityOpacityLabel = new TextBlock();
        void UpdateBackgroundSliderLabels()
        {
            tintOpacityLabel.Text = $"Tint opacity: {tintOpacitySlider.Value:P0}";
            luminosityOpacityLabel.Text = $"Luminosity opacity: {luminosityOpacitySlider.Value:P0}";
        }
        UpdateBackgroundSliderLabels();
        bool acrylicSupported = DesktopAcrylicController.IsSupported();
        void ApplyBackgroundPreview() => ApplyBackgroundMaterial(CreateBackgroundSettings());
        ExplorerSettings CreateBackgroundSettings() => new()
        {
            UseAcrylicBackground = useAcrylicBackground,
            BackgroundTintColor = FormatFontColor(selectedBackgroundTint),
            BackgroundTintOpacity = tintOpacitySlider.Value,
            BackgroundLuminosityOpacity = luminosityOpacitySlider.Value,
            BackgroundFallbackColor = FormatFontColor(selectedFallbackColor)
        };
        Button CreateColorButton(string title, Func<Windows.UI.Color> getColor, Action<Windows.UI.Color> setColor)
        {
            var swatch = new Border
            {
                Width = 28,
                Height = 20,
                Background = new SolidColorBrush(getColor()),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 120, 128, 140)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                VerticalAlignment = VerticalAlignment.Center
            };
            var button = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        swatch,
                        new TextBlock { Text = "Choose color", VerticalAlignment = VerticalAlignment.Center }
                    }
                },
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            button.Click += async (_, _) =>
            {
                var color = await ShowSettingsColorPickerAsync(settingsRoot.XamlRoot, title, getColor());
                if (color is null)
                    return;
                setColor(color.Value);
                swatch.Background = new SolidColorBrush(color.Value);
            };
            return button;
        }
        var fontColorButton = CreateColorButton(
            "Font color",
            () => selectedFontColor,
            color =>
            {
                selectedFontColor = color;
                previewText.Foreground = new SolidColorBrush(color);
            });
        var backgroundTintButton = CreateColorButton(
            "Acrylic tint color",
            () =>
            {
                return selectedBackgroundTint;
            },
            color =>
            {
                selectedBackgroundTint = color;
                ApplyBackgroundPreview();
            });
        var fallbackColorButton = CreateColorButton(
            "Fallback background color",
            () => selectedFallbackColor,
            color =>
            {
                selectedFallbackColor = color;
                ApplyBackgroundPreview();
            });
        void UpdateColorButtonSwatch(Button button, Windows.UI.Color color)
        {
            if (button.Content is StackPanel content &&
                content.Children.Count > 0 &&
                content.Children[0] is Border swatch)
                swatch.Background = new SolidColorBrush(color);
        }
        var acrylicToggle = new CheckBox
        {
            Content = "Use Windows acrylic material",
            IsChecked = useAcrylicBackground,
            IsEnabled = acrylicSupported
        };
        var acrylicSupportNote = new TextBlock
        {
            Text = acrylicSupported
                ? "Changes preview live. The acrylic blur amount is managed by Windows; tint and luminosity settings control its appearance."
                : "Acrylic is not supported on this system. The fallback background color is used.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75
        };
        var backgroundOptions = new StackPanel { Spacing = 12 };
        var acrylicRow = new Grid { ColumnSpacing = 16 };
        acrylicRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        acrylicRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var tintColorControl = new StackPanel { Spacing = 4 };
        tintColorControl.Children.Add(new TextBlock { Text = "Acrylic tint color" });
        tintColorControl.Children.Add(backgroundTintButton);
        acrylicRow.Children.Add(tintColorControl);
        var fallbackColorControl = new StackPanel { Spacing = 4 };
        fallbackColorControl.Children.Add(new TextBlock { Text = "Fallback background color" });
        fallbackColorControl.Children.Add(fallbackColorButton);
        Grid.SetColumn(fallbackColorControl, 1);
        acrylicRow.Children.Add(fallbackColorControl);
        backgroundOptions.Children.Add(acrylicToggle);
        backgroundOptions.Children.Add(acrylicRow);
        backgroundOptions.Children.Add(tintOpacityLabel);
        backgroundOptions.Children.Add(tintOpacitySlider);
        backgroundOptions.Children.Add(luminosityOpacityLabel);
        backgroundOptions.Children.Add(luminosityOpacitySlider);
        backgroundOptions.Children.Add(acrylicSupportNote);

        var desktopLaunchToggle = new CheckBox
        {
            Content = "Launch ExplorerPlusPlus when the empty desktop background is double-clicked",
            IsChecked = _settings.DesktopBackgroundDoubleClickLauncherEnabled
        };
        void UpdateBackgroundControls()
        {
            backgroundTintButton.IsEnabled = useAcrylicBackground && acrylicSupported;
            tintOpacitySlider.IsEnabled = useAcrylicBackground && acrylicSupported;
            luminosityOpacitySlider.IsEnabled = useAcrylicBackground && acrylicSupported;
        }
        acrylicToggle.Checked += (_, _) =>
        {
            useAcrylicBackground = true;
            UpdateBackgroundControls();
            ApplyBackgroundPreview();
        };
        acrylicToggle.Unchecked += (_, _) =>
        {
            useAcrylicBackground = false;
            UpdateBackgroundControls();
            ApplyBackgroundPreview();
        };
        tintOpacitySlider.ValueChanged += (_, _) =>
        {
            UpdateBackgroundSliderLabels();
            ApplyBackgroundPreview();
        };
        luminosityOpacitySlider.ValueChanged += (_, _) =>
        {
            UpdateBackgroundSliderLabels();
            ApplyBackgroundPreview();
        };
        UpdateBackgroundControls();

        fontFamilyInput.SelectionChanged += (_, _) =>
        {
            if (fontFamilyInput.SelectedItem is ComboBoxItem { Tag: string family })
                previewText.FontFamily = new FontFamily(family);
        };
        fontSizeInput.ValueChanged += (_, args) => previewText.FontSize = args.NewValue;

        var displayOptions = new StackPanel { Spacing = 10 };
        var typographyRow = new Grid { ColumnSpacing = 12 };
        typographyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        typographyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        typographyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        typographyRow.Children.Add(fontFamilyInput);
        Grid.SetColumn(fontSizeInput, 1);
        typographyRow.Children.Add(fontSizeInput);
        var colorControl = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Bottom };
        colorControl.Children.Add(new TextBlock { Text = "Font color" });
        colorControl.Children.Add(fontColorButton);
        Grid.SetColumn(colorControl, 2);
        typographyRow.Children.Add(colorControl);
        displayOptions.Children.Add(typographyRow);
        displayOptions.Children.Add(fileIconSizeInput);
        displayOptions.Children.Add(new TextBlock
        {
            Text = "Preview",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 0)
        });
        var previewLayout = new Grid { MinHeight = 132 };
        previewLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        previewLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        previewLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(preview, 1);
        previewLayout.Children.Add(preview);
        displayOptions.Children.Add(previewLayout);
        displayOptions.Children.Add(new TextBlock
        {
            Text = $"Place .ttf, .otf, or .ttc files in: {FontCatalogService.FontDirectory}",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75
        });

        var textCategoryButton = new Button
        {
            Content = "Text",
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 54, 91, 128))
        };
        var backgroundCategoryButton = new Button
        {
            Content = "Background",
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 38, 43, 51))
        };
        var desktopCategoryButton = new Button
        {
            Content = "Desktop",
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 38, 43, 51))
        };
        var categories = new StackPanel
        {
            Width = 120,
            Padding = new Thickness(0, 0, 12, 0),
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = "CATEGORIES",
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Opacity = 0.7,
                    Margin = new Thickness(8, 8, 0, 8)
                },
                textCategoryButton,
                backgroundCategoryButton,
                desktopCategoryButton
            }
        };
        var content = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(categories);
        var pageHost = new Grid();
        Grid.SetColumn(pageHost, 1);
        content.Children.Add(pageHost);
        var textPage = new StackPanel
        {
            Spacing = 12,
            Padding = new Thickness(18, 8, 16, 12),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        textPage.Children.Add(new TextBlock
        {
            Text = "Text",
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        textPage.Children.Add(new TextBlock
        {
            Text = "Customize how file and folder names look in both panes.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75
        });
        textPage.Children.Add(displayOptions);
        var backgroundPage = new StackPanel
        {
            Spacing = 12,
            Padding = new Thickness(18, 8, 16, 12),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed
        };
        backgroundPage.Children.Add(new TextBlock
        {
            Text = "Background",
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        backgroundPage.Children.Add(new TextBlock
        {
            Text = "Customize the main window material. Changes preview live and are saved when you choose Save.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75
        });
        backgroundPage.Children.Add(backgroundOptions);
        var desktopPage = new StackPanel
        {
            Spacing = 12,
            Padding = new Thickness(18, 8, 16, 12),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed
        };
        desktopPage.Children.Add(new TextBlock
        {
            Text = "Desktop",
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        desktopPage.Children.Add(new TextBlock
        {
            Text = "Monitor double-clicks on empty desktop space and start ExplorerPlusPlus. When enabled, the monitor starts automatically when you sign in.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75
        });
        desktopPage.Children.Add(desktopLaunchToggle);
        pageHost.Children.Add(textPage);
        pageHost.Children.Add(backgroundPage);
        pageHost.Children.Add(desktopPage);
        void SelectSettingsCategory(string category)
        {
            showingTextCategory = category == "Text";
            showingBackgroundCategory = category == "Background";
            showingDesktopCategory = category == "Desktop";
            textPage.Visibility = showingTextCategory ? Visibility.Visible : Visibility.Collapsed;
            backgroundPage.Visibility = showingBackgroundCategory ? Visibility.Visible : Visibility.Collapsed;
            desktopPage.Visibility = showingDesktopCategory ? Visibility.Visible : Visibility.Collapsed;
            textCategoryButton.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(
                255, showingTextCategory ? (byte)54 : (byte)38,
                showingTextCategory ? (byte)91 : (byte)43, showingTextCategory ? (byte)128 : (byte)51));
            backgroundCategoryButton.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(
                255, showingBackgroundCategory ? (byte)54 : (byte)38,
                showingBackgroundCategory ? (byte)91 : (byte)43,
                showingBackgroundCategory ? (byte)128 : (byte)51));
            desktopCategoryButton.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(
                255, showingDesktopCategory ? (byte)54 : (byte)38,
                showingDesktopCategory ? (byte)91 : (byte)43,
                showingDesktopCategory ? (byte)128 : (byte)51));
        }
        textCategoryButton.Click += (_, _) => SelectSettingsCategory("Text");
        backgroundCategoryButton.Click += (_, _) => SelectSettingsCategory("Background");
        desktopCategoryButton.Click += (_, _) => SelectSettingsCategory("Desktop");

        var viewport = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            ZoomMode = ZoomMode.Disabled
        };
        var saveButton = new Button
        {
            Content = "Save",
            MinWidth = 100
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 100
        };
        var resetButton = new Button
        {
            Content = "Reset to defaults",
            MinWidth = 140
        };
        var footer = new Grid { Padding = new Thickness(12, 10, 12, 10), ColumnSpacing = 8 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(resetButton);
        Grid.SetColumn(cancelButton, 2);
        footer.Children.Add(cancelButton);
        Grid.SetColumn(saveButton, 3);
        footer.Children.Add(saveButton);

        settingsRoot = new Grid { MinWidth = 480, MinHeight = 360 };
        settingsRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        settingsRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        settingsRoot.Children.Add(viewport);
        Grid.SetRow(footer, 1);
        settingsRoot.Children.Add(footer);

        var settingsWindow = new Window
        {
            Title = "Explorer++ Settings",
            Content = settingsRoot
        };
        _settingsWindow = settingsWindow;
        settingsWindow.Closed += (_, _) =>
        {
            if (!settingsSaved)
                ApplyBackgroundMaterial(_settings);
            _settingsWindow = null;
        };
        cancelButton.Click += (_, _) => settingsWindow.Close();
        resetButton.Click += async (_, _) =>
        {
            var prompt = new ContentDialog
            {
                Title = "Reset settings",
                Content = $"Choose whether to reset the { (showingTextCategory ? "Text" : showingBackgroundCategory ? "Background" : "Desktop") } category or all settings.",
                PrimaryButtonText = $"Reset { (showingTextCategory ? "Text" : showingBackgroundCategory ? "Background" : "Desktop") } category",
                SecondaryButtonText = "Reset all settings",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = settingsRoot.XamlRoot
            };
            var result = await prompt.ShowAsync();
            if (result is not (ContentDialogResult.Primary or ContentDialogResult.Secondary))
                return;

            var defaults = new ExplorerSettings();
            var resetAll = result == ContentDialogResult.Secondary;
            if (resetAll || showingTextCategory)
            {
                fontFamilyInput.SelectedItem = fontFamilyInput.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(item => string.Equals(item.Tag as string, defaults.ItemFontFamily,
                        StringComparison.OrdinalIgnoreCase));
                previewText.FontFamily = new FontFamily(defaults.ItemFontFamily);
                fontSizeInput.Value = defaults.ItemFontSize;
                previewText.FontSize = defaults.ItemFontSize;
                fileIconSizeInput.Value = defaults.FileIconSize;
                selectedFontColor = ParseFontColor(defaults.ItemFontColor);
                UpdateColorButtonSwatch(fontColorButton, selectedFontColor);
                previewText.Foreground = new SolidColorBrush(selectedFontColor);
            }
            if (resetAll || showingBackgroundCategory)
            {
                useAcrylicBackground = defaults.UseAcrylicBackground;
                acrylicToggle.IsChecked = useAcrylicBackground;
                selectedBackgroundTint = ParseFontColor(defaults.BackgroundTintColor);
                selectedFallbackColor = ParseFontColor(defaults.BackgroundFallbackColor);
                UpdateColorButtonSwatch(backgroundTintButton, selectedBackgroundTint);
                UpdateColorButtonSwatch(fallbackColorButton, selectedFallbackColor);
                tintOpacitySlider.Value = defaults.BackgroundTintOpacity;
                luminosityOpacitySlider.Value = defaults.BackgroundLuminosityOpacity;
                UpdateBackgroundSliderLabels();
                UpdateBackgroundControls();
                ApplyBackgroundPreview();
            }
            if (resetAll || showingDesktopCategory)
                desktopLaunchToggle.IsChecked = defaults.DesktopBackgroundDoubleClickLauncherEnabled;
        };
        saveButton.Click += async (_, _) =>
        {
            if (fontFamilyInput.SelectedItem is not ComboBoxItem { Tag: string fontFamily } ||
                !double.IsFinite(fontSizeInput.Value) ||
                fontSizeInput.Value is < 6 or > 48 ||
                !double.IsFinite(fileIconSizeInput.Value) ||
                fileIconSizeInput.Value is < 8 or > 64)
            {
                await ShowSettingsMessageAsync(settingsRoot.XamlRoot,
                    "Invalid settings", "Choose a font size from 6 to 48 pixels and an icon size from 8 to 64 pixels.");
                return;
            }

            var fontColor = FormatFontColor(selectedFontColor);
            try
            {
                CaptureSessionState();
                var updatedSettings = new ExplorerSettings
                {
                    ItemFontFamily = fontFamily,
                    ItemFontSize = fontSizeInput.Value,
                    FileIconSize = fileIconSizeInput.Value,
                    ItemFontColor = fontColor,
                    Bookmarks = _settings.Bookmarks,
                    RecentLocations = _settings.RecentLocations,
                    FolderSets = _settings.FolderSets,
                    UseAcrylicBackground = useAcrylicBackground,
                    BackgroundTintColor = FormatFontColor(selectedBackgroundTint),
                    BackgroundTintOpacity = tintOpacitySlider.Value,
                    BackgroundLuminosityOpacity = luminosityOpacitySlider.Value,
                    BackgroundFallbackColor = FormatFontColor(selectedFallbackColor),
                    DesktopBackgroundDoubleClickLauncherEnabled = desktopLaunchToggle.IsChecked == true,
                    Session = _settings.Session
                };
                ExplorerSettingsService.Save(updatedSettings);
                _settings = updatedSettings;
                var monitorError = DesktopBackgroundDoubleClickMonitor.ApplySetting(
                    updatedSettings.DesktopBackgroundDoubleClickLauncherEnabled);
                _itemFontFamily = fontFamily;
                ApplyBackgroundMaterial(_settings);
                ApplyItemTypography(_leftDirectoryEntries.Concat(_rightDirectoryEntries)
                    .Concat(_leftEntries).Concat(_rightEntries));
                ApplyFileIconSizeToNavigation(_settings.FileIconSize);
                StatusText.Text = monitorError ??
                    $"File name style set to {fontFamily}, {fontSizeInput.Value:0.#} px.";
                if (monitorError is not null)
                    await ShowSettingsMessageAsync(settingsRoot.XamlRoot, "Desktop launcher", monitorError);
                settingsSaved = true;
                settingsWindow.Close();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                await ShowSettingsMessageAsync(settingsRoot.XamlRoot,
                    "Unable to save settings", exception.Message);
            }
        };

        var mainHandle = WindowNative.GetWindowHandle(this);
        var settingsHandle = WindowNative.GetWindowHandle(settingsWindow);
        var ownerAppWindow = AppWindow.GetFromWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(mainHandle));
        var settingsAppWindow = AppWindow.GetFromWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(settingsHandle));
        settingsAppWindow.Resize(new Windows.Graphics.SizeInt32(
            Math.Max(480, Math.Min(1000, ownerAppWindow.Size.Width - 80)),
            Math.Max(360, Math.Min(850, ownerAppWindow.Size.Height - 80))));
        settingsAppWindow.Move(new Windows.Graphics.PointInt32(
            ownerAppWindow.Position.X + (ownerAppWindow.Size.Width - settingsAppWindow.Size.Width) / 2,
            ownerAppWindow.Position.Y + (ownerAppWindow.Size.Height - settingsAppWindow.Size.Height) / 2));
        settingsAppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        if (settingsAppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
        }
        settingsWindow.Activate();
    }

    private static async Task ShowSettingsMessageAsync(XamlRoot xamlRoot, string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = xamlRoot
        };
        await dialog.ShowAsync();
    }

    private static async Task<Windows.UI.Color?> ShowSettingsColorPickerAsync(
        XamlRoot xamlRoot,
        string title,
        Windows.UI.Color initialColor)
    {
        var picker = new ColorPicker
        {
            IsAlphaEnabled = false,
            IsColorSliderVisible = true,
            Color = initialColor,
            MinWidth = 320
        };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = picker,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? picker.Color : null;
    }

    private void ApplyItemTypography(ExplorerEntry entry)
    {
        entry.ItemFontFamily = _itemFontFamily;
        entry.ItemFontSize = _settings.ItemFontSize;
        entry.ItemFontColor = _settings.ItemFontColor;
        entry.FileIconSize = _settings.FileIconSize;
    }

    private void ApplyFileIconSizeToNavigation(double size)
    {
        void ApplyToTree(TreeViewNode node)
        {
            if (node.Content is TreeItem item)
                item.FileIconSize = size;
            foreach (var child in node.Children)
                ApplyToTree(child);
        }

        foreach (var root in FolderTree.RootNodes)
            ApplyToTree(root);
        foreach (var partition in _physicalDrives.SelectMany(drive => drive.Partitions))
            partition.FileIconSize = size;
    }

    private static Windows.UI.Color ParseFontColor(string color)
    {
        var argb = uint.Parse(color.AsSpan(1), System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture);
        return Windows.UI.Color.FromArgb(
            (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    }

    private static string FormatFontColor(Windows.UI.Color color) =>
        $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private void ApplyItemTypography(IEnumerable<ExplorerEntry> entries)
    {
        foreach (var entry in entries.Distinct())
            ApplyItemTypography(entry);
    }

    private void ShowNewItemsMenu(FrameworkElement target)
    {
        var menu = new MenuFlyout();
        AddMenuAction(menu, "Folder", () => CreateNewItemAsync(_leftActive, NewItemType.Folder));
        AddMenuAction(menu, "Shortcut", () => CreateNewItemAsync(_leftActive, NewItemType.Shortcut));
        AddMenuAction(menu, "Network drive", () => ShowNetworkDriveWizardAsync(_leftActive));
        AddMenuAction(menu, "Text document", () => CreateNewItemAsync(_leftActive, NewItemType.TextFile));
        AddMenuAction(menu, "Compressed (ZIP) folder", () => CreateNewItemAsync(_leftActive, NewItemType.ZipFile));
        var imageMenu = new MenuFlyoutSubItem { Text = "Image" };
        AddMenuAction(imageMenu, "PNG image", () => CreateNewItemAsync(_leftActive, NewItemType.PngImage));
        AddMenuAction(imageMenu, "JPEG image", () => CreateNewItemAsync(_leftActive, NewItemType.JpegImage));
        AddMenuAction(imageMenu, "Bitmap image", () => CreateNewItemAsync(_leftActive, NewItemType.BmpImage));
        menu.Items.Add(imageMenu);
        menu.ShowAt(target);
    }

    private MenuFlyoutSubItem BuildNewItemsMenu(bool left)
    {
        var newMenu = new MenuFlyoutSubItem { Text = "New" };
        AddMenuAction(newMenu, "Folder", () => CreateNewItemAsync(left, NewItemType.Folder));
        AddMenuAction(newMenu, "Shortcut", () => CreateNewItemAsync(left, NewItemType.Shortcut));
        AddMenuAction(newMenu, "Network drive", () => ShowNetworkDriveWizardAsync(left));
        AddMenuAction(newMenu, "Text document", () => CreateNewItemAsync(left, NewItemType.TextFile));
        AddMenuAction(newMenu, "Compressed (ZIP) folder", () => CreateNewItemAsync(left, NewItemType.ZipFile));

        var imageMenu = new MenuFlyoutSubItem { Text = "Image" };
        AddMenuAction(imageMenu, "PNG image", () => CreateNewItemAsync(left, NewItemType.PngImage));
        AddMenuAction(imageMenu, "JPEG image", () => CreateNewItemAsync(left, NewItemType.JpegImage));
        AddMenuAction(imageMenu, "Bitmap image", () => CreateNewItemAsync(left, NewItemType.BmpImage));
        newMenu.Items.Add(imageMenu);
        return newMenu;
    }

    private void ShowFolderContextMenu(FrameworkElement target, Windows.Foundation.Point position, bool left)
    {
        SetActivePane(left);
        var menu = new MenuFlyout();
        menu.Items.Add(BuildNewItemsMenu(left));
        menu.Items.Add(new MenuFlyoutSeparator());
        AddMenuAction(menu, "Paste", () => PasteToAsync(CurrentPath),
            !FileSystemService.IsArchivePath(CurrentPath));
        AddMenuAction(menu, "Refresh", () =>
        {
            LoadPane(left);
            return Task.CompletedTask;
        });
        AddMenuAction(menu, "Open parent folder", () =>
        {
            Navigate(FileSystemService.GetParent(CurrentPath));
            return Task.CompletedTask;
        }, !string.Equals(FileSystemService.GetParent(CurrentPath), CurrentPath, StringComparison.OrdinalIgnoreCase));
        menu.Items.Add(new MenuFlyoutSeparator());

        var sortMenu = new MenuFlyoutSubItem { Text = "Sort by" };
        foreach (var (field, label) in new[]
                 {
                     ("Name", "Name"), ("Type", "Type"), ("Size", "Size"), ("Modified", "Date modified")
                 })
        {
            AddMenuAction(sortMenu, label, () =>
            {
                var fields = left ? _leftTabSortFields : _rightTabSortFields;
                var descending = left ? _leftTabSortDescending : _rightTabSortDescending;
                var index = left ? _leftTabIndex : _rightTabIndex;
                if (fields[index] == field)
                    descending[index] = !descending[index];
                else
                {
                    fields[index] = field;
                    descending[index] = false;
                }
                LoadPane(left);
                return Task.CompletedTask;
            });
        }
        menu.Items.Add(sortMenu);
        var viewMenu = new MenuFlyoutSubItem { Text = "View" };
        AddMenuAction(viewMenu, "List", () => { SetPaneView(left, 0); return Task.CompletedTask; });
        AddMenuAction(viewMenu, "Details", () => { SetPaneView(left, 1); return Task.CompletedTask; });
        AddMenuAction(viewMenu, "Thumbnails", () => { SetPaneView(left, 2); return Task.CompletedTask; });
        menu.Items.Add(viewMenu);
        AddMenuAction(menu, "Add current folder to favorites", () =>
        {
            AddBookmark(CurrentPath);
            return Task.CompletedTask;
        });
        AddMenuAction(menu, "Save current pane pair as folder set", () => SaveFolderSetAsync());
        AddMenuAction(menu, "Open folder in File Explorer", () =>
        {
            Process.Start(new ProcessStartInfo(CurrentPath) { UseShellExecute = true });
            return Task.CompletedTask;
        }, !FileSystemService.IsArchivePath(CurrentPath) && !FileSystemService.IsRemotePath(CurrentPath));
        AddMenuAction(menu, "Open command prompt as administrator",
            () => OpenAdminCommandPromptAsync(CurrentPath),
            !FileSystemService.IsArchivePath(CurrentPath) && !FileSystemService.IsRemotePath(CurrentPath));
        menu.Items.Add(new MenuFlyoutSeparator());
        var list = left ? LeftList : RightList;
        var checkBoxesItem = new ToggleMenuFlyoutItem
        {
            Text = "Item check boxes",
            IsChecked = left ? _leftSelectionCheckBoxesEnabled : _rightSelectionCheckBoxesEnabled
        };
        checkBoxesItem.Click += (_, _) =>
        {
            SetPaneSelectionCheckBoxes(list, left, checkBoxesItem.IsChecked);
        };
        menu.Items.Add(checkBoxesItem);
        menu.ShowAt(target, position);
    }

    private void SetPaneSelectionCheckBoxes(ListView list, bool left, bool enabled)
    {
        var selectedEntries = list.SelectedItems.OfType<ExplorerEntry>().ToArray();
        _restoringPaneSelection = true;
        try
        {
            list.IsMultiSelectCheckBoxEnabled = enabled;
            list.SelectionMode = enabled ? ListViewSelectionMode.Multiple : ListViewSelectionMode.Extended;
            list.SelectedItems.Clear();
            foreach (var entry in selectedEntries)
                list.SelectedItems.Add(entry);
        }
        finally
        {
            _restoringPaneSelection = false;
        }

        if (left)
            _leftSelectionCheckBoxesEnabled = enabled;
        else
            _rightSelectionCheckBoxesEnabled = enabled;
    }

    private static void AddMenuAction(MenuFlyoutSubItem menu, string label, Func<Task> action, bool enabled = true)
    {
        var item = new MenuFlyoutItem { Text = label, IsEnabled = enabled };
        item.Click += async (_, _) => await action();
        menu.Items.Add(item);
    }

    private static void AddMenuAction(MenuFlyout menu, string label, Func<Task> action, bool enabled = true)
    {
        var item = new MenuFlyoutItem { Text = label, IsEnabled = enabled };
        item.Click += async (_, _) => await action();
        menu.Items.Add(item);
    }

    private async Task CreateNewItemAsync(bool left, NewItemType itemType)
    {
        SetActivePane(left);
        var destination = CurrentPath;
        if (FileSystemService.IsArchivePath(destination))
        {
            await ShowMessageAsync("Read-only archive", "New items cannot be created inside an archive.");
            return;
        }
        if (itemType == NewItemType.Shortcut)
        {
            if (FileSystemService.IsRemotePath(destination))
            {
                await ShowMessageAsync("Remote shortcut", "Shortcuts can only be created in a Windows folder.");
                return;
            }
            await ShowNewShortcutWizardAsync(destination);
            return;
        }

        var (promptTitle, suggestedName, extension) = itemType switch
        {
            NewItemType.Folder => ("New folder", "New folder", ""),
            NewItemType.TextFile => ("New text file", "New text document.txt", ".txt"),
            NewItemType.ZipFile => ("New ZIP file", "New compressed folder.zip", ".zip"),
            NewItemType.PngImage => ("New PNG image", "New image.png", ".png"),
            NewItemType.JpegImage => ("New JPEG image", "New image.jpg", ".jpg"),
            NewItemType.BmpImage => ("New bitmap image", "New image.bmp", ".bmp"),
            _ => throw new ArgumentOutOfRangeException(nameof(itemType))
        };
        var name = await PromptAsync(promptTitle, "Name", suggestedName);
        if (string.IsNullOrWhiteSpace(name))
            return;

        var normalizedName = name.Trim();
        if (!string.IsNullOrEmpty(extension) &&
            !normalizedName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            normalizedName += extension;
        if (!IsValidNewItemName(normalizedName))
        {
            await ShowMessageAsync("Invalid name", "Enter a valid file or folder name without a path.");
            return;
        }

        var path = FileSystemService.CombinePath(destination, normalizedName);
        var isRemote = FileSystemService.IsRemotePath(destination);
        try
        {
            if (isRemote
                    ? RemoteFileSystemService.Exists(path)
                    : File.Exists(path) || Directory.Exists(path))
            {
                await ShowMessageAsync("Item already exists", $"An item named '{normalizedName}' already exists.");
                return;
            }

            var created = await AccessRetryService.RunActionAsync(Root.XamlRoot, destination, () =>
            {
                if (isRemote)
                {
                    if (itemType == NewItemType.Folder)
                        RemoteFileSystemService.CreateDirectory(path);
                    else
                        FileSystemService.WriteFile(path, CreateNewItemBytes(itemType));
                    return;
                }

                switch (itemType)
                {
                    case NewItemType.Folder:
                        Directory.CreateDirectory(path);
                        break;
                    case NewItemType.TextFile:
                        using (File.Create(path)) { }
                        break;
                    case NewItemType.ZipFile:
                        using (ZipFile.Open(path, ZipArchiveMode.Create)) { }
                        break;
                    case NewItemType.PngImage:
                        CreateBlankImage(path, ImageFormat.Png);
                        break;
                    case NewItemType.JpegImage:
                        CreateBlankImage(path, ImageFormat.Jpeg);
                        break;
                    case NewItemType.BmpImage:
                        CreateBlankImage(path, ImageFormat.Bmp);
                        break;
                }
            });
            if (!created)
                return;

            LoadPane(left);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or
                                           InvalidOperationException or System.ComponentModel.Win32Exception or
                                           COMException or NotSupportedException)
        {
            await ShowMessageAsync("Unable to create item", exception.Message);
            return;
        }

        if (!isRemote && itemType is (NewItemType.PngImage or NewItemType.JpegImage or NewItemType.BmpImage))
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                await ShowMessageAsync("Image created", $"The image was created, but its default editor could not be opened: {exception.Message}");
            }
        }
    }

    private async Task ShowNewShortcutWizardAsync(string destination)
    {
        try
        {
            var startInfo = new ProcessStartInfo("rundll32.exe")
            {
                UseShellExecute = true,
                WorkingDirectory = destination
            };
            startInfo.ArgumentList.Add("appwiz.cpl,NewLinkHere");
            startInfo.ArgumentList.Add(destination);
            Process.Start(startInfo);
            _refreshShellChangesOnActivation = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            await ShowMessageAsync("Unable to create shortcut", exception.Message);
        }
    }

    private async Task ShowNetworkDriveWizardAsync(bool left)
    {
        SetActivePane(left);
        try
        {
            var startInfo = new ProcessStartInfo("rundll32.exe")
            {
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add("shell32.dll,SHHelpShortcuts_RunDLL");
            startInfo.ArgumentList.Add("Connect");
            Process.Start(startInfo);
            _refreshShellChangesOnActivation = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            await ShowMessageAsync("Unable to open network drive wizard", exception.Message);
        }
    }

    private static void CreateBlankImage(string path, ImageFormat format)
    {
        using var image = new SixLabors.ImageSharp.Image<Rgba32>(1024, 768, new Rgba32(255, 255, 255, 255));
        using var output = File.Create(path);
        CreateBlankImage(image, output, format);
    }

    private static byte[] CreateNewItemBytes(NewItemType itemType)
    {
        using var output = new MemoryStream();
        switch (itemType)
        {
            case NewItemType.TextFile:
                break;
            case NewItemType.ZipFile:
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true)) { }
                break;
            case NewItemType.PngImage:
                CreateBlankImage(output, ImageFormat.Png);
                break;
            case NewItemType.JpegImage:
                CreateBlankImage(output, ImageFormat.Jpeg);
                break;
            case NewItemType.BmpImage:
                CreateBlankImage(output, ImageFormat.Bmp);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(itemType));
        }
        return output.ToArray();
    }

    private static void CreateBlankImage(Stream output, ImageFormat format)
    {
        using var image = new SixLabors.ImageSharp.Image<Rgba32>(1024, 768, new Rgba32(255, 255, 255, 255));
        CreateBlankImage(image, output, format);
    }

    private static void CreateBlankImage(
        SixLabors.ImageSharp.Image<Rgba32> image,
        Stream output,
        ImageFormat format)
    {
        switch (format)
        {
            case ImageFormat.Png:
                image.Save(output, new PngEncoder());
                break;
            case ImageFormat.Jpeg:
                image.Save(output, new JpegEncoder());
                break;
            case ImageFormat.Bmp:
                image.Save(output, new BmpEncoder());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    private static bool IsValidNewItemName(string name) =>
        name is not "." and not ".." &&
        !name.Contains(Path.DirectorySeparatorChar) &&
        !name.Contains(Path.AltDirectorySeparatorChar) &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private enum NewItemType
    {
        Folder,
        Shortcut,
        TextFile,
        ZipFile,
        PngImage,
        JpegImage,
        BmpImage
    }

    private enum ImageFormat
    {
        Png,
        Jpeg,
        Bmp
    }

    private void ShowAppContextMenu(ListView list, ExplorerEntry? entry, Windows.Foundation.Point position)
    {
        var menu = new MenuFlyout();
        if (entry is not null)
        {
            AddContextAction(menu, "Preview", () => PreviewEntryAsync(entry),
                !FileSystemService.IsRemotePath(entry.FullPath) && !entry.IsDirectory &&
                (IsImage(entry.Name) || PreviewFormatRegistry.IsModel(entry.Name) || IsZip(entry.Name) || entry.IsArchiveEntry));
            AddContextAction(menu, "Hex / entry", () => ShowHexEntryAsync(entry),
                !FileSystemService.IsRemotePath(entry.FullPath));
            menu.Items.Add(new MenuFlyoutSeparator());
            AddContextAction(menu, "Copy", () => { SetClipboardForSelection(cut: false); return Task.CompletedTask; });
            AddContextAction(menu, "Cut", () => { SetClipboardForSelection(cut: true); return Task.CompletedTask; },
                !FileSystemService.IsArchivePath(CurrentPath));
            AddContextAction(menu, "Delete", () => DeleteEntriesAsync(
                list.SelectedItems.Cast<ExplorerEntry>().ToArray(), permanent: false),
                !FileSystemService.IsArchivePath(CurrentPath));
            var workingDirectory = Directory.Exists(entry.FullPath) ? entry.FullPath : CurrentPath;
            AddContextAction(menu, "Open command prompt as administrator",
                () => OpenAdminCommandPromptAsync(workingDirectory),
                !FileSystemService.IsArchivePath(CurrentPath) && !FileSystemService.IsRemotePath(CurrentPath));
        }

        AddContextAction(menu, "Paste", () => PasteToAsync(CurrentPath));
        var selectedImages = list.SelectedItems.Cast<ExplorerEntry>()
            .Where(item => !item.IsDirectory && !item.IsArchiveEntry && IsImage(item.Name))
            .ToArray();
        if (selectedImages.Length > 0)
            AddContextAction(menu, "Convert selected images to PNG",
                () => ConvertSelectedImagesToPngAsync(selectedImages));
        if (entry is not null)
        {
            AddContextAction(menu, "Rename", () => RenameEntryAsync(entry),
                !FileSystemService.IsArchivePath(CurrentPath) && list.SelectedItems.Count == 1);
            menu.Items.Add(new MenuFlyoutSeparator());
            AddContextAction(menu, "Explorer++ properties", () =>
            {
                OpenPropertiesWindow(entry);
                return Task.CompletedTask;
            });
        }

        menu.ShowAt(list, position);
    }

    private static void AddContextAction(MenuFlyout menu, string label, Func<Task> action, bool enabled = true)
    {
        var item = new MenuFlyoutItem { Text = label, IsEnabled = enabled };
        item.Click += async (_, _) => await action();
        menu.Items.Add(item);
    }

    private void OpenPropertiesWindow(ExplorerEntry entry)
    {
        var window = new FilePropertiesWindow(entry);
        _propertyWindows.Add(window);
        window.Closed += (_, _) => _propertyWindows.Remove(window);
        window.Activate();
    }

    private async Task OpenAdminCommandPromptAsync(string workingDirectory)
    {
        if (FileSystemService.IsArchivePath(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            await ShowMessageAsync("Unable to open command prompt", "The current location is not a regular folder.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = workingDirectory
            });
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            // The user cancelled the elevation prompt.
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            await ShowMessageAsync("Unable to open command prompt", exception.Message);
        }
    }

    private void ExplorerList_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if ((GetKeyState(0x11) & 0x8000) == 0)
            return;
        var delta = e.GetCurrentPoint(sender as UIElement).Properties.MouseWheelDelta;
        IconSizeSlider.Value = Math.Clamp(IconSizeSlider.Value + Math.Sign(delta) * 16, IconSizeSlider.Minimum, IconSizeSlider.Maximum);
        e.Handled = true;
    }

    private void ExplorerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListView list)
            return;

        if (_restoringPaneSelection)
            return;

        var left = ReferenceEquals(list, LeftList);
        var selectedPaths = left ? _leftTabSelectedPaths : _rightTabSelectedPaths;
        var index = left ? _leftTabIndex : _rightTabIndex;
        if (index >= 0 && index < selectedPaths.Count)
        {
            selectedPaths[index] = list.SelectedItems.Cast<ExplorerEntry>()
                .Select(entry => entry.FullPath).ToList();
            ScheduleSessionSave();
        }
    }

    private async void ExplorerList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Delete || sender is not ListView list)
            return;
        SetActivePane(ReferenceEquals(list, LeftList));
        var selected = list.SelectedItems.Cast<ExplorerEntry>().ToArray();
        if (selected.Length == 0)
            return;
        var permanent = (GetKeyState(0x10) & 0x8000) != 0;
        e.Handled = true;
        await DeleteEntriesAsync(selected, permanent);
    }

    private void MainWindow_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.F6)
        {
            SetActivePane(!_leftActive);
            e.Handled = true;
            return;
        }
        if (e.Key == Windows.System.VirtualKey.F3)
        {
            SearchBox.Focus(FocusState.Programmatic);
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.F2 && e.OriginalSource is not (TextBox or AutoSuggestBox))
        {
            UpdateActivePaneFromKeyboardSource(e.OriginalSource as DependencyObject);
            if (ActiveList.SelectedItems.Count == 1 && !FileSystemService.IsArchivePath(CurrentPath))
            {
                var entry = (ExplorerEntry)ActiveList.SelectedItem;
                e.Handled = true;
                _ = RenameEntryAsync(entry);
            }
            return;
        }
        var controlDown = (GetKeyState(0x11) & 0x8000) != 0;
        var shiftDown = (GetKeyState(0x10) & 0x8000) != 0;
        var altDown = (GetKeyState(0x12) & 0x8000) != 0;
        if (altDown && (e.Key is Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right))
        {
            if (e.Key == Windows.System.VirtualKey.Left)
                BackButton_Click(this, new RoutedEventArgs());
            else
                ForwardButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (controlDown && shiftDown && e.Key == Windows.System.VirtualKey.P)
        {
            _ = ShowCommandPaletteAsync();
            e.Handled = true;
            return;
        }
        if (controlDown && e.Key == Windows.System.VirtualKey.L)
        {
            AddressEdit_Click(_leftActive ? LeftAddressButton : RightAddressButton, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (!controlDown || e.OriginalSource is TextBox or AutoSuggestBox)
            return;
        UpdateActivePaneFromKeyboardSource(e.OriginalSource as DependencyObject);

        if (e.Key == Windows.System.VirtualKey.Tab)
        {
            var tabs = _leftActive ? _leftTabs : _rightTabs;
            var selected = _leftActive ? _leftTabIndex : _rightTabIndex;
            selected = (selected + (shiftDown ? tabs.Count - 1 : 1)) % tabs.Count;
            if (_leftActive)
                _leftTabIndex = selected;
            else
                _rightTabIndex = selected;
            SetPaneView(_leftActive, GetViewIndex(_leftActive));
            LoadPane(_leftActive);
            e.Handled = true;
            return;
        }

        if (shiftDown && e.Key == Windows.System.VirtualKey.T)
        {
            ReopenClosedTab();
            e.Handled = true;
            return;
        }
        if (e.Key == Windows.System.VirtualKey.T)
        {
            AddTab(_leftActive, CurrentPath);
            e.Handled = true;
            return;
        }
        if (e.Key == Windows.System.VirtualKey.W)
        {
            CloseTab(_leftActive, _leftActive ? _leftTabIndex : _rightTabIndex);
            e.Handled = true;
            return;
        }
        if (shiftDown && e.Key == Windows.System.VirtualKey.N)
        {
            e.Handled = true;
            _ = CreateNewItemAsync(_leftActive, NewItemType.Folder);
            return;
        }

        switch (e.Key)
        {
            case Windows.System.VirtualKey.C:
                SetClipboardForSelection(cut: false);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.X:
                SetClipboardForSelection(cut: true);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.V:
                _ = PasteToAsync(CurrentPath);
                e.Handled = true;
                break;
        }
    }

    private async Task ShowCommandPaletteAsync()
    {
        var commands = new[]
        {
            "New tab", "Duplicate tab", "Close tab", "Reopen closed tab", "Move tab to other pane",
            "Bookmark current folder", "Save current folder pair", "Sort by name", "Sort by type",
            "Sort by size", "Sort by modified date", "Select by pattern", "Convert selected images to PNG",
            "Toggle single / dual pane",
            "Focus filter", "Edit current path"
        };
        var search = new TextBox { PlaceholderText = "Search commands", Margin = new Thickness(0, 0, 0, 8) };
        var list = new ListView
        {
            ItemsSource = commands,
            SelectionMode = ListViewSelectionMode.Single,
            Height = 300
        };
        search.TextChanged += (_, _) =>
            list.ItemsSource = commands.Where(item =>
                item.Contains(search.Text, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(search);
        content.Children.Add(list);
        var dialog = new ContentDialog
        {
            Title = "Command palette",
            Content = content,
            PrimaryButtonText = "Run",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot
        };
        dialog.Opened += (_, _) => search.Focus(FocusState.Programmatic);
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && list.SelectedItem is string command)
            await ExecutePaletteCommandAsync(command);
    }

    private async Task ExecutePaletteCommandAsync(string command)
    {
        var index = _leftActive ? _leftTabIndex : _rightTabIndex;
        switch (command)
        {
            case "New tab": AddTab(_leftActive, CurrentPath); break;
            case "Duplicate tab": DuplicateTab(_leftActive, index); break;
            case "Close tab": CloseTab(_leftActive, index); break;
            case "Reopen closed tab": ReopenClosedTab(); break;
            case "Move tab to other pane": MoveTabToOtherPane(_leftActive, index); break;
            case "Bookmark current folder": AddBookmark(CurrentPath); break;
            case "Save current folder pair": await SaveFolderSetAsync(); break;
            case "Sort by name": SetSort(_leftActive, "Name"); break;
            case "Sort by type": SetSort(_leftActive, "Type"); break;
            case "Sort by size": SetSort(_leftActive, "Size"); break;
            case "Sort by modified date": SetSort(_leftActive, "Modified"); break;
            case "Select by pattern": await SelectEntriesByPatternAsync(); break;
            case "Convert selected images to PNG":
                await ConvertSelectedImagesToPngAsync(ActiveList.SelectedItems.Cast<ExplorerEntry>().ToArray());
                break;
            case "Toggle single / dual pane": ViewModeButton_Click(SinglePaneButton, new RoutedEventArgs()); break;
            case "Focus filter": SearchBox.Focus(FocusState.Programmatic); SearchBox.SelectAll(); break;
            case "Edit current path": AddressEdit_Click(_leftActive ? LeftAddressButton : RightAddressButton, new RoutedEventArgs()); break;
        }
    }

    private void SetSort(bool left, string field)
    {
        var fields = left ? _leftTabSortFields : _rightTabSortFields;
        var descending = left ? _leftTabSortDescending : _rightTabSortDescending;
        var index = left ? _leftTabIndex : _rightTabIndex;
        if (fields[index] == field)
            descending[index] = !descending[index];
        else
        {
            fields[index] = field;
            descending[index] = false;
        }
        LoadPane(left);
    }

    private async Task SelectEntriesByPatternAsync()
    {
        var pattern = await PromptAsync("Select by pattern", "For example: *.bin");
        if (string.IsNullOrWhiteSpace(pattern))
            return;
        var entries = ActiveEntries.Where(entry =>
            FileSystemName.MatchesSimpleExpression(pattern.Trim(), entry.Name, ignoreCase: true)).ToList();
        ActiveList.SelectedItems.Clear();
        foreach (var entry in entries)
            ActiveList.SelectedItems.Add(entry);
    }

    private void UpdateActivePaneFromKeyboardSource(DependencyObject? source)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, LeftList))
            {
                SetActivePane(true);
                return;
            }
            if (ReferenceEquals(source, RightList))
            {
                SetActivePane(false);
                return;
            }
            source = VisualTreeHelper.GetParent(source);
        }
    }

    private void LeftList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args) =>
        LoadEntryPreviewIfNeeded(args, true);

    private void RightList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args) =>
        LoadEntryPreviewIfNeeded(args, false);

    private void LoadEntryPreviewIfNeeded(ContainerContentChangingEventArgs args, bool left)
    {
        if (args.Item is not ExplorerEntry entry || entry.IsRecycleBinEntry)
            return;

        if (args.InRecycleQueue || GetViewIndex(left) != 2)
            return;
        var size = (int)GetIconSize(left);
        if (entry.PreviewSizeLoaded == size)
            return;
        entry.PreviewSizeLoaded = size;
        _ = LoadEntryPreviewAsync(entry, size);
    }

    private async Task LoadEntryPreviewAsync(ExplorerEntry entry, int size)
    {
        try
        {
            if (FileSystemService.IsRemotePath(entry.FullPath))
            {
                entry.PreviewText = "Remote preview unavailable";
                return;
            }
            if (!entry.IsDirectory && !entry.IsArchiveEntry)
            {
                var preview = await PreviewThumbnailService.LoadAsync(entry.FullPath, size);
                if (preview is not null)
                {
                    entry.PreviewImage = preview;
                    return;
                }

                if (!PreviewFormatRegistry.IsImage(entry.Name))
                {
                    var text = await AccessRetryService.RunAsync(Root.XamlRoot, entry.FullPath,
                        () => PreviewThumbnailService.LoadContentPreviewAsync(entry.FullPath).GetAwaiter().GetResult());
                    entry.PreviewText = text ?? "Preview unavailable";
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException or SixLabors.ImageSharp.ImageFormatException)
        {
            entry.PreviewText = exception is UnauthorizedAccessException ? "Access denied" : "Preview unavailable";
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    private void FolderTreeItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        TreeItem? item = null;
        FrameworkElement? target = null;
        while (element is not null)
        {
            if (element is FrameworkElement frameworkElement)
            {
                item = frameworkElement.DataContext switch
                {
                    TreeViewNode node => node.Content as TreeItem,
                    TreeItem treeItem => treeItem,
                    _ => null
                };
                if (item is not null)
                {
                    target = frameworkElement;
                    break;
                }
            }
            element = VisualTreeHelper.GetParent(element);
        }
        if (item is null || target is null)
            return;

        var shellContextPath = item.ShellPath ?? item.Path;
        if (!item.IsArchive &&
            (item.ShellPath is not null || File.Exists(shellContextPath) || Directory.Exists(shellContextPath)))
        {
            try
            {
                var command = ShellContextMenuService.Show(
                    WindowNative.GetWindowHandle(this),
                    [shellContextPath],
                    showPaste: false,
                    showRemoveFavorite: item.IsQuickAccessEntry && item.CanRemove,
                    includeExplorerPlusPlusCommands: false);
                if (command == ShellContextMenuService.RemoveFavoriteCommand)
                {
                    if (item.OtherPath is null)
                    {
                        _settings.Bookmarks.RemoveAll(path =>
                            string.Equals(path, item.Path, StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        _settings.FolderSets.RemoveAll(set =>
                            string.Equals(set.Name, item.Name, StringComparison.OrdinalIgnoreCase));
                    }
                    RefreshQuickAccessTree();
                    ScheduleSessionSave();
                }
                else if (command == 0xFFFFFFFF)
                {
                    RefreshTree(CurrentPath);
                    RefreshComputerTree();
                }
                e.Handled = true;
                return;
            }
            catch (Exception exception) when (exception is COMException or InvalidOperationException or ArgumentException)
            {
                _ = ShowMessageAsync("Unable to show Windows context menu", exception.Message);
                e.Handled = true;
                return;
            }
        }

        if (item is { IsQuickAccessEntry: true, CanRemove: true })
        {
            var menu = new MenuFlyout();
            AddMenuAction(menu, item.OtherPath is null ? "Remove favorite" : "Remove folder set", () =>
            {
                if (item.OtherPath is null)
                    _settings.Bookmarks.RemoveAll(path =>
                        string.Equals(path, item.Path, StringComparison.OrdinalIgnoreCase));
                else
                    _settings.FolderSets.RemoveAll(set =>
                        string.Equals(set.Name, item.Name, StringComparison.OrdinalIgnoreCase));
                RefreshQuickAccessTree();
                ScheduleSessionSave();
                return Task.CompletedTask;
            });
            menu.ShowAt(target, e.GetPosition(target));
        }
        e.Handled = true;
    }

    private void FolderTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        var item = args.InvokedItem switch
        {
            TreeViewNode node => node.Content as TreeItem,
            TreeItem treeItem => treeItem,
            _ => null
        };
        if (item is { IsQuickAccessEntry: true })
        {
            if (item.OtherPath is null)
            {
                Navigate(item.Path);
                return;
            }

            var wasLeftActive = _leftActive;
            SetActivePane(true);
            Navigate(item.Path);
            SetActivePane(false);
            Navigate(item.OtherPath);
            SetActivePane(wasLeftActive);
        }
        else if (item is { IsRecycleBin: true })
            Navigate(RecycleBinService.Path);
        else if (item is { IsDirectory: true })
        {
            Navigate(item.Path);
        }
    }

    private void Splitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border splitter || splitter.Tag is not string splitTarget)
            return;

        var isSidebar = splitTarget == "Sidebar";
        var grid = isSidebar ? Root : PanesGrid;
        var columnIndex = 0;
        _activeSplitter = splitter;
        _splitterGrid = grid;
        _splitterColumnIndex = columnIndex;
        _splitterMinimumWidth = isSidebar ? 160 : 300;
        _splitterMinimumOtherWidth = 300;
        if (grid.ColumnDefinitions[columnIndex].Width.IsStar)
            grid.ColumnDefinitions[columnIndex].Width = new GridLength(grid.ColumnDefinitions[columnIndex].ActualWidth);

        splitter.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Splitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_splitterGrid is null || _activeSplitter is null || !ReferenceEquals(sender, _activeSplitter))
            return;

        var dividerWidth = _splitterGrid.ColumnDefinitions[1].ActualWidth;
        var availableWidth = _splitterGrid.ActualWidth - dividerWidth;
        var maximumWidth = Math.Max(_splitterMinimumWidth, availableWidth - _splitterMinimumOtherWidth);
        var pointerX = e.GetCurrentPoint(_splitterGrid).Position.X;
        var width = Math.Clamp(pointerX - dividerWidth / 2, _splitterMinimumWidth, maximumWidth);
        _splitterGrid.ColumnDefinitions[_splitterColumnIndex].Width = new GridLength(width);
        e.Handled = true;
    }

    private void Splitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border splitter && ReferenceEquals(splitter, _activeSplitter))
        {
            splitter.ReleasePointerCapture(e.Pointer);
            ClearSplitterDrag();
            e.Handled = true;
        }
    }

    private void Splitter_PointerCanceled(object sender, PointerRoutedEventArgs e) => ClearSplitterDrag();
    private void Splitter_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => ClearSplitterDrag();

    private void ClearSplitterDrag()
    {
        _activeSplitter = null;
        _splitterGrid = null;
    }

    private void LeftList_ItemClick(object sender, ItemClickEventArgs e)
    {
        SetActivePane(true);
        _leftLastClickedEntry = e.ClickedItem as ExplorerEntry;
        RefreshTree(CurrentPath, _leftDirectoryEntries);
    }

    private void RightList_ItemClick(object sender, ItemClickEventArgs e)
    {
        SetActivePane(false);
        _rightLastClickedEntry = e.ClickedItem as ExplorerEntry;
        RefreshTree(CurrentPath, _rightDirectoryEntries);
    }

    private void LeftList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) =>
        HandleListDoubleTapped(true, e);

    private void RightList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) =>
        HandleListDoubleTapped(false, e);

    private void HandleListDoubleTapped(bool left, DoubleTappedRoutedEventArgs e)
    {
        SetActivePane(left);
        var element = e.OriginalSource as DependencyObject;
        while (element is not null && element is not ListViewItem)
            element = VisualTreeHelper.GetParent(element);

        if (element is ListViewItem container &&
            (container.DataContext as ExplorerEntry ?? container.Content as ExplorerEntry) is { } entry)
            OpenEntry(entry);
        else
            Navigate(FileSystemService.GetParent(CurrentPath));
        e.Handled = true;
    }

    private async void OpenEntry(ExplorerEntry? entry)
    {
        if (entry is null)
            return;
        if (entry.IsRecycleBinEntry)
        {
            try
            {
                Process.Start(new ProcessStartInfo(entry.FullPath) { UseShellExecute = true });
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or COMException or ArgumentException)
            {
                await ShowMessageAsync("Unable to open Recycle Bin item", exception.Message);
            }
        }
        else if (entry.IsDirectory)
            Navigate(entry.FullPath);
        else if (!entry.IsArchiveEntry)
            await LaunchFileAsync(entry.FullPath);
        else
            await LaunchArchiveEntryAsync(entry.FullPath);
    }

    private async Task LaunchFileAsync(string path)
    {
        try
        {
            if (FileSystemService.IsRemotePath(path))
            {
                await ShowMessageAsync("Remote file", "Copy the remote file to a local folder before opening it.");
                return;
            }
            if (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return;
            }

            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            if (!await Windows.System.Launcher.LaunchFileAsync(file))
            {
                if (PreviewFormatRegistry.IsTexture(path))
                    await PreviewImageAsync(path);
                else
                    await ShowMessageAsync("Unable to open file", $"Windows could not find an application associated with:\n{path}");
            }
        }
        catch (UnauthorizedAccessException)
        {
            await ShowMessageAsync("Unable to open file",
                "Windows could not access this file using the current user's file association. Check file permissions or use Hex / entry for in-app access.");
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Unable to open file", exception.Message);
        }
    }

    private async Task LaunchArchiveEntryAsync(string path)
    {
        var (archivePath, entryName) = FileSystemService.SplitArchivePath(path);
        try
        {
            var tempPath = await AccessRetryService.RunAsync(Root.XamlRoot, archivePath, () =>
            {
                var openDirectory = Path.Combine(Path.GetTempPath(), "FirmwareExplorer", "ArchiveOpen");
                Directory.CreateDirectory(openDirectory);
                var tempFile = Path.Combine(openDirectory, $"{Guid.NewGuid():N}_{Path.GetFileName(entryName)}");
                using var archive = ZipFile.OpenRead(archivePath);
                var archiveEntry = archive.GetEntry(entryName) ?? throw new FileNotFoundException(entryName);
                using var source = archiveEntry.Open();
                using var destination = new FileStream(tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                source.CopyTo(destination);
                return tempFile;
            });
            if (tempPath is null)
                return;
            await LaunchFileAsync(tempPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            await ShowMessageAsync("Unable to open archive file", exception.Message);
        }
    }

    private async Task PreviewImageAsync(string path)
    {
        try
        {
            ImageSource? source = null;
            if (PreviewFormatRegistry.IsImage(path))
            {
                try
                {
                    var bytes = await AccessRetryService.RunAsync(Root.XamlRoot, path,
                        () => PreviewFormatRegistry.DecodeImage(path, 1600) ??
                            throw new InvalidDataException("No image decoder is available for this file."));
                    if (bytes is null)
                        return;
                    source = await CreateBitmapSourceAsync(bytes);
                }
                catch (Exception exception) when (!PreviewFormatRegistry.IsTexture(path) &&
                    (exception is InvalidDataException or ArgumentException or NotSupportedException or SixLabors.ImageSharp.ImageFormatException))
                {
                }
            }

            if (source is null)
            {
                var stream = await AccessRetryService.RunAsync(Root.XamlRoot, path, () => File.OpenRead(path));
                if (stream is null)
                    return;
                await using (stream)
                {
                    var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                    await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
                    source = bitmap;
                }
            }

            var image = new Image { Source = source, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, MaxWidth = 1200, MaxHeight = 800 };
            var dialog = new ContentDialog
            {
                Title = Path.GetFileName(path),
                Content = image,
                CloseButtonText = "Close",
                XamlRoot = Root.XamlRoot
            };
            await dialog.ShowAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or SixLabors.ImageSharp.ImageFormatException)
        {
            await ShowMessageAsync("Unable to preview image", exception.Message);
        }
    }

    private static async Task<Microsoft.UI.Xaml.Media.Imaging.BitmapImage> CreateBitmapSourceAsync(byte[] bytes)
    {
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
        using var stream = new MemoryStream(bytes);
        await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
        return bitmap;
    }

    private static bool IsImage(string name) => PreviewFormatRegistry.IsImage(name);

    private static bool IsZip(string name) =>
        new[] { ".zip", ".cbz" }.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);

    private async Task PreviewModelAsync(string path)
    {
        try
        {
            var scene = await AccessRetryService.RunAsync(Root.XamlRoot, path,
                () => PreviewFormatRegistry.ImportModel(path));
            if (scene is null)
            {
                await ShowMessageAsync("3D preview unavailable",
                    $"No importer is registered for {Path.GetExtension(path)}.");
                return;
            }
            var viewer = new ModelViewerDialog(scene, path) { XamlRoot = Root.XamlRoot };
            await viewer.ShowAsync();
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Unable to load 3D model", exception.Message);
        }
    }

    private async void HexButton_Click(object sender, RoutedEventArgs e)
    {
        var clicked = _leftActive ? _leftLastClickedEntry : _rightLastClickedEntry;
        var entry = clicked is not null && ActiveEntries.Contains(clicked)
            ? clicked
            : ActiveList.SelectedItem as ExplorerEntry;
        if (entry is null)
        {
            await ShowMessageAsync("Select an item", "Select a file or folder to inspect its filesystem entry or open its contents in the hex editor.");
            return;
        }

        await ShowHexEntryAsync(entry);
    }

    private async Task ShowHexEntryAsync(ExplorerEntry entry)
    {
        if (entry.IsDirectory && entry.IsArchiveEntry)
        {
            var (archive, path) = FileSystemService.SplitArchivePath(entry.FullPath);
            await ShowMessageAsync("Archive folder entry", $"Archive: {archive}\nEntry: {path}/\nType: Folder entry in the ZIP archive");
        }
        else if (entry.IsDirectory)
        {
            await ShowEntryMetadataAsync(entry.FullPath);
        }
        else if (entry.IsArchiveEntry)
        {
            await ShowArchiveFileHexAsync(entry.FullPath);
        }
        else
        {
            await ShowFileHexAsync(entry.FullPath);
        }
    }

    private async Task ShowEntryMetadataAsync(string path)
    {
        try
        {
            var details = await AccessRetryService.RunAsync(Root.XamlRoot, path, () =>
            {
                var info = new DirectoryInfo(path);
                var attributes = File.GetAttributes(path);
                return new StringBuilder()
                    .AppendLine($"Path: {info.FullName}")
                    .AppendLine($"Entry: {info.Name}")
                    .AppendLine("Type: Folder")
                    .AppendLine($"Attributes: {attributes}")
                    .AppendLine($"Created: {info.CreationTime}")
                    .AppendLine($"Modified: {info.LastWriteTime}")
                    .AppendLine($"Accessed: {info.LastAccessTime}")
                    .ToString();
            });
            if (details is not null)
                await ShowMessageAsync("Filesystem entry", details);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await ShowMessageAsync("Unable to read filesystem entry", exception.Message);
        }
    }

    private async Task ShowArchiveFileHexAsync(string archivePath)
    {
        var (archiveFile, entryName) = FileSystemService.SplitArchivePath(archivePath);
        try
        {
            var archiveStream = await AccessRetryService.RunAsync(Root.XamlRoot, archiveFile,
                () => new FileStream(archiveFile, FileMode.Open, FileAccess.Read, FileShare.Read));
            if (archiveStream is null)
                return;
            await using var ownedArchiveStream = archiveStream;
            using var archive = new ZipArchive(ownedArchiveStream, ZipArchiveMode.Read);
            var entry = archive.GetEntry(entryName) ?? throw new FileNotFoundException($"Archive entry not found: {entryName}");
            using var content = new ZipEntryReadStream(entry);
            await HexEditorDialog.ShowAsync(Root.XamlRoot, content, entry.Length, entryName, readOnly: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await ShowMessageAsync("Unable to read archive entry", exception.Message);
        }
    }

    private async Task ShowFileHexAsync(string path)
    {
        try
        {
            var opened = await AccessRetryService.RunAsync(Root.XamlRoot, path, () =>
            {
                var readOnly = (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
                var stream = new FileStream(path, FileMode.Open,
                    readOnly ? FileAccess.Read : FileAccess.ReadWrite, FileShare.Read);
                return new OpenedHexFile(stream, readOnly);
            });
            if (opened is null)
                return;
            await using var stream = opened.Stream;
            await HexEditorDialog.ShowAsync(Root.XamlRoot, stream, stream.Length, path, opened.ReadOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await ShowMessageAsync("Unable to open file in hex editor", exception.Message);
        }
    }

    private sealed record OpenedHexFile(FileStream Stream, bool ReadOnly);

    private async Task OpenRawRegionAsync(string devicePath, long offset, long length, string title)
    {
        if (length <= 0)
            return;
        var warning = new ContentDialog
        {
            Title = "Open raw device data read-only?",
            Content = $"This opens {title} from {devicePath} at byte offset {offset:N0}. The hex viewer is read-only and will not save changes.",
            PrimaryButtonText = "Open read-only",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot
        };
        if (await warning.ShowAsync() != ContentDialogResult.Primary)
            return;
        try
        {
            var stream = await AccessRetryService.RunAsync(Root.XamlRoot, devicePath,
                () => new FileStream(devicePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            if (stream is null)
                return;
            await using (stream)
            {
                await HexEditorDialog.ShowAsync(Root.XamlRoot, stream, length,
                    title, readOnly: true, regionOffset: offset);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            await ShowMessageAsync("Unable to open raw device data", exception.Message);
        }
    }

    private void Address_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || sender is not TextBox address)
            return;
        e.Handled = true;
        SetActivePane(ReferenceEquals(address, LeftAddress));
        Navigate(address.Text.Trim());
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e) => SetClipboard(cut: false);
    private void CutButton_Click(object sender, RoutedEventArgs e) => SetClipboard(cut: true);

    private void InternalDrag_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_internalDragList is null)
            return;

        var point = e.GetCurrentPoint(Root);
        if (!point.Properties.IsLeftButtonPressed)
        {
            ResetInternalDrag();
            return;
        }

        if (!_internalDragStarted &&
            (Math.Abs(point.Position.X - _internalDragStart.X) > 8 ||
             Math.Abs(point.Position.Y - _internalDragStart.Y) > 8))
        {
            var selected = _internalDragList.SelectedItems
                .OfType<ExplorerEntry>()
                .Select(entry => entry.FullPath)
                .ToArray();
            _internalDragSources = selected.Contains(_internalDragPath, StringComparer.OrdinalIgnoreCase)
                ? selected
                : [_internalDragPath!];
            _internalDragStarted = true;
        }

        if (_internalDragStarted)
            UpdateInternalDragTarget(GetInternalDragTabDestination(point.Position));
    }

    private async void InternalDrag_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_internalDragStarted)
        {
            ResetInternalDrag();
            return;
        }

        var destination = GetInternalDragTabDestination(e.GetCurrentPoint(Root).Position);
        var sources = _internalDragSources;
        ResetInternalDrag();
        e.Handled = true;

        if (destination is null || FileSystemService.IsArchivePath(destination))
            return;

        try
        {
            var copy = await PromptForDropActionAsync(sources);
            if (copy is null)
                return;

            foreach (var source in sources)
            {
                if (!FileSystemService.IsArchivePath(source) &&
                    string.Equals(Path.GetDirectoryName(Path.GetFullPath(source)), Path.GetFullPath(destination),
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!await PastePathAsync(source, destination, move: !copy.Value))
                    return;
            }

            RefreshDropPanes(sources, destination);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Unable to drop items", exception.Message);
        }
    }

    private string? GetInternalDragTabDestination(Windows.Foundation.Point point)
    {
        return GetTabDestination(LeftTabs, _leftTabs) ?? GetTabDestination(RightTabs, _rightTabs);

        string? GetTabDestination(StackPanel container, IReadOnlyList<string> tabs)
        {
            for (var index = 0; index < tabs.Count && index < container.Children.Count; index++)
            {
                if (container.Children[index] is not Button button)
                    continue;

                var origin = button.TransformToVisual(Root)
                    .TransformPoint(new Windows.Foundation.Point(0, 0));
                if (point.X >= origin.X && point.X <= origin.X + button.ActualWidth &&
                    point.Y >= origin.Y && point.Y <= origin.Y + button.ActualHeight)
                    return tabs[index];
            }

            return null;
        }
    }

    private void UpdateInternalDragTarget(string? destination)
    {
        if (string.Equals(_internalDragDestination, destination, StringComparison.OrdinalIgnoreCase))
            return;

        if (_internalDragTargetButton is not null)
            _internalDragTargetButton.Background = _internalDragTargetBackground;

        _internalDragTargetButton = null;
        _internalDragTargetBackground = null;
        _internalDragDestination = destination;
        if (destination is null)
            return;

        var tabs = _leftTabs.Contains(destination, StringComparer.OrdinalIgnoreCase) ? _leftTabs : _rightTabs;
        var container = ReferenceEquals(tabs, _leftTabs) ? LeftTabs : RightTabs;
        var index = tabs.FindIndex(path => string.Equals(path, destination, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index >= container.Children.Count ||
            container.Children[index] is not Button button)
            return;

        _internalDragTargetButton = button;
        _internalDragTargetBackground = button.Background;
        var color = FileSystemService.IsArchivePath(destination)
            ? Windows.UI.Color.FromArgb(160, 220, 65, 65)
            : Windows.UI.Color.FromArgb(160, 45, 145, 85);
        button.Background = new SolidColorBrush(color);
    }

    private void ResetInternalDrag()
    {
        if (_internalDragTargetButton is not null)
            _internalDragTargetButton.Background = _internalDragTargetBackground;
        _internalDragTargetButton = null;
        _internalDragTargetBackground = null;
        _internalDragList = null;
        _internalDragPath = null;
        _internalDragDestination = null;
        _internalDragSources = [];
        _internalDragStarted = false;
    }

    private void ExplorerList_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not ListView list || !HasDropData(e.DataView) ||
            FileSystemService.IsArchivePath(GetListDropDestination(list, e.OriginalSource as DependencyObject)))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.AcceptedOperation = GetDragOperation(e);
    }

    private async void ExplorerList_Drop(object sender, DragEventArgs e)
    {
        if (sender is not ListView list)
            return;

        var destination = GetListDropDestination(list, e.OriginalSource as DependencyObject);
        await ProcessDropAsync(e, destination, promptForAction: false);
    }

    private void Tab_DragOver(string destination, DragEventArgs e)
    {
        e.AcceptedOperation = HasDropData(e.DataView) && !FileSystemService.IsArchivePath(destination)
            ? DataPackageOperation.Copy | DataPackageOperation.Move
            : DataPackageOperation.None;
    }

    private async Task Tab_DropAsync(string destination, DragEventArgs e) =>
        await ProcessDropAsync(e, destination, promptForAction: true);

    private async Task ProcessDropAsync(DragEventArgs e, string destination, bool promptForAction)
    {
        e.Handled = true;
        if (!HasDropData(e.DataView) || FileSystemService.IsArchivePath(destination))
            return;

        try
        {
            var sources = await GetDropSourcesAsync(e.DataView);
            if (sources.Length == 0)
                return;

            var copy = promptForAction
                ? await PromptForDropActionAsync(sources)
                : GetDragOperation(e) == DataPackageOperation.Copy;
            if (copy is null)
                return;

            foreach (var source in sources)
            {
                if (!FileSystemService.IsArchivePath(source) &&
                    string.Equals(Path.GetDirectoryName(Path.GetFullPath(source)), Path.GetFullPath(destination),
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!await PastePathAsync(source, destination, move: !copy.Value))
                    return;
            }

            RefreshDropPanes(sources, destination);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Unable to drop items", exception.Message);
        }
    }

    private static bool HasDropData(DataPackageView data) =>
        data.AvailableFormats.Contains(InternalDragFormat) ||
        data.AvailableFormats.Contains(StandardDataFormats.StorageItems);

    private static DataPackageOperation GetDragOperation(DragEventArgs e)
    {
        if (e.Modifiers.HasFlag(DragDropModifiers.Control))
            return DataPackageOperation.Copy;

        var requested = e.DataView.RequestedOperation;
        return (requested & DataPackageOperation.Move) != 0
            ? DataPackageOperation.Move
            : DataPackageOperation.Copy;
    }

    private string GetListDropDestination(ListView list, DependencyObject? source)
    {
        while (source is not null && source is not ListViewItem)
        {
            if (source is not UIElement element)
                break;
            source = VisualTreeHelper.GetParent(element);
        }

        var entry = source is ListViewItem item
            ? item.DataContext as ExplorerEntry ?? item.Content as ExplorerEntry
            : null;
        return entry is { IsDirectory: true } ? entry.FullPath :
            ReferenceEquals(list, LeftList) ? _leftTabs[_leftTabIndex] : _rightTabs[_rightTabIndex];
    }

    private static async Task<string[]> GetDropSourcesAsync(DataPackageView data)
    {
        if (data.AvailableFormats.Contains(InternalDragFormat))
        {
            var payload = await data.GetDataAsync(InternalDragFormat);
            if (payload is not string json)
                throw new InvalidDataException("The dragged Explorer++ item list is invalid.");
            return JsonSerializer.Deserialize<string[]>(json) ??
                throw new InvalidDataException("The dragged Explorer++ item list is invalid.");
        }

        var items = await data.GetStorageItemsAsync();
        return items.Select(item => item.Path).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
    }

    private async Task<bool?> PromptForDropActionAsync(IReadOnlyCollection<string> sources)
    {
        var archiveSource = sources.Any(FileSystemService.IsArchivePath);
        var dialog = new ContentDialog
        {
            Title = "Drop items",
            PrimaryButtonText = archiveSource ? "Copy here" : "Move here",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot
        };
        if (archiveSource)
            dialog.Content = "Items from archives can only be copied.";
        else
        {
            dialog.Content = "What would you like to do with these items?";
            dialog.SecondaryButtonText = "Copy here";
        }

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
            return archiveSource;
        if (result == ContentDialogResult.Secondary && !archiveSource)
            return true;
        return null;
    }

    private void RefreshDropPanes(IEnumerable<string> sources, string destination)
    {
        var sourcePaths = sources
            .Where(source => !FileSystemService.IsArchivePath(source))
            .Select(source => Path.GetDirectoryName(Path.GetFullPath(source)))
            .ToArray();

        if (_leftPaneOpen && (string.Equals(_leftTabs[_leftTabIndex], destination, StringComparison.OrdinalIgnoreCase) ||
            sourcePaths.Contains(_leftTabs[_leftTabIndex], StringComparer.OrdinalIgnoreCase)))
            LoadPane(true);
        if (_rightPaneOpen && (string.Equals(_rightTabs[_rightTabIndex], destination, StringComparison.OrdinalIgnoreCase) ||
            sourcePaths.Contains(_rightTabs[_rightTabIndex], StringComparer.OrdinalIgnoreCase)))
            LoadPane(false);
    }

    private void SetClipboard(bool cut)
    {
        SetClipboardForSelection(cut);
    }

    private void SetClipboardForSelection(bool cut)
    {
        var selected = ActiveList.SelectedItems.Cast<ExplorerEntry>().ToArray();
        if (selected.Length == 0)
        {
            StatusText.Text = "Select one or more items first.";
            return;
        }
        _clipboardPath = string.Join(Environment.NewLine, selected.Select(entry => entry.FullPath));
        _clipboardIsCut = cut;
        StatusText.Text = $"{(cut ? "Cut" : "Copied")} {selected.Length} item(s).";
        _ = RecordSelectedClipboardReferencesAsync(selected);
    }

    private ClipboardHistoryService GetClipboardHistory()
        => _clipboardHistory ??= new ClipboardHistoryService(_settings.ClipboardHistoryDirectory);

    private async Task RecordSelectedClipboardReferencesAsync(IReadOnlyCollection<ExplorerEntry> entries)
    {
        await _clipboardHistoryCaptureGate.WaitAsync();
        try
        {
            var items = entries.Select(entry =>
                (entry.FullPath, entry.Name, entry.IsDirectory)).ToArray();
            await GetClipboardHistory().AddPathReferencesAsync(items);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not save clipboard history: {exception.Message}";
            Debug.WriteLine(StatusText.Text);
        }
        finally
        {
            _clipboardHistoryCaptureGate.Release();
        }
    }

    private async void WindowsClipboardChanged(IReadOnlyList<WindowsClipboardFormat> formats)
    {
        _clipboardPath = null;
        _clipboardIsCut = false;
        if (formats.Count == 0)
        {
            StatusText.Text = "Windows clipboard changed, but it contains no readable binary clipboard formats.";
            return;
        }

        await _clipboardHistoryCaptureGate.WaitAsync();
        try
        {
            var history = GetClipboardHistory();
            foreach (var format in formats)
            {
                history.AddNativeFormat(format);
                if (format.StoragePaths.Count > 0)
                {
                    var pathReferences = format.StoragePaths.Select(path =>
                        (path, Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                            Directory.Exists(path))).ToArray();
                    await history.AddPathReferencesAsync(pathReferences);
                }
            }
            StatusText.Text = $"Windows clipboard history: {history.Entries.Count} item(s), " +
                $"{ClipboardHistoryEntry.FormatBytes(history.StoredBytes)} stored.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not save Windows clipboard history: {exception.Message}";
            Debug.WriteLine(StatusText.Text);
        }
        finally
        {
            _clipboardHistoryCaptureGate.Release();
        }
    }

    private async void ClipboardHistoryButton_Click(object sender, RoutedEventArgs e)
        => await ShowClipboardHistoryAsync();

    private async Task ShowClipboardHistoryAsync()
    {
        while (true)
        {
            ClipboardHistoryService history;
            try
            {
                history = GetClipboardHistory();
            }
            catch (Exception exception)
            {
                await ShowMessageAsync("Unable to open clipboard history", exception.Message);
                return;
            }

            var entries = history.Entries.OrderByDescending(entry => entry.CapturedAt).ToArray();
            var list = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,
                Height = 360
            };
            foreach (var entry in entries)
                list.Items.Add(await CreateClipboardHistoryRowAsync(history, entry));
            var summary = new TextBlock
            {
                Text = $"Stored: {ClipboardHistoryEntry.FormatBytes(history.StoredBytes)} / " +
                    $"{ClipboardHistoryEntry.FormatBytes(ClipboardHistoryService.MaximumHistoryBytes)} " +
                    $"({entries.Length} entries)\nHistory folder: {history.DirectoryPath}\n" +
                    "Each entry is one Windows clipboard format; unknown formats are kept as raw blobs. " +
                    "File and folder entries retain source paths, not copies of their contents.",
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var setCurrentButton = new Button { Content = "Select current" };
            var deleteButton = new Button { Content = "Delete selected" };
            var deleteAllButton = new Button { Content = "Delete all" };
            var refreshButton = new Button { Content = "Refresh" };
            buttons.Children.Add(setCurrentButton);
            buttons.Children.Add(deleteButton);
            buttons.Children.Add(deleteAllButton);
            buttons.Children.Add(refreshButton);
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(summary);
            content.Children.Add(list);
            content.Children.Add(buttons);
            var dialog = new ContentDialog
            {
                Title = "Clipboard history",
                Content = content,
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Root.XamlRoot
            };
            var action = "";
            ClipboardHistoryEntry? selected = null;
            setCurrentButton.Click += (_, _) =>
            {
                selected = (list.SelectedItem as ListViewItem)?.Tag as ClipboardHistoryEntry;
                if (selected is not null)
                {
                    action = "current";
                    dialog.Hide();
                }
            };
            deleteButton.Click += (_, _) =>
            {
                selected = (list.SelectedItem as ListViewItem)?.Tag as ClipboardHistoryEntry;
                if (selected is not null)
                {
                    action = "delete";
                    dialog.Hide();
                }
            };
            deleteAllButton.Click += (_, _) =>
            {
                action = "clear";
                dialog.Hide();
            };
            refreshButton.Click += (_, _) =>
            {
                action = "refresh";
                dialog.Hide();
            };

            await dialog.ShowAsync();
            try
            {
                if (action == "current" && selected is not null)
                {
                    await SetClipboardFromHistoryAsync(history, selected);
                    return;
                }
                if (action == "delete" && selected is not null)
                    history.Delete(selected);
                else if (action == "clear")
                {
                    var confirm = new ContentDialog
                    {
                        Title = "Delete all clipboard history?",
                        Content = "This permanently removes all saved clipboard content and history entries.",
                        PrimaryButtonText = "Delete all",
                        CloseButtonText = "Cancel",
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = Root.XamlRoot
                    };
                    if (await confirm.ShowAsync() == ContentDialogResult.Primary)
                        history.Clear();
                }
                else if (action is not "refresh")
                    return;
            }
            catch (Exception exception)
            {
                await ShowMessageAsync("Clipboard history action failed", exception.Message);
                return;
            }
        }
    }

    private async Task<ListViewItem> CreateClipboardHistoryRowAsync(
        ClipboardHistoryService history,
        ClipboardHistoryEntry entry)
    {
        var content = new StackPanel { Padding = new Thickness(8, 6, 8, 6), Spacing = 3 };
        content.Children.Add(new TextBlock
        {
            Text = entry.DisplayName,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis
        });
        content.Children.Add(new TextBlock
        {
            Text = entry.Details,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 170, 180, 194)),
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
        });

        if (!string.IsNullOrWhiteSpace(entry.SourcePath))
        {
            content.Children.Add(new TextBlock
            {
                Text = entry.SourcePath,
                FontSize = 9,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 150, 161, 177)),
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            });
        }

        if (entry.PayloadFileName is not null)
        {
            var bytes = history.ReadPayload(entry);
            if (entry.Kind == "Image")
            {
                var pngBytes = TryCreateClipboardImagePreview(bytes, entry.ClipboardFormatId);
                if (pngBytes is not null)
                {
                    var image = await CreateBitmapSourceAsync(pngBytes);
                    content.Children.Add(new Image
                    {
                        Source = image,
                        MaxWidth = 240,
                        MaxHeight = 120,
                        Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Left
                    });
                }
            }
            else if (entry.Kind is "Text" or "HTML" or "Rich text")
            {
                var preview = DecodeClipboardTextPreview(bytes, entry);
                if (!string.IsNullOrWhiteSpace(preview))
                {
                    content.Children.Add(new TextBlock
                    {
                        Text = preview,
                        MaxHeight = 90,
                        TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                        TextTrimming = Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis,
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 221, 229, 239)),
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI")
                    });
                }
            }
        }

        return new ListViewItem { Content = content, Tag = entry };
    }

    private static string DecodeClipboardTextPreview(byte[] bytes, ClipboardHistoryEntry entry)
    {
        var text = entry.ClipboardFormatId switch
        {
            13 => Encoding.Unicode.GetString(bytes),
            1 or 7 => Encoding.Latin1.GetString(bytes),
            _ => Encoding.UTF8.GetString(bytes)
        };
        text = text.TrimEnd('\0').Trim();
        if (entry.Kind == "HTML")
        {
            var start = text.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
            var end = text.LastIndexOf("</html>", StringComparison.OrdinalIgnoreCase);
            if (start >= 0 && end >= start)
                text = text[start..(end + "</html>".Length)];
        }
        return text.Length <= 800 ? text : text[..800] + "…";
    }

    private static byte[]? TryCreateClipboardImagePreview(byte[] bytes, uint? formatId)
    {
        try
        {
            using var input = new MemoryStream(bytes, writable: false);
            using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(input);
            using var output = new MemoryStream();
            image.Save(output, new PngEncoder());
            return output.ToArray();
        }
        catch (Exception exception) when (exception is SixLabors.ImageSharp.ImageFormatException or
            ArgumentException or InvalidDataException)
        {
            if (formatId is not (8 or 17) || bytes.Length < 40)
                return null;

            var headerSize = BitConverter.ToUInt32(bytes, 0);
            if (headerSize < 40 || headerSize > bytes.Length)
                return null;
            var bitCount = BitConverter.ToUInt16(bytes, 14);
            var colorsUsed = BitConverter.ToUInt32(bytes, 32);
            var paletteEntries = colorsUsed > 0 ? colorsUsed : bitCount <= 8 ? 1u << bitCount : 0;
            var pixelOffset = 14L + headerSize + paletteEntries * 4L;
            if (pixelOffset > int.MaxValue || pixelOffset > 14L + bytes.Length)
                return null;

            var bmp = new byte[14 + bytes.Length];
            bmp[0] = (byte)'B';
            bmp[1] = (byte)'M';
            BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
            BitConverter.GetBytes((int)pixelOffset).CopyTo(bmp, 10);
            bytes.CopyTo(bmp, 14);
            using var dibStream = new MemoryStream(bmp, writable: false);
            using var dibImage = SixLabors.ImageSharp.Image.Load<Rgba32>(dibStream);
            using var pngStream = new MemoryStream();
            dibImage.Save(pngStream, new PngEncoder());
            return pngStream.ToArray();
        }
        catch (Exception exception) when (exception is SixLabors.ImageSharp.ImageFormatException or
            ArgumentException or InvalidDataException or OverflowException)
        {
            return null;
        }
    }

    private async Task SetClipboardFromHistoryAsync(
        ClipboardHistoryService history,
        ClipboardHistoryEntry entry)
    {
        if (entry.ClipboardFormatId is uint formatId && entry.ClipboardFormatName is string formatName)
        {
            WindowsClipboardMonitor.SetClipboardFormat(
                WindowNative.GetWindowHandle(this),
                formatId,
                formatName,
                history.ReadPayload(entry));
            StatusText.Text = $"Restored Windows clipboard format {formatName}.";
            return;
        }

        var package = new DataPackage();
        switch (entry.Kind)
        {
            case "Image":
            {
                var payloadPath = Path.Combine(history.DirectoryPath, entry.PayloadFileName!);
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(payloadPath);
                package.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
                break;
            }
            case "HTML":
                package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(
                    Encoding.UTF8.GetString(history.ReadPayload(entry))));
                break;
            case "Rich text":
                package.SetRtf(Encoding.UTF8.GetString(history.ReadPayload(entry)));
                break;
            case "Text":
                package.SetText(Encoding.UTF8.GetString(history.ReadPayload(entry)));
                break;
            case "File reference":
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(entry.SourcePath!);
                package.SetStorageItems([file], readOnly: true);
                break;
            }
            case "Folder reference":
            {
                var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(entry.SourcePath!);
                package.SetStorageItems([folder], readOnly: true);
                break;
            }
            default:
                throw new InvalidOperationException($"Clipboard history type '{entry.Kind}' is not supported.");
        }

        Clipboard.SetContent(package);
        Clipboard.Flush();
        StatusText.Text = $"Set clipboard to {entry.DisplayName}.";
    }

    private async Task ConvertSelectedImagesToPngAsync(IReadOnlyCollection<ExplorerEntry> entries)
    {
        var candidates = entries
            .Where(entry => !entry.IsDirectory && !entry.IsArchiveEntry && IsImage(entry.Name) &&
                !Path.GetExtension(entry.Name).Equals(".png", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (candidates.Length == 0)
        {
            await ShowMessageAsync("No convertible images selected",
                "Select one or more supported non-PNG image files in a regular folder.");
            return;
        }

        var converted = new List<string>();
        var errors = new List<string>();
        foreach (var entry in candidates)
        {
            IReadOnlyList<DecodedTextureImage>? images;
            try
            {
                images = await AccessRetryService.RunAsync(Root.XamlRoot, entry.FullPath,
                    () => PreviewFormatRegistry.DecodeImagesOriginal(entry.FullPath));
                if (images is null)
                {
                    errors.Add($"{entry.Name}: File access was cancelled.");
                    continue;
                }
            }
            catch (Exception exception)
            {
                errors.Add($"{entry.Name}: {exception.Message}");
                continue;
            }

            foreach (var image in images)
            {
                var outputName = image.Name is null ? entry.Name : $"{entry.Name} ({image.Name})";
                try
                {
                    var targetPath = GetAvailablePngPath(entry.FullPath, image.Name);
                    var saved = await AccessRetryService.RunActionAsync(Root.XamlRoot, targetPath,
                        () => WriteNewFile(targetPath, image.PngBytes));
                    if (!saved)
                    {
                        errors.Add($"{outputName}: File access was cancelled.");
                        continue;
                    }
                    converted.Add(Path.GetFileName(targetPath));
                }
                catch (Exception exception)
                {
                    errors.Add($"{outputName}: {exception.Message}");
                }
            }
        }

        StatusText.Text = $"Created {converted.Count} PNG file(s) from {candidates.Length} selected image file(s).";
        var details = converted.Count > 0
            ? $"Created {converted.Count} PNG file(s):\n{string.Join("\n", converted.Take(8))}" +
              (converted.Count > 8 ? "\n…" : "")
            : "No files were converted.";
        if (errors.Count > 0)
            details += $"\n\nCould not convert {errors.Count} item(s):\n{string.Join("\n", errors.Take(8))}" +
                       (errors.Count > 8 ? "\n…" : "");
        if (converted.Count > 0)
            LoadPane(_leftActive);
        await ShowMessageAsync("Image conversion complete", details);
    }

    private static string GetAvailablePngPath(string sourcePath, string? textureName = null)
    {
        var sourceName = Path.GetFileNameWithoutExtension(sourcePath);
        var outputName = textureName is null ? sourceName : $"{sourceName}_{SanitizeFileName(textureName)}";
        var directory = Path.GetDirectoryName(sourcePath)!;
        var target = Path.Combine(directory, $"{outputName}.png");
        if (!File.Exists(target) && !Directory.Exists(target))
            return target;

        for (var suffix = 1; ; suffix++)
        {
            var convertedName = suffix == 1
                ? $"{outputName} (converted).png"
                : $"{outputName} (converted {suffix}).png";
            target = Path.Combine(directory, convertedName);
            if (!File.Exists(target) && !Directory.Exists(target))
                return target;
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = new string(name
            .Select(character => invalidCharacters.Contains(character) || character is '/' or '\\' ? '_' : character)
            .ToArray())
            .Trim(' ', '.');
        return string.IsNullOrEmpty(sanitized) ? "texture" : sanitized;
    }

    private static void WriteNewFile(string path, byte[] contents)
    {
        if (FileSystemService.IsRemotePath(path))
        {
            using var input = new MemoryStream(contents, writable: false);
            RemoteFileSystemService.WriteFile(path, input);
            return;
        }

        var created = false;
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            stream.Write(contents);
        }
        catch (Exception writeException)
        {
            if (!created)
                throw;
            try
            {
                File.Delete(path);
            }
            catch (Exception cleanupException)
            {
                throw new IOException($"Could not remove incomplete output file '{path}'.",
                    new AggregateException(writeException, cleanupException));
            }
            throw;
        }
    }

    private async void PasteButton_Click(object sender, RoutedEventArgs e)
        => await PasteToAsync(CurrentPath);

    private async Task PasteToAsync(string destinationPath)
    {
        if (FileSystemService.IsArchivePath(destinationPath))
        {
            await ShowMessageAsync("Read-only archive", "Paste into a regular folder, not an archive.");
            return;
        }
        try
        {
            var internalSources = _clipboardPath?.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            if (internalSources is { Length: > 0 })
            {
                foreach (var source in internalSources)
                {
                    var succeeded = await PastePathAsync(source, destinationPath, _clipboardIsCut);
                    if (!succeeded)
                        return;
                }
                if (_clipboardIsCut)
                    _clipboardPath = null;
            }
            else
            {
                var clipboard = Clipboard.GetContent();
                if (clipboard.Contains(StandardDataFormats.StorageItems))
                {
                    var items = await clipboard.GetStorageItemsAsync();
                    var move = (clipboard.RequestedOperation & DataPackageOperation.Move) != 0;
                    foreach (var item in items)
                    {
                        if (string.IsNullOrWhiteSpace(item.Path))
                            continue;
                        var succeeded = await PastePathAsync(item.Path, destinationPath, move);
                        if (!succeeded)
                            return;
                    }
                }
                else if (clipboard.Contains(StandardDataFormats.Bitmap))
                {
                    var bitmap = await clipboard.GetBitmapAsync();
                    using var clipboardStream = await bitmap.OpenReadAsync();
                    using var imageStream = clipboardStream.AsStreamForRead();
                    using var image = await SixLabors.ImageSharp.Image.LoadAsync<Rgba32>(imageStream);
                    await using var pngStream = new MemoryStream();
                    await image.SaveAsync(pngStream, new PngEncoder());
                    var outputPath = GetAvailableClipboardPath(destinationPath, "clipboardImage", ".png");
                    WriteNewFile(outputPath, pngStream.ToArray());
                }
                else if (clipboard.Contains(StandardDataFormats.Html))
                {
                    var html = await clipboard.GetHtmlFormatAsync();
                    var fragment = HtmlFormatHelper.GetStaticFragment(html);
                    var document = $"<!doctype html><html><head><meta charset=\"utf-8\"></head><body>{fragment}</body></html>";
                    var outputPath = GetAvailableClipboardPath(destinationPath, "clipboardHtml", ".html");
                    WriteNewFile(outputPath, Encoding.UTF8.GetBytes(document));
                }
                else if (clipboard.Contains(StandardDataFormats.Rtf))
                {
                    var rtf = await clipboard.GetRtfAsync();
                    var outputPath = GetAvailableClipboardPath(destinationPath, "clipboardRichText", ".rtf");
                    WriteNewFile(outputPath, Encoding.UTF8.GetBytes(rtf));
                }
                else if (clipboard.Contains(StandardDataFormats.Text))
                {
                    var text = await clipboard.GetTextAsync();
                    var outputPath = GetAvailableClipboardPath(destinationPath, "clipboardText", ".txt");
                    WriteNewFile(outputPath, Encoding.UTF8.GetBytes(text));
                }
                else
                    return;
            }
            if (string.Equals(destinationPath, CurrentPath, StringComparison.OrdinalIgnoreCase))
                LoadPane(_leftActive);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Unable to paste items", exception.Message);
        }
    }

    private static string GetAvailableClipboardPath(string directory, string name, string extension)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        var fileName = $"{name}-{timestamp}{extension}";
        var path = FileSystemService.CombinePath(directory, fileName);
        if (!File.Exists(path) && !Directory.Exists(path))
            return path;

        for (var suffix = 2; ; suffix++)
        {
            path = FileSystemService.CombinePath(directory, $"{name}-{timestamp}-{suffix}{extension}");
            if (!File.Exists(path) && !Directory.Exists(path))
                return path;
        }
    }

    private async Task<bool> PastePathAsync(string source, string destinationPath, bool move)
    {
        if (FileSystemService.IsRemotePath(source) || FileSystemService.IsRemotePath(destinationPath))
        {
            var actionPath = FileSystemService.IsRemotePath(destinationPath) ? destinationPath : source;
            return await AccessRetryService.RunActionAsync(Root.XamlRoot, actionPath, () =>
            {
                var remoteSource = FileSystemService.IsRemotePath(source);
                var remoteDestination = FileSystemService.IsRemotePath(destinationPath);
                var sourceIsDirectory = remoteSource
                    ? RemoteFileSystemService.IsDirectory(source)
                    : Directory.Exists(source);

                if (remoteSource && remoteDestination)
                    RemoteFileSystemService.CopyRemoteEntry(source, destinationPath, sourceIsDirectory);
                else if (remoteSource)
                    RemoteFileSystemService.CopyRemoteEntryToLocal(source, destinationPath, sourceIsDirectory);
                else if (remoteDestination)
                    RemoteFileSystemService.CopyLocalEntryToRemote(source, destinationPath);
                else
                    RemoteFileSystemService.CopyLocalEntryToLocal(source, destinationPath);

                if (!move)
                    return;
                if (remoteSource)
                    RemoteFileSystemService.Delete(source, sourceIsDirectory);
                else if (sourceIsDirectory)
                    Directory.Delete(source, recursive: true);
                else
                    File.Delete(source);
            });
        }

        return await AccessRetryService.RunActionAsync(Root.XamlRoot, source, () =>
        {
            if (FileSystemService.IsArchivePath(source))
            {
                FileSystemService.CopyEntryTo(source, destinationPath);
                return;
            }
            var destination = Path.Combine(destinationPath, Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar)));
            if (move)
            {
                var fullSource = Path.GetFullPath(source);
                var fullDestination = Path.GetFullPath(destination);
                if (Directory.Exists(source) &&
                    fullDestination.StartsWith(fullSource.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A folder cannot be moved into itself or one of its subfolders.");

                var sourceRoot = Path.GetPathRoot(fullSource);
                var destinationRoot = Path.GetPathRoot(fullDestination);
                if (string.Equals(sourceRoot, destinationRoot, StringComparison.OrdinalIgnoreCase))
                {
                    if (Directory.Exists(source))
                        Directory.Move(source, destination);
                    else
                        File.Move(source, destination);
                }
                else
                {
                    if (Directory.Exists(source))
                    {
                        FileSystemService.CopyDirectory(source, destination);
                        Directory.Delete(source, recursive: true);
                    }
                    else
                    {
                        File.Copy(source, destination, overwrite: false);
                        File.Delete(source);
                    }
                }
            }
            else
            {
                if (Directory.Exists(source))
                    FileSystemService.CopyDirectory(source, destination);
                else
                    File.Copy(source, destination, overwrite: false);
            }
        });
    }

    private async void RenameButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveList.SelectedItems.Count != 1 || FileSystemService.IsArchivePath(CurrentPath))
            return;
        var entry = (ExplorerEntry)ActiveList.SelectedItem;
        await RenameEntryAsync(entry);
    }

    private async Task RenameEntryAsync(ExplorerEntry entry)
    {
        if (FileSystemService.IsArchivePath(CurrentPath))
            return;
        var name = await PromptAsync("Rename", "New name", entry.Name);
        if (string.IsNullOrWhiteSpace(name) || name == entry.Name)
            return;
        if (!IsValidNewItemName(name.Trim()))
        {
            await ShowMessageAsync("Invalid name", "Enter a valid file or folder name without a path.");
            return;
        }
        try
        {
            var destination = FileSystemService.CombinePath(CurrentPath, name.Trim());
            if (await AccessRetryService.RunActionAsync(Root.XamlRoot, entry.FullPath, () =>
                    FileSystemService.Rename(entry.FullPath, destination, entry.IsDirectory)))
                LoadPane(_leftActive);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Unable to rename item", exception.Message);
        }
    }

    private async Task DeleteEntriesAsync(IReadOnlyCollection<ExplorerEntry> selected, bool permanent)
    {
        if (selected.Count == 0)
            return;
        if (selected.Any(entry => entry.IsArchiveEntry) || FileSystemService.IsArchivePath(CurrentPath))
        {
            await ShowMessageAsync("Read-only archive", "Archive contents cannot be deleted here. Extract the archive first.");
            return;
        }

        var remoteLocation = FileSystemService.IsRemotePath(CurrentPath);
        var operation = remoteLocation
            ? "delete from the server"
            : permanent
                ? "permanently delete"
                : "move to the Recycle Bin";
        var confirm = new ContentDialog
        {
            Title = selected.Count > 1 ? $"Delete {selected.Count} selected items?" : "Delete selected item?",
            Content = selected.Count > 1
                ? $"This will {operation} all {selected.Count} selected files and folders."
                : remoteLocation || permanent
                    ? $"This will permanently delete \"{selected.First().Name}\". This cannot be undone."
                    : $"\"{selected.First().Name}\" will be moved to the Recycle Bin.",
            PrimaryButtonText = remoteLocation || permanent ? "Delete permanently" : "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            return;
        try
        {
            foreach (var entry in selected)
            {
                var deleted = await AccessRetryService.RunActionAsync(Root.XamlRoot, entry.FullPath, () =>
                    FileSystemService.Delete(entry.FullPath, entry.IsDirectory, permanent || remoteLocation));
                if (!deleted)
                    return;
            }
            LoadPane(_leftActive);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Unable to delete items", exception.Message);
        }
    }

    private async void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        var entry = (_leftActive ? _leftLastClickedEntry : _rightLastClickedEntry) ?? ActiveList.SelectedItem as ExplorerEntry;
        if (entry is null)
            return;
        await PreviewEntryAsync(entry);
    }

    private async Task PreviewEntryAsync(ExplorerEntry entry)
    {
        if (!entry.IsArchiveEntry && IsImage(entry.Name))
            await PreviewImageAsync(entry.FullPath);
        else if (!entry.IsArchiveEntry && PreviewFormatRegistry.IsModel(entry.Name))
            await PreviewModelAsync(entry.FullPath);
        else if (!entry.IsArchiveEntry && IsZip(entry.Name))
            Navigate(FileSystemService.ArchivePath(entry.FullPath));
        else if (entry.IsArchiveEntry && !entry.IsDirectory)
            await PreviewArchiveEntryAsync(entry.FullPath);
    }

    private async Task PreviewArchiveEntryAsync(string path)
    {
        var (archiveFile, entryName) = FileSystemService.SplitArchivePath(path);
        if (!IsImage(entryName))
        {
            await ShowMessageAsync("Preview unavailable", "Only images can be previewed directly from an archive.");
            return;
        }
        try
        {
            using var archive = ZipFile.OpenRead(archiveFile);
            var entry = archive.GetEntry(entryName) ?? throw new FileNotFoundException(entryName);
            if (entry.Length > 128 * 1024 * 1024)
                throw new InvalidDataException("Archive image exceeds the 128 MB preview limit.");
            await using var stream = entry.Open();
            using var imageStream = new MemoryStream();
            await stream.CopyToAsync(imageStream);
            imageStream.Position = 0;
            byte[]? bytes;
            try
            {
                bytes = PreviewFormatRegistry.DecodeImage(imageStream, Path.GetExtension(entryName), 1600);
            }
            catch (SixLabors.ImageSharp.ImageFormatException)
            {
                bytes = null;
            }

            ImageSource? imageSource;
            if (bytes is not null)
                imageSource = await CreateBitmapSourceAsync(bytes);
            else
            {
                imageStream.Position = 0;
                var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                await bitmap.SetSourceAsync(imageStream.AsRandomAccessStream());
                imageSource = bitmap;
            }
            var dialog = new ContentDialog
            {
                Title = Path.GetFileName(entryName),
                Content = new Image { Source = imageSource, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, MaxWidth = 1000, MaxHeight = 700 },
                CloseButtonText = "Close",
                XamlRoot = Root.XamlRoot
            };
            await dialog.ShowAsync();
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Unable to preview image", exception.Message);
        }
    }

    private async Task<string?> PromptAsync(string title, string placeholder, string? initialValue = null)
    {
        var input = new TextBox { PlaceholderText = placeholder, Text = initialValue ?? "" };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = input,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? input.Text : null;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = Root.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private sealed record QuickLocation(
        string Name,
        string Path,
        string Icon,
        string? OtherPath = null,
        bool CanRemove = false,
        int? IconResourceIndex = null);
}
