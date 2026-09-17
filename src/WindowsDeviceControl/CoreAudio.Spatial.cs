using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Windows.Media.Audio;

namespace WindowsDeviceControl;

// Spatial sound (Windows Sonic, Dolby Atmos, DTS) per playback endpoint. This is the one Core Audio
// setting with a public API: Windows.Media.Audio.SpatialAudioDeviceConfiguration reads and writes
// the same state the Sound settings page does, and applies it to the running engine at once.
public static partial class CoreAudio
{
    /// <summary>The spatial sound formats Windows knows by name.</summary>
    /// <remarks>
    ///     These are the values <c>Windows.Media.Audio.SpatialAudioFormatSubtype</c> reports,
    ///     fixed here so a caller can persist and compare them without a WinRT call. Any other
    ///     GUID a device reports is a format registered by its own provider app and is passed
    ///     through unchanged.
    /// </remarks>
    public static class SpatialAudioFormats
    {
        /// <summary>Spatial sound off: plain stereo or multichannel output.</summary>
        public static readonly Guid Off = Guid.Empty;

        /// <summary>Windows Sonic for Headphones, shipped with Windows and free.</summary>
        public static readonly Guid WindowsSonic = new("B53D940C-B846-4831-9F76-D102B9B725A0");

        /// <summary>Dolby Atmos for Headphones, licensed through the Dolby Access app.</summary>
        public static readonly Guid DolbyAtmosForHeadphones = new("1459AC38-3875-49BF-BB59-0FE80F4D395D");

        /// <summary>Dolby Atmos for built-in speakers, licensed by the OEM or Dolby Access.</summary>
        public static readonly Guid DolbyAtmosForSpeakers = new("4C81E564-C8EF-4AD9-9F2C-9EF995533790");

        /// <summary>Dolby Atmos over HDMI to a receiver, licensed through the Dolby Access app.</summary>
        public static readonly Guid DolbyAtmosForHomeTheater = new("A289735D-FA3E-4E35-9D7D-B6F896ACB2E7");

        /// <summary>DTS Headphone:X, licensed through the DTS Sound Unbound app.</summary>
        public static readonly Guid DtsHeadphoneX = new("4444ACB0-8DC0-4C2C-A0D8-2C76DB470F86");

        /// <summary>DTS:X Ultra for speakers, licensed by the OEM or DTS Sound Unbound.</summary>
        public static readonly Guid DtsXUltra = new("ADAFD3C6-AC4C-404A-836A-9615E9060564");
    }

    /// <summary>Why a spatial sound change was accepted or refused.</summary>
    /// <remarks>The values match <c>Windows.Media.Audio.SetDefaultSpatialAudioFormatStatus</c>.</remarks>
    public enum SpatialAudioSetStatus
    {
        /// <summary>The format is now the endpoint's default and active format.</summary>
        Succeeded = 0,

        /// <summary>Windows refused the caller. Seen for formats reserved to their provider app.</summary>
        AccessDenied = 1,

        /// <summary>The format's licence has lapsed.</summary>
        LicenseExpired = 2,

        /// <summary>
        ///     No licence covers this format on this endpoint. This is the answer for Dolby and DTS
        ///     formats until their provider app has activated them.
        /// </summary>
        LicenseNotValidForAudioEndpoint = 3,

        /// <summary>The endpoint cannot carry this format at all.</summary>
        NotSupportedOnAudioEndpoint = 4,

        /// <summary>Windows gave no reason.</summary>
        UnknownError = 5
    }

    /// <summary>The device-interface class that turns a Core Audio render endpoint id into a WinRT device id.</summary>
    private const string AudioRenderInterfaceClass = "{e6327cad-dcec-4949-ae8a-991e976a79d2}";

    /// <summary>The device-interface class that turns a Core Audio capture endpoint id into a WinRT device id.</summary>
    private const string AudioCaptureInterfaceClass = "{2eef81be-33fa-4800-9670-1cd474972c3f}";

    /// <summary>Reads one playback endpoint's spatial sound state.</summary>
    /// <param name="endpointId">
    ///     The endpoint identifier from
    ///     <see cref="ListEndpoints(AudioDirection, out IReadOnlyList{AudioEndpoint})" />.
    /// </param>
    /// <param name="state">The state read; default when the call fails.</param>
    /// <returns>Zero on success, otherwise the HRESULT Windows returned.</returns>
    /// <remarks>
    ///     <see cref="SpatialAudioState.SupportedFormats" /> lists which of the
    ///     <see cref="SpatialAudioFormats" /> the endpoint can carry, not which are licensed;
    ///     <see cref="SetSpatialAudio" /> is where a missing licence is reported.
    /// </remarks>
    public static int GetSpatialAudio(string endpointId, out SpatialAudioState state)
    {
        state = default;
        if (string.IsNullOrEmpty(endpointId))
        {
            return InvalidArgument;
        }

        try
        {
            var known = CheckEndpointExists(endpointId);
            if (known < 0)
            {
                return known;
            }

            var configuration = SpatialAudioDeviceConfiguration.GetForDeviceId(ToWinRtDeviceId(endpointId));
            var supported = new List<Guid>();
            if (configuration.IsSpatialAudioSupported)
            {
                foreach (var format in KnownSpatialFormats)
                {
                    if (configuration.IsSpatialAudioFormatSupported(format.ToString("B")))
                    {
                        supported.Add(format);
                    }
                }
            }

            state = new SpatialAudioState(
                configuration.IsSpatialAudioSupported,
                ParseFormat(configuration.DefaultSpatialAudioFormat),
                ParseFormat(configuration.ActiveSpatialAudioFormat),
                supported);
            return 0;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            return ex.HResult != 0 ? ex.HResult : Failure;
        }
    }

    /// <summary>Selects one playback endpoint's spatial sound format, or turns it off.</summary>
    /// <param name="endpointId">
    ///     The endpoint identifier from
    ///     <see cref="ListEndpoints(AudioDirection, out IReadOnlyList{AudioEndpoint})" />.
    /// </param>
    /// <param name="format">
    ///     One of the <see cref="SpatialAudioFormats" />, or any format GUID the endpoint reported.
    ///     <see cref="SpatialAudioFormats.Off" /> turns spatial sound off.
    /// </param>
    /// <param name="status">
    ///     Windows' verdict when the call itself completed. Only
    ///     <see cref="SpatialAudioSetStatus.Succeeded" /> means the format changed.
    /// </param>
    /// <returns>
    ///     Zero when Windows answered, in which case <paramref name="status" /> is the answer;
    ///     otherwise the HRESULT of the failed call and <paramref name="status" /> is
    ///     <see cref="SpatialAudioSetStatus.UnknownError" />.
    /// </returns>
    /// <remarks>
    ///     The change applies to the running audio engine immediately; there is no service
    ///     restart and no re-read needed. Windows' documentation says the caller must be the app
    ///     that owns the format, but an ordinary desktop process switched Windows Sonic and a
    ///     licensed Dolby Atmos for Headphones on and off on Windows 11 build 26200. An unlicensed
    ///     Dolby or DTS format answers <see cref="SpatialAudioSetStatus.LicenseNotValidForAudioEndpoint" />
    ///     and leaves the endpoint as it was. This call blocks for the WinRT operation, so keep it
    ///     off the UI thread.
    /// </remarks>
    public static int SetSpatialAudio(string endpointId, Guid format, out SpatialAudioSetStatus status)
    {
        status = SpatialAudioSetStatus.UnknownError;
        if (string.IsNullOrEmpty(endpointId))
        {
            return InvalidArgument;
        }

        try
        {
            var known = CheckEndpointExists(endpointId);
            if (known < 0)
            {
                return known;
            }

            var configuration = SpatialAudioDeviceConfiguration.GetForDeviceId(ToWinRtDeviceId(endpointId));
            var result = configuration.SetDefaultSpatialAudioFormatAsync(format.ToString("B")).WaitWinRt();
            status = (SpatialAudioSetStatus)result.Status;
            return 0;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            return ex.HResult != 0 ? ex.HResult : Failure;
        }
    }

    /// <summary>The known formats in the order <see cref="SpatialAudioState.SupportedFormats" /> reports them.</summary>
    private static readonly Guid[] KnownSpatialFormats =
    [
        SpatialAudioFormats.WindowsSonic,
        SpatialAudioFormats.DolbyAtmosForHeadphones,
        SpatialAudioFormats.DolbyAtmosForSpeakers,
        SpatialAudioFormats.DolbyAtmosForHomeTheater,
        SpatialAudioFormats.DtsHeadphoneX,
        SpatialAudioFormats.DtsXUltra
    ];

    /// <summary>
    ///     Confirms Core Audio knows the endpoint. The WinRT spatial API answers "unsupported, no
    ///     format" for an id it cannot resolve instead of failing, which would make a vanished
    ///     device look like one without spatial sound.
    /// </summary>
    private static int CheckEndpointExists(string endpointId)
    {
        IMMDevice? device = null;
        try
        {
            var result = Enumerator().GetDevice(endpointId, out device);
            return result < 0 ? result : device is null ? Failure : 0;
        }
        finally
        {
            Release(device);
        }
    }

    /// <summary>
    ///     Builds the WinRT device id for a Core Audio endpoint id. The WinRT spatial API accepts
    ///     any string and answers "unsupported, no format" for one it cannot resolve, so a bare
    ///     endpoint id fails silently rather than loudly; this is the form it resolves.
    /// </summary>
    internal static string ToWinRtDeviceId(string endpointId, AudioDirection direction = AudioDirection.Render)
    {
        if (endpointId.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return endpointId;
        }

        var interfaceClass = direction == AudioDirection.Capture
            ? AudioCaptureInterfaceClass
            : AudioRenderInterfaceClass;
        return @"\\?\SWD#MMDEVAPI#" + endpointId + "#" + interfaceClass;
    }

    private static Guid ParseFormat(string? value)
    {
        return Guid.TryParse(value, out var format) ? format : Guid.Empty;
    }

    /// <summary>One playback endpoint's spatial sound state.</summary>
    /// <param name="IsSupported">Whether the endpoint can carry any spatial format.</param>
    /// <param name="DefaultFormat">
    ///     The format the user selected, or <see cref="SpatialAudioFormats.Off" />.
    /// </param>
    /// <param name="ActiveFormat">
    ///     The format the engine is running now. Windows can differ from
    ///     <paramref name="DefaultFormat" /> here, for instance while a licence check is pending.
    /// </param>
    /// <param name="SupportedFormats">
    ///     The <see cref="SpatialAudioFormats" /> the endpoint can carry. Licensing is not
    ///     part of this answer.
    /// </param>
    public readonly record struct SpatialAudioState(
        bool IsSupported,
        Guid DefaultFormat,
        Guid ActiveFormat,
        IReadOnlyList<Guid> SupportedFormats);
}
