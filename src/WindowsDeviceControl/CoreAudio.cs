using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

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

    private const uint DeviceStateActive = 1;
    private const int InvalidArgument = unchecked((int)0x80070057);
    private const int Failure = unchecked((int)0x80004005);

    private static readonly Guid AudioEndpointVolumeId =
        new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly PropertyKey DeviceFriendlyNameKey = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        14);

    private static readonly AudioRole[] DefaultRoles =
        [AudioRole.Console, AudioRole.Multimedia, AudioRole.Communications];

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
}
