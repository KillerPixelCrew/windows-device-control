using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace WindowsDeviceControl;

/// <summary>Core Audio endpoint enumeration, default-device selection and master volume.</summary>
/// <remarks>
/// Methods returning <see cref="int"/> return an HRESULT: zero is success, anything else is the
/// failure Core Audio reported, so a caller can log or branch on the real reason rather than a
/// thrown exception. Audio devices appear and disappear underneath you, and a failure here is
/// usually a device that vanished rather than a bug.
/// <para>
/// <see cref="SetDefaultEndpoint"/> goes through <c>IPolicyConfig</c>, which Microsoft has never
/// documented and never made public. It is the only way to change the default playback device from
/// code, and it is used here because there is no alternative — not because it is supported.
/// </para>
/// </remarks>
public static partial class CoreAudio
{
    /// <summary>Which direction of audio endpoint an operation applies to.</summary>
    /// <remarks>The values match Core Audio's own <c>EDataFlow</c>, so they can be passed straight
    /// through to the underlying interfaces.</remarks>
    public enum AudioDirection
    {
        /// <summary>Playback endpoints — speakers, headphones, HDMI.</summary>
        Render = 0,

        /// <summary>Recording endpoints — microphones and line inputs.</summary>
        Capture = 1,
    }

    /// <summary>What a hardware volume key is asking for.</summary>
    /// <remarks>The values are Windows' own <c>APPCOMMAND_VOLUME_*</c> constants, so the command
    /// decoded from a <c>WM_APPCOMMAND</c> message can be cast directly to this enum.</remarks>
    public enum VolumeCommand
    {
        /// <summary>Mute if unmuted, unmute if muted.</summary>
        ToggleMute = 8,

        /// <summary>One step quieter, by Windows' own step size.</summary>
        StepDown = 9,

        /// <summary>One step louder, by Windows' own step size.</summary>
        StepUp = 10,
    }

    private const int ClsctxAll = 23;
    private const uint DeviceStateActive = 1;
    private const uint DeviceStateAll = 0x0000000F;
    private const uint StorageModeRead = 0;
    private const uint KsPropertyTypeGet = 1;
    private const int InvalidArgument = unchecked((int)0x80070057);
    private const int Failure = unchecked((int)0x80004005);

    private static readonly Guid AudioEndpointVolumeId =
        new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly Guid DeviceTopologyId =
        new("2A07407E-6497-4A18-9787-32F79BD0D98F");
    private static readonly Guid KsControlId =
        new("28F54685-06FD-11D2-B27A-00A0C9223196");
    private static readonly Guid BluetoothAudioPropertySet =
        new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
    private static readonly PropertyKey DeviceFriendlyNameKey = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        14);
    private static readonly PropertyKey DeviceContainerIdKey = new(
        new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"),
        2);

    /// <summary>One active Core Audio endpoint.</summary>
    /// <param name="Id">The opaque endpoint identifier used when selecting it.</param>
    /// <param name="Name">The friendly name shown to the user.</param>
    /// <param name="IsDefault">Whether it is the current console default.</param>
    public readonly record struct AudioEndpoint(string Id, string Name, bool IsDefault);

    /// <summary>One device container that exposes Core Audio endpoints.</summary>
    /// <param name="Container">The container identifier, which is what ties an audio endpoint back
    /// to the Bluetooth device it belongs to.</param>
    /// <param name="Active">Whether the container currently has an active endpoint — that is,
    /// whether the device is connected rather than merely paired.</param>
    public readonly record struct BluetoothAudioContainer(string Container, bool Active);

    /// <summary>Lists every audio endpoint container, including disconnected Bluetooth devices.</summary>
    /// <returns>One entry per container. A paired but disconnected Bluetooth headset appears with
    /// <see cref="BluetoothAudioContainer.Active"/> false, which is how you offer to reconnect
    /// it.</returns>
    public static IReadOnlyList<BluetoothAudioContainer> ListBluetoothAudioContainers()
    {
        var groups = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = CreateEnumerator();
            foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
            {
                IMMDeviceCollection? collection = null;
                try
                {
                    var result = enumerator.EnumAudioEndpoints(flow, DeviceStateAll, out collection);
                    Marshal.ThrowExceptionForHR(result);
                    if (collection is null)
                    {
                        continue;
                    }
                    Marshal.ThrowExceptionForHR(collection.GetCount(out var count));
                    for (var index = 0u; index < count; index++)
                    {
                        IMMDevice? endpoint = null;
                        try
                        {
                            if (collection.Item(index, out endpoint) < 0 || endpoint is null
                                || TryReadContainer(endpoint) is not { } container)
                            {
                                continue;
                            }
                            var active = endpoint.GetState(out var state) >= 0
                                && state == DeviceStateActive;
                            groups[container] = groups.GetValueOrDefault(container) || active;
                        }
                        finally
                        {
                            Release(endpoint);
                        }
                    }
                }
                finally
                {
                    Release(collection);
                }
            }
            return groups.Select(entry => new BluetoothAudioContainer(entry.Key, entry.Value))
                .ToArray();
        }
        finally
        {
            Release(enumerator);
        }
    }

    /// <summary>Connects or disconnects one paired Bluetooth audio device.</summary>
    /// <param name="containerId">The container identifier from
    /// <see cref="ListBluetoothAudioContainers"/>.</param>
    /// <param name="connect">True to connect, false to disconnect.</param>
    /// <remarks>The request is made to the audio endpoint's device topology; the device may take a
    /// moment to appear or disappear afterwards, so re-read the container list rather than assuming
    /// the change is immediate.</remarks>
    public static void SetBluetoothAudioConnection(string containerId, bool connect)
    {
        ArgumentException.ThrowIfNullOrEmpty(containerId);
        var target = containerId.Trim('{', '}').ToLowerInvariant();
        IMMDeviceEnumerator? enumerator = null;
        Exception? last = null;
        var matched = false;
        try
        {
            enumerator = CreateEnumerator();
            foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
            {
                IMMDeviceCollection? collection = null;
                try
                {
                    Marshal.ThrowExceptionForHR(
                        enumerator.EnumAudioEndpoints(flow, DeviceStateAll, out collection));
                    if (collection is null)
                    {
                        continue;
                    }
                    Marshal.ThrowExceptionForHR(collection.GetCount(out var count));
                    for (var index = 0u; index < count; index++)
                    {
                        IMMDevice? endpoint = null;
                        try
                        {
                            if (collection.Item(index, out endpoint) < 0 || endpoint is null
                                || !string.Equals(
                                    TryReadContainer(endpoint), target, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                            matched = true;
                            try
                            {
                                SendBluetoothAudioOneShot(enumerator, endpoint, connect);
                                return;
                            }
                            catch (Exception ex)
                            {
                                last = ex;
                            }
                        }
                        finally
                        {
                            Release(endpoint);
                        }
                    }
                }
                finally
                {
                    Release(collection);
                }
            }
        }
        finally
        {
            Release(enumerator);
        }
        throw last ?? new InvalidOperationException(matched
            ? "No endpoint accepted the Bluetooth audio request."
            : "The Bluetooth device has no audio endpoint.");
    }

    private static string? TryReadContainer(IMMDevice endpoint)
    {
        IPropertyStore? store = null;
        var value = default(PropVariant);
        try
        {
            if (endpoint.OpenPropertyStore(StorageModeRead, out store) < 0 || store is null)
            {
                return null;
            }
            var key = DeviceContainerIdKey;
            return store.GetValue(ref key, out value) >= 0
                ? value.GuidValue?.ToString("D")
                : null;
        }
        finally
        {
            PropVariantClear(ref value);
            Release(store);
        }
    }

    private static void SendBluetoothAudioOneShot(
        IMMDeviceEnumerator enumerator,
        IMMDevice endpoint,
        bool connect)
    {
        object? activated = null;
        IDeviceTopology? topology = null;
        IConnector? connector = null;
        IMMDevice? adapter = null;
        IKsControl? control = null;
        try
        {
            var topologyId = DeviceTopologyId;
            Marshal.ThrowExceptionForHR(endpoint.Activate(
                ref topologyId, ClsctxAll, 0, out activated));
            topology = activated as IDeviceTopology
                ?? throw new InvalidCastException("The endpoint does not expose IDeviceTopology.");
            activated = null;
            Marshal.ThrowExceptionForHR(topology.GetConnector(0, out connector));
            if (connector is null)
            {
                throw new InvalidOperationException("The endpoint has no topology connector.");
            }
            Marshal.ThrowExceptionForHR(connector.GetDeviceIdConnectedTo(out var adapterId));
            if (string.IsNullOrEmpty(adapterId))
            {
                throw new InvalidOperationException("The endpoint connector has no adapter device.");
            }
            Marshal.ThrowExceptionForHR(enumerator.GetDevice(adapterId, out adapter));
            if (adapter is null)
            {
                throw new InvalidOperationException("The audio adapter could not be opened.");
            }
            var ksControlId = KsControlId;
            Marshal.ThrowExceptionForHR(adapter.Activate(
                ref ksControlId, ClsctxAll, 0, out activated));
            control = activated as IKsControl
                ?? throw new InvalidCastException("The audio adapter does not expose IKsControl.");
            activated = null;
            var property = new KsProperty
            {
                Set = BluetoothAudioPropertySet,
                Id = connect ? 0u : 1u,
                Flags = KsPropertyTypeGet,
            };
            Marshal.ThrowExceptionForHR(control.KsProperty(
                ref property,
                (uint)Marshal.SizeOf<KsProperty>(),
                0,
                0,
                out _));
        }
        finally
        {
            Release(control);
            Release(adapter);
            Release(connector);
            Release(topology);
            Release(activated);
        }
    }

    /// <summary>Applies one hardware volume-key command to the default playback endpoint.</summary>
    /// <param name="command">What the key asked for. The values match the
    /// <c>APPCOMMAND_VOLUME_*</c> constants a <c>WM_APPCOMMAND</c> message carries, so the value
    /// decoded from that message can be cast straight to this enum; anything else is
    /// rejected.</param>
    /// <param name="percentage">The resulting volume, 0 to 100.</param>
    /// <param name="muted">The resulting mute state: non-zero when muted.</param>
    /// <returns>Zero on success, otherwise the HRESULT Core Audio returned.</returns>
    /// <remarks>This applies the same step Windows itself uses for a volume key, so a hardware
    /// button behaves identically to the built-in handling — which is the point: a step computed
    /// by hand lands on different values than the system's and makes the button feel wrong.
    /// </remarks>
    public static int ApplyCommand(VolumeCommand command, out int percentage, out int muted)
    {
        percentage = 0;
        muted = 0;
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioEndpointVolume? volume = null;
        try
        {
            var result = OpenDefaultVolume(
                AudioDirection.Render,
                out enumerator,
                out device,
                out volume);
            if (result < 0 || volume is null)
            {
                return result;
            }
            switch (command)
            {
                case VolumeCommand.ToggleMute:
                    result = volume.GetMute(out var isMuted);
                    if (result >= 0)
                    {
                        result = volume.SetMute(!isMuted, 0);
                    }
                    break;
                case VolumeCommand.StepDown:
                    result = volume.VolumeStepDown(0);
                    break;
                case VolumeCommand.StepUp:
                    result = volume.VolumeStepUp(0);
                    break;
                default:
                    result = InvalidArgument;
                    break;
            }
            return result < 0 ? result : ReadVolume(volume, out percentage, out muted);
        }
        catch (COMException ex)
        {
            return ex.HResult;
        }
        finally
        {
            Release(volume);
            Release(device);
            Release(enumerator);
        }
    }

    /// <summary>Reads the default playback endpoint's master volume and mute state.</summary>
    /// <param name="percentage">The current volume, 0 to 100.</param>
    /// <param name="muted">The current mute state: non-zero when muted.</param>
    /// <returns>Zero on success, otherwise the HRESULT Core Audio returned.</returns>
    public static int GetVolume(out int percentage, out int muted) =>
        GetVolume(AudioDirection.Render, out percentage, out muted);

    /// <summary>Reads the default endpoint's master volume and mute state in one direction.</summary>
    /// <param name="direction">Playback or recording endpoint.</param>
    /// <param name="percentage">The current volume, 0 to 100.</param>
    /// <param name="muted">The current mute state: non-zero when muted.</param>
    /// <returns>Zero on success, otherwise the HRESULT Core Audio returned.</returns>
    public static int GetVolume(
        AudioDirection direction,
        out int percentage,
        out int muted)
    {
        percentage = 0;
        muted = 0;
        if (direction is not AudioDirection.Render and not AudioDirection.Capture)
        {
            return InvalidArgument;
        }

        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioEndpointVolume? volume = null;
        try
        {
            var result = OpenDefaultVolume(direction, out enumerator, out device, out volume);
            return result < 0 || volume is null
                ? result
                : ReadVolume(volume, out percentage, out muted);
        }
        catch (COMException ex)
        {
            return ex.HResult;
        }
        finally
        {
            Release(volume);
            Release(device);
            Release(enumerator);
        }
    }

    /// <summary>Sets the default playback endpoint's master volume.</summary>
    /// <param name="percentage">The volume to set, 0 to 100. Values outside that range are
    /// clamped.</param>
    /// <param name="muted">The mute state afterwards: non-zero when muted. A positive volume
    /// also unmutes the endpoint.</param>
    /// <returns>Zero on success, otherwise the HRESULT Core Audio returned.</returns>
    public static int SetVolume(int percentage, out int muted) =>
        SetVolume(AudioDirection.Render, percentage, out muted);

    /// <summary>Sets the default endpoint's master volume in one direction.</summary>
    /// <param name="direction">Playback or recording endpoint.</param>
    /// <param name="percentage">The volume to set, 0 to 100. Values outside that range are
    /// clamped.</param>
    /// <param name="muted">The mute state afterwards: non-zero when muted. A positive volume
    /// also unmutes the endpoint.</param>
    /// <returns>Zero on success, otherwise the HRESULT Core Audio returned.</returns>
    public static int SetVolume(
        AudioDirection direction,
        int percentage,
        out int muted)
    {
        muted = 0;
        if (direction is not AudioDirection.Render and not AudioDirection.Capture)
        {
            return InvalidArgument;
        }

        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioEndpointVolume? volume = null;
        try
        {
            percentage = Math.Clamp(percentage, 0, 100);
            var result = OpenDefaultVolume(direction, out enumerator, out device, out volume);
            if (result >= 0 && volume is not null)
            {
                result = volume.SetMasterVolumeLevelScalar(percentage / 100.0f, 0);
            }
            if (result >= 0 && percentage > 0 && volume is not null)
            {
                result = volume.SetMute(false, 0);
            }
            if (result >= 0 && volume is not null)
            {
                result = volume.GetMute(out var isMuted);
                muted = isMuted ? 1 : 0;
            }
            return result;
        }
        catch (COMException ex)
        {
            return ex.HResult;
        }
        finally
        {
            Release(volume);
            Release(device);
            Release(enumerator);
        }
    }

    /// <summary>Sets the default playback endpoint's mute state.</summary>
    /// <param name="muted">True to mute, false to unmute. This sets the state rather than
    /// toggling, so repeating the call is harmless.</param>
    /// <returns>Zero on success, otherwise the HRESULT Core Audio returned.</returns>
    public static int SetMuted(bool muted)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioEndpointVolume? volume = null;
        try
        {
            var result = OpenDefaultVolume(
                AudioDirection.Render,
                out enumerator,
                out device,
                out volume);
            return result < 0 || volume is null
                ? result
                : volume.SetMute(muted, 0);
        }
        catch (COMException ex)
        {
            return ex.HResult;
        }
        finally
        {
            Release(volume);
            Release(device);
            Release(enumerator);
        }
    }

    /// <summary>Lists the active audio endpoints in one direction.</summary>
    /// <param name="direction">Playback or recording endpoints.</param>
    /// <param name="endpoints">The endpoints found, newest state; empty when the call fails.</param>
    /// <returns>Zero on success, otherwise the HRESULT Core Audio returned.</returns>
    public static int ListEndpoints(
        AudioDirection direction,
        out IReadOnlyList<AudioEndpoint> endpoints)
    {
        var records = new List<AudioEndpoint>();
        endpoints = records;
        if (direction is not AudioDirection.Render and not AudioDirection.Capture)
        {
            return InvalidArgument;
        }

        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;
        IMMDevice? defaultDevice = null;
        try
        {
            enumerator = CreateEnumerator();
            var dataFlow = direction == AudioDirection.Render ? DataFlow.Render : DataFlow.Capture;
            var result = enumerator.EnumAudioEndpoints(
                dataFlow,
                DeviceStateActive,
                out collection);
            string? defaultId = null;
            if (result >= 0
                && enumerator.GetDefaultAudioEndpoint(
                    dataFlow,
                    Role.Console,
                    out defaultDevice) >= 0
                && defaultDevice is not null)
            {
                defaultDevice.GetId(out defaultId);
            }
            if (result < 0 || collection is null)
            {
                return result;
            }
            result = collection.GetCount(out var count);
            if (result < 0)
            {
                return result;
            }
            for (uint index = 0; index < count; index++)
            {
                IMMDevice? device = null;
                IPropertyStore? properties = null;
                var friendlyName = default(PropVariant);
                try
                {
                    var itemResult = collection.Item(index, out device);
                    if (itemResult < 0 || device is null)
                    {
                        continue;
                    }
                    itemResult = device.GetId(out var id);
                    if (itemResult < 0 || string.IsNullOrEmpty(id))
                    {
                        continue;
                    }
                    var name = "Audio device";
                    itemResult = device.OpenPropertyStore(StorageModeRead, out properties);
                    if (itemResult >= 0 && properties is not null)
                    {
                        var key = DeviceFriendlyNameKey;
                        itemResult = properties.GetValue(ref key, out friendlyName);
                        if (itemResult >= 0
                            && friendlyName.StringValue is string { Length: > 0 } value)
                        {
                            name = value;
                        }
                    }
                    records.Add(new AudioEndpoint(
                        id,
                        name,
                        string.Equals(defaultId, id, StringComparison.Ordinal)));
                }
                finally
                {
                    PropVariantClear(ref friendlyName);
                    Release(properties);
                    Release(device);
                }
            }
            records.Sort((left, right) => right.IsDefault.CompareTo(left.IsDefault));
            return result;
        }
        catch (COMException ex)
        {
            return ex.HResult;
        }
        finally
        {
            Release(defaultDevice);
            Release(collection);
            Release(enumerator);
        }
    }

    /// <summary>Makes one endpoint the default for every role.</summary>
    /// <param name="endpointId">The endpoint identifier from
    /// <see cref="ListEndpoints(AudioDirection, out IReadOnlyList{AudioEndpoint})"/>.</param>
    /// <returns>Zero on success, otherwise the HRESULT the policy interface returned.</returns>
    /// <remarks>
    /// Sets the console, multimedia and communications roles together, which is what a user means
    /// by "make this my speakers"; setting only one leaves applications split across devices.
    /// <para>
    /// This is the <c>IPolicyConfig</c> call — undocumented, never public, and the only way to do
    /// this from code. Its interface identifier differs across Windows versions, so a failure here
    /// on a future release is the expected way this breaks.
    /// </para>
    /// </remarks>
    public static int SetDefaultEndpoint(string endpointId)
    {
        if (string.IsNullOrEmpty(endpointId))
        {
            return InvalidArgument;
        }

        IPolicyConfig? policy = null;
        try
        {
            policy = (IPolicyConfig)(object)new PolicyConfigClient();
            var result = policy.SetDefaultEndpoint(endpointId, Role.Console);
            if (result >= 0)
            {
                result = policy.SetDefaultEndpoint(endpointId, Role.Multimedia);
            }
            if (result >= 0)
            {
                result = policy.SetDefaultEndpoint(endpointId, Role.Communications);
            }
            return result;
        }
        catch (COMException ex)
        {
            return ex.HResult;
        }
        finally
        {
            Release(policy);
        }
    }

    private static IMMDeviceEnumerator CreateEnumerator()
        => (IMMDeviceEnumerator)(object)new MMDeviceEnumerator();

    private static int OpenDefaultVolume(
        AudioDirection direction,
        out IMMDeviceEnumerator? enumerator,
        out IMMDevice? device,
        out IAudioEndpointVolume? volume)
    {
        enumerator = CreateEnumerator();
        volume = null;
        var result = enumerator.GetDefaultAudioEndpoint(
            direction == AudioDirection.Render ? DataFlow.Render : DataFlow.Capture,
            Role.Console,
            out device);
        if (result < 0 || device is null)
        {
            return result;
        }
        var interfaceId = AudioEndpointVolumeId;
        result = device.Activate(
            ref interfaceId,
            ClsctxAll,
            0,
            out var activated);
        if (result >= 0)
        {
            volume = activated as IAudioEndpointVolume;
            if (volume is null)
            {
                Release(activated);
                return Failure;
            }
        }
        return result;
    }

    private static int ReadVolume(
        IAudioEndpointVolume volume,
        out int percentage,
        out int muted)
    {
        percentage = 0;
        muted = 0;
        var result = volume.GetMasterVolumeLevelScalar(out var scalar);
        if (result >= 0)
        {
            result = volume.GetMute(out var isMuted);
            if (result >= 0)
            {
                percentage = (int)((scalar * 100.0f) + 0.5f);
                muted = isMuted ? 1 : 0;
            }
        }
        return result;
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(ref PropVariant value);

    private enum DataFlow
    {
        Render,
        Capture,
        All,
    }

    private enum Role
    {
        Console,
        Multimedia,
        Communications,
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        internal readonly Guid FormatId = formatId;
        internal readonly uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit)]
    private readonly struct PropVariant
    {
        [FieldOffset(0)]
        private readonly ushort _variantType;

        [FieldOffset(8)]
        private readonly nint _pointerValue;

        internal string? StringValue
            => _variantType == 31 && _pointerValue != 0
                ? Marshal.PtrToStringUni(_pointerValue)
                : null;

        internal Guid? GuidValue
            => _variantType == 72 && _pointerValue != 0
                ? Marshal.PtrToStructure<Guid>(_pointerValue)
                : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KsProperty
    {
        internal Guid Set;
        internal uint Id;
        internal uint Flags;
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumerator;

    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private sealed class PolicyConfigClient;

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(
            DataFlow dataFlow,
            uint stateMask,
            out IMMDeviceCollection? devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(
            DataFlow dataFlow,
            Role role,
            out IMMDevice? endpoint);

        [PreserveSig]
        int GetDevice(
            [MarshalAs(UnmanagedType.LPWStr)] string id,
            out IMMDevice? device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(nint client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(nint client);
    }

    [ComImport]
    // IID_IMMDeviceCollection from mmdeviceapi.h. A wrong IID here is invisible until runtime:
    // EnumAudioEndpoints succeeds natively, then the interop QI for the declared IID answers
    // E_NOINTERFACE and every endpoint enumeration throws InvalidCastException.
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(uint index, out IMMDevice? device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid interfaceId,
            uint classContext,
            nint activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object? instance);

        [PreserveSig]
        int OpenPropertyStore(uint storageAccess, out IPropertyStore? properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string? id);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint propertyCount);

        [PreserveSig]
        int GetAt(uint propertyIndex, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig]
        int RegisterControlChangeNotify(nint notify);

        [PreserveSig]
        int UnregisterControlChangeNotify(nint notify);

        [PreserveSig]
        int GetChannelCount(out uint channelCount);

        [PreserveSig]
        int SetMasterVolumeLevel(float level, nint eventContext);

        [PreserveSig]
        int SetMasterVolumeLevelScalar(float level, nint eventContext);

        [PreserveSig]
        int GetMasterVolumeLevel(out float level);

        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float level);

        [PreserveSig]
        int SetChannelVolumeLevel(uint channel, float level, nint eventContext);

        [PreserveSig]
        int SetChannelVolumeLevelScalar(uint channel, float level, nint eventContext);

        [PreserveSig]
        int GetChannelVolumeLevel(uint channel, out float level);

        [PreserveSig]
        int GetChannelVolumeLevelScalar(uint channel, out float level);

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, nint eventContext);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);

        [PreserveSig]
        int GetVolumeStepInfo(out uint step, out uint stepCount);

        [PreserveSig]
        int VolumeStepUp(nint eventContext);

        [PreserveSig]
        int VolumeStepDown(nint eventContext);

        [PreserveSig]
        int QueryHardwareSupport(out uint hardwareSupportMask);

        [PreserveSig]
        int GetVolumeRange(out float minimum, out float maximum, out float increment);
    }

    [ComImport]
    [Guid("2A07407E-6497-4A18-9787-32F79BD0D98F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceTopology
    {
        [PreserveSig]
        int GetConnectorCount(out uint count);

        [PreserveSig]
        int GetConnector(uint index, out IConnector? connector);

        [PreserveSig]
        int GetSubunitCount(out uint count);

        [PreserveSig]
        int GetSubunit(uint index, out nint subunit);

        [PreserveSig]
        int GetPartById(uint id, out nint part);

        [PreserveSig]
        int GetDeviceId([MarshalAs(UnmanagedType.LPWStr)] out string? deviceId);

        [PreserveSig]
        int GetSignalPath(nint from, nint to, int rejectMixedPaths, out nint parts);
    }

    [ComImport]
    [Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IConnector
    {
        [PreserveSig]
        int GetType(out int connectorType);

        [PreserveSig]
        int GetDataFlow(out int flow);

        [PreserveSig]
        int ConnectTo(IConnector other);

        [PreserveSig]
        int Disconnect();

        [PreserveSig]
        int IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);

        [PreserveSig]
        int GetConnectedTo(out IConnector? connector);

        [PreserveSig]
        int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string? connectorId);

        [PreserveSig]
        int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string? deviceId);
    }

    [ComImport]
    [Guid("28F54685-06FD-11D2-B27A-00A0C9223196")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IKsControl
    {
        [PreserveSig]
        int KsProperty(
            ref KsProperty property,
            uint propertyLength,
            nint propertyData,
            uint dataLength,
            out uint bytesReturned);

        [PreserveSig]
        int KsMethod(nint method, uint methodLength, nint methodData, uint dataLength, out uint bytesReturned);

        [PreserveSig]
        int KsEvent(nint eventData, uint eventLength, nint eventDataOut, uint dataLength, out uint bytesReturned);
    }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig]
        int GetMixFormat(nint deviceId, out nint format);

        [PreserveSig]
        int GetDeviceFormat(nint deviceId, int isDefault, out nint format);

        [PreserveSig]
        int ResetDeviceFormat(nint deviceId);

        [PreserveSig]
        int SetDeviceFormat(nint deviceId, nint endpointFormat, nint mixFormat);

        [PreserveSig]
        int GetProcessingPeriod(nint deviceId, int isDefault, out long defaultPeriod, out long minimumPeriod);

        [PreserveSig]
        int SetProcessingPeriod(nint deviceId, nint period);

        [PreserveSig]
        int GetShareMode(nint deviceId, nint mode);

        [PreserveSig]
        int SetShareMode(nint deviceId, nint mode);

        [PreserveSig]
        int GetPropertyValue(nint deviceId, ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetPropertyValue(nint deviceId, ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int SetDefaultEndpoint(
            [MarshalAs(UnmanagedType.LPWStr)] string endpointId,
            Role role);

        [PreserveSig]
        int SetEndpointVisibility(nint deviceId, int visible);
    }
}
