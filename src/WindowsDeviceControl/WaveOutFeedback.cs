using System;
using System.Runtime.InteropServices;

namespace WindowsDeviceControl;

/// <summary>One pre-opened waveOut stream for the short volume feedback cue.</summary>
/// <remarks>
///     Single owner and not thread-safe: <see cref="Play" /> and <see cref="Dispose" /> are called from
///     one thread at a time. The stream stays bound to the endpoint selected at Open; recreate it to
///     follow a changed default endpoint. Dispose every successfully opened owner.
/// </remarks>
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

    /// <summary>Attempts to close the stream and releases buffers only when the driver confirms teardown.</summary>
    /// <remarks>
    ///     Idempotent. If reset, unprepare or close fails, the buffers are deliberately retained for the
    ///     process lifetime rather than freed while a driver may still reference them. No failure is thrown.
    /// </remarks>
    public void Dispose()
    {
        // A failed teardown can leave winmm referencing both buffers; retain them rather than free live memory.
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
            // Repeated disposal must not free deliberately retained buffers.
            _header = 0;
            _samples = 0;
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

    /// <summary>Opens the default waveOut endpoint and prepares its cue buffers without playing.</summary>
    /// <param name="feedback">
    ///     The opened stream on success; <see langword="null" /> otherwise. The
    ///     caller owns it and must dispose it.
    /// </param>
    /// <returns>
    ///     Zero on success, otherwise an HRESULT built from the <c>MMRESULT</c> waveOut returned:
    ///     the Win32 facility with the <c>MMRESULT</c> in its low word, so it is not a Win32 error
    ///     code despite the facility.
    /// </returns>
    /// <remarks>
    ///     Synchronous; use a worker when opening on a latency-sensitive UI path. Keep the returned
    ///     owner for repeated cues, then dispose it. System volume and default endpoint selection are unchanged.
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
    ///     HRESULT containing the <c>MMRESULT</c> waveOut returned. After disposal, returns an HRESULT
    ///     containing MMSYSERR_ERROR (1) rather than throwing ObjectDisposedException.
    /// </returns>
    /// <remarks>
    ///     Returns after queueing, not after audible completion. An already queued cue is left unchanged.
    /// </remarks>
    public int Play()
    {
        if (_output == 0 || _header == 0)
        {
            return HResultFromMultimedia(1);
        }

        uint flags;
        unsafe
        {
            // The driver updates this stable unmanaged header while playback is queued.
            flags = ((WaveHeader*)_header)->Flags;
        }

        if (IsQueued(flags))
        {
            return 0;
        }

        var result = WaveOutWrite(_output, _header, HeaderSize);
        return HResultFromMultimedia(result);
    }

    private int OpenCore()
    {
        var format = new CoreAudio.WaveFormat
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
        ref CoreAudio.WaveFormat format,
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
