using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WindowsDeviceControl;

/// <summary>
///     Storage device primitives for eject, card and format flows: volume-to-disk mapping
///     (<c>IOCTL_STORAGE_GET_DEVICE_NUMBER</c>), the hotplug classification that separates a USB device
///     from a built-in card reader (<c>IOCTL_STORAGE_GET_HOTPLUG_INFO</c>), disk identity, capacity and
///     partition layout, the PnP device eject (<c>CM_Request_Device_EjectW</c>), the media-level
///     dismount sequence (<c>FSCTL_LOCK_VOLUME</c>, <c>FSCTL_DISMOUNT_VOLUME</c>,
///     <c>IOCTL_STORAGE_EJECT_MEDIA</c>) and volume labels.
/// </summary>
/// <remarks>
///     Everything here is cfgmgr32, kernel32 and one user32 broadcast: no COM and no WMI. Devnode
///     discovery goes through the cfgmgr32 interface list rather than SetupAPI's devinfo sets: same
///     data, no variable-size detail-struct marshalling. Each call is issued once; the caller decides
///     which flow is safe, in what order, and how to report a refusal. Methods that return false leave
///     the native error on the calling thread for <see cref="LastWin32Error" />.
/// </remarks>
public static unsafe partial class WindowsStorage
{
    /// <summary>CONFIGRET success.</summary>
    public const int CrSuccess = 0;

    /// <summary>CONFIGRET: the eject was vetoed; the veto type and name say why.</summary>
    public const int CrRemoveVetoed = 0x17;

    /// <summary><c>STORAGE_DEVICE_NUMBER.DeviceType</c> for a disk (<c>FILE_DEVICE_DISK</c>).</summary>
    public const int FileDeviceDisk = 0x7;

    /// <summary><c>STORAGE_BUS_TYPE</c>: the disk sits in a native SD host slot.</summary>
    public const int BusTypeSd = 12;

    /// <summary><c>STORAGE_BUS_TYPE</c>: eMMC/MMC bus.</summary>
    public const int BusTypeMmc = 13;

    /// <summary>
    ///     <c>STORAGE_BUS_TYPE</c>: USB-attached (sticks, external drives, and USB-bridged card readers
    ///     alike).
    /// </summary>
    public const int BusTypeUsb = 7;

    /// <summary>Size of a <c>STORAGE_DEVICE_NUMBER</c> record.</summary>
    internal const int DeviceNumberRecordSize = 12;

    /// <summary>Size of a <c>STORAGE_HOTPLUG_INFO</c> record.</summary>
    internal const int HotplugRecordSize = 8;

    /// <summary>
    ///     <c>DRIVE_LAYOUT_INFORMATION_EX</c> geometry: entries start after the 48-byte header, one
    ///     <c>PARTITION_INFORMATION_EX</c> (144 bytes) each.
    /// </summary>
    internal const int DriveLayoutHeaderSize = 48;

    /// <summary>The size of one <c>PARTITION_INFORMATION_EX</c> record.</summary>
    internal const int PartitionRecordSize = 144;

    /// <summary>
    ///     CONFIGRET: the buffer sized by the preceding size query no longer fits, because the device set
    ///     changed in between. Re-query and retry.
    /// </summary>
    private const int CrBufferSmall = 0x1A;

    /// <summary>How often the interface-list size query and list call are retried as a pair.</summary>
    private const int InterfaceListAttempts = 3;

    /// <summary><c>CM_DEVCAP_REMOVABLE</c>: the devnode itself can be ejected.</summary>
    private const uint DevCapRemovable = 0x4;

    // CM_DRP_* registry properties (SPDRP value + 1).
    private const uint DrpDeviceDesc = 0x01;
    private const uint DrpFriendlyName = 0x0D;
    private const uint DrpCapabilities = 0x10;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = Kernel32.FileShareRead | Kernel32.FileShareWrite;

    private const uint IoctlStorageGetHotplugInfo = 0x2D0C14;
    private const uint IoctlStorageQueryProperty = 0x2D1400;
    private const uint IoctlDiskGetLengthInfo = 0x7405C;
    private const uint IoctlDiskGetDriveGeometryEx = 0x700A0;
    private const uint IoctlDiskGetDriveLayoutEx = 0x70050;
    private const uint FsctlLockVolume = 0x090018;
    private const uint FsctlDismountVolume = 0x090020;
    private const uint IoctlStorageMediaRemoval = 0x2D4804;
    private const uint IoctlStorageEjectMedia = 0x2D4808;

    /// <summary>The fixed header size of <c>STORAGE_DEVICE_DESCRIPTOR</c>.</summary>
    private const int DeviceDescriptorHeaderSize = 36;

    /// <summary><c>DEVPKEY_Device_InstanceId</c>.</summary>
    private static readonly DevPropKey DevicePropertyInstanceId = new()
    {
        Fmtid = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"),
        Pid = 256
    };

    /// <summary>
    ///     <c>GUID_DEVINTERFACE_DISK</c>: every present disk exposes one of these interfaces; enumerating
    ///     them is how a volume's device number becomes a devnode.
    /// </summary>
    private static readonly Guid DiskInterfaceGuid = new("53f56307-b6bf-11d0-94f2-00a0c91efb8b");

    /// <summary><c>GUID_DEVINTERFACE_VOLUME</c>: every volume the volume manager exposes.</summary>
    private static readonly Guid VolumeInterfaceGuid = new("53f5630d-b6bf-11d0-94f2-00a0c91efb8b");

    /// <summary>GPT partition-type GUID for Linux filesystem data, such as an ext4 partition.</summary>
    internal static Guid LinuxFilesystemGuid { get; } = new("0fc63daf-8483-4772-8e79-3d69d8477de4");

    /// <summary>Decodes a <c>STORAGE_DEVICE_NUMBER</c> buffer.</summary>
    /// <param name="buffer">At least <see cref="DeviceNumberRecordSize" /> bytes.</param>
    /// <returns>Native device type, disk number and partition number from the fixed-layout record.</returns>
    internal static (int DeviceType, int DeviceNumber, int PartitionNumber) ReadDeviceNumber(
        ReadOnlySpan<byte> buffer)
    {
        return (BitConverter.ToInt32(buffer), BitConverter.ToInt32(buffer[4..]), BitConverter.ToInt32(buffer[8..]));
    }

    /// <summary>
    ///     Decodes a <c>STORAGE_HOTPLUG_INFO</c> buffer: whether the media is removable from the device,
    ///     and whether the device itself is hot-pluggable.
    /// </summary>
    /// <param name="buffer">At least <see cref="HotplugRecordSize" /> bytes.</param>
    /// <returns>Independent removable-media and hot-pluggable-device flags.</returns>
    internal static (bool MediaRemovable, bool DeviceHotplug) ReadHotplugInfo(ReadOnlySpan<byte> buffer)
    {
        return (buffer[4] != 0, buffer[6] != 0);
    }

    /// <summary>Opens a volume for attribute queries only: zero access needs no privilege and touches no media.</summary>
    /// <param name="letter">The drive letter.</param>
    /// <returns>Caller-owned handle, possibly invalid on failure; inspect IsInvalid and dispose after use.</returns>
    public static SafeFileHandle OpenVolumeForQuery(char letter)
    {
        return Kernel32.CreateFile($@"\\.\{letter}:", 0, FileShareReadWrite, 0, Kernel32.OpenExisting, 0, 0);
    }

    /// <summary>Opens a volume for the lock, dismount and eject sequence.</summary>
    /// <param name="letter">The drive letter.</param>
    /// <returns>Caller-owned handle, possibly invalid on failure; inspect IsInvalid and dispose after use.</returns>
    public static SafeFileHandle OpenVolumeForEject(char letter)
    {
        return Kernel32.CreateFile($@"\\.\{letter}:", GenericRead | GenericWrite, FileShareReadWrite, 0,
            Kernel32.OpenExisting, 0, 0);
    }

    /// <summary>Opens the exact enumerated disk interface for a media eject request.</summary>
    /// <param name="path">
    ///     Exact currently enumerated device-interface path; this method does not revalidate its physical
    ///     identity.
    /// </param>
    /// <returns>Caller-owned handle; inspect IsInvalid and LastWin32Error before use, then dispose it.</returns>
    public static SafeFileHandle OpenDeviceForMediaEject(string path)
    {
        return Kernel32.CreateFile(path, GenericRead | GenericWrite, FileShareReadWrite, 0, Kernel32.OpenExisting,
            0, 0);
    }

    /// <summary>Opens a device-interface path for attribute queries only.</summary>
    /// <param name="path">A path from <see cref="ListDiskInterfaces" /> or <see cref="ListVolumeInterfaces" />.</param>
    /// <returns>Caller-owned handle, possibly invalid on failure; inspect IsInvalid and dispose after use.</returns>
    public static SafeFileHandle OpenVolumeForQueryPath(string path)
    {
        return Kernel32.CreateFile(path, 0, FileShareReadWrite, 0, Kernel32.OpenExisting, 0, 0);
    }

    /// <summary>Opens a physical disk for attribute queries only.</summary>
    /// <param name="number">The disk number.</param>
    /// <returns>Caller-owned handle, possibly invalid on failure; inspect IsInvalid and dispose after use.</returns>
    public static SafeFileHandle OpenDiskForQuery(int number)
    {
        return Kernel32.CreateFile($@"\\.\PhysicalDrive{number}", 0, FileShareReadWrite, 0, Kernel32.OpenExisting,
            0, 0);
    }

    /// <summary>
    ///     Opens a physical disk for reading; some queries (<c>IOCTL_DISK_GET_LENGTH_INFO</c>) demand read
    ///     access.
    /// </summary>
    /// <param name="number">The disk number.</param>
    /// <returns>Caller-owned handle, possibly invalid on failure; inspect IsInvalid and dispose after use.</returns>
    public static SafeFileHandle OpenDiskForRead(int number)
    {
        return Kernel32.CreateFile($@"\\.\PhysicalDrive{number}", GenericRead, FileShareReadWrite, 0,
            Kernel32.OpenExisting, 0, 0);
    }

    /// <summary>Reads which physical disk a volume lives on.</summary>
    /// <param name="volume">An open volume handle.</param>
    /// <param name="deviceType">The <c>FILE_DEVICE_*</c> type of the underlying device.</param>
    /// <param name="deviceNumber">The physical disk number.</param>
    /// <returns>True for a complete native record; false sets deviceType to zero and deviceNumber to -1.</returns>
    public static bool TryGetDeviceNumber(SafeFileHandle volume, out int deviceType, out int deviceNumber)
    {
        var buffer = stackalloc byte[DeviceNumberRecordSize];
        if (!Kernel32.DeviceIoControl(volume, IoctlStorageGetDeviceNumber, 0, 0, (nint)buffer,
                DeviceNumberRecordSize, out var written, 0)
            || written < DeviceNumberRecordSize)
        {
            deviceType = 0;
            deviceNumber = -1;
            return false;
        }

        (deviceType, deviceNumber, _) = ReadDeviceNumber(new ReadOnlySpan<byte>(buffer, DeviceNumberRecordSize));
        return true;
    }

    /// <summary>
    ///     Reads the disk's hotplug facts: the classification that decides between a device-level and a
    ///     media-level eject.
    /// </summary>
    /// <param name="disk">An open physical-disk handle.</param>
    /// <param name="mediaRemovable">Whether the media can leave the device.</param>
    /// <param name="deviceHotplug">Whether the device itself is hot-pluggable.</param>
    /// <returns>
    ///     True for a complete record; false clears both flags and must not be treated as verified fixed
    ///     media.
    /// </returns>
    public static bool TryGetHotplugInfo(SafeFileHandle disk, out bool mediaRemovable, out bool deviceHotplug)
    {
        var buffer = stackalloc byte[HotplugRecordSize];
        if (!Kernel32.DeviceIoControl(disk, IoctlStorageGetHotplugInfo, 0, 0, (nint)buffer, HotplugRecordSize,
                out var written, 0)
            || written < HotplugRecordSize)
        {
            mediaRemovable = false;
            deviceHotplug = false;
            return false;
        }

        (mediaRemovable, deviceHotplug) = ReadHotplugInfo(new ReadOnlySpan<byte>(buffer, HotplugRecordSize));
        return true;
    }

    /// <summary>
    ///     Every local Fixed or Removable drive letter whose volume answered the device-number query.
    /// </summary>
    /// <returns>
    ///     A new snapshot of resolvable volume identities and mount points. No readiness or device-type
    ///     filtering is applied: a letterless or not-ready card is meaningful to some callers. A drive that
    ///     vanishes mid-walk or may not be queried is skipped.
    /// </returns>
    public static List<MountedVolume> MountedVolumes()
    {
        var result = new List<MountedVolume>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                {
                    continue;
                }

                var letter = char.ToUpperInvariant(drive.Name[0]);
                using var volume = OpenVolumeForQuery(letter);
                if (volume.IsInvalid || !TryGetDeviceNumber(volume, out var type, out var disk))
                {
                    continue;
                }

                var ready = drive.IsReady;
                result.Add(new MountedVolume(letter, disk, type, ready, drive.DriveType, ready ? drive.TotalSize : 0));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A volume that vanished mid-scan or that may not even be queried is not one a caller can act on.
            }
        }

        return result;
    }

    /// <summary>Lists the device-interface paths of every present disk.</summary>
    /// <returns>Present disk-interface paths, or an empty list when configuration-manager enumeration fails.</returns>
    /// <remarks>
    ///     The size query and the list call are a documented race: a disk arriving or leaving between them
    ///     makes the list call report <c>CR_BUFFER_SMALL</c>, so the pair is retried with a freshly queried
    ///     size before giving up.
    /// </remarks>
    public static string[] ListDiskInterfaces()
    {
        return ListInterfaces(DiskInterfaceGuid);
    }

    /// <summary>Lists the device-interface paths of every volume the volume manager exposes, mounted or not.</summary>
    /// <returns>Present volume-interface paths, or an empty list when configuration-manager enumeration fails.</returns>
    /// <remarks>
    ///     Each path opens with <see cref="OpenVolumeForQueryPath" /> for a device-number query, which maps
    ///     it back to its disk. The list call is retried as <see cref="ListDiskInterfaces" /> describes.
    /// </remarks>
    public static string[] ListVolumeInterfaces()
    {
        return ListInterfaces(VolumeInterfaceGuid);
    }

    /// <summary>Resolves a device-interface path to its devnode.</summary>
    /// <param name="interfacePath">A path from <see cref="ListDiskInterfaces" />.</param>
    /// <param name="devInst">The devnode handle.</param>
    /// <returns>True when a nonempty interface instance ID resolves to a current devnode; otherwise false.</returns>
    public static bool TryGetDevNode(string interfacePath, out uint devInst)
    {
        devInst = 0;
        var size = 1024u;
        var buffer = stackalloc char[512];
        if (CM_Get_Device_Interface_PropertyW(interfacePath, in DevicePropertyInstanceId, out _, buffer, ref size,
                0) != CrSuccess)
        {
            return false;
        }

        // size is the byte count cfgmgr32 wrote back (buffer holds 512 chars).
        var instanceId = NativeText.ReadFixed(buffer, (int)(Math.Min(size, 1024u) / 2));
        return instanceId.Length > 0 && CM_Locate_DevNodeW(out devInst, instanceId, 0) == CrSuccess;
    }

    /// <summary>Reads the devnode's device instance path, the stable identity a list row keys on.</summary>
    /// <param name="devInst">The devnode.</param>
    /// <returns>Device instance ID bounded to the native buffer, or an empty string on query failure.</returns>
    public static string GetDeviceInstanceId(uint devInst)
    {
        // CM_Get_Device_IDW is capped at MAX_DEVICE_ID_LEN (200); an id that exactly fills the buffer
        // carries no terminator, so it is decoded bounded.
        var buffer = stackalloc char[200];
        return CM_Get_Device_IDW(devInst, buffer, 200, 0) == CrSuccess ? NativeText.ReadFixed(buffer, 200) : "";
    }

    /// <summary>Reads the devnode's display name: the friendly name when set, else the device description.</summary>
    /// <param name="devInst">The devnode.</param>
    /// <returns>Friendly name, device description, or an empty string when neither is available.</returns>
    public static string GetDeviceDisplayName(uint devInst)
    {
        var name = ReadDevNodeString(devInst, DrpFriendlyName);
        return name.Length > 0 ? name : ReadDevNodeString(devInst, DrpDeviceDesc);
    }

    /// <summary>
    ///     Walks from a disk devnode to the node a PnP eject should target: the first ancestor (or the disk
    ///     itself) whose capabilities say <c>CM_DEVCAP_REMOVABLE</c>.
    /// </summary>
    /// <param name="diskDevInst">The disk devnode.</param>
    /// <returns>The first removable node within four inspected levels, otherwise the immediate parent or original node.</returns>
    /// <remarks>
    ///     For USB storage that is the USB device above the USBSTOR disk; ejecting the disk node itself
    ///     commonly fails. When no ancestor claims removability, the disk's parent is the classic fallback.
    /// </remarks>
    public static uint FindEjectTarget(uint diskDevInst)
    {
        var node = diskDevInst;
        for (var depth = 0; depth < 4; depth++)
        {
            if ((GetCapabilities(node) & DevCapRemovable) != 0)
            {
                return node;
            }

            if (CM_Get_Parent(out var parent, node, 0) != CrSuccess)
            {
                break;
            }

            node = parent;
        }

        return CM_Get_Parent(out var fallback, diskDevInst, 0) == CrSuccess ? fallback : diskDevInst;
    }

    /// <summary>Reads the disk's total size in bytes.</summary>
    /// <param name="disk">A disk handle opened with read access.</param>
    /// <returns>Native byte length, or zero when the IOCTL fails or returns a short record.</returns>
    public static long GetDiskLength(SafeFileHandle disk)
    {
        long length = 0;
        return Kernel32.DeviceIoControl(disk, IoctlDiskGetLengthInfo, 0, 0, (nint)(&length), 8, out var written, 0)
               && written >= 8
            ? length
            : 0;
    }

    /// <summary>Reads capacity without requiring a privileged physical-disk read handle.</summary>
    /// <param name="disk">Borrowed disk handle with query access; it is not closed by this method.</param>
    /// <returns>Nonnegative geometry capacity in bytes, or zero when unavailable.</returns>
    public static long GetDiskCapacityForQuery(SafeFileHandle disk)
    {
        var buffer = stackalloc byte[256];
        return Kernel32.DeviceIoControl(disk, IoctlDiskGetDriveGeometryEx, 0, 0, (nint)buffer, 256,
            out var written, 0)
            ? ReadGeometryCapacity(new ReadOnlySpan<byte>(buffer, (int)Math.Min(written, 256)))
            : 0;
    }

    /// <summary>Decodes <c>DISK_GEOMETRY_EX.DiskSize</c> after its 24-byte <c>DISK_GEOMETRY</c>.</summary>
    /// <param name="buffer">Borrowed <c>DISK_GEOMETRY_EX</c> record; at least 32 bytes are needed for DiskSize.</param>
    /// <returns>Nonnegative byte capacity; zero for a short record or negative native value.</returns>
    internal static long ReadGeometryCapacity(ReadOnlySpan<byte> buffer)
    {
        return buffer.Length >= 32 ? Math.Max(0, BinaryPrimitives.ReadInt64LittleEndian(buffer[24..32])) : 0;
    }

    /// <summary>
    ///     Reads the disk's bus type and vendor/product identity through <c>IOCTL_STORAGE_QUERY_PROPERTY</c>
    ///     (<c>StorageDeviceProperty</c>).
    /// </summary>
    /// <param name="disk">An open disk handle (query access suffices).</param>
    /// <param name="busType">The <c>STORAGE_BUS_TYPE</c> value, -1 on failure.</param>
    /// <param name="product">Vendor and product strings, trimmed, possibly empty.</param>
    /// <returns>True for a complete descriptor header; false leaves busType at -1 and product empty.</returns>
    public static bool TryGetDeviceDescriptor(SafeFileHandle disk, out int busType, out string product)
    {
        busType = -1;
        product = "";
        // STORAGE_PROPERTY_QUERY { StorageDeviceProperty=0, PropertyStandardQuery=0 }.
        var query = stackalloc byte[12];
        var buffer = stackalloc byte[1024];
        if (!Kernel32.DeviceIoControl(disk, IoctlStorageQueryProperty, (nint)query, 12, (nint)buffer, 1024,
                out var written, 0)
            || written < DeviceDescriptorHeaderSize)
        {
            return false;
        }

        (busType, product) = ReadDeviceDescriptor(new ReadOnlySpan<byte>(buffer, (int)written));
        return true;
    }

    /// <summary>
    ///     Decodes a <c>STORAGE_DEVICE_DESCRIPTOR</c> buffer: the bus type and the combined vendor and
    ///     product identity string.
    /// </summary>
    /// <param name="buffer">The descriptor, header plus trailing string data.</param>
    /// <returns>Bus type and trimmed vendor/product text; (-1, empty) for a short header.</returns>
    internal static (int BusType, string Product) ReadDeviceDescriptor(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < DeviceDescriptorHeaderSize)
        {
            return (-1, "");
        }

        var busType = BitConverter.ToInt32(buffer[28..]);
        var vendor = ReadAnsiAt(buffer, BitConverter.ToInt32(buffer[12..]));
        var product = ReadAnsiAt(buffer, BitConverter.ToInt32(buffer[16..]));
        return (busType, $"{vendor} {product}".Trim());
    }

    /// <summary>Reads the disk's per-partition types.</summary>
    /// <param name="disk">An open disk handle.</param>
    /// <param name="partitions">The partition types found.</param>
    /// <returns>
    ///     True when the layout IOCTL succeeds, including a layout with no recognized partitions; false
    ///     otherwise. RAW or uninitialized disks commonly fail here.
    /// </returns>
    public static bool TryGetPartitionTypes(SafeFileHandle disk, out List<PartitionType> partitions)
    {
        const int BufferSize = 8192;
        var buffer = stackalloc byte[BufferSize];
        if (!Kernel32.DeviceIoControl(disk, IoctlDiskGetDriveLayoutEx, 0, 0, (nint)buffer, BufferSize,
                out var written, 0))
        {
            var size = BufferSize;
            while (Marshal.GetLastPInvokeError() is 122 or 234)
            {
                size = checked(size * 2);
                var larger = new byte[size];
                fixed (byte* data = larger)
                {
                    if (Kernel32.DeviceIoControl(disk, IoctlDiskGetDriveLayoutEx, 0, 0, (nint)data, (uint)size,
                            out written, 0))
                    {
                        (_, partitions) = ReadDriveLayout(larger.AsSpan(0, (int)written));
                        return true;
                    }
                }
            }

            partitions = [];
            return false;
        }

        (_, partitions) = ReadDriveLayout(new ReadOnlySpan<byte>(buffer, (int)written));
        return true;
    }

    /// <summary>
    ///     Decodes a <c>DRIVE_LAYOUT_INFORMATION_EX</c> buffer into the partition style and each partition's
    ///     type. Zeroed MBR entries (empty table slots; MBR layouts always report 4-slot multiples) are
    ///     skipped.
    /// </summary>
    /// <param name="buffer">The layout buffer as returned by the IOCTL.</param>
    /// <returns>Native style and complete decoded entries; a short header returns RAW style with no partitions.</returns>
    internal static (int Style, List<PartitionType> Partitions) ReadDriveLayout(ReadOnlySpan<byte> buffer)
    {
        var partitions = new List<PartitionType>();
        if (buffer.Length < DriveLayoutHeaderSize)
        {
            return (2, partitions);
        }

        var style = BitConverter.ToInt32(buffer);
        var count = BitConverter.ToInt32(buffer[4..]);
        for (var i = 0; i < count; i++)
        {
            var at = DriveLayoutHeaderSize + i * PartitionRecordSize;
            if (at + PartitionRecordSize > buffer.Length)
            {
                break;
            }

            var entry = buffer.Slice(at, PartitionRecordSize);
            // Union at offset 32: GPT PartitionType GUID / MBR PartitionType byte.
            switch (style)
            {
                case 1:
                    partitions.Add(new PartitionType(0, new Guid(entry.Slice(32, 16))));
                    break;
                case 0:
                {
                    var mbrType = entry[32];
                    if (mbrType != 0)
                    {
                        partitions.Add(new PartitionType(mbrType, Guid.Empty));
                    }

                    break;
                }
            }
        }

        return (style, partitions);
    }

    /// <summary>
    ///     Broadcasts a synthetic volume-arrival notification (<c>WM_DEVICECHANGE</c> /
    ///     <c>DBT_DEVICEARRIVAL</c> / <c>DEV_BROADCAST_VOLUME</c>) for a drive letter, the same message a
    ///     real insertion generates, so drive watchers look at the volume again. Best effort.
    /// </summary>
    /// <param name="letter">The drive letter that should be re-scanned; a non-letter is ignored.</param>
    /// <remarks>Sent with <c>SMTO_ABORTIFHUNG</c> and a one-second timeout, so a hung window cannot stall the caller.</remarks>
    public static void BroadcastVolumeArrival(char letter)
    {
        const uint WmDeviceChange = 0x0219;
        const nuint DbtDeviceArrival = 0x8000;
        const int DbtDevTypVolume = 2;
        const uint SmtoAbortIfHung = 0x0002;
        const nint HwndBroadcast = 0xFFFF;
        var index = char.ToUpperInvariant(letter) - 'A';
        if (index is < 0 or > 25)
        {
            return;
        }

        // DEV_BROADCAST_VOLUME: size, devicetype, reserved, unitmask, flags.
        var broadcast = stackalloc byte[20];
        BitConverter.TryWriteBytes(new Span<byte>(broadcast, 4), 20);
        BitConverter.TryWriteBytes(new Span<byte>(broadcast + 4, 4), DbtDevTypVolume);
        BitConverter.TryWriteBytes(new Span<byte>(broadcast + 12, 4), 1u << index);
        SendMessageTimeoutW(HwndBroadcast, WmDeviceChange, (nint)DbtDeviceArrival, (nint)broadcast, SmtoAbortIfHung,
            1000, out _);
    }

    /// <summary>
    ///     Asks PnP to eject a device, the same operation as Explorer's "Safely Remove Hardware". Windows
    ///     dismounts and flushes every volume on the device.
    /// </summary>
    /// <param name="devInst">The devnode to eject (see <see cref="FindEjectTarget" />).</param>
    /// <param name="vetoType">Why the eject was refused, when it was.</param>
    /// <param name="vetoName">The vetoing module, service or path, possibly empty.</param>
    /// <returns>The CONFIGRET code: <see cref="CrSuccess" />, <see cref="CrRemoveVetoed" />, or another CR_* failure.</returns>
    public static int RequestDeviceEject(uint devInst, out PnpVetoType vetoType, out string vetoName)
    {
        const int MaxPath = 260;
        var buffer = stackalloc char[MaxPath];
        var result = CM_Request_Device_EjectW(devInst, out var rawVeto, buffer, MaxPath, 0);
        vetoType = (PnpVetoType)rawVeto;
        vetoName = NativeText.ReadFixed(buffer, MaxPath);
        return result;
    }

    /// <summary>
    ///     Takes the exclusive volume lock, the open-files check for the media-level eject. Fails while any
    ///     other handle is open on the volume.
    /// </summary>
    /// <param name="volume">A volume opened through <see cref="OpenVolumeForEject" />.</param>
    /// <returns>True when the exclusive lock was obtained; false preserves the native error for LastWin32Error.</returns>
    public static bool LockVolume(SafeFileHandle volume)
    {
        return Kernel32.DeviceIoControl(volume, FsctlLockVolume, 0, 0, 0, 0, out _, 0);
    }

    /// <summary>Dismounts the file system, flushing it first.</summary>
    /// <param name="volume">A locked volume handle.</param>
    /// <returns>True when Windows accepted the dismount; false preserves the native error for LastWin32Error.</returns>
    public static bool DismountVolume(SafeFileHandle volume)
    {
        return Kernel32.DeviceIoControl(volume, FsctlDismountVolume, 0, 0, 0, 0, out _, 0);
    }

    /// <summary>
    ///     Releases any software media lock (<c>PREVENT_MEDIA_REMOVAL</c> = FALSE), then asks the device to
    ///     eject the media.
    /// </summary>
    /// <param name="volume">A locked, dismounted volume handle.</param>
    /// <returns>True when Windows accepted media eject; false preserves the native error for LastWin32Error.</returns>
    /// <remarks>Card readers without a motor may fail the final call; lock and dismount are the real removal.</remarks>
    public static bool EjectMedia(SafeFileHandle volume)
    {
        byte allow = 0;
        Kernel32.DeviceIoControl(volume, IoctlStorageMediaRemoval, (nint)(&allow), 1, 0, 0, out _, 0);
        return Kernel32.DeviceIoControl(volume, IoctlStorageEjectMedia, 0, 0, 0, 0, out _, 0);
    }

    /// <summary>The calling thread's last Win32 error, for the caller's report.</summary>
    /// <returns>Last P/Invoke error on the current thread; read immediately after the failed native operation.</returns>
    public static int LastWin32Error()
    {
        return Marshal.GetLastPInvokeError();
    }

    /// <summary>
    ///     Resolves a drive letter to its volume GUID path (<c>\\?\Volume{...}\</c>): the name of the volume
    ///     rather than of the mount point that currently exposes it.
    /// </summary>
    /// <param name="letter">The drive letter, without a colon.</param>
    /// <param name="volumeRoot">The volume GUID path with its trailing separator, or null when the letter names no volume.</param>
    /// <returns>True when the letter resolved.</returns>
    /// <remarks>
    ///     A drive letter is a mount point and can be re-pointed at different media without any user
    ///     action. File work that must stay on the medium it validated addresses the volume: if the volume
    ///     goes away, opens through this path fail instead of landing on whatever took the letter over.
    /// </remarks>
    public static bool TryGetVolumeGuidPath(char letter, out string? volumeRoot)
    {
        volumeRoot = null;
        // The documented minimum for a volume GUID path is 50 characters.
        var buffer = stackalloc char[64];
        if (!GetVolumeNameForVolumeMountPointW($"{letter}:\\", buffer, 64))
        {
            return false;
        }

        var value = NativeText.ReadFixed(buffer, 64);
        if (value.Length == 0)
        {
            return false;
        }

        volumeRoot = value;
        return true;
    }

    /// <summary>Reads a mounted volume's label and filesystem name.</summary>
    /// <param name="volumeRoot">A root path with a trailing separator: a drive root or a volume GUID path.</param>
    /// <param name="label">The current volume label, empty when it has none.</param>
    /// <param name="fileSystem">The filesystem name, for example <c>NTFS</c>.</param>
    /// <returns>False when the volume could not be queried; the Win32 error is the caller's to report.</returns>
    public static bool TryGetVolumeInformation(string volumeRoot, out string label, out string fileSystem)
    {
        label = "";
        fileSystem = "";
        // MAX_PATH+1 each, the documented cap for both strings.
        var labelBuffer = stackalloc char[261];
        var fileSystemBuffer = stackalloc char[261];
        if (!GetVolumeInformationW(volumeRoot, labelBuffer, 261, null, null, null, fileSystemBuffer, 261))
        {
            return false;
        }

        label = NativeText.ReadFixed(labelBuffer, 261);
        fileSystem = NativeText.ReadFixed(fileSystemBuffer, 261);
        return true;
    }

    /// <summary>Sets a mounted volume's label.</summary>
    /// <param name="volumeRoot">A root path with a trailing separator: a drive root or a volume GUID path.</param>
    /// <param name="label">
    ///     The new label; it must already be within the filesystem's length limit and free of characters it
    ///     rejects.
    /// </param>
    /// <returns>False when Windows refused; the Win32 error is the caller's to report.</returns>
    public static bool TrySetVolumeLabel(string volumeRoot, string label)
    {
        return SetVolumeLabelW(volumeRoot, label);
    }

    private static string[] ListInterfaces(Guid guid)
    {
        char[]? buffer = null;
        for (var attempt = 0; attempt < InterfaceListAttempts; attempt++)
        {
            if (CM_Get_Device_Interface_List_SizeW(out var length, in guid, null, 0) != CrSuccess || length < 2)
            {
                return [];
            }

            var candidate = new char[length];
            int code;
            fixed (char* p = candidate)
            {
                code = CM_Get_Device_Interface_ListW(in guid, null, p, length, 0);
            }

            if (code == CrSuccess)
            {
                buffer = candidate;
                break;
            }

            if (code != CrBufferSmall)
            {
                return [];
            }
        }

        if (buffer is null)
        {
            return [];
        }

        // Double-NUL-terminated multi-string.
        var result = new List<string>();
        var start = 0;
        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != '\0')
            {
                continue;
            }

            if (i > start)
            {
                result.Add(new string(buffer, start, i - start));
            }

            start = i + 1;
        }

        return [.. result];
    }

    private static string ReadDevNodeString(uint devInst, uint property)
    {
        var length = 1024u;
        var buffer = stackalloc byte[1024];
        // REG_SZ data is not guaranteed NUL-terminated; decode at most the returned byte count.
        return CM_Get_DevNode_Registry_PropertyW(devInst, property, out _, buffer, ref length, 0) != CrSuccess
            ? ""
            : NativeText.ReadFixed((char*)buffer, (int)(Math.Min(length, 1024u) / 2));
    }

    /// <summary>Reads the devnode's <c>CM_DEVCAP_*</c> capability bits, 0 on failure.</summary>
    private static uint GetCapabilities(uint devInst)
    {
        var length = 4u;
        uint capabilities = 0;
        return CM_Get_DevNode_Registry_PropertyW(devInst, DrpCapabilities, out _, (byte*)&capabilities, ref length,
            0) == CrSuccess
            ? capabilities
            : 0;
    }

    /// <summary>Reads a NUL-terminated ANSI string at a descriptor-relative offset; empty when out of range.</summary>
    private static string ReadAnsiAt(ReadOnlySpan<byte> buffer, int offset)
    {
        if (offset <= 0 || offset >= buffer.Length)
        {
            return "";
        }

        var slice = buffer[offset..];
        var end = slice.IndexOf((byte)0);
        if (end >= 0)
        {
            slice = slice[..end];
        }

        return Encoding.ASCII.GetString(slice).Trim();
    }

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_Interface_List_SizeW(
        out uint length, in Guid interfaceClassGuid, string? deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_Interface_ListW(
        in Guid interfaceClassGuid, string? deviceId, char* buffer, uint bufferLength, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_PropertyW",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_Interface_PropertyW(
        string deviceInterface, in DevPropKey propertyKey, out uint propertyType, char* propertyBuffer,
        ref uint propertyBufferSize, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Locate_DevNodeW(out uint devInst, string deviceInstanceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Registry_PropertyW")]
    private static partial int CM_Get_DevNode_Registry_PropertyW(
        uint devInst, uint property, out uint regDataType, byte* buffer, ref uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent")]
    private static partial int CM_Get_Parent(out uint parentDevInst, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Request_Device_EjectW")]
    private static partial int CM_Request_Device_EjectW(
        uint devInst, out int vetoType, char* vetoName, uint nameLength, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW")]
    private static partial int CM_Get_Device_IDW(uint devInst, char* buffer, uint bufferLength, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeNameForVolumeMountPointW(string mountPoint, char* volumeName, uint length);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeInformationW(
        string rootPath, char* volumeName, uint volumeNameLength, uint* serialNumber, uint* maximumComponentLength,
        uint* fileSystemFlags, char* fileSystemName, uint fileSystemNameLength);

    [LibraryImport("kernel32.dll", EntryPoint = "SetVolumeLabelW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetVolumeLabelW(string rootPath, string? volumeLabel);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static partial nint SendMessageTimeoutW(
        nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);

    /// <summary>How Windows says an eject was refused (cfg.h <c>PNP_VETO_TYPE</c>, zero-based).</summary>
    public enum PnpVetoType
    {
        /// <summary>No reason was named.</summary>
        TypeUnknown = 0,

        /// <summary>A legacy device cannot be ejected.</summary>
        LegacyDevice = 1,

        /// <summary>A close is still pending on the device.</summary>
        PendingClose = 2,

        /// <summary>An application vetoed; the veto name is a module.</summary>
        WindowsApp = 3,

        /// <summary>A service vetoed; the veto name is a service name.</summary>
        WindowsService = 4,

        /// <summary>Open handles remain on the device.</summary>
        OutstandingOpen = 5,

        /// <summary>The device itself refused.</summary>
        Device = 6,

        /// <summary>The driver refused.</summary>
        Driver = 7,

        /// <summary>The request is illegal for this device.</summary>
        IllegalDeviceRequest = 8,

        /// <summary>Insufficient power to complete the operation.</summary>
        InsufficientPower = 9,

        /// <summary>The device cannot be disabled.</summary>
        NonDisableable = 10,

        /// <summary>A legacy driver vetoed.</summary>
        LegacyDriver = 11,

        /// <summary>The caller lacks the rights to eject.</summary>
        InsufficientRights = 12
    }

    /// <summary>DEVPROPKEY, blittable: a property category GUID plus an id.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid Fmtid;
        public uint Pid;
    }

    /// <summary>One mounted local volume, mapped back to its physical disk.</summary>
    /// <param name="Letter">The upper-case drive letter.</param>
    /// <param name="Disk">The physical disk number from the device-number query.</param>
    /// <param name="DeviceType">The <c>FILE_DEVICE_*</c> type of the underlying device.</param>
    /// <param name="Ready">Whether the media was ready when walked.</param>
    /// <param name="DriveType">The .NET drive type (Fixed or Removable).</param>
    /// <param name="SizeBytes">The volume size, 0 when the media is not ready.</param>
    public readonly record struct MountedVolume(
        char Letter,
        int Disk,
        int DeviceType,
        bool Ready,
        DriveType DriveType,
        long SizeBytes);

    /// <summary>One partition's identifying type facts.</summary>
    /// <param name="MbrType">The MBR partition-type byte (0x83 = Linux), 0 for GPT disks.</param>
    /// <param name="GptType">The GPT partition-type GUID, empty for MBR disks.</param>
    public readonly record struct PartitionType(byte MbrType, Guid GptType)
    {
        /// <summary>Whether this partition looks like a Linux filesystem.</summary>
        public bool IsLinux => MbrType == 0x83 || GptType == LinuxFilesystemGuid;
    }
}
