using System;
using System.Runtime.InteropServices;

namespace WSGM.Interop;

/// <summary>One pre-opened waveOut stream for the short volume feedback cue.</summary>
internal sealed partial class WaveOutFeedback : IDisposable
{
    private const uint WaveMapper = uint.MaxValue;
    private const ushort PcmFormat = 1;
    private const uint SampleRate = 44100;
    private const uint DurationMilliseconds = 80;
    private const uint HeaderInQueue = 0x10;

    private nint _output;
    private nint _samples;
    private nint _header;
    private bool _prepared;

    private WaveOutFeedback()
    {
    }

    /// <summary>Opens and prewarms the default waveOut endpoint.</summary>
    internal static int Open(out WaveOutFeedback? feedback)
    {
        feedback = new WaveOutFeedback();
        var result = feedback.OpenCore();
        if (result >= 0)
        {
            return result;
        }
        feedback.Dispose();
        feedback = null;
        return result;
    }

    /// <summary>Writes the cue unless the previous cue is still queued.</summary>
    internal int Play()
    {
        if (_output == 0 || _header == 0)
        {
            return 1;
        }
        var header = Marshal.PtrToStructure<WaveHeader>(_header);
        if ((header.Flags & HeaderInQueue) != 0)
        {
            return 1;
        }
        var result = WaveOutWrite(_output, _header, (uint)Marshal.SizeOf<WaveHeader>());
        return HResultFromMultimedia(result);
    }

    private int OpenCore()
    {
        var format = new WaveFormat
        {
            FormatTag = PcmFormat,
            Channels = 1,
            SamplesPerSecond = SampleRate,
            BitsPerSample = 16,
            BlockAlign = 2,
            AverageBytesPerSecond = SampleRate * 2,
        };
        var result = WaveOutOpen(out _output, WaveMapper, ref format, 0, 0, 0);
        if (result != 0)
        {
            _output = 0;
            return HResultFromMultimedia(result);
        }

        var samples = BuildSamples();
        _samples = Marshal.AllocHGlobal(samples.Length * sizeof(short));
        Marshal.Copy(samples, 0, _samples, samples.Length);
        var header = new WaveHeader
        {
            Data = _samples,
            BufferLength = (uint)(samples.Length * sizeof(short)),
        };
        _header = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
        Marshal.StructureToPtr(header, _header, fDeleteOld: false);
        result = WaveOutPrepareHeader(_output, _header, (uint)Marshal.SizeOf<WaveHeader>());
        if (result != 0)
        {
            return HResultFromMultimedia(result);
        }
        _prepared = true;
        return 0;
    }

    /// <summary>Closes the stream and releases its stable unmanaged buffers.</summary>
    public void Dispose()
    {
        if (_output != 0)
        {
            WaveOutReset(_output);
            if (_prepared)
            {
                WaveOutUnprepareHeader(
                    _output,
                    _header,
                    (uint)Marshal.SizeOf<WaveHeader>());
                _prepared = false;
            }
            WaveOutClose(_output);
            _output = 0;
        }
        if (_header != 0)
        {
            Marshal.FreeHGlobal(_header);
            _header = 0;
        }
        if (_samples != 0)
        {
            Marshal.FreeHGlobal(_samples);
            _samples = 0;
        }
    }

    private static short[] BuildSamples()
    {
        var sampleCount = checked((int)(SampleRate * DurationMilliseconds / 1000));
        var samples = new short[sampleCount];
        var phase = 0.0;
        for (var index = 0; index < samples.Length; index++)
        {
            var time = (double)index / SampleRate;
            var progress = (double)index / samples.Length;
            var frequency = 520.0 - (190.0 * progress);
            phase += 2.0 * Math.PI * frequency / SampleRate;
            var attack = Math.Min(1.0, time / 0.012);
            var release = Math.Min(1.0, (1.0 - progress) / 0.35);
            var envelope = attack * release * release;
            var tone = Math.Sin(phase) + (0.28 * Math.Sin(phase * 0.5));
            samples[index] = (short)(tone * envelope * 5200.0);
        }
        return samples;
    }

    private static int HResultFromMultimedia(uint result)
        => result == 0 ? 0 : unchecked((int)(0x80070000 | (result & 0xFFFF)));

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormat
    {
        internal ushort FormatTag;
        internal ushort Channels;
        internal uint SamplesPerSecond;
        internal uint AverageBytesPerSecond;
        internal ushort BlockAlign;
        internal ushort BitsPerSample;
        internal ushort ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        internal nint Data;
        internal uint BufferLength;
        internal uint BytesRecorded;
        internal nint User;
        internal uint Flags;
        internal uint Loops;
        internal nint Next;
        internal nint Reserved;
    }

    [LibraryImport("winmm.dll")]
    private static partial uint WaveOutOpen(
        out nint output,
        uint deviceId,
        ref WaveFormat format,
        nint callback,
        nint instance,
        uint flags);

    [LibraryImport("winmm.dll")]
    private static partial uint WaveOutPrepareHeader(nint output, nint header, uint headerSize);

    [LibraryImport("winmm.dll")]
    private static partial uint WaveOutUnprepareHeader(nint output, nint header, uint headerSize);

    [LibraryImport("winmm.dll")]
    private static partial uint WaveOutWrite(nint output, nint header, uint headerSize);

    [LibraryImport("winmm.dll")]
    private static partial uint WaveOutReset(nint output);

    [LibraryImport("winmm.dll")]
    private static partial uint WaveOutClose(nint output);
}
