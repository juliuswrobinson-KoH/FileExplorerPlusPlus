using System.Management;
using System.Text.RegularExpressions;
using UI.Models;

namespace UI.Services;

public static partial class PhysicalDriveService
{
    [GeneratedRegex(@"Disk #(?<disk>\d+), Partition #(?<partition>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PartitionReferenceRegex();

    public static IReadOnlyList<PhysicalDrive> GetDrives()
    {
        var drives = new Dictionary<int, PhysicalDrive>();
        var partitions = new Dictionary<(int Disk, int Partition), List<string>>();
        var partitionSizes = new Dictionary<(int Disk, int Partition), string>();
        var partitionRanges = new Dictionary<(int Disk, int Partition), (long Offset, long Length)>();

        var removableDisks = new HashSet<int>();
        using (var searcher = new ManagementObjectSearcher("SELECT Index, Model, Size, InterfaceType, MediaType, PNPDeviceID FROM Win32_DiskDrive"))
        using (var results = searcher.Get())
        {
            foreach (ManagementObject item in results)
            {
                var index = Convert.ToInt32(item["Index"]);
                var model = Convert.ToString(item["Model"])?.Trim() ?? "Physical disk";
                var size = item["Size"] is null ? 0L : Convert.ToInt64(item["Size"]);
                var interfaceType = Convert.ToString(item["InterfaceType"]) ?? "";
                var mediaType = Convert.ToString(item["MediaType"]) ?? "";
                var pnpDeviceId = Convert.ToString(item["PNPDeviceID"]) ?? "";
                var isRemovable = interfaceType.Equals("USB", StringComparison.OrdinalIgnoreCase) ||
                    mediaType.Contains("removable", StringComparison.OrdinalIgnoreCase) ||
                    pnpDeviceId.Contains("USB", StringComparison.OrdinalIgnoreCase);
                drives[index] = new PhysicalDrive
                {
                    DiskIndex = index,
                    Name = $"Physical drive {index}",
                    Model = model,
                    SizeBytes = size,
                    SizeText = size > 0 ? FileSystemService.FormatSize(size) : "",
                    IsRemovable = isRemovable
                };
                if (isRemovable)
                    removableDisks.Add(index);
            }
        }

        using (var searcher = new ManagementObjectSearcher("SELECT DeviceID, DiskIndex, Index, Size, StartingOffset FROM Win32_DiskPartition"))
        using (var results = searcher.Get())
        {
            foreach (ManagementObject item in results)
            {
                var diskIndex = Convert.ToInt32(item["DiskIndex"]);
                var partitionIndex = Convert.ToInt32(item["Index"]);
                var key = (diskIndex, partitionIndex);
                partitions.TryAdd(key, []);
                var size = item["Size"] is null ? 0L : Convert.ToInt64(item["Size"]);
                partitionSizes[key] = FileSystemService.FormatSize(size);
                var offset = item["StartingOffset"] is null ? 0L : Convert.ToInt64(item["StartingOffset"]);
                partitionRanges[key] = (offset, size);
            }
        }

        using (var searcher = new ManagementObjectSearcher("SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition"))
        using (var results = searcher.Get())
        {
            foreach (ManagementObject item in results)
            {
                var reference = Convert.ToString(item["Antecedent"]) ?? "";
                var match = PartitionReferenceRegex().Match(reference);
                if (!match.Success)
                    continue;

                var key = (int.Parse(match.Groups["disk"].Value), int.Parse(match.Groups["partition"].Value));
                if (!partitions.TryGetValue(key, out var volumes))
                    continue;
                var volume = Convert.ToString(item["Dependent"]) ?? "";
                var volumeMatch = Regex.Match(volume, @"DeviceID=""(?<drive>[A-Za-z]:)""", RegexOptions.IgnoreCase);
                if (volumeMatch.Success)
                    volumes.Add(volumeMatch.Groups["drive"].Value.ToUpperInvariant());
            }
        }

        foreach (var (key, volumePaths) in partitions)
        {
            if (!drives.TryGetValue(key.Disk, out var drive))
                continue;
            var partitionName = $"Partition {key.Partition + 1}";
            var size = partitionSizes[key];
            if (volumePaths.Count == 0)
            {
                drive.Partitions.Add(new DrivePartition
                {
                    Name = partitionName,
                    Details = $"{size}",
                    DiskIndex = key.Disk,
                    IsRemovable = removableDisks.Contains(key.Disk),
                    RawDevicePath = $@"\\.\PhysicalDrive{key.Disk}",
                    ByteOffset = partitionRanges[key].Offset,
                    ByteLength = partitionRanges[key].Length
                });
                continue;
            }

            foreach (var volumePath in volumePaths)
            {
                var info = new DriveInfo(volumePath + Path.DirectorySeparatorChar);
                var label = info.IsReady && !string.IsNullOrWhiteSpace(info.VolumeLabel) ? $" — {info.VolumeLabel}" : "";
                var details = info.IsReady
                    ? $"{info.DriveFormat} • {FileSystemService.FormatSize(info.TotalSize)}"
                    : $"{size} • Not ready";
                drive.Partitions.Add(new DrivePartition
                {
                    Name = $"{volumePath}{label}",
                    Details = details,
                    Path = volumePath + Path.DirectorySeparatorChar,
                    DiskIndex = key.Disk,
                    IsRemovable = removableDisks.Contains(key.Disk),
                    RawDevicePath = $@"\\.\PhysicalDrive{key.Disk}",
                    ByteOffset = partitionRanges[key].Offset,
                    ByteLength = partitionRanges[key].Length
                });
            }
        }

        return drives.Values.OrderBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
