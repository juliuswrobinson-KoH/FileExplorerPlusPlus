using System.Globalization;
using System.IO.Compression;
using UI.Models;

namespace UI.Services;

public static class FileSystemService
{
    public static bool IsArchivePath(string path) => path.Contains('|');

    public static string ArchivePath(string archiveFile, string entryPath = "") =>
        $"{archiveFile}|{entryPath.Replace('\\', '/').Trim('/')}";

    public static (string Archive, string Entry) SplitArchivePath(string path)
    {
        var separator = path.IndexOf('|');
        return (path[..separator], path[(separator + 1)..].Trim('/'));
    }

    public static IEnumerable<ExplorerEntry> GetEntries(string path)
    {
        return IsArchivePath(path) ? GetArchiveEntries(path) : GetFileSystemEntries(path);
    }

    private static IEnumerable<ExplorerEntry> GetFileSystemEntries(string path)
    {
        var entries = new List<ExplorerEntry>();
        foreach (var directory in Directory.EnumerateDirectories(path))
        {
            var info = new DirectoryInfo(directory);
            entries.Add(new ExplorerEntry
            {
                Name = info.Name,
                FullPath = info.FullName,
                Kind = ExplorerEntryKind.Directory,
                Type = "Folder",
                Modified = info.LastWriteTime.ToString("g", CultureInfo.CurrentCulture),
                ModifiedTime = info.LastWriteTime
            });
        }

        foreach (var file in Directory.EnumerateFiles(path))
        {
            var info = new FileInfo(file);
            entries.Add(new ExplorerEntry
            {
                Name = info.Name,
                FullPath = info.FullName,
                Kind = ExplorerEntryKind.File,
                Type = string.IsNullOrEmpty(info.Extension) ? "File" : $"{info.Extension.TrimStart('.').ToUpperInvariant()} file",
                Size = FormatSize(info.Length),
                Modified = info.LastWriteTime.ToString("g", CultureInfo.CurrentCulture),
                SizeBytes = info.Length,
                ModifiedTime = info.LastWriteTime,
                IsModel = PreviewFormatRegistry.IsModel(info.Name)
            });
        }

        return entries.OrderByDescending(item => item.IsDirectory).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase);
    }

    private static IEnumerable<ExplorerEntry> GetArchiveEntries(string path)
    {
        var (archiveFile, currentDirectory) = SplitArchivePath(path);
        using var archive = ZipFile.OpenRead(archiveFile);
        var prefix = currentDirectory.Length == 0 ? "" : currentDirectory.TrimEnd('/') + "/";
        var entries = new Dictionary<string, ExplorerEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in archive.Entries)
        {
            var name = item.FullName.Replace('\\', '/');
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || name.Length == prefix.Length)
                continue;

            var remainder = name[prefix.Length..];
            var separator = remainder.IndexOf('/');
            if (separator >= 0)
            {
                var childName = remainder[..separator];
                var childPath = string.IsNullOrEmpty(prefix) ? childName : prefix + childName;
                entries.TryAdd(childName, new ExplorerEntry
                {
                    Name = childName,
                    FullPath = ArchivePath(archiveFile, childPath),
                    Kind = ExplorerEntryKind.ArchiveDirectory,
                    Type = "Folder"
                });
            }
            else if (!string.IsNullOrEmpty(remainder))
            {
                var isDirectory = item.FullName.EndsWith('/');
                entries[remainder] = new ExplorerEntry
                {
                    Name = remainder,
                    FullPath = ArchivePath(archiveFile, string.IsNullOrEmpty(prefix) ? remainder : prefix + remainder),
                    Kind = isDirectory ? ExplorerEntryKind.ArchiveDirectory : ExplorerEntryKind.ArchiveFile,
                    Type = isDirectory ? "Folder" : $"{Path.GetExtension(remainder).TrimStart('.').ToUpperInvariant()} file",
                    Size = isDirectory ? "" : FormatSize(item.Length),
                    Modified = item.LastWriteTime.LocalDateTime.ToString("g", CultureInfo.CurrentCulture),
                    SizeBytes = isDirectory ? 0 : item.Length,
                    ModifiedTime = item.LastWriteTime.LocalDateTime,
                    IsModel = !isDirectory && PreviewFormatRegistry.IsModel(remainder)
                };
            }
        }

        return entries.Values.OrderByDescending(item => item.IsDirectory).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase);
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.#} {units[unit]}";
    }

    public static string GetParent(string path)
    {
        if (IsArchivePath(path))
        {
            var (archive, entry) = SplitArchivePath(path);
            var slash = entry.LastIndexOf('/');
            return ArchivePath(archive, slash < 0 ? "" : entry[..slash]);
        }

        var parent = Directory.GetParent(path)?.FullName;
        return parent ?? path;
    }

    public static void CopyEntryTo(string source, string destinationDirectory)
    {
        if (!IsArchivePath(source))
        {
            var destination = Path.Combine(destinationDirectory, Path.GetFileName(source));
            if (Directory.Exists(source))
                CopyDirectory(source, destination);
            else
                File.Copy(source, destination, overwrite: false);
            return;
        }

        var (archiveFile, entryName) = SplitArchivePath(source);
        using var archive = ZipFile.OpenRead(archiveFile);
        var file = archive.GetEntry(entryName);
        if (file is not null && !file.FullName.EndsWith('/'))
        {
            file.ExtractToFile(Path.Combine(destinationDirectory, Path.GetFileName(entryName)), overwrite: false);
            return;
        }

        var prefix = entryName.TrimEnd('/') + "/";
        var targetRoot = Path.Combine(destinationDirectory, Path.GetFileName(entryName.TrimEnd('/')));
        Directory.CreateDirectory(targetRoot);
        foreach (var entry in archive.Entries.Where(item => item.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            var relativePath = entry.FullName[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrEmpty(relativePath))
                continue;
            var targetPath = Path.Combine(targetRoot, relativePath);
            if (entry.FullName.EndsWith('/'))
                Directory.CreateDirectory(targetPath);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                entry.ExtractToFile(targetPath, overwrite: false);
            }
        }
    }

    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
