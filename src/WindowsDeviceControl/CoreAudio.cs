using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsDeviceControl;

/// <summary>Core Audio endpoint enumeration, default-device selection and master volume.</summary>
/// <remarks>
/// Methods returning <see cref="int"/> return an HRESULT: zero is success, anything else is the
/// failure Core Audio reported, so a caller can log or branch on the real reason rather than a
/// thrown exception. Audio devices appear and disappear underneath you, and a failure here is
/// usually a device that vanished rather than a bug.
/// <para>
/// <see cref="SetDefaultEndpoint(string)"/> goes through <c>IPolicyConfig</c>, which Microsoft has never
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

    /// <summary>The three Windows roles that can independently select a default endpoint.</summary>
    public enum AudioRole
    {
        /// <summary>System sounds and applications that ask for the console default.</summary>
        Console,

        /// <summary>Media playback applications.</summary>
        Multimedia,

        /// <summary>Voice and communications applications.</summary>
        Communications,
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

    /// <summary>Both flows a device container can expose endpoints in.</summary>
    private static readonly DataFlow[] EndpointFlows = [DataFlow.Render, DataFlow.Capture];

    private static readonly AudioRole[] DefaultRoles =
        [AudioRole.Console, AudioRole.Multimedia, AudioRole.Communications];

    private static readonly object EnumeratorGate = new();
    private static IMMDeviceEnumerator? _enumerator;

    /// <summary>One active Core Audio endpoint.</summary>
    /// <param name="Id">The opaque endpoint identifier used when selecting it.</param>
    /// <param name="Name">The friendly name shown to the user.</param>
    /// <param name="IsDefault">Whether it is the current console default.</param>
    public readonly record struct AudioEndpoint(string Id, string Name, bool IsDefault);

    /// <summary>The apply and optional rollback result for one default-endpoint role.</summary>
    /// <param name="Role">The role updated.</param>
    /// <param name="ApplyHResult">The HRESULT returned while assigning the requested endpoint.</param>
    /// <param name="RollbackHResult">The HRESULT returned while restoring the previous endpoint;
    /// <see langword="null"/> when no rollback was needed.</param>
    public readonly record struct DefaultEndpointRoleResult(
        AudioRole Role,
        int ApplyHResult,
        int? RollbackHResult);

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
        ForEachEndpoint(Enumerator(), endpoint =>
        {
            if (TryReadContainer(endpoint) is { } container)
            {
                var active = endpoint.GetState(out var state) >= 0
                    && state == DeviceStateActive;
                groups[container] = groups.GetValueOrDefault(container) || active;
            }
            return false;
        });
        return groups.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new BluetoothAudioContainer(entry.Key, entry.Value))
            .ToArray();
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
        var enumerator = Enumerator();
        Exception? last = null;
        var matched = false;
        var sent = ForEachEndpoint(enumerator, endpoint =>
        {
            if (!string.Equals(TryReadContainer(endpoint), target, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            matched = true;
            try
            {
                SendBluetoothAudioOneShot(enumerator, endpoint, connect);
                return true;
            }
            catch (Exception ex)
            {
                last = ex;
                return false;
            }
        });
        if (sent)
        {
            return;
        }
        throw last ?? new InvalidOperationException(matched
            ? "No endpoint accepted the Bluetooth audio request."
            : "The Bluetooth device has no audio endpoint.");
    }

    /// <summary>Visits every endpoint of both flows in any state, releasing each after its visit.</summary>
    /// <returns>True when a visit stopped the walk by returning true.</returns>
    private static bool ForEachEndpoint(IMMDeviceEnumerator enumerator, Func<IMMDevice, bool> visit)
    {
        foreach (var flow in EndpointFlows)
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
                        if (collection.Item(index, out endpoint) >= 0 && endpoint is not null
                            && visit(endpoint))
                        {
                            return true;
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
        return false;
    }

    private static string? TryReadContainer(IMMDevice endpoint)
        => ReadStringProperty(endpoint, DeviceContainerIdKey, static value => value.GuidValue?.ToString("D"));

    private static void SendBluetoothAudioOneShot(
        IMMDeviceEnumerator enumerator,
        IMMDevice endpoint,
        bool connect)
    {
        IDeviceTopology? topology = null;
        IConnector? connector = null;
        IMMDevice? adapter = null;
        IKsControl? control = null;
        try
        {
            Marshal.ThrowExceptionForHR(Activate(endpoint, DeviceTopologyId, out topology));
            if (topology is null)
            {
                throw new InvalidCastException("The endpoint does not expose IDeviceTopology.");
            }
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
            Marshal.ThrowExceptionForHR(Activate(adapter, KsControlId, out control));
            if (control is null)
            {
                throw new InvalidCastException("The audio adapter does not expose IKsControl.");
            }
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
        var call = new VolumeCall { Command = command };
        var result = WithDefaultVolume(AudioDirection.Render, ref call, ApplyVolumeCommand);
        percentage = call.Percentage;
        muted = call.Muted;
        return result;
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
        if (!IsDirection(direction))
        {
            return InvalidArgument;
        }

        var call = new VolumeCall();
        var result = WithDefaultVolume(direction, ref call,
            static (IAudioEndpointVolume volume, ref VolumeCall state)
                => ReadVolume(volume, out state.Percentage, out state.Muted));
        percentage = call.Percentage;
        muted = call.Muted;
        return result;
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
        if (!IsDirection(direction))
        {
            return InvalidArgument;
        }

        var call = new VolumeCall { Percentage = Math.Clamp(percentage, 0, 100) };
        var result = WithDefaultVolume(direction, ref call, SetVolumeLevel);
        muted = call.Muted;
        return result;
    }

    /// <summary>Sets the default playback endpoint's mute state.</summary>
    /// <param name="muted">True to mute, false to unmute. This sets the state rather than
    /// toggling, so repeating the call is harmless.</param>
    /// <returns>Zero on success, otherwise the HRESULT Core Audio returned.</returns>
    public static int SetMuted(bool muted)
    {
        var call = new VolumeCall { Mute = muted };
        return WithDefaultVolume(AudioDirection.Render, ref call,
            static (IAudioEndpointVolume volume, ref VolumeCall state) => volume.SetMute(state.Mute, 0));
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
        if (!IsDirection(direction))
        {
            return InvalidArgument;
        }

        IMMDeviceCollection? collection = null;
        IMMDevice? defaultDevice = null;
        try
        {
            var enumerator = Enumerator();
            var dataFlow = (DataFlow)direction;
            var result = enumerator.EnumAudioEndpoints(
                dataFlow,
                DeviceStateActive,
                out collection);
            string? defaultId = null;
            if (result >= 0
                && enumerator.GetDefaultAudioEndpoint(
                    dataFlow,
                    AudioRole.Console,
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
                try
                {
                    if (collection.Item(index, out device) < 0 || device is null
                        || device.GetId(out var id) < 0 || string.IsNullOrEmpty(id))
                    {
                        continue;
                    }
                    var name = ReadStringProperty(device, DeviceFriendlyNameKey, static value => value.StringValue)
                        is { Length: > 0 } friendlyName
                        ? friendlyName
                        : "Audio device";
                    records.Add(new AudioEndpoint(
                        id,
                        name,
                        string.Equals(defaultId, id, StringComparison.Ordinal)));
                }
                finally
                {
                    Release(device);
                }
            }
            records.Sort(CompareEndpoints);
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
        }
    }

    /// <summary>Makes one endpoint the default for every role.</summary>
    /// <param name="endpointId">The endpoint identifier from
    /// <see cref="ListEndpoints(AudioDirection, out IReadOnlyList{AudioEndpoint})"/>.</param>
    /// <returns>Zero on success, otherwise the HRESULT the policy interface returned.</returns>
    /// <remarks>
    /// Sets the console, multimedia and communications roles together, which is what a user means
    /// by "make this my speakers"; setting only one leaves applications split across devices.
    /// The current endpoint for all three roles is captured before any change. If a later role
    /// fails, every earlier role is restored in reverse order and every rollback is attempted;
    /// the original update HRESULT remains the return value.
    /// <para>
    /// This is the <c>IPolicyConfig</c> call — undocumented, never public, and the only way to do
    /// this from code. Its interface identifier differs across Windows versions, so a failure here
    /// on a future release is the expected way this breaks.
    /// </para>
    /// </remarks>
    public static int SetDefaultEndpoint(string endpointId)
        => SetDefaultEndpoint(endpointId, out _);

    /// <summary>Makes one endpoint the default for every role and reports each role operation.</summary>
    /// <param name="endpointId">The endpoint identifier from
    /// <see cref="ListEndpoints(AudioDirection, out IReadOnlyList{AudioEndpoint})"/>.</param>
    /// <param name="roleResults">The apply result for every attempted role and, when an apply
    /// fails, the result of restoring each role already changed.</param>
    /// <returns>Zero on success, otherwise the HRESULT from the failed apply operation. Inspect
    /// <paramref name="roleResults"/> for any additional rollback failures.</returns>
    /// <remarks>All previous role defaults are captured before any role is changed. A failure
    /// restores every role already changed in reverse order, attempting all rollbacks even if one
    /// of them fails.</remarks>
    public static int SetDefaultEndpoint(
        string endpointId,
        out IReadOnlyList<DefaultEndpointRoleResult> roleResults)
    {
        roleResults = [];
        if (string.IsNullOrEmpty(endpointId))
        {
            return InvalidArgument;
        }

        IPolicyConfig? policy = null;
        try
        {
            var enumerator = Enumerator();
            var previous = new Dictionary<AudioRole, string>();
            foreach (var role in DefaultRoles)
            {
                var snapshot = ReadDefaultEndpointId(enumerator, role, out var previousId);
                if (snapshot < 0 || string.IsNullOrEmpty(previousId))
                {
                    return snapshot < 0 ? snapshot : Failure;
                }
                previous.Add(role, previousId);
            }
            var activePolicy = (IPolicyConfig)(object)new PolicyConfigClient();
            policy = activePolicy;
            var result = ApplyDefaultEndpointTransaction(
                endpointId,
                previous,
                (id, role) => SetRoleDefault(activePolicy, id, role),
                out var updates);
            roleResults = updates;
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

    internal static int CompareEndpoints(AudioEndpoint left, AudioEndpoint right)
    {
        var comparison = right.IsDefault.CompareTo(left.IsDefault);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = StringComparer.Ordinal.Compare(left.Name, right.Name);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(left.Id, right.Id);
    }

    private static int ReadDefaultEndpointId(
        IMMDeviceEnumerator enumerator,
        AudioRole role,
        out string? endpointId)
    {
        endpointId = null;
        IMMDevice? device = null;
        try
        {
            var result = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role, out device);
            return result < 0 || device is null ? result : device.GetId(out endpointId);
        }
        finally
        {
            Release(device);
        }
    }

    private static int SetRoleDefault(
        IPolicyConfig policy,
        string endpointId,
        AudioRole role)
    {
        try
        {
            return policy.SetDefaultEndpoint(endpointId, role);
        }
        catch (COMException ex)
        {
            return ex.HResult;
        }
    }

    internal static int ApplyDefaultEndpointTransaction(
        string endpointId,
        IReadOnlyDictionary<AudioRole, string> previous,
        Func<string, AudioRole, int> setDefault,
        out IReadOnlyList<DefaultEndpointRoleResult> updates)
    {
        var results = new List<DefaultEndpointRoleResult>(DefaultRoles.Length);
        var applied = new List<AudioRole>(DefaultRoles.Length);
        var failure = 0;
        foreach (var role in DefaultRoles)
        {
            var result = setDefault(endpointId, role);
            results.Add(new DefaultEndpointRoleResult(role, result, null));
            if (result < 0)
            {
                failure = result;
                break;
            }
            applied.Add(role);
        }
        if (failure < 0)
        {
            for (var index = applied.Count - 1; index >= 0; index--)
            {
                var role = applied[index];
                var rollback = setDefault(previous[role], role);
                var resultIndex = results.FindIndex(item => item.Role == role);
                results[resultIndex] = results[resultIndex] with { RollbackHResult = rollback };
            }
        }
        updates = results;
        return failure;
    }

    private static bool IsDirection(AudioDirection direction)
        => direction is AudioDirection.Render or AudioDirection.Capture;

    /// <summary>The process-wide device enumerator, created on first use.</summary>
    /// <remarks>MMDeviceEnumerator is free-threaded. It is created on a multithreaded-apartment
    /// thread, so a first call from a UI thread cannot tie it to that thread's message loop. A
    /// failed creation is not remembered: the next call tries again, as a fresh enumerator per
    /// call did.</remarks>
    private static IMMDeviceEnumerator Enumerator()
    {
        if (Volatile.Read(ref _enumerator) is { } existing)
        {
            return existing;
        }
        lock (EnumeratorGate)
        {
            return _enumerator ??= Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA
                ? CreateEnumerator()
                : Task.Run(CreateEnumerator).GetAwaiter().GetResult();
        }
    }

    private static IMMDeviceEnumerator CreateEnumerator()
        => (IMMDeviceEnumerator)(object)new MMDeviceEnumerator();

    /// <summary>The inputs and results of one call on the default endpoint's volume.</summary>
    private struct VolumeCall
    {
        internal VolumeCommand Command;
        internal bool Mute;
        internal int Percentage;
        internal int Muted;
    }

    private delegate int VolumeAction(IAudioEndpointVolume volume, ref VolumeCall call);

    /// <summary>Opens the default endpoint's volume in one direction, runs one action on it and
    /// releases everything. A COM failure is returned as its HRESULT.</summary>
    private static int WithDefaultVolume(AudioDirection direction, ref VolumeCall call, VolumeAction action)
    {
        IMMDevice? device = null;
        IAudioEndpointVolume? volume = null;
        try
        {
            var result = OpenDefaultVolume(direction, out device, out volume);
            return result < 0 || volume is null ? result : action(volume, ref call);
        }
        catch (COMException ex)
        {
            return ex.HResult;
        }
        finally
        {
            Release(volume);
            Release(device);
        }
    }

    private static int ApplyVolumeCommand(IAudioEndpointVolume volume, ref VolumeCall call)
    {
        int result;
        switch (call.Command)
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
        return result < 0 ? result : ReadVolume(volume, out call.Percentage, out call.Muted);
    }

    private static int SetVolumeLevel(IAudioEndpointVolume volume, ref VolumeCall call)
    {
        var result = volume.SetMasterVolumeLevelScalar(call.Percentage / 100.0f, 0);
        if (result >= 0 && call.Percentage > 0)
        {
            result = volume.SetMute(false, 0);
        }
        if (result >= 0)
        {
            result = volume.GetMute(out var isMuted);
            call.Muted = isMuted ? 1 : 0;
        }
        return result;
    }

    private static int OpenDefaultVolume(
        AudioDirection direction,
        out IMMDevice? device,
        out IAudioEndpointVolume? volume)
    {
        volume = null;
        var result = Enumerator().GetDefaultAudioEndpoint(
            (DataFlow)direction,
            AudioRole.Console,
            out device);
        if (result < 0 || device is null)
        {
            return result;
        }
        result = Activate(device, AudioEndpointVolumeId, out volume);
        return result >= 0 && volume is null ? Failure : result;
    }

    /// <summary>Activates one interface on a device. An activation that succeeds without exposing
    /// <typeparamref name="T"/> is released and leaves the instance null.</summary>
    private static int Activate<T>(IMMDevice device, Guid interfaceId, out T? instance)
        where T : class
    {
        var result = device.Activate(ref interfaceId, ClsctxAll, 0, out var activated);
        instance = result >= 0 ? activated as T : null;
        if (instance is null)
        {
            Release(activated);
        }
        return result;
    }

    /// <summary>Reads one endpoint property, clearing the variant and releasing the store before
    /// returning.</summary>
    private static string? ReadStringProperty(
        IMMDevice endpoint,
        PropertyKey key,
        Func<PropVariant, string?> read)
    {
        IPropertyStore? store = null;
        var value = default(PropVariant);
        try
        {
            return endpoint.OpenPropertyStore(StorageModeRead, out store) >= 0 && store is not null
                && store.GetValue(ref key, out value) >= 0
                ? read(value)
                : null;
        }
        finally
        {
            PropVariantClear(ref value);
            Release(store);
        }
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
            AudioRole role,
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
            AudioRole role);

        [PreserveSig]
        int SetEndpointVisibility(nint deviceId, int visible);
    }
}
