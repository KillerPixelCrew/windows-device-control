using System.Runtime.InteropServices;
using Xunit;

namespace WindowsDeviceControl.Tests;

public sealed class DisplayTopologyTests
{
    [Theory]
    [InlineData(0u, false)]
    [InlineData(1u, false)]
    [InlineData(2u, false)]
    [InlineData(3u, false)]
    [InlineData(4u, true)]
    [InlineData(5u, true)]
    [InlineData(6u, true)]
    [InlineData(7u, true)]
    [InlineData(0x80000000u, false)]
    [InlineData(0x80000004u, true)]
    public void EdidIdsFollowOnlyTheValidityBit(uint flags, bool expectedPresent)
    {
        var (manufacturerId, productCodeId) = DisplayTopology.DecodeEdidIds(flags, 0x4321, 0x7654);

        if (expectedPresent)
        {
            Assert.Equal((ushort?)0x4321, manufacturerId);
            Assert.Equal((ushort?)0x7654, productCodeId);
            return;
        }

        Assert.Null(manufacturerId);
        Assert.Null(productCodeId);
    }

    [Fact]
    public void ValidZeroEdidIdsArePreserved()
    {
        var (manufacturerId, productCodeId) = DisplayTopology.DecodeEdidIds(4u, 0, 0);

        Assert.Equal((ushort?)0, manufacturerId);
        Assert.Equal((ushort?)0, productCodeId);
    }

    [Fact]
    public void ExistingPathIdentitySurvivesEdidPopulation()
    {
        DisplayTargetIdentity saved = new(
            DevicePath: "monitor-path",
            EdidManufacturerId: null,
            EdidProductCodeId: null,
            FriendlyName: "Monitor",
            AdapterLowPart: 1,
            AdapterHighPart: 0,
            TargetId: 2);
        DisplayTargetIdentity current = new(
            DevicePath: "monitor-path",
            EdidManufacturerId: 0x4321,
            EdidProductCodeId: 0x7654,
            FriendlyName: "Monitor",
            AdapterLowPart: 1,
            AdapterHighPart: 0,
            TargetId: 2);
        DisplayTargetIdentity differentPath = new(
            DevicePath: "other-monitor-path",
            EdidManufacturerId: 0x4321,
            EdidProductCodeId: 0x7654,
            FriendlyName: "Monitor",
            AdapterLowPart: 1,
            AdapterHighPart: 0,
            TargetId: 2);

        Assert.True(saved.Matches(current));
        Assert.False(current.Matches(differentPath));
        Assert.False(saved.Matches(differentPath));
    }

    [Fact]
    public void TheNativeModeRecordKeepsItsDocumentedLayout()
    {
        // The union sits at offset 16 and the whole record is 64 bytes; a wrong offset here would
        // silently write a resolution into the wrong field.
        Assert.Equal(64, Marshal.SizeOf<DisplayTopology.ModeInfo>());
        Assert.Equal(48, Marshal.SizeOf<DisplayTopology.ModeUnion>());
        Assert.Equal(20, Marshal.SizeOf<DisplayTopology.SourceMode>());
        Assert.Equal(48, Marshal.SizeOf<DisplayTopology.VideoSignalInfo>());
        Assert.Equal(16, Marshal.OffsetOf<DisplayTopology.ModeInfo>(nameof(DisplayTopology.ModeInfo.Mode)).ToInt32());
    }
}
