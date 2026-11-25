using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Windows.Storage;

namespace UI.Services;

public sealed class ClipboardHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.Now;
    public string Kind { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? ContentHash { get; set; }
    public string? PayloadFileName { get; set; }
    public uint? ClipboardFormatId { get; set; }
    public string? ClipboardFormatName { get; set; }
    public string? SourcePath { get; set; }
    public bool IsFolder { get; set; }
    public long ItemCount { get; set; }
    public long SourceBytes { get; set; }
    public long StoredBytes { get; set; }

    public string Details =>
        $"{CapturedAt.ToLocalTime():g}  |  {Kind}  |  {FormatBytes(IsFolder || SourcePath is not null ? SourceBytes : StoredBytes)}" +
        (IsFolder ? $"  |  {ItemCount:N0} items" : "");

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var size = Math.Max(0, (double)bytes);
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.##} {units[unit]}";
    }
}

public sealed class ClipboardHistoryService
{
    public const long MaximumHistoryBytes = 1024L * 1024 * 1024;

    private readonly string _directory;
    private readonly string _manifestPath;
    private readonly List<ClipboardHistoryEntry> _entries;

    public IReadOnlyList<ClipboardHistoryEntry> Entries => _entries;
    public long StoredBytes => _entries.Sum(entry => entry.StoredBytes);
    public string DirectoryPath => _directory;

    public ClipboardHistoryService(string directory)
    {
        _directory = directory;
        _manifestPath = Path.Combine(directory, "history.json");
        Directory.CreateDirectory(directory);
        File.SetAttributes(directory, File.GetAttributes(directory) | System.IO.FileAttributes.Hidden);
        _entries = File.Exists(_manifestPath)
            ? JsonSerializer.Deserialize<List<ClipboardHistoryEntry>>(File.ReadAllText(_manifestPath)) ?? []
            : [];
        _entries.RemoveAll(entry => entry is null);
    }

    public ClipboardHistoryEntry AddPayload(string kind, string displayName, string extension, byte[] contents)
        => AddPayload(kind, displayName, extension, contents, null, null);

    public ClipboardHistoryEntry AddNativeFormat(WindowsClipboardFormat format)
    {
        var (kind, extension) = GetFormatKind(format);
        var displayName = kind == "Blob"
            ? $"Blob — {format.Name}"
            : $"{kind} — {format.Name}";
        return AddPayload(kind, displayName, extension, format.Data, format.Id, format.Name);
    }

    private ClipboardHistoryEntry AddPayload(
        string kind,
        string displayName,
        string extension,
        byte[] contents,
        uint? clipboardFormatId,
        string? clipboardFormatName)
    {
        if (contents.LongLength > MaximumHistoryBytes)
            throw new InvalidOperationException("This clipboard item exceeds the 1 GB history limit.");

        var contentHash = Convert.ToHexString(SHA256.HashData(contents));
        var existingEntry = _entries.FirstOrDefault(entry =>
            entry.Kind == kind &&
            entry.ContentHash == contentHash &&
            entry.ClipboardFormatId == clipboardFormatId &&
            entry.ClipboardFormatName == clipboardFormatName);
        if (existingEntry is not null)
        {
            var originalOrder = _entries.ToArray();
            _entries.Remove(existingEntry);
            existingEntry.CapturedAt = DateTimeOffset.Now;
            _entries.Insert(0, existingEntry);
            try
            {
                Save();
            }
            catch
            {
                _entries.Clear();
                _entries.AddRange(originalOrder);
                throw;
            }
            return existingEntry;
        }

        var payloadFileName = $"{Guid.NewGuid():N}{extension}";
        var payloadPath = Path.Combine(_directory, payloadFileName);
        using (var stream = new FileStream(payloadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.Write(contents);

        var entry = new ClipboardHistoryEntry
        {
            Kind = kind,
            DisplayName = displayName,
            ContentHash = contentHash,
            PayloadFileName = payloadFileName,
            ClipboardFormatId = clipboardFormatId,
            ClipboardFormatName = clipboardFormatName,
            StoredBytes = contents.LongLength
        };
        var previousEntries = _entries.ToArray();
        List<ClipboardHistoryEntry> removedEntries;
        try
        {
            _entries.Insert(0, entry);
            removedEntries = EnforceSizeLimit(entry);
            Save();
        }
        catch
        {
            _entries.Clear();
            _entries.AddRange(previousEntries);
            File.Delete(payloadPath);
            throw;
        }
        foreach (var removedEntry in removedEntries)
            DeletePayload(removedEntry);
        return entry;
    }

    public async Task<IReadOnlyList<ClipboardHistoryEntry>> AddStorageItemsAsync(
        IReadOnlyList<IStorageItem> items)
        => await AddPathReferencesAsync(items
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .Select(item => (item.Path, item.Name, item.IsOfType(StorageItemTypes.Folder)))
            .ToArray());

    public async Task<IReadOnlyList<ClipboardHistoryEntry>> AddPathReferencesAsync(
        IReadOnlyList<(string Path, string Name, bool IsFolder)> items)
    {
        var added = new List<ClipboardHistoryEntry>();
        var previousEntries = _entries.ToArray();
        var reorderedExisting = false;
        foreach (var (path, name, isFolder) in items)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            var kind = isFolder ? "Folder reference" : "File reference";
            var existingEntry = _entries.FirstOrDefault(entry =>
                entry.Kind == kind && string.Equals(entry.SourcePath, path, StringComparison.OrdinalIgnoreCase));
            if (existingEntry is not null)
            {
                _entries.Remove(existingEntry);
                existingEntry.CapturedAt = DateTimeOffset.Now;
                _entries.Insert(0, existingEntry);
                reorderedExisting = true;
                continue;
            }

            var (count, bytes) = await Task.Run(() => GetSourceDetails(path, isFolder));
            var entry = new ClipboardHistoryEntry
            {
                Kind = kind,
                DisplayName = name,
                SourcePath = path,
                IsFolder = isFolder,
                ItemCount = count,
                SourceBytes = bytes,
                StoredBytes = Encoding.UTF8.GetByteCount(path) + Encoding.UTF8.GetByteCount(name) + 128
            };
            _entries.Insert(0, entry);
            added.Add(entry);
        }

        if (added.Count > 0 || reorderedExisting)
        {
            List<ClipboardHistoryEntry> removedEntries = [];
            try
            {
                foreach (var entry in added)
                    removedEntries.AddRange(EnforceSizeLimit(entry));
                Save();
            }
            catch
            {
                _entries.Clear();
                _entries.AddRange(previousEntries);
                throw;
            }
            foreach (var entry in removedEntries)
                DeletePayload(entry);
        }
        return added;
    }

    public byte[] ReadPayload(ClipboardHistoryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.PayloadFileName))
            throw new InvalidOperationException("This history entry has no saved clipboard content.");
        var path = Path.Combine(_directory, entry.PayloadFileName);
        return File.ReadAllBytes(path);
    }

    public void Delete(ClipboardHistoryEntry entry)
    {
        var previousEntries = _entries.ToArray();
        _entries.RemoveAll(candidate => candidate.Id == entry.Id);
        try
        {
            Save();
        }
        catch
        {
            _entries.Clear();
            _entries.AddRange(previousEntries);
            throw;
        }
        DeletePayload(entry);
    }

    public void Clear()
    {
        var entriesToDelete = _entries.ToArray();
        _entries.Clear();
        try
        {
            Save();
        }
        catch
        {
            _entries.AddRange(entriesToDelete);
            throw;
        }
        foreach (var entry in entriesToDelete)
            DeletePayload(entry);
    }

    private List<ClipboardHistoryEntry> EnforceSizeLimit(ClipboardHistoryEntry newEntry)
    {
        var removedEntries = new List<ClipboardHistoryEntry>();
        while (StoredBytes > MaximumHistoryBytes && _entries.Count > 0)
        {
            var oldest = _entries[^1];
            _entries.RemoveAt(_entries.Count - 1);
            removedEntries.Add(oldest);
        }

        if (!_entries.Any(entry => entry.Id == newEntry.Id))
            throw new InvalidOperationException("The clipboard entry could not fit within the history limit.");
        return removedEntries;
    }

    private void DeletePayload(ClipboardHistoryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.PayloadFileName))
            return;
        var path = Path.Combine(_directory, entry.PayloadFileName);
        if (File.Exists(path))
            File.Delete(path);
    }

    private void Save()
    {
        var temporaryPath = _manifestPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_entries));
        File.Move(temporaryPath, _manifestPath, overwrite: true);
    }

    private static (long Count, long Bytes) GetSourceDetails(string path, bool isFolder)
    {
        if (!isFolder)
            return File.Exists(path) ? (1, new FileInfo(path).Length) : (0, 0);
        if (!Directory.Exists(path))
            return (0, 0);

        long count = 0;
        long bytes = 0;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var info = new FileInfo(file);
                if ((info.Attributes & System.IO.FileAttributes.ReparsePoint) != 0)
                    continue;
                count++;
                bytes = checked(bytes + info.Length);
            }
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var info = new DirectoryInfo(child);
                if ((info.Attributes & System.IO.FileAttributes.ReparsePoint) != 0)
                    continue;
                count++;
                pending.Push(child);
            }
        }
        return (count, bytes);
    }

    private static (string Kind, string Extension) GetFormatKind(WindowsClipboardFormat format)
    {
        var name = format.Name;
        if (format.Id is 1 or 7 or 13 || name.Equals("HTML Format", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("text/plain", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Rich Text", StringComparison.OrdinalIgnoreCase))
            return (name.Contains("HTML", StringComparison.OrdinalIgnoreCase) ? "HTML" :
                name.Contains("Rich Text", StringComparison.OrdinalIgnoreCase) ? "Rich text" : "Text",
                name.Contains("HTML", StringComparison.OrdinalIgnoreCase) ? ".html" :
                name.Contains("Rich Text", StringComparison.OrdinalIgnoreCase) ? ".rtf" : ".txt");
        if (format.Id is 8 or 17 || name.Contains("PNG", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Image", StringComparison.OrdinalIgnoreCase))
            return ("Image", name.Contains("PNG", StringComparison.OrdinalIgnoreCase) ? ".png" : ".dib");
        return ("Blob", ".bin");
    }
}
