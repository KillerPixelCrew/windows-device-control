using System;
using System.Collections.Generic;
using Xunit;

namespace WindowsDeviceControl.Tests;

public sealed class DisplayEdidOverrideTests
{
    [Fact]
    public void ConsecutiveBinaryBlocksJoinInOrder()
    {
        var first = new byte[128];
        var second = new byte[128];
        first[0] = 1;
        second[0] = 2;

        var edid = DisplayEdid.JoinBlocks(new Dictionary<string, object?> { ["1"] = second, ["0"] = first });

        Assert.NotNull(edid);
        Assert.Equal(256, edid.Length);
        Assert.Equal((1, 2), (edid[0], edid[128]));
    }

    [Fact]
    public void AnyOtherShapeIsNotAnEdid()
    {
        var block = new byte[128];
        Assert.Null(DisplayEdid.JoinBlocks(new Dictionary<string, object?>()));
        Assert.Null(DisplayEdid.JoinBlocks(new Dictionary<string, object?> { ["0"] = block, ["2"] = block }));
        Assert.Null(DisplayEdid.JoinBlocks(new Dictionary<string, object?> { ["0"] = block, ["1"] = null }));
        Assert.Null(DisplayEdid.JoinBlocks(new Dictionary<string, object?> { ["0"] = new byte[127] }));
    }

    [Fact]
    public void OnlyAMonitorInterfacePathNamesAKey()
    {
        Assert.Equal(@"SYSTEM\CurrentControlSet\Enum\DISPLAY\BOE0CFF\4&1a2b&0&UID8388688\Device Parameters",
            DisplayEdid.DeviceParameters(@"\\?\DISPLAY#BOE0CFF#4&1a2b&0&UID8388688#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}"));
        Assert.Null(DisplayEdid.DeviceParameters(@"\\?\USB#VID_1#2#{guid}"));
        Assert.Null(DisplayEdid.DeviceParameters(@"\\?\DISPLAY#BOE\0CFF#4#{guid}"));
        Assert.Throws<ArgumentException>(() => DisplayEdid.ReadOverride("not a path"));
    }
}
