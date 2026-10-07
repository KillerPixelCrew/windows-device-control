using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Windows.Media.Audio;

namespace WindowsDeviceControl;

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
        /// <summary>Windows accepted the requested default spatial format; no confirming read is performed.</summary>
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

    /// <summary>Reads one playback endpoint's spatial sound state.</summary>
    /// <param name="endpointId">
    ///     The endpoint identifier from
    ///     <see cref="ListEndpoints(AudioDirection, out IReadOnlyList{AudioEndpoint})" />.
    /// </param>
    /// <param name="state">The observed state; default on failure, with no usable format list.</param>
    /// <returns>Zero on success; E_INVALIDARG for a null or empty ID; otherwise the Windows failure HRESULT.</returns>
    /// <remarks>
    ///     Probes the known <see cref="SpatialAudioFormats" /> only; custom provider formats can still
    ///     appear in DefaultFormat or ActiveFormat. Support does not establish licensing. An existing
    ///     endpoint without spatial support returns success with IsSupported false and an empty list.
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
    ///     <see cref="SpatialAudioSetStatus.Succeeded" /> means the request was accepted.
    /// </param>
    /// <returns>
    ///     Zero when Windows answered, in which case <paramref name="status" /> is the answer;
    ///     otherwise the HRESULT of the failed call and <paramref name="status" /> is
    ///     <see cref="SpatialAudioSetStatus.UnknownError" />.
    /// </returns>
    /// <remarks>
    ///     Blocks for the WinRT operation; use a worker thread. Accepted requests apply to the running
    ///     engine without a service restart or confirming read. Support and licensing are separate:
    ///     Windows can answer LicenseNotValidForAudioEndpoint even for a supported format. No retry occurs.
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
            status = MapSpatialStatus((int)result.Status);
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
            var enumerator = Enumerator();
            var result = ForgetEnumeratorIfGone(enumerator, enumerator.GetDevice(endpointId, out device));
            return result < 0 ? result : device is null ? Failure : 0;
        }
        finally
        {
            Release(device);
        }
    }

    /// <summary>
    ///     Builds the WinRT device id for a Core Audio playback endpoint id. The WinRT spatial API
    ///     accepts any string and answers "unsupported, no format" for one it cannot resolve, so a
    ///     bare endpoint id fails silently rather than loudly; this is the form it resolves.
    /// </summary>
    /// <param name="endpointId">A non-null Core Audio playback endpoint ID or already-qualified WinRT device ID.</param>
    /// <returns>The qualified render-interface ID; already-qualified IDs are unchanged.</returns>
    internal static string ToWinRtDeviceId(string endpointId)
    {
        if (endpointId.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return endpointId;
        }

        return @"\\?\SWD#MMDEVAPI#" + endpointId + "#" + AudioRenderInterfaceClass;
    }

    /// <summary>
    ///     Maps Windows' set status onto <see cref="SpatialAudioSetStatus" />. A value this library
    ///     does not know is reported as <see cref="SpatialAudioSetStatus.UnknownError" /> rather than
    ///     as an undefined enum value.
    /// </summary>
    /// <param name="status">Raw SpatialAudioFormatConfigurationStatus value.</param>
    /// <returns>The corresponding known status, or UnknownError for an unrecognized value.</returns>
    internal static SpatialAudioSetStatus MapSpatialStatus(int status)
    {
        return status is >= (int)SpatialAudioSetStatus.Succeeded and <= (int)SpatialAudioSetStatus.UnknownError
            ? (SpatialAudioSetStatus)status
            : SpatialAudioSetStatus.UnknownError;
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
