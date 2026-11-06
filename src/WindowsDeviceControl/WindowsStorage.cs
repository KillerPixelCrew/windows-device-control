using System;
using System.Collections.Generic;
using System.IO;

namespace WindowsDeviceControl;

/// <summary>One mounted volume and the physical disk behind it.</summary>
/// <remarks>
///     Disk numbers identify the current Windows enumeration, not persistent hardware identity.
///     Re-read after device arrival/removal and never group entries whose disk number is -1.
/// </remarks>
/// <param name="MountPath">Where the volume is mounted, for example <c>D:\</c>.</param>
/// <param name="DiskNumber">
///     The physical disk this volume lives on, or -1 when Windows would not say. Volumes sharing a
///     number are partitions of one device.
/// </param>
/// <param name="Label">The volume label, empty when it has none or could not be read.</param>
/// <param name="CapacityBytes">The volume's size, or zero when it is not ready.</param>
/// <param name="FreeBytes">Free space on it, or zero when it is not ready.</param>
/// <param name="Ready">Whether media is present and the volume can be read.</param>
public sealed record StorageVolume(
    string MountPath,
    int DiskNumber,
    string Label,
    long CapacityBytes,
    long FreeBytes,
    bool Ready);

/// <summary>
///     How Windows relates volumes to the disks underneath them, and the storage device primitives an
///     eject, card or format flow needs: disk identity, capacity and partitions, PnP and media eject,
///     volume GUID paths and labels.
/// </summary>
/// <remarks>
///     Synchronous. Call from a worker thread because local device metadata reads can block. Volume
///     handles opened inside a query are closed within it; handles returned by an <c>Open*</c> method
///     belong to the caller. Nothing here formats or mounts, and the caller decides which disk is safe
///     to eject or write.
/// </remarks>
public static partial class WindowsStorage
{
    private const uint IoctlStorageGetDeviceNumber = 0x2D1080;

    /// <summary>Every mounted volume Windows will describe, with its backing disk.</summary>
    /// <returns>
    ///     One entry per local drive-letter volume whose metadata could be read, in no particular order.
    ///     A failed disk-number lookup retains the entry with DiskNumber -1; never join such entries as
    ///     though they identify one disk. A not-ready drive has an empty label and zero capacity/free
    ///     space. Metadata IO or access failures omit that drive, and an IO failure enumerating drives
    ///     returns an empty list. Network drives are excluded to avoid disconnected-share timeouts.
    /// </returns>
    public static IReadOnlyList<StorageVolume> DescribeVolumes()
    {
        return WalkDrives<StorageVolume>((drive, volumes) =>
        {
            if (drive.DriveType == DriveType.Network)
            {
                return;
            }

            var ready = drive.IsReady;
            volumes.Add(new StorageVolume(
                drive.Name,
                DiskNumberFor(drive.Name[0]),
                ready ? drive.VolumeLabel : "",
                ready ? drive.TotalSize : 0,
                ready ? drive.AvailableFreeSpace : 0,
                ready));
        });
    }

    /// <summary>The physical disk one mounted volume lives on.</summary>
    /// <param name="mountPath">
    ///     A drive-letter path, for example <c>D:\</c> or <c>D:</c>. Only its first character is used;
    ///     directory mount points, volume GUID paths and UNC paths are not resolved here (see
    ///     <see cref="DiskOfVolume" /> for those).
    /// </param>
    /// <returns>The disk number, or -1 when Windows would not say.</returns>
    public static int DiskNumberFor(string mountPath)
    {
        return string.IsNullOrEmpty(mountPath) ? -1 : DiskNumberFor(mountPath[0]);
    }

    /// <summary>The physical disk one drive letter lives on.</summary>
    /// <param name="letter">The drive letter, with or without case.</param>
    /// <returns>The disk number, or -1 when Windows would not say.</returns>
    /// <remarks>
    ///     Uses a zero-access volume handle for <c>IOCTL_STORAGE_GET_DEVICE_NUMBER</c>, so the query
    ///     does not require media-read access or elevation. An invalid letter or failed lookup yields -1.
    ///     The device type is not checked, as the drive-letter walk has always reported it.
    /// </remarks>
    public static int DiskNumberFor(char letter)
    {
        if (!char.IsLetter(letter))
        {
            return -1;
        }

        using var volume = OpenVolumeForQuery(char.ToUpperInvariant(letter));
        return !volume.IsInvalid && TryGetDeviceNumber(volume, out _, out var disk) ? disk : -1;
    }

    /// <summary>The physical disk a volume lives on, when that device is a disk.</summary>
    /// <param name="path">
    ///     A volume or disk device path: a volume interface path from <see cref="ListVolumeInterfaces" />,
    ///     a disk interface path from <see cref="ListDiskInterfaces" />, or a volume GUID root from
    ///     <see cref="TryGetVolumeGuidPath" />.
    /// </param>
    /// <returns>
    ///     The disk number, or -1 when the path cannot be opened for query, the device number cannot be
    ///     read, or the device is not a disk (<see cref="FileDeviceDisk" />).
    /// </returns>
    /// <remarks>Opens a zero-access handle, closed before returning.</remarks>
    public static int DiskOfVolume(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var volume = OpenVolumeForQueryPath(path);
        return !volume.IsInvalid && TryGetDeviceNumber(volume, out var type, out var disk) && type == FileDeviceDisk
            ? disk
            : -1;
    }

    /// <summary>The volume interface paths Windows currently exposes on one disk, mounted or not.</summary>
    /// <param name="disk">The physical disk number.</param>
    /// <returns>
    ///     Every volume interface whose device is that disk, in enumeration order; empty when there is
    ///     none or enumeration fails. A blank partition with no drive letter is included.
    /// </returns>
    public static IReadOnlyList<string> VolumesOnDisk(int disk)
    {
        return disk < 0 ? [] : VolumesOnDisk(disk, ListVolumeInterfaces(), DiskOfVolume);
    }

    /// <summary>The volume paths whose disk is <paramref name="disk" />, in the order given.</summary>
    /// <param name="disk">The physical disk number; a negative number matches nothing.</param>
    /// <param name="volumes">The volume interface paths to consider.</param>
    /// <param name="diskOf">Reads one path's disk number, -1 when unknown.</param>
    /// <returns>The matching paths.</returns>
    internal static IReadOnlyList<string> VolumesOnDisk(int disk, IEnumerable<string> volumes,
        Func<string, int> diskOf)
    {
        List<string> matching = [];
        if (disk < 0)
        {
            return matching;
        }

        foreach (var path in volumes)
        {
            if (diskOf(path) == disk)
            {
                matching.Add(path);
            }
        }

        return matching;
    }

    /// <summary>
    ///     The one local drive-letter walk behind <see cref="DescribeVolumes" /> and
    ///     <see cref="MountedVolumes" />.
    /// </summary>
    /// <param name="read">Reads one drive and adds what it found, or nothing to skip it.</param>
    /// <returns>
    ///     The entries read. A drive whose metadata throws an IO or access failure is skipped, and an IO
    ///     failure enumerating drives returns an empty list.
    /// </returns>
    private static List<T> WalkDrives<T>(Action<DriveInfo, List<T>> read)
    {
        List<T> entries = [];
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            return entries;
        }

        foreach (var drive in drives)
        {
            if (drive.Name.Length == 0)
            {
                continue;
            }

            try
            {
                read(drive, entries);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A volume that vanished mid-walk or that may not be queried is not one a caller can act on.
            }
        }

        return entries;
    }
}
