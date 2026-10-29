using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WindowsDeviceControl;

public static partial class CoreAudio
{
    /// <summary>The HRESULT Windows returns for a format the endpoint cannot run: AUDCLNT_E_UNSUPPORTED_FORMAT.</summary>
    public const int UnsupportedFormat = unchecked((int)0x88890008);

    private const ushort WaveFormatExtensibleTag = 0xFFFE;
    private const ushort WaveFormatExtensibleExtraSize = 22;
    private const uint ShareModeExclusive = 1;

    /// <summary>AUDCLNT_E_DEVICE_INVALIDATED: the endpoint went away.</summary>
    private const int DeviceInvalidated = unchecked((int)0x88890004);

    private const uint KsPropertyTypeSet = 2;
    private const uint PinProposeDataFormat = 14;
    private const uint TopologyNodes = 1;
    private const uint TopologyConnections = 2;
    private const uint FilterNode = uint.MaxValue;
    private const uint AudioEngineHostInput = 1;
    private const uint MultipleItemHeaderSize = 8;
    private const uint MaximumTopologyBytes = 64 * 1024;

    /// <summary>Endpoint, topology and wave filters, with room for an extension filter between them.</summary>
    private const int MaximumTopologyDevices = 8;

    private static readonly Guid PinPropertySet = new("8C134960-51AD-11CF-878A-94F801C10000");
    private static readonly Guid TopologyPropertySet = new("720D4AC0-7533-11D0-A5D6-28DB04C10000");

    /// <summary>KSNODETYPE_AUDIO_ENGINE: the hardware engine of an endpoint that supports offload.</summary>
    private static readonly Guid AudioEngineNode = new("35CAF6E4-F3B3-4168-BB4B-55E77A461C7E");

    private static readonly Guid AudioMajorFormat = new("73647561-0000-0010-8000-00AA00389B71");
    private static readonly Guid WaveFormatSpecifier = new("05589F81-C356-11CE-BF01-00AA0055595A");

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
    /// <returns>Zero on success; E_INVALIDARG for a null or empty ID; otherwise a failure HRESULT.</returns>
    /// <remarks>
    ///     Reads the shared-mode default, not an active stream's negotiated format. Unsupported native
    ///     format layouts return E_FAIL with a default output.
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
    ///     Zero on success; E_INVALIDARG for an empty ID or implausible numeric format.
    ///     <see cref="UnsupportedFormat" /> when the endpoint cannot run the
    ///     format, in which case nothing changed. Otherwise the HRESULT the policy interface
    ///     returned.
    /// </returns>
    /// <remarks>
    ///     Synchronously issues one IPolicyConfig request with the endpoint format and corresponding
    ///     float mix format. Windows validates support and can restart open streams on acceptance.
    ///     No readback, retry or rollback is performed; run this operation on a worker thread.
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
    ///     The accepted formats, including the current shared default even when exclusive-mode probing
    ///     refuses it, channel count first, then sample rate, then bit depth; empty
    ///     when the call fails.
    /// </param>
    /// <returns>
    ///     Zero on success, including no accepted candidates; E_INVALIDARG for an empty ID; otherwise
    ///     a failure HRESULT. A device invalidated or an
    ///     audio service that stopped during the probe fails the whole call with that HRESULT rather
    ///     than reporting the formats probed so far as complete.
    /// </returns>
    /// <remarks>
    ///     Candidates use 1, 2, 4, 6 or 8 channels; 44.1, 48, 88.2, 96, 176.4 or 192 kHz; and 16, 24 or
    ///     32 bits. This is a finite candidate set, not every possible driver format. Each is offered to
    ///     the driver in exclusive mode. An endpoint with a hardware audio engine (offload) answers
    ///     exclusive requests from its offload pin, so there each candidate is instead proposed to the
    ///     host pin the shared engine streams through, which is what a default-format write is
    ///     validated against. Individual candidate refusals are omitted, including when
    ///     exclusive mode is unavailable. The endpoint's actual shared default is retained because
    ///     an exclusive-mode refusal does not make its running shared format unsupported.
    ///     The synchronous probe opens no stream and changes no format;
    ///     run it on a worker thread. Later SetDeviceFormat requests can still be refused.
    /// </remarks>
    public static int ListSupportedDeviceFormats(string endpointId, out IReadOnlyList<AudioDeviceFormat> formats)
    {
        formats = [];
        if (string.IsNullOrEmpty(endpointId))
        {
            return InvalidArgument;
        }

        IMMDevice? device = null;
        IAudioClient? client = null;
        IKsControl? hostFilter = null;
        var candidate = nint.Zero;
        try
        {
            var enumerator = Enumerator();
            var result = ForgetEnumeratorIfGone(enumerator, enumerator.GetDevice(endpointId, out device));
            if (result < 0 || device is null)
            {
                return result < 0 ? result : Failure;
            }

            result = Activate(device, AudioClientId, out client);
            if (result < 0 || client is null)
            {
                return result < 0 ? result : Failure;
            }

            candidate = Marshal.AllocCoTaskMem(Marshal.SizeOf<KsDataFormat>() + Marshal.SizeOf<WaveFormatExtensible>());
            var buffer = candidate;
            Func<AudioDeviceFormat, int> verdict;
            hostFilter = FindHostPinFilter(enumerator, device, endpointId, out var hostPin);
            if (hostFilter is not null)
            {
                // Behind a hardware audio engine, exclusive streams are answered by the offload
                // pin, which can accept far more than the host pin the shared engine runs on.
                var pinControl = (IKsPinControl)hostFilter;
                verdict = format => ProposeHostPinFormat(pinControl, hostPin, buffer, format);
            }
            else
            {
                var probe = client;
                verdict = format =>
                {
                    Marshal.StructureToPtr(BuildFormat(format), buffer, false);
                    return probe.IsFormatSupported(ShareModeExclusive, buffer, 0);
                };
            }

            result = ProbeDeviceFormats(verdict, out var accepted);
            if (result == 0)
            {
                formats = IncludeCurrentDeviceFormat(accepted,
                    GetDeviceFormat(endpointId, out var current) >= 0 ? current : null);
            }

            return result;
        }
        catch (COMException ex)
        {
            return ex.HResult;
        }
        finally
        {
            Marshal.FreeCoTaskMem(candidate);
            Release(hostFilter);
            Release(client);
            Release(device);
        }
    }

    /// <summary>
    ///     Walks the endpoint's device topology to the filter whose hardware audio engine node has a
    ///     host input, and returns that filter with the pin feeding it.
    /// </summary>
    /// <param name="enumerator">The shared device enumerator.</param>
    /// <param name="endpoint">The open endpoint.</param>
    /// <param name="endpointId">The endpoint's own identifier, which the walk does not revisit.</param>
    /// <param name="pin">The host pin on success; zero otherwise.</param>
    /// <returns>
    ///     The filter's caller-owned control, or null when no reachable filter has an audio engine node,
    ///     which is every endpoint without hardware offload.
    /// </returns>
    private static IKsControl? FindHostPinFilter(
        IMMDeviceEnumerator enumerator,
        IMMDevice endpoint,
        string endpointId,
        out uint pin)
    {
        pin = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { endpointId };
        var pending = new Queue<string>();
        QueueConnectedDevices(endpoint, seen, pending);
        while (pending.Count > 0 && seen.Count <= MaximumTopologyDevices)
        {
            IMMDevice? filter = null;
            IKsControl? control = null;
            try
            {
                if (ForgetEnumeratorIfGone(enumerator, enumerator.GetDevice(pending.Dequeue(), out filter)) < 0
                    || filter is null)
                {
                    continue;
                }

                if (Activate(filter, KsControlId, out control) >= 0
                    && control is not null
                    && TryReadTopology(control, out var nodes, out var connections)
                    && SelectHostPin(nodes, connections) is { } found)
                {
                    pin = found;
                    var owned = control;
                    control = null;
                    return owned;
                }

                QueueConnectedDevices(filter, seen, pending);
            }
            finally
            {
                Release(control);
                Release(filter);
            }
        }

        return null;
    }

    private static void QueueConnectedDevices(IMMDevice device, HashSet<string> seen, Queue<string> pending)
    {
        IDeviceTopology? topology = null;
        try
        {
            if (Activate(device, DeviceTopologyId, out topology) < 0
                || topology is null
                || topology.GetConnectorCount(out var count) < 0)
            {
                return;
            }

            for (uint index = 0; index < count; index++)
            {
                IConnector? connector = null;
                try
                {
                    if (topology.GetConnector(index, out connector) >= 0
                        && connector is not null
                        && connector.IsConnected(out var connected) >= 0
                        && connected
                        && connector.GetDeviceIdConnectedTo(out var id) >= 0
                        && !string.IsNullOrEmpty(id)
                        && seen.Add(id))
                    {
                        pending.Enqueue(id);
                    }
                }
                finally
                {
                    Release(connector);
                }
            }
        }
        finally
        {
            Release(topology);
        }
    }

    /// <summary>Reads a KS filter's node types and connections.</summary>
    private static bool TryReadTopology(
        IKsControl control,
        out IReadOnlyList<Guid> nodes,
        out IReadOnlyList<KsTopologyConnection> connections)
    {
        nodes = [];
        connections = [];
        if (!TryReadMultipleItem(control, TopologyNodes, out var nodeData, out var nodeCount, itemSize: 16)
            || !TryReadMultipleItem(control, TopologyConnections, out var connectionData, out var connectionCount,
                itemSize: 16))
        {
            return false;
        }

        var nodeTypes = new Guid[nodeCount];
        for (var index = 0; index < nodeCount; index++)
        {
            nodeTypes[index] = new Guid(nodeData.AsSpan(16 * index, 16));
        }

        var links = new KsTopologyConnection[connectionCount];
        for (var index = 0; index < connectionCount; index++)
        {
            var offset = 16 * index;
            links[index] = new KsTopologyConnection(
                BitConverter.ToUInt32(connectionData, offset),
                BitConverter.ToUInt32(connectionData, offset + 4),
                BitConverter.ToUInt32(connectionData, offset + 8),
                BitConverter.ToUInt32(connectionData, offset + 12));
        }

        nodes = nodeTypes;
        connections = links;
        return true;
    }

    /// <summary>Reads one KSMULTIPLE_ITEM topology property: a size query, then the items.</summary>
    /// <param name="control">The filter.</param>
    /// <param name="id">The KSPROPSETID_Topology property.</param>
    /// <param name="items">The item bytes after the header.</param>
    /// <param name="count">The item count, bounded by the bytes returned.</param>
    /// <param name="itemSize">Bytes per item.</param>
    /// <returns>Whether the property was read.</returns>
    private static bool TryReadMultipleItem(IKsControl control, uint id, out byte[] items, out int count,
        int itemSize)
    {
        items = [];
        count = 0;
        var property = new KsProperty { Set = TopologyPropertySet, Id = id, Flags = KsPropertyTypeGet };
        var length = (uint)Marshal.SizeOf<KsProperty>();
        control.KsProperty(ref property, length, 0, 0, out var required);
        if (required < MultipleItemHeaderSize || required > MaximumTopologyBytes)
        {
            return false;
        }

        var buffer = Marshal.AllocCoTaskMem((int)required);
        try
        {
            if (control.KsProperty(ref property, length, buffer, required, out var returned) < 0
                || returned < MultipleItemHeaderSize
                || returned > required)
            {
                return false;
            }

            var data = new byte[returned];
            Marshal.Copy(buffer, data, 0, (int)returned);
            count = Math.Min(BitConverter.ToInt32(data, 4), (int)(returned - MultipleItemHeaderSize) / itemSize);
            if (count < 0)
            {
                return false;
            }

            items = data[(int)MultipleItemHeaderSize..];
            return true;
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    /// <summary>Finds the filter pin wired to an audio engine node's host input.</summary>
    /// <param name="nodes">The filter's node types, indexed by node id.</param>
    /// <param name="connections">The filter's topology connections.</param>
    /// <returns>
    ///     The host pin, or null when the filter has no audio engine node or nothing feeds its host
    ///     input. The engine's pins are fixed by Windows: 0 output, 1 host, 2 offload, 3 loopback.
    /// </returns>
    internal static uint? SelectHostPin(IReadOnlyList<Guid> nodes, IReadOnlyList<KsTopologyConnection> connections)
    {
        for (var node = 0; node < nodes.Count; node++)
        {
            if (nodes[node] != AudioEngineNode)
            {
                continue;
            }

            foreach (var connection in connections)
            {
                if (connection.FromNode == FilterNode
                    && connection.ToNode == (uint)node
                    && connection.ToNodePin == AudioEngineHostInput)
                {
                    return connection.FromNodePin;
                }
            }
        }

        return null;
    }

    /// <summary>Asks one pin whether it can stream a format, without opening it.</summary>
    /// <returns>Zero when the pin accepts the format; otherwise the HRESULT of its refusal.</returns>
    private static int ProposeHostPinFormat(IKsPinControl control, uint pin, nint buffer, AudioDeviceFormat format)
    {
        var wave = BuildFormat(format);
        var dataFormatSize = Marshal.SizeOf<KsDataFormat>();
        Marshal.StructureToPtr(new KsDataFormat
        {
            FormatSize = (uint)(dataFormatSize + Marshal.SizeOf<WaveFormatExtensible>()),
            SampleSize = wave.BlockAlign,
            MajorFormat = AudioMajorFormat,
            SubFormat = wave.SubFormat,
            Specifier = WaveFormatSpecifier
        }, buffer, false);
        Marshal.StructureToPtr(wave, buffer + dataFormatSize, false);
        var property = new KsPinProperty
        {
            Property = new KsProperty { Set = PinPropertySet, Id = PinProposeDataFormat, Flags = KsPropertyTypeSet },
            PinId = pin
        };
        return control.KsProperty(ref property, (uint)Marshal.SizeOf<KsPinProperty>(), buffer,
            (uint)(dataFormatSize + Marshal.SizeOf<WaveFormatExtensible>()), out _);
    }

    /// <summary>One KSTOPOLOGY_CONNECTION; <see cref="FilterNode" /> as a node names a filter pin.</summary>
    internal readonly record struct KsTopologyConnection(uint FromNode, uint FromNodePin, uint ToNode, uint ToNodePin);

    /// <summary>Retains the endpoint's actual shared default without inventing additional supported formats.</summary>
    /// <param name="accepted">Formats accepted by the driver probe.</param>
    /// <param name="current">Exact shared default, or null when the policy read failed.</param>
    /// <returns>Distinct actual/probed formats ordered by channels, sample rate and valid bit depth.</returns>
    internal static IReadOnlyList<AudioDeviceFormat> IncludeCurrentDeviceFormat(
        IReadOnlyList<AudioDeviceFormat> accepted, AudioDeviceFormat? current)
    {
        var formats = new List<AudioDeviceFormat>(accepted);
        if (current is { IsPlausible: true } format && !formats.Contains(format))
        {
            formats.Add(format);
        }

        formats.Sort(static (first, second) =>
        {
            var order = first.Channels.CompareTo(second.Channels);
            if (order == 0)
            {
                order = first.SampleRate.CompareTo(second.SampleRate);
            }

            return order == 0 ? first.BitsPerSample.CompareTo(second.BitsPerSample) : order;
        });
        return formats;
    }

    /// <summary>Offers every candidate format to the driver's verdict and keeps the accepted ones.</summary>
    /// <param name="verdict">Answers one candidate with the HRESULT <c>IsFormatSupported</c> returned.</param>
    /// <param name="formats">The accepted formats in candidate order; empty when the probe fails.</param>
    /// <returns>
    ///     Zero, or the HRESULT that says the device or the audio service went away, which ends
    ///     the probe at once. Every other refusal only skips its candidate: unsupported formats,
    ///     exclusive mode turned off for the device, and drivers that answer E_INVALIDARG for a
    ///     single layout.
    /// </returns>
    internal static int ProbeDeviceFormats(
        Func<AudioDeviceFormat, int> verdict,
        out IReadOnlyList<AudioDeviceFormat> formats)
    {
        formats = [];
        var accepted = new List<AudioDeviceFormat>();
        foreach (var channels in CandidateChannelCounts)
        {
            foreach (var rate in CandidateSampleRates)
            {
                foreach (var bits in CandidateBitDepths)
                {
                    var format = AudioDeviceFormat.Pcm(channels, rate, bits);
                    var answer = verdict(format);
                    if (answer == 0)
                    {
                        accepted.Add(format);
                    }
                    else if (answer is DeviceInvalidated or AudioServiceNotRunning)
                    {
                        return answer;
                    }
                }
            }
        }

        formats = accepted;
        return 0;
    }

    /// <summary>The speaker mask Windows pairs with a channel count when the caller has no better one.</summary>
    /// <param name="channels">PCM channel count.</param>
    /// <returns>The standard mask for 1, 2, 4, 6 or 8 channels; zero for other counts.</returns>
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

    /// <summary>Creates the native extensible wave-format packet without probing or validating it.</summary>
    /// <param name="format">Previously validated numeric sample format.</param>
    /// <returns>A packet containing PCM or IEEE-float subtype, speaker mask and derived byte rates.</returns>
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

    /// <summary>Decodes supported wave-format layouts without opening a device.</summary>
    /// <param name="native">Copied WAVEFORMATEX or WAVEFORMATEXTENSIBLE fields.</param>
    /// <param name="format">Decoded value on success; default for an unrecognized layout.</param>
    /// <returns>True for extensible, PCM or IEEE-float layouts; this does not establish driver acceptance.</returns>
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
    /// <param name="BitsPerSample">Valid bits per sample; the candidate list uses 16, 24 or 32.</param>
    /// <param name="ContainerBitsPerSample">
    ///     The bits each sample occupies. Windows carries 24-bit audio in a 32-bit container,
    ///     which <see cref="Pcm" /> chooses for you.
    /// </param>
    /// <param name="ChannelMask">
    ///     The speaker positions, as the <c>SPEAKER_*</c> bits: 0x3 stereo, 0x3F 5.1, 0x63F 7.1.
    /// </param>
    /// <param name="IsFloat">
    ///     Whether samples are IEEE float rather than integer PCM. Pcm creates integer formats;
    ///     the engine's corresponding mix format is 32-bit float.
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
        /// <returns>An integer PCM value; this factory does not query an endpoint or validate driver support.</returns>
        /// <remarks>Unrecognized channel counts get a zero mask unless an explicit mask is supplied.</remarks>
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
        /// <returns>A copy retaining channels, sample rate and speaker mask, with 32-bit floating-point samples.</returns>
        internal AudioDeviceFormat AsMixFormat()
        {
            return this with { BitsPerSample = 32, ContainerBitsPerSample = 32, IsFloat = true };
        }
    }
}
