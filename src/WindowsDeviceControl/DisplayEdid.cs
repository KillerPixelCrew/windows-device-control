using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Windows.Devices.Display;

namespace WindowsDeviceControl;

/// <summary>Reads advertised monitor timings without activating a Windows display source.</summary>
public static class DisplayEdid
{
    /// <summary>Reads EDID through the exact monitor interface, including a connected disabled monitor.</summary>
    /// <param name="target">Monitor interface identity obtained from display discovery.</param>
    /// <returns>Progressive timing candidates, not a driver validation or an active mode snapshot.</returns>
    /// <remarks>Call on a worker. Interface lookup has a three-second cancellation budget; lookup
    /// failures propagate to the caller. Invalid descriptors produce no modes. Reading does not
    /// enable the display, validate a topology, or apply any setting.</remarks>
    public static IReadOnlyList<DisplayMode> ReadModes(DisplayTargetIdentity target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrEmpty(target.DevicePath)) { return []; }
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
        DisplayMonitor monitor = DisplayMonitor.FromInterfaceIdAsync(target.DevicePath).WaitWinRt(timeout.Token);
        return Parse(monitor.GetDescriptor(DisplayMonitorDescriptorKind.Edid));
    }

    internal static IReadOnlyList<DisplayMode> Parse(ReadOnlySpan<byte> edid)
    {
        if (edid.Length < 128 || edid.Length > 32768 || edid[18] != 1
            || !edid[..8].SequenceEqual(new byte[] { 0, 255, 255, 255, 255, 255, 255, 0 })
            || !Checksum(edid[..128])) { return []; }
        HashSet<DisplayMode> modes = [];
        AddStandard(edid.Slice(38, 16), edid[19], modes);
        DisplayMode[] established = [new(720, 400, 70), new(720, 400, 88), new(640, 480, 60),
            new(640, 480, 67), new(640, 480, 72), new(640, 480, 75), new(800, 600, 56), new(800, 600, 60),
            new(800, 600, 72), new(800, 600, 75), new(832, 624, 75), new(1024, 768, 87),
            new(1024, 768, 60), new(1024, 768, 70), new(1024, 768, 75), new(1280, 1024, 75), new(1152, 870, 75)];
        for (int bit = 0; bit < established.Length; bit++)
        {
            // The 1024x768@87 entry is interlaced, which DisplayMode cannot represent.
            if (bit != 11 && (edid[35 + bit / 8] & (128 >> (bit % 8))) != 0) { modes.Add(established[bit]); }
        }
        for (int offset = 54; offset + 18 <= 126; offset += 18)
        {
            ReadOnlySpan<byte> descriptor = edid.Slice(offset, 18);
            AddDetailed(descriptor, modes);
            if (descriptor[0] == 0 && descriptor[1] == 0 && descriptor[3] == 0xfa)
            { AddStandard(descriptor.Slice(5, 12), edid[19], modes); }
        }
        int blocks = Math.Min(edid[126], edid.Length / 128 - 1);
        for (int index = 1; index <= blocks; index++)
        {
            ReadOnlySpan<byte> block = edid.Slice(index * 128, 128);
            if (!Checksum(block)) { continue; }
            if (block[0] == 2) { AddCta(block, modes); }
            else if (block[0] == 0x70) { AddDisplayId(block, modes); }
        }
        return modes.OrderBy(mode => mode.Width).ThenBy(mode => mode.Height).ThenBy(mode => mode.RefreshHz).ToArray();
    }

    private static bool Checksum(ReadOnlySpan<byte> block)
    {
        int sum = 0;
        foreach (byte value in block) { sum += value; }
        return (sum & 255) == 0;
    }

    private static void AddStandard(ReadOnlySpan<byte> timings, int revision, HashSet<DisplayMode> modes)
    {
        for (int offset = 0; offset + 2 <= timings.Length; offset += 2)
        {
            if (timings[offset] < 2) { continue; }
            int width = (timings[offset] + 31) * 8;
            int height = (timings[offset + 1] >> 6) switch
            { 0 => revision < 3 ? width : width * 10 / 16, 1 => width * 3 / 4, 2 => width * 4 / 5, _ => width * 9 / 16 };
            modes.Add(new(width, height, (timings[offset + 1] & 63) + 60));
        }
    }

    private static void AddDetailed(ReadOnlySpan<byte> timing, HashSet<DisplayMode> modes)
    {
        int clock = timing[0] | (timing[1] << 8);
        if (clock == 0 || (timing[17] & 128) != 0) { return; }
        int width = timing[2] | ((timing[4] & 240) << 4);
        int height = timing[5] | ((timing[7] & 240) << 4);
        int blankX = timing[3] | ((timing[4] & 15) << 8);
        int blankY = timing[6] | ((timing[7] & 15) << 8);
        AddTiming(width, height, blankX, blankY, clock * 10000L, modes);
    }

    private static void AddTiming(int width, int height, int blankX, int blankY, long clock, HashSet<DisplayMode> modes)
    {
        if (width <= 0 || height <= 0 || blankX <= 0 || blankY <= 0) { return; }
        int hz = (int)Math.Round((double)clock / ((long)(width + blankX) * (height + blankY)));
        if (hz >= 2 && hz <= 1000) { modes.Add(new(width, height, hz)); }
    }

    private static void AddCta(ReadOnlySpan<byte> block, HashSet<DisplayMode> modes)
    {
        int end = block[2];
        if (end < 4 || end > 127) { return; }
        for (int offset = 4; offset < end;)
        {
            int tag = block[offset] >> 5, length = block[offset++] & 31;
            if (offset + length > end) { return; }
            if (tag == 2)
            {
                for (int index = offset; index < offset + length; index++)
                {
                    int vic = block[index] is >= 129 and <= 192 ? block[index] & 127 : block[index];
                    if (CtaMode(vic) is { } mode) { modes.Add(mode); }
                }
            }
            offset += length;
        }
        for (int offset = end; offset + 18 <= 127; offset += 18) { AddDetailed(block.Slice(offset, 18), modes); }
    }

    private static DisplayMode? CtaMode(int vic) => vic switch
    {
        1 => new(640, 480, 60),
        2 or 3 => new(720, 480, 60),
        4 => new(1280, 720, 60),
        16 => new(1920, 1080, 60),
        17 or 18 => new(720, 576, 50),
        19 => new(1280, 720, 50),
        31 => new(1920, 1080, 50),
        32 => new(1920, 1080, 24),
        33 => new(1920, 1080, 25),
        34 => new(1920, 1080, 30),
        41 => new(1280, 720, 100),
        42 or 43 => new(720, 576, 100),
        47 => new(1280, 720, 120),
        48 or 49 => new(720, 480, 120),
        52 or 53 => new(720, 576, 200),
        56 or 57 => new(720, 480, 240),
        60 => new(1280, 720, 24),
        61 => new(1280, 720, 25),
        62 => new(1280, 720, 30),
        63 => new(1920, 1080, 120),
        64 => new(1920, 1080, 100),
        >= 65 and <= 71 => new(1280, 720, VideoRate(vic - 65)),
        >= 72 and <= 78 => new(1920, 1080, VideoRate(vic - 72)),
        >= 79 and <= 85 => new(1680, 720, VideoRate(vic - 79)),
        >= 86 and <= 92 => new(2560, 1080, VideoRate(vic - 86)),
        >= 93 and <= 97 => new(3840, 2160, VideoRate(vic - 93)),
        >= 98 and <= 102 => new(4096, 2160, VideoRate(vic - 98)),
        >= 103 and <= 107 => new(3840, 2160, VideoRate(vic - 103)),
        108 or 109 => new(1280, 720, 48),
        110 => new(1680, 720, 48),
        111 or 112 => new(1920, 1080, 48),
        113 => new(2560, 1080, 48),
        114 or 116 => new(3840, 2160, 48),
        115 => new(4096, 2160, 48),
        117 or 119 => new(3840, 2160, 100),
        118 or 120 => new(3840, 2160, 120),
        _ => null,
    };

    private static int VideoRate(int index) => index switch { 0 => 24, 1 => 25, 2 => 30, 3 => 50, 4 => 60, 5 => 100, _ => 120 };

    private static void AddDisplayId(ReadOnlySpan<byte> block, HashSet<DisplayMode> modes)
    {
        int end = 5 + block[2];
        int version = block[1] >> 4;
        if (version is not (1 or 2) || end > 126 || !Checksum(block.Slice(1, end))) { return; }
        for (int offset = 5; offset + 3 <= end;)
        {
            int tag = block[offset], length = block[offset + 2];
            offset += 3;
            if (offset + length > end) { return; }
            if (((version == 1 && tag == 3) || (version == 2 && tag == 0x22)) && length % 20 == 0)
            {
                for (int index = offset; index < offset + length; index += 20)
                {
                    ReadOnlySpan<byte> timing = block.Slice(index, 20);
                    if ((timing[3] & 16) != 0) { continue; }
                    long clock = 1 + (timing[0] | (timing[1] << 8) | (timing[2] << 16));
                    AddTiming(Word(timing, 4) + 1, Word(timing, 12) + 1,
                        Word(timing, 6) + 1, Word(timing, 14) + 1, clock * (tag == 3 ? 10000 : 1000), modes);
                }
            }
            offset += length;
        }
    }

    private static int Word(ReadOnlySpan<byte> data, int offset) => data[offset] | (data[offset + 1] << 8);
}
