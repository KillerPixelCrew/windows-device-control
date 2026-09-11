using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WindowsDeviceControl;

/// <summary>One mounted volume and the physical disk behind it.</summary>
/// <remarks>
///     The disk number is the fact that makes this worth a contract. Windows exposes a volume by
///     its mount path and a disk by its number, and nothing in the managed surface relates the two:
///     <see cref="DriveInfo" /> knows the letter and the size, and knows nothing about which piece
///     of hardware it lives on. Two components that each enumerate storage will therefore identify
///     the same card by different handles and have no way to agree that it is the same card, which
///     is a bug that only appears once something tries to join them.
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
///     Read-only. Nothing here formats, mounts, ejects or writes: those are destructive operations
///     whose safety comes from the identity re-checks their caller performs, and putting them
///     behind a general-purpose library would separate the check from the act.
/// </remarks>
public static class WindowsStorage
{
    private const uint IoctlStorageGetDeviceNumber = 0x2D1080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    /// <summary>Every mounted volume Windows will describe, with its backing disk.</summary>
    /// <returns>
    ///     One entry per volume that could be opened, in no particular order. A volume that cannot
    ///     be queried is omitted rather than reported with invented values: a caller joining on the
    ///     disk number needs to be able to trust the ones it gets.
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

        foreach (DriveInfo drive in drives)
        {
            if (drive.Name.Length == 0)
            {
                continue;
            }

            try
            {
                bool ready = drive.IsReady;
                volumes.Add(new StorageVolume(
                    drive.Name,
                    DiskNumberFor(drive.Name[0]),
                    ready ? drive.VolumeLabel : "",
                    ready ? drive.TotalSize : 0,
                    ready ? drive.AvailableFreeSpace : 0,
                    ready));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or DriveNotFoundException)
            {
                // Not a volume this caller can act on, so not one worth reporting.
            }
        }

        return volumes;
    }

    /// <summary>The physical disk one mounted volume lives on.</summary>
    /// <param name="mountPath">The volume's mount path, for example <c>D:\</c> or <c>D:</c>.</param>
    /// <returns>The disk number, or -1 when Windows would not say.</returns>
    public static int DiskNumberFor(string mountPath) =>
        string.IsNullOrEmpty(mountPath) ? -1 : DiskNumberFor(mountPath[0]);

    /// <summary>The physical disk one drive letter lives on.</summary>
    /// <param name="letter">The drive letter, with or without case.</param>
    /// <returns>The disk number, or -1 when Windows would not say.</returns>
    /// <remarks>
    ///     The volume is opened with no access rights at all. That is deliberate and is what lets
    ///     this run unelevated: <c>IOCTL_STORAGE_GET_DEVICE_NUMBER</c> is answered from the device
    ///     object rather than the media, so a zero-access handle is enough, while asking for read
    ///     access would fail on a volume the caller has no business reading.
    /// </remarks>
    public static int DiskNumberFor(char letter)
    {
        if (!char.IsLetter(letter))
        {
            return -1;
        }

        using SafeFileHandle volume = CreateFileW(
            $@"\\.\{char.ToUpperInvariant(letter)}:",
            0,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
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
        byte* buffer = stackalloc byte[recordSize];
        if (!DeviceIoControl(volume, IoctlStorageGetDeviceNumber, IntPtr.Zero, 0, (IntPtr)buffer,
                recordSize, out uint written, IntPtr.Zero)
            || written < recordSize)
        {
            return -1;
        }

        return MemoryMarshal.Read<int>(new ReadOnlySpan<byte>(buffer + 4, sizeof(int)));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        IntPtr inBuffer,
        uint inBufferSize,
        IntPtr outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);
}
