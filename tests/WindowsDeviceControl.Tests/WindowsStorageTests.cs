using System;
using Xunit;

namespace WindowsDeviceControl.Tests;

public sealed class WindowsStorageTests
{
    // STORAGE_DEVICE_NUMBER: DeviceType @0, DeviceNumber @4, PartitionNumber @8.
    [Fact]
    public void DeviceNumberRecordsDecodeTheFixedLayout()
    {
        var buffer = new byte[12];
        BitConverter.GetBytes(7).CopyTo(buffer, 0);
        BitConverter.GetBytes(2).CopyTo(buffer, 4);
        BitConverter.GetBytes(1).CopyTo(buffer, 8);

        var (deviceType, deviceNumber, partition) = WindowsStorage.ReadDeviceNumber(buffer);

        Assert.Equal(7, deviceType);
        Assert.Equal(2, deviceNumber);
        Assert.Equal(1, partition);
    }

    // STORAGE_HOTPLUG_INFO: Size @0, MediaRemovable @4, MediaHotplug @5, DeviceHotplug @6.
    [Fact]
    public void HotplugRecordsDecodeTheFixedLayout()
    {
        var buffer = new byte[8];
        buffer[4] = 1;
        buffer[5] = 1;
        buffer[6] = 0;

        var (mediaRemovable, deviceHotplug) = WindowsStorage.ReadHotplugInfo(buffer);

        Assert.True(mediaRemovable);
        Assert.False(deviceHotplug);
    }

    // DISK_GEOMETRY_EX: a 24-byte DISK_GEOMETRY, then DiskSize @24.
    [Fact]
    public void GeometryCapacityUsesFullDiskSizeAndRejectsTruncatedOrNegativeRecords()
    {
        var record = new byte[32];
        BitConverter.GetBytes(4_000_000_000_000L).CopyTo(record, 24);
        Assert.Equal(4_000_000_000_000L, WindowsStorage.ReadGeometryCapacity(record));
        Assert.Equal(0, WindowsStorage.ReadGeometryCapacity(record.AsSpan(0, 31)));
        BitConverter.GetBytes(-1L).CopyTo(record, 24);
        Assert.Equal(0, WindowsStorage.ReadGeometryCapacity(record));
    }

    // STORAGE_DEVICE_DESCRIPTOR: VendorIdOffset @12, ProductIdOffset @16, BusType @28, 36-byte header.
    [Fact]
    public void DeviceDescriptorDecodesBusTypeAndProductStrings()
    {
        var buffer = new byte[64];
        BitConverter.GetBytes(36).CopyTo(buffer, 12);
        BitConverter.GetBytes(44).CopyTo(buffer, 16);
        BitConverter.GetBytes(12).CopyTo(buffer, 28);
        "SanDisk\0"u8.CopyTo(buffer.AsSpan(36));
        "Extreme\0"u8.CopyTo(buffer.AsSpan(44));

        var (busType, product) = WindowsStorage.ReadDeviceDescriptor(buffer);

        Assert.Equal(WindowsStorage.BusTypeSd, busType);
        Assert.Equal("SanDisk Extreme", product);
    }

    [Fact]
    public void ShortDeviceDescriptorHeaderHasNoBusType()
    {
        Assert.Equal((-1, ""), WindowsStorage.ReadDeviceDescriptor(new byte[35]));
    }

    // DRIVE_LAYOUT_INFORMATION_EX: PartitionStyle @0, PartitionCount @4, entries from 48, 144 bytes each,
    // with the GPT type GUID or MBR type byte at entry offset 32.
    [Fact]
    public void GptLinuxPartitionIsRecognised()
    {
        var buffer = new byte[48 + 144];
        BitConverter.GetBytes(1).CopyTo(buffer, 0);
        BitConverter.GetBytes(1).CopyTo(buffer, 4);
        new Guid("0fc63daf-8483-4772-8e79-3d69d8477de4").ToByteArray().CopyTo(buffer, 48 + 32);

        var (style, partitions) = WindowsStorage.ReadDriveLayout(buffer);

        Assert.Equal(1, style);
        Assert.Single(partitions);
        Assert.True(partitions[0].IsLinux);
    }

    [Fact]
    public void MbrLinuxPartitionTypeByteIsRecognised()
    {
        var buffer = new byte[48 + 144];
        BitConverter.GetBytes(0).CopyTo(buffer, 0);
        BitConverter.GetBytes(1).CopyTo(buffer, 4);
        buffer[48 + 32] = 0x83;

        var (_, partitions) = WindowsStorage.ReadDriveLayout(buffer);

        Assert.Single(partitions);
        Assert.True(partitions[0].IsLinux);
    }

    [Fact]
    public void EmptyMbrSlotsAreSkipped()
    {
        var buffer = new byte[48 + 4 * 144];
        BitConverter.GetBytes(0).CopyTo(buffer, 0);
        BitConverter.GetBytes(4).CopyTo(buffer, 4);
        buffer[48 + 32] = 0x07;

        var (_, partitions) = WindowsStorage.ReadDriveLayout(buffer);

        Assert.Single(partitions);
        Assert.False(partitions[0].IsLinux);
    }

    [Fact]
    public void LayoutEntriesPastTheBufferAreNotRead()
    {
        var buffer = new byte[48 + 144];
        BitConverter.GetBytes(0).CopyTo(buffer, 0);
        BitConverter.GetBytes(3).CopyTo(buffer, 4);
        buffer[48 + 32] = 0x83;

        var (_, partitions) = WindowsStorage.ReadDriveLayout(buffer);

        Assert.Single(partitions);
    }

    [Fact]
    public void ShortLayoutHeaderIsRawWithoutPartitions()
    {
        var (style, partitions) = WindowsStorage.ReadDriveLayout(new byte[47]);

        Assert.Equal(2, style);
        Assert.Empty(partitions);
    }
}
