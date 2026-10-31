using System.Runtime.InteropServices;
using Xunit;
using static WindowsDeviceControl.Tests.TestFixtures;

namespace WindowsDeviceControl.Tests;

public sealed class DisplayTopologyTests
{
    [Fact]
    public void EdidSerialSurvivesDriverRouteChangesAndRejectsAReplacementOnTheOldRoute()
    {
        var saved = new DisplayTargetIdentity("old-path", 1, 2, "Monitor", 1, 0, 2)
            { EdidIdentity = "01000200:00000042:serial" };
        var shuffled = saved with { DevicePath = "new-path", AdapterLowPart = 7, TargetId = 9 };
        var replacement = saved with { EdidIdentity = "01000200:00000043:other" };
        Assert.True(saved.Matches(shuffled));
        Assert.False(saved.Matches(replacement));
    }

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
        var saved = Target("monitor-path", id: 2);
        var current = saved with { EdidManufacturerId = 0x4321, EdidProductCodeId = 0x7654 };
        var differentPath = current with { DevicePath = "other-monitor-path" };

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
