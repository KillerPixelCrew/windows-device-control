using System;
using Xunit;


namespace WindowsDeviceControl.Tests;

/// <summary>Ports the WakeWatch decoder test suite (same author): the POWER_REQUEST
/// layout is undocumented, so every structural surprise must decode to null
/// ("unknown"), never to a plausible-looking wrong result.</summary>
public sealed class PowerRequestListTests
{
    private const uint Win11Build = 26200;

    /// <summary>Builds a synthetic V4 buffer with one process request holding DISPLAY.</summary>
    private static byte[] Synth()
    {
        var b = new byte[512];
        BitConverter.GetBytes(1UL).CopyTo(b, 0);   // POWER_REQUEST_LIST.Count
        BitConverter.GetBytes(64UL).CopyTo(b, 8);  // Offsets[0]

        const int req = 64;
        BitConverter.GetBytes(0x3Fu).CopyTo(b, req);      // SupportedRequestMask
        BitConverter.GetBytes(1u).CopyTo(b, req + 4);     // DISPLAY = 1

        const int db = req + 32;
        BitConverter.GetBytes(120UL).CopyTo(b, db);       // DIAGNOSTIC_BUFFER.Size
        BitConverter.GetBytes(1u).CopyTo(b, db + 8);      // CallerType = process
        BitConverter.GetBytes(64UL).CopyTo(b, db + 16);   // name offset
        BitConverter.GetBytes(1234u).CopyTo(b, db + 24);  // pid

        var name = db + 64;
        foreach (var unit in "a.exe")
        {
            BitConverter.GetBytes((ushort)unit).CopyTo(b, name);
            name += 2;
        }
        return b;
    }

    [Fact]
    public void DecodesAWellFormedRequest()
    {
        var entries = PowerRequestList.DecodeWithBuild(Synth(), Win11Build);

        Assert.NotNull(entries);
        var entry = Assert.Single(entries!);
        Assert.True(entry.HoldsDisplay);
        Assert.False(entry.HoldsSystem);
        Assert.Equal("a.exe", entry.Name);
        Assert.Equal(1234u, entry.Pid);
    }

    [Fact]
    public void DiagOffsetsMatchKnownLayouts()
    {
        Assert.Equal(32, PowerRequestList.DiagOffset(6)); // V4, confirmed against live data
        Assert.Equal(24, PowerRequestList.DiagOffset(5));
        Assert.Equal(40, PowerRequestList.DiagOffset(9));
        Assert.Equal(16, PowerRequestList.DiagOffset(3));
    }

    [Theory]
    [InlineData(26200u, 6)]
    [InlineData(14393u, 6)]
    [InlineData(9600u, 5)]
    [InlineData(9200u, 9)]
    [InlineData(7601u, 3)]
    public void ModeCountTracksBuild(uint build, int expected)
        => Assert.Equal(expected, PowerRequestList.ModeCount(build));

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(15)]
    [InlineData(40)]
    [InlineData(64)]
    [InlineData(70)]
    [InlineData(100)]
    [InlineData(120)]
    public void TruncatedBufferIsMalformedNotACrash(int cut)
        => Assert.Null(PowerRequestList.DecodeWithBuild(Synth().AsSpan(0, cut), Win11Build));

    [Fact]
    public void AbsurdCountIsRejected()
    {
        var b = Synth();
        BitConverter.GetBytes(ulong.MaxValue).CopyTo(b, 0);

        Assert.Null(PowerRequestList.DecodeWithBuild(b, Win11Build));
    }

    [Fact]
    public void CountBeyondBufferIsRejected()
    {
        var b = Synth();
        BitConverter.GetBytes(5000UL).CopyTo(b, 0);

        Assert.Null(PowerRequestList.DecodeWithBuild(b, Win11Build));
    }

    [Fact]
    public void OffsetPastEndIsRejected()
    {
        var b = Synth();
        BitConverter.GetBytes(100_000UL).CopyTo(b, 8);

        Assert.Null(PowerRequestList.DecodeWithBuild(b, Win11Build));
    }

    [Fact]
    public void BadCallerTypeIsRejected()
    {
        var b = Synth();
        BitConverter.GetBytes(7u).CopyTo(b, 64 + 32 + 8);

        Assert.Null(PowerRequestList.DecodeWithBuild(b, Win11Build));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(10_000_000UL)]
    public void ZeroAndOversizedDiagSizeAreRejected(ulong size)
    {
        var b = Synth();
        BitConverter.GetBytes(size).CopyTo(b, 64 + 32);

        Assert.Null(PowerRequestList.DecodeWithBuild(b, Win11Build));
    }

    [Fact]
    public void UnterminatedStringIsRejected()
    {
        var b = Synth();
        for (var i = 64 + 32 + 64; i < b.Length; i++)
        {
            b[i] = 0x41;
        }

        Assert.Null(PowerRequestList.DecodeWithBuild(b, Win11Build));
    }

    [Fact]
    public void ZeroNameOffsetYieldsAnEmptyName()
    {
        var b = Synth();
        BitConverter.GetBytes(0UL).CopyTo(b, 64 + 32 + 16);

        var entries = PowerRequestList.DecodeWithBuild(b, Win11Build);

        Assert.NotNull(entries);
        Assert.Equal("", entries![0].Name);
    }

    [Fact]
    public void EmptyBufferIsMalformed()
        => Assert.Null(PowerRequestList.DecodeWithBuild(ReadOnlySpan<byte>.Empty, Win11Build));

    /// <summary>Adds a simple-string COUNTED_REASON_CONTEXT_RELATIVE to the synthetic
    /// buffer: ReasonOffset at DIAGNOSTIC_BUFFER+32, then flags and a string offset
    /// relative to the context itself.</summary>
    private static byte[] SynthWithReason(string reason, uint flags = 1)
    {
        var b = Synth();
        const int db = 64 + 32;
        const int context = db + 128;
        BitConverter.GetBytes((ulong)(context - db)).CopyTo(b, db + 32);
        BitConverter.GetBytes(flags).CopyTo(b, context);
        BitConverter.GetBytes(32UL).CopyTo(b, context + 8);
        var text = context + 32;
        foreach (var unit in reason)
        {
            BitConverter.GetBytes((ushort)unit).CopyTo(b, text);
            text += 2;
        }
        return b;
    }

    // The reason string is the one decoded field whose failure does NOT reject the
    // entry, so an offset regression here would silently produce a plausible wrong
    // string instead of the "unknown" state everything else falls back to.
    [Fact]
    public void DecodesASimpleStringReason()
    {
        var entries = PowerRequestList.DecodeWithBuild(SynthWithReason("Steam download"), Win11Build);

        Assert.NotNull(entries);
        Assert.Equal("Steam download", entries![0].Reason);
    }

    [Fact]
    public void AReasonContextWithoutTheSimpleStringFlagIsIgnored()
    {
        var entries = PowerRequestList.DecodeWithBuild(
            SynthWithReason("Steam download", flags: 0), Win11Build);

        Assert.NotNull(entries);
        Assert.Null(entries![0].Reason);
    }

    [Fact]
    public void AnUnterminatedReasonStringLeavesTheEntryDecodableWithoutAReason()
    {
        var b = SynthWithReason("Steam download");
        for (var i = 64 + 32 + 128 + 32; i < b.Length; i++)
        {
            b[i] = 0x41;
        }

        var entries = PowerRequestList.DecodeWithBuild(b, Win11Build);

        Assert.NotNull(entries);
        Assert.Null(entries![0].Reason);
        Assert.True(entries[0].HoldsDisplay);
    }

    [Fact]
    public void AReasonOffsetPastTheBufferIsIgnoredRatherThanRead()
    {
        var b = SynthWithReason("Steam download");
        BitConverter.GetBytes(100_000UL).CopyTo(b, 64 + 32 + 32);

        var entries = PowerRequestList.DecodeWithBuild(b, Win11Build);

        Assert.NotNull(entries);
        Assert.Null(entries![0].Reason);
    }

}
