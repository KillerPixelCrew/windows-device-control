using System;
using Xunit;

namespace WindowsDeviceControl.Tests;

/// <summary>
///     Ports the WakeWatch decoder test suite (same author): the POWER_REQUEST
///     layout is undocumented, so every structural surprise must decode to null
///     ("unknown"), never to a plausible-looking wrong result.
/// </summary>
public sealed class PowerRequestListTests
{
    private const uint Win11Build = 26200;

    // Where the synthetic V4 buffer puts each structure.
    private const int CountAt = 0; // POWER_REQUEST_LIST.Count
    private const int FirstOffsetAt = 8; // POWER_REQUEST_LIST.Offsets[0]
    private const int Request = 64; // POWER_REQUEST
    private const int Diagnostic = Request + 32; // DIAGNOSTIC_BUFFER
    private const int NameString = Diagnostic + 64;
    private const int ReasonContext = Diagnostic + 128; // COUNTED_REASON_CONTEXT_RELATIVE
    private const int ReasonString = ReasonContext + 32;

    /// <summary>Builds a synthetic V4 buffer with one process request holding DISPLAY.</summary>
    private static byte[] Synth()
    {
        var b = new byte[512];
        BitConverter.GetBytes(1UL).CopyTo(b, CountAt);
        BitConverter.GetBytes((ulong)Request).CopyTo(b, FirstOffsetAt);

        BitConverter.GetBytes(0x3Fu).CopyTo(b, Request); // SupportedRequestMask
        BitConverter.GetBytes(1u).CopyTo(b, Request + 4); // DISPLAY = 1

        BitConverter.GetBytes(120UL).CopyTo(b, Diagnostic); // DIAGNOSTIC_BUFFER.Size
        BitConverter.GetBytes(1u).CopyTo(b, Diagnostic + 8); // CallerType = process
        BitConverter.GetBytes((ulong)(NameString - Diagnostic)).CopyTo(b, Diagnostic + 16); // name offset
        BitConverter.GetBytes(1234u).CopyTo(b, Diagnostic + 24); // pid

        WriteUtf16(b, NameString, "a.exe");
        return b;
    }

    private static void WriteUtf16(byte[] buffer, int offset, string text)
    {
        foreach (var unit in text)
        {
            BitConverter.GetBytes((ushort)unit).CopyTo(buffer, offset);
            offset += 2;
        }
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
    {
        Assert.Equal(expected, PowerRequestList.ModeCount(build));
    }

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
    {
        Assert.Null(PowerRequestList.DecodeWithBuild(Synth().AsSpan(0, cut), Win11Build));
    }

    [Theory]
    [InlineData(CountAt, ulong.MaxValue, 8)] // an absurd request count
    [InlineData(CountAt, 5000UL, 8)] // a count beyond the buffer
    [InlineData(FirstOffsetAt, 100_000UL, 8)] // a request offset past the end
    [InlineData(Diagnostic + 8, 7UL, 4)] // a caller type that does not exist
    [InlineData(Diagnostic, 0UL, 8)] // a zero diagnostic size
    [InlineData(Diagnostic, 10_000_000UL, 8)] // a diagnostic size beyond the buffer
    public void OneImplausibleFieldMakesTheListUnknown(int offset, ulong value, int width)
    {
        var b = Synth();
        var field = width == 4 ? BitConverter.GetBytes((uint)value) : BitConverter.GetBytes(value);
        field.CopyTo(b, offset);

        Assert.Null(PowerRequestList.DecodeWithBuild(b, Win11Build));
    }

    [Fact]
    public void UnterminatedStringIsRejected()
    {
        var b = Synth();
        for (var i = NameString; i < b.Length; i++)
        {
            b[i] = 0x41;
        }

        Assert.Null(PowerRequestList.DecodeWithBuild(b, Win11Build));
    }

    [Fact]
    public void ZeroNameOffsetYieldsAnEmptyName()
    {
        var b = Synth();
        BitConverter.GetBytes(0UL).CopyTo(b, Diagnostic + 16);

        var entries = PowerRequestList.DecodeWithBuild(b, Win11Build);

        Assert.NotNull(entries);
        Assert.Equal("", entries![0].Name);
    }

    [Fact]
    public void EmptyBufferIsMalformed()
    {
        Assert.Null(PowerRequestList.DecodeWithBuild(ReadOnlySpan<byte>.Empty, Win11Build));
    }

    /// <summary>
    ///     Adds a simple-string COUNTED_REASON_CONTEXT_RELATIVE to the synthetic
    ///     buffer: ReasonOffset at DIAGNOSTIC_BUFFER+32, then flags and a string offset
    ///     relative to the context itself.
    /// </summary>
    private static byte[] SynthWithReason(string reason, uint flags = 1)
    {
        var b = Synth();
        BitConverter.GetBytes((ulong)(ReasonContext - Diagnostic)).CopyTo(b, Diagnostic + 32);
        BitConverter.GetBytes(flags).CopyTo(b, ReasonContext);
        BitConverter.GetBytes((ulong)(ReasonString - ReasonContext)).CopyTo(b, ReasonContext + 8);
        WriteUtf16(b, ReasonString, reason);
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
            SynthWithReason("Steam download", 0), Win11Build);

        Assert.NotNull(entries);
        Assert.Null(entries![0].Reason);
    }

    [Fact]
    public void AnUnterminatedReasonStringLeavesTheEntryDecodableWithoutAReason()
    {
        var b = SynthWithReason("Steam download");
        for (var i = ReasonString; i < b.Length; i++)
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
        BitConverter.GetBytes(100_000UL).CopyTo(b, Diagnostic + 32);

        var entries = PowerRequestList.DecodeWithBuild(b, Win11Build);

        Assert.NotNull(entries);
        Assert.Null(entries![0].Reason);
    }
}
