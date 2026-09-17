using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WindowsDeviceControl;

// The endpoint default format: channel count and layout, sample rate and bit depth, as the
// Advanced tab and the speaker-setup wizard write it. Reads and writes go through IPolicyConfig,
// which the audio service applies to the running engine at once.
public static partial class CoreAudio
{
    /// <summary>The HRESULT Windows returns for a format the endpoint cannot run: AUDCLNT_E_UNSUPPORTED_FORMAT.</summary>
    public const int UnsupportedFormat = unchecked((int)0x88890008);

    private const ushort WaveFormatExtensibleTag = 0xFFFE;
    private const ushort WaveFormatExtensibleExtraSize = 22;
    private const uint ShareModeExclusive = 1;

    private static readonly Guid AudioClientId =
        new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");

    private static readonly Guid PcmSubFormat =
        new("00000001-0000-0010-8000-00AA00389B71");

    private static readonly Guid IeeeFloatSubFormat =
        new("00000003-0000-0010-8000-00AA00389B71");

    /// <summary>The formats the Advanced tab offers, in the order it lists them.</summary>
    private static readonly int[] CandidateChannelCounts = [1, 2, 4, 6, 8];

    private static readonly int[] CandidateSampleRates = [44100, 48000, 88200, 96000, 176400, 192000];

    private static readonly int[] CandidateBitDepths = [16, 24, 32];

    /// <summary>Reads one endpoint's current default format.</summary>
    /// <param name="endpointId">
    ///     The endpoint identifier from
    ///     <see cref="ListEndpoints(AudioDirection, out IReadOnlyList{AudioEndpoint})" />.
    /// </param>
    /// <param name="format">The format read; default when the call fails.</param>
    /// <returns>Zero on success, otherwise the HRESULT the policy interface returned.</returns>
    /// <remarks>
    ///     This is the shared-mode default format shown on the endpoint's Advanced tab and
    ///     written by the speaker-setup wizard, so its channel count and mask are the layout the
    ///     endpoint currently plays: 2 and 0x3 for stereo, 6 and 0x3F for 5.1, 8 and 0x63F for 7.1.
    /// </remarks>
    public static int GetDeviceFormat(string endpointId, out AudioDeviceFormat format)
    {
        format = default;
        if (string.IsNullOrEmpty(endpointId))
        {
            return InvalidArgument;
        }

        IPolicyConfig? policy = null;
        var native = nint.Zero;
        try
        {
            policy = (IPolicyConfig)(object)new PolicyConfigClient();
            var result = policy.GetDeviceFormat(endpointId, 0, out native);
            if (result < 0)
            {
                return result;
            }

            return TryReadFormat(native, out format) ? 0 : Failure;
        }
        catch (COMException ex)
        {
            return ex.HResult;
        }
        finally
        {
            Marshal.FreeCoTaskMem(native);
            Release(policy);
        }
    }

    /// <summary>Sets one endpoint's default format: channel layout, sample rate and bit depth.</summary>
    /// <param name="endpointId">
    ///     The endpoint identifier from
    ///     <see cref="ListEndpoints(AudioDirection, out IReadOnlyList{AudioEndpoint})" />.
    /// </param>
    /// <param name="format">
    ///     The format to run. Build it with <see cref="AudioDeviceFormat.Pcm" /> or take one from
    ///     <see cref="ListSupportedDeviceFormats" />.
    /// </param>
    /// <returns>
    ///     Zero on success. <see cref="UnsupportedFormat" /> when the endpoint cannot run the
    ///     format, in which case nothing changed. Otherwise the HRESULT the policy interface
    ///     returned.
    /// </returns>
    /// <remarks>
    ///     The audio service validates the format against the driver and applies it to the
    ///     running engine at once; open streams are restarted on the new format, which is the same
    ///     glitch the Advanced tab causes. A stereo endpoint asked for six channels answers
    ///     <see cref="UnsupportedFormat" /> and keeps its format, so there is nothing to roll back.
    ///     <para>
    ///         This is the undocumented <c>IPolicyConfig</c> call, used for the same reason as
    ///         <see cref="SetDefaultEndpoint(string)" />: there is no public way to do it.
    ///     </para>
    /// </remarks>
    public static int SetDeviceFormat(string endpointId, AudioDeviceFormat format)
    {
        if (string.IsNullOrEmpty(endpointId) || !format.IsPlausible)
        {
            return InvalidArgument;
        }

        IPolicyConfig? policy = null;
        var endpointFormat = nint.Zero;
        var mixFormat = nint.Zero;
        try
        {
            policy = (IPolicyConfig)(object)new PolicyConfigClient();
            endpointFormat = AllocateFormat(BuildFormat(format));
            // The engine mixes in 32-bit float at the endpoint's channel count and rate; that is
            // what Windows itself pairs with every default format it writes.
            mixFormat = AllocateFormat(BuildFormat(format.AsMixFormat()));
            return policy.SetDeviceFormat(endpointId, endpointFormat, mixFormat);
        }
        catch (COMException ex)
        {
            return ex.HResult;
        }
        finally
        {
            Marshal.FreeCoTaskMem(mixFormat);
            Marshal.FreeCoTaskMem(endpointFormat);
            Release(policy);
        }
    }

    /// <summary>Lists the default formats one endpoint accepts, as the Advanced tab would offer them.</summary>
    /// <param name="endpointId">
    ///     The endpoint identifier from
    ///     <see cref="ListEndpoints(AudioDirection, out IReadOnlyList{AudioEndpoint})" />.
    /// </param>
    /// <param name="formats">
    ///     The accepted formats, channel count first, then sample rate, then bit depth; empty
    ///     when the call fails.
    /// </param>
    /// <returns>Zero on success, otherwise the HRESULT Core Audio returned.</returns>
    /// <remarks>
    ///     Each candidate from 1 to 8 channels, 44.1 to 192 kHz and 16 to 32 bits is offered to
    ///     the driver in exclusive mode, which is the documented question behind that tab. The
    ///     answer tells you which channel layouts an HDMI or USB endpoint can be switched to before
    ///     <see cref="SetDeviceFormat" /> is asked. The probe opens no stream and changes nothing.
    /// </remarks>
    public static int ListSupportedDeviceFormats(string endpointId, out IReadOnlyList<AudioDeviceFormat> formats)
    {
        var accepted = new List<AudioDeviceFormat>();
        formats = accepted;
        if (string.IsNullOrEmpty(endpointId))
        {
            return InvalidArgument;
        }

        IMMDevice? device = null;
        IAudioClient? client = null;
        var candidate = nint.Zero;
        try
        {
            var result = Enumerator().GetDevice(endpointId, out device);
            if (result < 0 || device is null)
            {
                return result < 0 ? result : Failure;
            }

            result = Activate(device, AudioClientId, out client);
            if (result < 0 || client is null)
            {
                return result < 0 ? result : Failure;
            }

            candidate = Marshal.AllocCoTaskMem(Marshal.SizeOf<WaveFormatExtensible>());
            foreach (var channels in CandidateChannelCounts)
            {
                foreach (var rate in CandidateSampleRates)
                {
                    foreach (var bits in CandidateBitDepths)
                    {
                        var format = AudioDeviceFormat.Pcm(channels, rate, bits);
                        Marshal.StructureToPtr(BuildFormat(format), candidate, false);
                        var closest = nint.Zero;
                        var verdict = client.IsFormatSupported(ShareModeExclusive, candidate, out closest);
                        Marshal.FreeCoTaskMem(closest);
                        if (verdict == 0)
                        {
                            accepted.Add(format);
                        }
                    }
                }
            }

            return 0;
        }
        catch (COMException ex)
        {
            return ex.HResult;
        }
        finally
        {
            Marshal.FreeCoTaskMem(candidate);
            Release(client);
            Release(device);
        }
    }

    /// <summary>The speaker mask Windows pairs with a channel count when the caller has no better one.</summary>
    internal static uint DefaultChannelMask(int channels)
    {
        return channels switch
        {
            1 => 0x4, // front centre
            2 => 0x3, // front left and right
            4 => 0x33, // quadraphonic
            6 => 0x3F, // 5.1
            8 => 0x63F, // 7.1
            _ => 0
        };
    }

    internal static WaveFormatExtensible BuildFormat(AudioDeviceFormat format)
    {
        var blockAlign = (ushort)(format.Channels * format.ContainerBitsPerSample / 8);
        return new WaveFormatExtensible
        {
            FormatTag = WaveFormatExtensibleTag,
            Channels = (ushort)format.Channels,
            SamplesPerSecond = (uint)format.SampleRate,
            AverageBytesPerSecond = (uint)format.SampleRate * blockAlign,
            BlockAlign = blockAlign,
            BitsPerSample = (ushort)format.ContainerBitsPerSample,
            ExtraSize = WaveFormatExtensibleExtraSize,
            ValidBitsPerSample = (ushort)format.BitsPerSample,
            ChannelMask = format.ChannelMask,
            SubFormat = format.IsFloat ? IeeeFloatSubFormat : PcmSubFormat
        };
    }

    internal static bool TryReadFormat(WaveFormatExtensible native, out AudioDeviceFormat format)
    {
        format = default;
        if (native.FormatTag == WaveFormatExtensibleTag && native.ExtraSize >= WaveFormatExtensibleExtraSize)
        {
            format = new AudioDeviceFormat(
                native.Channels,
                (int)native.SamplesPerSecond,
                native.ValidBitsPerSample,
                native.BitsPerSample,
                native.ChannelMask,
                native.SubFormat == IeeeFloatSubFormat);
            return true;
        }

        // Plain WAVEFORMATEX: PCM (1) or IEEE float (3), with no mask or valid-bits field.
        if (native.FormatTag is 1 or 3)
        {
            format = new AudioDeviceFormat(
                native.Channels,
                (int)native.SamplesPerSecond,
                native.BitsPerSample,
                native.BitsPerSample,
                DefaultChannelMask(native.Channels),
                native.FormatTag == 3);
            return true;
        }

        return false;
    }

    private static bool TryReadFormat(nint native, out AudioDeviceFormat format)
    {
        format = default;
        if (native == 0)
        {
            return false;
        }

        // Read the fixed header first: a plain WAVEFORMATEX is 18 bytes, and reading the
        // extensible tail past a driver's shorter allocation would read off its end.
        var header = Marshal.PtrToStructure<WaveFormat>(native);
        if (header.FormatTag == WaveFormatExtensibleTag && header.ExtraSize >= WaveFormatExtensibleExtraSize)
        {
            return TryReadFormat(Marshal.PtrToStructure<WaveFormatExtensible>(native), out format);
        }

        return TryReadFormat(new WaveFormatExtensible
        {
            FormatTag = header.FormatTag,
            Channels = header.Channels,
            SamplesPerSecond = header.SamplesPerSecond,
            AverageBytesPerSecond = header.AverageBytesPerSecond,
            BlockAlign = header.BlockAlign,
            BitsPerSample = header.BitsPerSample,
            ExtraSize = header.ExtraSize
        }, out format);
    }

    private static nint AllocateFormat(WaveFormatExtensible format)
    {
        var native = Marshal.AllocCoTaskMem(Marshal.SizeOf<WaveFormatExtensible>());
        Marshal.StructureToPtr(format, native, false);
        return native;
    }

    /// <summary>One endpoint default format: the channel layout, sample rate and bit depth it plays.</summary>
    /// <param name="Channels">The channel count: 2 for stereo, 6 for 5.1, 8 for 7.1.</param>
    /// <param name="SampleRate">The sample rate in hertz.</param>
    /// <param name="BitsPerSample">The audible bits per sample: 16, 24 or 32.</param>
    /// <param name="ContainerBitsPerSample">
    ///     The bits each sample occupies. Windows carries 24-bit audio in a 32-bit container,
    ///     which <see cref="Pcm" /> chooses for you.
    /// </param>
    /// <param name="ChannelMask">
    ///     The speaker positions, as the <c>SPEAKER_*</c> bits: 0x3 stereo, 0x3F 5.1, 0x63F 7.1.
    /// </param>
    /// <param name="IsFloat">
    ///     Whether samples are IEEE float rather than integer PCM. Default formats are integer;
    ///     the float form is what the engine mixes in.
    /// </param>
    public readonly record struct AudioDeviceFormat(
        int Channels,
        int SampleRate,
        int BitsPerSample,
        int ContainerBitsPerSample,
        uint ChannelMask,
        bool IsFloat)
    {
        /// <summary>Builds an integer PCM format with Windows' usual container and speaker mask.</summary>
        /// <param name="channels">The channel count: 1, 2, 4, 6 or 8 get their standard mask.</param>
        /// <param name="sampleRate">The sample rate in hertz.</param>
        /// <param name="bitsPerSample">16, 24 or 32 audible bits; 24 is carried in a 32-bit container.</param>
        /// <param name="channelMask">
        ///     A speaker mask to use instead of the standard one for
        ///     <paramref name="channels" />; zero picks the standard mask.
        /// </param>
        public static AudioDeviceFormat Pcm(int channels, int sampleRate, int bitsPerSample, uint channelMask = 0)
        {
            return new AudioDeviceFormat(
                channels,
                sampleRate,
                bitsPerSample,
                bitsPerSample == 24 ? 32 : bitsPerSample,
                channelMask != 0 ? channelMask : DefaultChannelMask(channels),
                false);
        }

        /// <summary>Whether the values describe a format Windows could be asked for at all.</summary>
        internal bool IsPlausible
            => Channels is > 0 and <= 32
               && SampleRate > 0
               && BitsPerSample is > 0 and <= 32
               && ContainerBitsPerSample >= BitsPerSample
               && ContainerBitsPerSample % 8 == 0;

        /// <summary>The 32-bit float form the engine mixes in for this channel count and rate.</summary>
        internal AudioDeviceFormat AsMixFormat()
        {
            return this with { BitsPerSample = 32, ContainerBitsPerSample = 32, IsFloat = true };
        }
    }
}
