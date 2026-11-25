using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace UI.Models;

public enum ExplorerEntryKind
{
    Directory,
    File,
    ArchiveDirectory,
    ArchiveFile
}

public sealed class ExplorerEntry : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public ExplorerEntryKind Kind { get; set; }
    public string Type { get; set; } = "";
    public string Size { get; set; } = "";
    public string Modified { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime ModifiedTime { get; set; }
    public bool IsModel { get; set; }
    public bool IsRecycleBinEntry { get; set; }
    public string PreviewBadge => IsModel ? "3D" : "";
    public int PreviewSizeLoaded { get; set; }
    private double _itemFontSize = 11;
    private string _itemFontFamily = "Segoe UI";
    private double _fileIconSize = 16;
    public double FileIconSize
    {
        get => _fileIconSize;
        set
        {
            if (_fileIconSize == value)
                return;
            _fileIconSize = value;
            OnPropertyChanged();
        }
    }
    public double ItemFontSize
    {
        get => _itemFontSize;
        set
        {
            if (_itemFontSize == value)
                return;
            _itemFontSize = value;
            OnPropertyChanged();
        }
    }
    public string ItemFontFamily
    {
        get => _itemFontFamily;
        set
        {
            if (_itemFontFamily == value)
                return;
            _itemFontFamily = value;
            OnPropertyChanged();
        }
    }
    private string _itemFontColor = "#FFE8EDF4";
    public string ItemFontColor
    {
        get => _itemFontColor;
        set
        {
            if (_itemFontColor == value)
                return;
            _itemFontColor = value;
            OnPropertyChanged();
        }
    }
    public Visibility PreviewImageVisibility => PreviewImage is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PreviewTextVisibility => PreviewText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PreviewIconVisibility => PreviewImage is null && PreviewText.Length == 0
        ? Visibility.Visible
        : Visibility.Collapsed;
    private ImageSource? _iconImage;
    private ImageSource? _previewImage;
    private string _previewText = "";
    private double _tileSize = 120;
    public ImageSource? PreviewImage
    {
        get => _previewImage;
        set
        {
            if (ReferenceEquals(_previewImage, value))
                return;
            _previewImage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PreviewImageVisibility));
            OnPropertyChanged(nameof(PreviewIconVisibility));
        }
    }
    public string PreviewText
    {
        get => _previewText;
        set
        {
            if (_previewText == value)
                return;
            _previewText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PreviewTextVisibility));
            OnPropertyChanged(nameof(PreviewIconVisibility));
        }
    }
    public double TileSize
    {
        get => _tileSize;
        set
        {
            if (_tileSize == value)
                return;
            _tileSize = value;
            OnPropertyChanged();
        }
    }
    public ImageSource? IconImage
    {
        get => _iconImage;
        set
        {
            if (ReferenceEquals(_iconImage, value))
                return;
            _iconImage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IconFallbackVisibility));
        }
    }
    public Visibility IconFallbackVisibility => IconImage is null ? Visibility.Visible : Visibility.Collapsed;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    public bool IsDirectory => Kind is ExplorerEntryKind.Directory or ExplorerEntryKind.ArchiveDirectory;
    public bool IsArchiveEntry => Kind is ExplorerEntryKind.ArchiveDirectory or ExplorerEntryKind.ArchiveFile;
    public string Icon => IsDirectory ? "\uE8B7" : Kind == ExplorerEntryKind.ArchiveFile ? "\uE7B8" : "\uE8A5";
}

public sealed class TreeItem : INotifyPropertyChanged
{
    public TreeItem(
        string name,
        string path,
        bool isArchive,
        bool isDirectory,
        string? icon = null,
        bool isComputerRoot = false,
        bool isQuickAccessEntry = false,
        string? otherPath = null,
        bool canRemove = false,
        string? shellPath = null,
        bool isRecycleBin = false)
    {
        Name = name;
        Path = path;
        IsArchive = isArchive;
        IsDirectory = isDirectory;
        _icon = icon ?? (isDirectory ? "\uE8B7" : "\uE8A5");
        IsComputerRoot = isComputerRoot;
        IsQuickAccessEntry = isQuickAccessEntry;
        OtherPath = otherPath;
        CanRemove = canRemove;
        ShellPath = shellPath;
        IsRecycleBin = isRecycleBin;
    }

    public string Name { get; }
    public string Path { get; }
    public bool IsArchive { get; }
    public bool IsDirectory { get; }
    public bool IsComputerRoot { get; }
    public bool IsQuickAccessEntry { get; }
    public string? OtherPath { get; }
    public bool CanRemove { get; }
    public string? ShellPath { get; }
    public bool IsRecycleBin { get; }
    private readonly string _icon;
    private double _fileIconSize = 16;
    private string _iconForeground = "#FF83BFFF";
    public double FileIconSize
    {
        get => _fileIconSize;
        set
        {
            if (_fileIconSize == value)
                return;
            _fileIconSize = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileIconSize)));
        }
    }
    public string IconForeground
    {
        get => _iconForeground;
        set
        {
            if (_iconForeground == value)
                return;
            _iconForeground = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconForeground)));
        }
    }
    private ImageSource? _iconImage;
    public ImageSource? IconImage
    {
        get => _iconImage;
        set
        {
            if (ReferenceEquals(_iconImage, value))
                return;
            _iconImage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconImage)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconFallbackVisibility)));
        }
    }
    public Visibility IconFallbackVisibility => IconImage is null ? Visibility.Visible : Visibility.Collapsed;
    public string Icon => _icon;
    public event PropertyChangedEventHandler? PropertyChanged;
    public override string ToString() => Name;
}

public sealed class PhysicalDrive
{
    public int DiskIndex { get; set; }
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public long SizeBytes { get; set; }
    public string SizeText { get; set; } = "";
    public bool IsRemovable { get; set; }
    public bool IsNetwork { get; set; }
    public List<DrivePartition> Partitions { get; } = [];
}

public sealed class DrivePartition : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string Details { get; set; } = "";
    public string? Path { get; set; }
    public string RawDevicePath { get; set; } = "";
    public int DiskIndex { get; set; }
    public bool IsRemovable { get; set; }
    public bool IsNetwork { get; set; }
    public long ByteOffset { get; set; }
    public long ByteLength { get; set; }
    public bool HasPath => !string.IsNullOrEmpty(Path);
    public bool CanReadRaw => !string.IsNullOrEmpty(RawDevicePath) && ByteLength > 0;
    private ImageSource? _iconImage;
    private double _fileIconSize = 16;
    public double FileIconSize
    {
        get => _fileIconSize;
        set
        {
            if (_fileIconSize == value)
                return;
            _fileIconSize = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileIconSize)));
        }
    }
    public ImageSource? IconImage
    {
        get => _iconImage;
        set
        {
            if (ReferenceEquals(_iconImage, value))
                return;
            _iconImage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconImage)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconFallbackVisibility)));
        }
    }
    public Visibility IconFallbackVisibility => IconImage is null ? Visibility.Visible : Visibility.Collapsed;
    public string Icon => "\uE7F4";
    public event PropertyChangedEventHandler? PropertyChanged;
}
