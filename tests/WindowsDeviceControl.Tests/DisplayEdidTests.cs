using System;
using System.Buffers.Binary;
using System.Linq;
using Xunit;

namespace WindowsDeviceControl.Tests;

public sealed class DisplayEdidTests
{
    [Fact]
    public void BaseTimingsAreDecodedAndInvalidDescriptorsAreRejected()
    {
        byte[] edid = Descriptor();
        edid[38] = 209; edid[39] = 192; // 1920x1080 at 60 Hz standard timing.
        Convert.FromHexString("023A801871382D40582C450000000000001E").CopyTo(edid, 54);
        FixChecksum(edid.AsSpan(0, 128));
        Assert.Equal(new DisplayMode(1920, 1080, 60), Assert.Single(DisplayEdid.Parse(edid)));
        Assert.Empty(DisplayEdid.Parse(edid.AsSpan(0, 127)));
        edid[54]++;
        Assert.Empty(DisplayEdid.Parse(edid));
    }

    [Fact]
    public void CtaOffersProgressiveVideoModesAndRejectsBrokenExtensionChecksums()
    {
        byte[] edid = Descriptor(1);
        edid[128] = 2; edid[129] = 3; edid[130] = 8;
        edid[132] = 0x43; edid[133] = 0x90; edid[134] = 97; edid[135] = 5;
        FixChecksum(edid.AsSpan(128, 128));
        Assert.Equal(new[] { new DisplayMode(1920, 1080, 60), new DisplayMode(3840, 2160, 60) }, DisplayEdid.Parse(edid));
        edid[134]++;
        Assert.Empty(DisplayEdid.Parse(edid));
    }

    [Theory]
    [InlineData(3, 220415)]
    [InlineData(0x22, 2204159)]
    public void DisplayIdDetailedTimingsIncludeUltrawideHighRefreshModes(int tag, int clock)
    {
        byte[] edid = Descriptor(1);
        Span<byte> block = edid.AsSpan(128, 128);
        block[0] = 0x70; block[1] = (byte)(tag == 3 ? 0x12 : 0x20); block[2] = 23;
        block[5] = (byte)tag; block[7] = 20;
        Span<byte> timing = block.Slice(8, 20);
        timing[0] = (byte)clock; timing[1] = (byte)(clock >> 8); timing[2] = (byte)(clock >> 16);
        BinaryPrimitives.WriteUInt16LittleEndian(timing[4..], 5119);
        BinaryPrimitives.WriteUInt16LittleEndian(timing[6..], 479);
        BinaryPrimitives.WriteUInt16LittleEndian(timing[12..], 1439);
        BinaryPrimitives.WriteUInt16LittleEndian(timing[14..], 199);
        FixChecksum(block.Slice(1, 28));
        FixChecksum(block);
        Assert.Equal(new DisplayMode(5120, 1440, 240), Assert.Single(DisplayEdid.Parse(edid)));
        timing[3] = 16; // Interlaced modes cannot be represented by DisplayMode.
        FixChecksum(block.Slice(1, 28)); FixChecksum(block);
        Assert.Empty(DisplayEdid.Parse(edid));
    }

    [Fact]
    public void TruncatedAndOverlongExtensionPayloadsDoNotEscapeTheirBlock()
    {
        byte[] edid = Descriptor(1);
        edid[128] = 0x70; edid[130] = 255;
        FixChecksum(edid.AsSpan(128, 128));
        Assert.Empty(DisplayEdid.Parse(edid));
        edid[128] = 2; edid[130] = 5; edid[132] = 0x5f;
        FixChecksum(edid.AsSpan(128, 128));
        Assert.Empty(DisplayEdid.Parse(edid));
        Assert.Empty(DisplayEdid.Parse(edid.AsSpan(0, 200)));
    }

    private static byte[] Descriptor(int extensions = 0)
    {
        byte[] edid = new byte[128 * (extensions + 1)];
        for (int index = 1; index < 7; index++) { edid[index] = 255; }
        edid[18] = 1; edid[19] = 4; edid[126] = (byte)extensions;
        edid.AsSpan(38, 16).Fill(1);
        FixChecksum(edid.AsSpan(0, 128));
        return edid;
    }

    private static void FixChecksum(Span<byte> block)
    {
        block[^1] = 0;
        block[^1] = unchecked((byte)-block.ToArray().Sum(value => value));
    }
}
