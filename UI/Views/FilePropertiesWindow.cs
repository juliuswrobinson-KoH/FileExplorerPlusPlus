using System.Diagnostics;
using System.IO.Compression;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UI.Models;
using UI.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace UI.Views;

public sealed class FilePropertiesWindow : Window
{
    private readonly StackPanel _details = new() { Spacing = 8, Padding = new Thickness(18) };

    public FilePropertiesWindow(ExplorerEntry entry)
    {
        Title = $"Properties: {entry.Name}";
        Content = new Grid
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 29, 33, 40)),
            Children =
            {
                new ScrollViewer
                {
                    Content = _details,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                }
            }
        };
        AddProperty("Name", entry.Name);
        AddProperty("Location", entry.FullPath);
        try
        {
            AddFileSystemDetails(entry);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            AddProperty("Metadata unavailable", exception.Message);
        }

        var handle = WindowNative.GetWindowHandle(this);
        AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle))
            .Resize(new SizeInt32(560, 640));
    }

    private void AddFileSystemDetails(ExplorerEntry entry)
    {
        if (entry.IsArchiveEntry)
        {
            AddArchiveDetails(entry);
            return;
        }

        var info = entry.IsDirectory
            ? (FileSystemInfo)new DirectoryInfo(entry.FullPath)
            : new FileInfo(entry.FullPath);
        info.Refresh();
        AddProperty("Type", entry.IsDirectory ? "Folder" : entry.Type);
        AddProperty("Attributes", info.Attributes.ToString());
        AddProperty("Created", info.CreationTime.ToString("g"));
        AddProperty("Modified", info.LastWriteTime.ToString("g"));
        AddProperty("Accessed", info.LastAccessTime.ToString("g"));

        if (info is FileInfo file)
        {
            AddProperty("Size", $"{file.Length:N0} bytes ({FileSystemService.FormatSize(file.Length)})");
            AddProperty("Extension", file.Extension.Length == 0 ? "(none)" : file.Extension);
            if (string.Equals(file.Extension, ".exe", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(file.Extension, ".dll", StringComparison.OrdinalIgnoreCase))
            {
                var version = FileVersionInfo.GetVersionInfo(file.FullName);
                AddOptionalProperty("Product", version.ProductName);
                AddOptionalProperty("Company", version.CompanyName);
                AddOptionalProperty("File version", version.FileVersion);
            }
        }
    }

    private void AddArchiveDetails(ExplorerEntry entry)
    {
        var (archivePath, entryName) = FileSystemService.SplitArchivePath(entry.FullPath);
        AddProperty("Type", entry.IsDirectory ? "Folder inside ZIP archive" : entry.Type);
        AddProperty("Archive", archivePath);
        using var archive = ZipFile.OpenRead(archivePath);
        if (entry.IsDirectory)
        {
            var prefix = entryName.TrimEnd('/') + "/";
            var children = archive.Entries
                .Where(item => item.FullName.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            AddProperty("Contained entries", children.Length.ToString("N0"));
            AddProperty("Uncompressed size", $"{children.Where(item => !item.FullName.EndsWith('/')).Sum(item => item.Length):N0} bytes");
        }
        else
        {
            var archiveEntry = archive.GetEntry(entryName) ?? throw new FileNotFoundException(entryName);
            AddProperty("Uncompressed size", $"{archiveEntry.Length:N0} bytes ({FileSystemService.FormatSize(archiveEntry.Length)})");
            AddProperty("Compressed size", $"{archiveEntry.CompressedLength:N0} bytes");
            AddProperty("Modified", archiveEntry.LastWriteTime.LocalDateTime.ToString("g"));
        }
    }

    private void AddOptionalProperty(string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            AddProperty(label, value);
    }

    private void AddProperty(string label, string value)
    {
        var row = new Grid { ColumnSpacing = 14 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var labelText = new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 164, 176, 193)),
            TextWrapping = TextWrapping.Wrap
        };
        var valueText = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        Grid.SetColumn(valueText, 1);
        row.Children.Add(labelText);
        row.Children.Add(valueText);
        _details.Children.Add(row);
    }
}
