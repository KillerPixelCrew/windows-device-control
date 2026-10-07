using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

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

/// <summary>Reads how Windows relates mounted volumes to the disks underneath them.</summary>
/// <remarks>
///     Synchronous, read-only drive-letter discovery. Call from a worker thread because local device
///     metadata reads can block. Native volume handles are owned and closed within each query.
/// </remarks>
public static class WindowsStorage
{
    private const uint IoctlStorageGetDeviceNumber = 0x2D1080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;

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
        var volumes = new List<StorageVolume>();
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            return volumes;
        }

        foreach (var drive in drives)
        {
            if (drive.Name.Length == 0 || drive.DriveType == DriveType.Network)
            {
                continue;
            }

            try
            {
                var ready = drive.IsReady;
                volumes.Add(new StorageVolume(
                    drive.Name,
                    DiskNumberFor(drive.Name[0]),
                    ready ? drive.VolumeLabel : "",
                    ready ? drive.TotalSize : 0,
                    ready ? drive.AvailableFreeSpace : 0,
                    ready));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return volumes;
    }

    /// <summary>The physical disk one mounted volume lives on.</summary>
    /// <param name="mountPath">
    ///     A drive-letter path, for example <c>D:\</c> or <c>D:</c>. Only its first character is used;
    ///     directory mount points, volume GUID paths and UNC paths are not resolved.
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
    /// </remarks>
    public static int DiskNumberFor(char letter)
    {
        if (!char.IsLetter(letter))
        {
            return -1;
        }

        using var volume = Kernel32.CreateFile(
            $@"\\.\{char.ToUpperInvariant(letter)}:",
            0,
            FileShareRead | FileShareWrite,
            0,
            Kernel32.OpenExisting,
            0,
            0);
        if (volume.IsInvalid)
        {
            return -1;
        }

        return DiskNumber(volume);
    }

    private static unsafe int DiskNumber(SafeFileHandle volume)
    {
        // STORAGE_DEVICE_NUMBER: DEVICE_TYPE DeviceType; ULONG DeviceNumber; ULONG PartitionNumber.
        const int recordSize = 12;
        var buffer = stackalloc byte[recordSize];
        if (!Kernel32.DeviceIoControl(volume, IoctlStorageGetDeviceNumber, 0, 0, (nint)buffer,
                recordSize, out var written, 0)
            || written < recordSize)
        {
            return -1;
        }

        return MemoryMarshal.Read<int>(new ReadOnlySpan<byte>(buffer + 4, sizeof(int)));
    }
}
