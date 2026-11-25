using System.Runtime.InteropServices;
using System.Text;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;
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
                var formattedSize = size > 0 ? FileSystemService.FormatSize(size) : "";
                var interfaceType = Convert.ToString(item["InterfaceType"]) ?? "";
                var mediaType = Convert.ToString(item["MediaType"]) ?? "";
                var pnpDeviceId = Convert.ToString(item["PNPDeviceID"]) ?? "";
                var isRemovable = interfaceType.Equals("USB", StringComparison.OrdinalIgnoreCase) ||
                    mediaType.Contains("removable", StringComparison.OrdinalIgnoreCase) ||
                    pnpDeviceId.Contains("USB", StringComparison.OrdinalIgnoreCase);
                drives[index] = new PhysicalDrive
                {
                    DiskIndex = index,
                    Name = $"{index}.{mediaType}",
                    Model = $"[{index}] {model} ({formattedSize}) - {mediaType}",
                    SizeBytes = size,
                    SizeText = formattedSize,
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
            var partitionName = $"Part. {key.Partition + 1}";
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

        var networkDriveInfo = DriveInfo.GetDrives()
            .Where(drive => drive.DriveType == DriveType.Network)
            .ToDictionary(drive => drive.Name, StringComparer.OrdinalIgnoreCase);
        var networkDriveTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in networkDriveInfo.Keys)
        {
            var target = GetNetworkDriveTarget(drive.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(target))
                networkDriveTargets[drive] = target;
        }
        using (var mappedDrivesKey = Registry.CurrentUser.OpenSubKey("Network", writable: false))
        {
            foreach (var driveLetter in mappedDrivesKey?.GetSubKeyNames() ?? [])
            {
                if (driveLetter.Length != 1 || !char.IsLetter(driveLetter[0]))
                    continue;
                using var mappedDriveKey = mappedDrivesKey!.OpenSubKey(driveLetter, writable: false);
                var target = mappedDriveKey?.GetValue("RemotePath") as string;
                var drivePath = driveLetter + @":\";
                if (!string.IsNullOrWhiteSpace(target))
                    networkDriveTargets.TryAdd(drivePath, target);
                else
                    networkDriveTargets.TryAdd(drivePath, "");
            }
        }

        var networkDrivePaths = networkDriveInfo.Keys
            .Concat(networkDriveTargets.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (networkDrivePaths.Length > 0)
        {
            var networkGroup = new PhysicalDrive
            {
                DiskIndex = -1,
                Name = "Network drives",
                Model = "Network drives",
                IsNetwork = true
            };
            foreach (var drivePath in networkDrivePaths)
            {
                networkDriveInfo.TryGetValue(drivePath, out var networkDrive);
                networkDriveTargets.TryGetValue(drivePath, out var target);
                var name = drivePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var details = string.IsNullOrWhiteSpace(target) ? "Network drive" : target;
                var navigationPath = string.IsNullOrWhiteSpace(target) ? drivePath : target;
                try
                {
                    if (networkDrive is not null && networkDrive.IsReady)
                    {
                        if (!string.IsNullOrWhiteSpace(networkDrive.VolumeLabel))
                            name += $" — {networkDrive.VolumeLabel}";
                        details = $"{(string.IsNullOrWhiteSpace(target) ? "Network drive" : target)} • {networkDrive.DriveFormat} • {FileSystemService.FormatSize(networkDrive.TotalSize)}";
                    }
                    else
                    {
                        details = string.IsNullOrWhiteSpace(target)
                            ? networkDrive is null ? "??" : "!?"
                            : $"{target}{(networkDrive is null ? "" : " • !?")}";
                    }
                }
                catch (IOException)
                {
                    details = string.IsNullOrWhiteSpace(target) ? "!?" : $"{target} • !?";
                }
                catch (UnauthorizedAccessException)
                {
                    details = string.IsNullOrWhiteSpace(target) ? "!?" : $"{target} • !?";
                }

                networkGroup.Partitions.Add(new DrivePartition
                {
                    Name = name,
                    Details = details,
                    Path = navigationPath,
                    DiskIndex = -1,
                    IsNetwork = true
                });
            }
            drives.Add(networkGroup.DiskIndex, networkGroup);
        }

        return drives.Values.OrderBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? GetNetworkDriveTarget(string driveName)
    {
        var bufferLength = 512;
        var buffer = new StringBuilder(bufferLength);
        var status = WNetGetConnection(driveName, buffer, ref bufferLength);
        if (status == 234 && bufferLength > 0)
        {
            buffer = new StringBuilder(bufferLength);
            status = WNetGetConnection(driveName, buffer, ref bufferLength);
        }
        return status == 0 ? buffer.ToString() : null;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);
}
