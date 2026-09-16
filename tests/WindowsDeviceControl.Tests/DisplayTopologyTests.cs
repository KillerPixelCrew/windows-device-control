using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using static WindowsDeviceControl.Tests.TestFixtures;

namespace WindowsDeviceControl.Tests;

public sealed class DisplayTopologyTests
{
    [Theory]
    [InlineData(284u, 568u)]
    [InlineData(4096u, 8192u)]
    public void PossibleRouteCountsCanExceedTheNumberOfDisplays(uint paths, uint modes)
    {
        DisplayTopology.ValidateBufferCounts(paths, modes);
    }

    [Theory]
    [InlineData(4097u, 8192u)]
    [InlineData(4096u, 8193u)]
    [InlineData(uint.MaxValue, uint.MaxValue)]
    public void ExcessiveRouteBuffersAreStillRefusedBeforeAllocation(uint paths, uint modes)
    {
        Assert.Throws<InvalidOperationException>(() => DisplayTopology.ValidateBufferCounts(paths, modes));
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

    [Fact]
    public async Task AvailableWaitRejectsInvalidDeadlineAndCancelsBeforeNativeQuery()
    {
        var target = Target("display", "TV", 0);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            DisplayTopology.WaitForAvailableAsync(target, TimeSpan.Zero));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DisplayTopology.WaitForAvailableAsync(target, TimeSpan.FromSeconds(5), new CancellationToken(true)));
    }
}
