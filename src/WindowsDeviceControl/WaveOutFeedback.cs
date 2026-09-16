using System;
using System.Runtime.InteropServices;

namespace WindowsDeviceControl;

/// <summary>One pre-opened waveOut stream for the short volume feedback cue.</summary>
public sealed partial class WaveOutFeedback : IDisposable
{
    private const uint WaveMapper = uint.MaxValue;
    private const ushort PcmFormat = 1;
    private const uint SampleRate = 44100;
    private const uint DurationMilliseconds = 80;
    private const uint HeaderInQueue = 0x10;
    private static readonly uint HeaderSize = (uint)Marshal.SizeOf<WaveHeader>();
    private nint _header;

    private nint _output;
    private bool _prepared;
    private nint _samples;

    private WaveOutFeedback()
    {
    }

    /// <summary>Closes the stream and releases its stable unmanaged buffers.</summary>
    public void Dispose()
    {
        // A device that disconnects mid-playback (Bluetooth or USB headset) makes reset, unprepare
        // or close return an error while winmm still holds these buffers. Freeing them anyway hands
        // the driver freed heap; leaking them instead is the safe trade, since the process either
        // owns them until exit or is discarding this one-shot stream regardless.
        var driverReleasedBuffers = true;
        if (_output != 0)
        {
            driverReleasedBuffers &= WaveOutReset(_output) == 0;
            if (_prepared)
            {
                driverReleasedBuffers &= WaveOutUnprepareHeader(_output, _header, HeaderSize) == 0;
                _prepared = false;
            }

            driverReleasedBuffers &= WaveOutClose(_output) == 0;
            _output = 0;
        }

        if (!driverReleasedBuffers)
        {
            return;
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

    /// <summary>Opens and prewarms the default waveOut endpoint.</summary>
    /// <param name="feedback">
    ///     The opened stream on success; <see langword="null" /> otherwise. The
    ///     caller owns it and must dispose it.
    /// </param>
    /// <returns>
    ///     Zero on success, otherwise an HRESULT containing the <c>MMRESULT</c> waveOut
    ///     returned.
    /// </returns>
    /// <remarks>
    ///     Opening is separated from playing on purpose: opening a waveOut endpoint takes
    ///     long enough to be audible as a delay, so the stream is opened once and kept.
    /// </remarks>
    public static int Open(out WaveOutFeedback? feedback)
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

    /// <summary>Plays the cue, unless the previous one is still queued.</summary>
    /// <returns>
    ///     Zero on success or when the previous cue is still playing, otherwise the
    ///     HRESULT containing the <c>MMRESULT</c> waveOut returned.
    /// </returns>
    /// <remarks>
    ///     Dropping the cue while one is queued is deliberate: holding a volume key repeats
    ///     faster than the sound lasts, and queueing every repeat turns the feedback into a
    ///     rattle.
    /// </remarks>
    public int Play()
    {
        if (_output == 0 || _header == 0)
        {
            return HResultFromMultimedia(1);
        }

        var header = Marshal.PtrToStructure<WaveHeader>(_header);
        if (IsQueued(header.Flags))
        {
            return 0;
        }

        var result = WaveOutWrite(_output, _header, HeaderSize);
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
            AverageBytesPerSecond = SampleRate * 2
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
            BufferLength = (uint)(samples.Length * sizeof(short))
        };
        _header = Marshal.AllocHGlobal((int)HeaderSize);
        Marshal.StructureToPtr(header, _header, false);
        result = WaveOutPrepareHeader(_output, _header, HeaderSize);
        if (result != 0)
        {
            return HResultFromMultimedia(result);
        }

        _prepared = true;
        return 0;
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
            var frequency = 520.0 - 190.0 * progress;
            phase += 2.0 * Math.PI * frequency / SampleRate;
            var attack = Math.Min(1.0, time / 0.012);
            var release = Math.Min(1.0, (1.0 - progress) / 0.35);
            var envelope = attack * release * release;
            var tone = Math.Sin(phase) + 0.28 * Math.Sin(phase * 0.5);
            samples[index] = (short)(tone * envelope * 5200.0);
        }

        return samples;
    }

    private static int HResultFromMultimedia(uint result)
    {
        return result == 0 ? 0 : unchecked((int)(0x80070000 | (result & 0xFFFF)));
    }

    internal static bool IsQueued(uint headerFlags)
    {
        return (headerFlags & HeaderInQueue) != 0;
    }

    [LibraryImport("winmm.dll", EntryPoint = "waveOutOpen")]
    private static partial uint WaveOutOpen(
        out nint output,
        uint deviceId,
        ref WaveFormat format,
        nint callback,
        nint instance,
        uint flags);

    [LibraryImport("winmm.dll", EntryPoint = "waveOutPrepareHeader")]
    private static partial uint WaveOutPrepareHeader(nint output, nint header, uint headerSize);

    [LibraryImport("winmm.dll", EntryPoint = "waveOutUnprepareHeader")]
    private static partial uint WaveOutUnprepareHeader(nint output, nint header, uint headerSize);

    [LibraryImport("winmm.dll", EntryPoint = "waveOutWrite")]
    private static partial uint WaveOutWrite(nint output, nint header, uint headerSize);

    [LibraryImport("winmm.dll", EntryPoint = "waveOutReset")]
    private static partial uint WaveOutReset(nint output);

    [LibraryImport("winmm.dll", EntryPoint = "waveOutClose")]
    private static partial uint WaveOutClose(nint output);

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
}
