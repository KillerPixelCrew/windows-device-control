using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WindowsDeviceControl;

/// <summary>Core Audio endpoint enumeration, default-device selection and master volume.</summary>
/// <remarks>
///     Calls are synchronous; run driver and service operations on a worker thread. Integer results
///     are HRESULTs: nonnegative means success, negative means failure. Outputs are usable only on
///     success unless documented otherwise. Default-volume operations use the Console role.
///     <para>Default selection and device formats use the undocumented IPolicyConfig interface.</para>
/// </remarks>
public static partial class CoreAudio
{
    /// <summary>Which direction of audio endpoint an operation applies to.</summary>
    /// <remarks>
    ///     The values match Core Audio's own <c>EDataFlow</c>, so they can be passed straight
    ///     through to the underlying interfaces.
    /// </remarks>
    public enum AudioDirection
    {
        /// <summary>Playback endpoints — speakers, headphones, HDMI.</summary>
        Render = 0,

        /// <summary>Recording endpoints — microphones and line inputs.</summary>
        Capture = 1
    }

    /// <summary>The three Windows roles that can independently select a default endpoint.</summary>
    public enum AudioRole
    {
        /// <summary>System sounds and applications that ask for the console default.</summary>
        Console,

        /// <summary>Media playback applications.</summary>
        Multimedia,

        /// <summary>Voice and communications applications.</summary>
        Communications
    }

    /// <summary>What a hardware volume key is asking for.</summary>
    /// <remarks>
    ///     The values are Windows' own <c>APPCOMMAND_VOLUME_*</c> constants, so the command
    ///     decoded from a <c>WM_APPCOMMAND</c> message can be cast directly to this enum.
    /// </remarks>
    public enum VolumeCommand
    {
        /// <summary>Mute if unmuted, unmute if muted.</summary>
        ToggleMute = 8,

        /// <summary>One step quieter, by Windows' own step size.</summary>
        StepDown = 9,

        /// <summary>One step louder, by Windows' own step size.</summary>
        StepUp = 10
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

    /// <summary>Applies one hardware volume-key command to the default playback endpoint.</summary>
    /// <param name="command">
    ///     The APPCOMMAND_VOLUME_* operation. An undefined value returns E_INVALIDARG.
    /// </param>
    /// <param name="percentage">The resulting volume, 0 to 100, on success; ignore on failure.</param>
    /// <param name="muted">The resulting mute state, nonzero when muted, on success; ignore on failure.</param>
    /// <returns>Zero on success, otherwise the HRESULT Core Audio returned.</returns>
    /// <remarks>
    ///     Uses Windows' native volume step. Output reads follow an accepted command, so an error
    ///     can follow an applied change. Do not automatically repeat the command on failure.
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
    /// <param name="percentage">The current volume, 0 to 100, on success; zero on failure.</param>
    /// <param name="muted">The current mute state, nonzero when muted, on success; zero on failure.</param>
    /// <returns>Zero on success, otherwise the HRESULT Core Audio returned.</returns>
    public static int GetVolume(out int percentage, out int muted)
    {
        return GetVolume(AudioDirection.Render, out percentage, out muted);
    }

    /// <summary>Reads the default endpoint's master volume and mute state in one direction.</summary>
    /// <param name="direction">Playback or recording endpoint; an undefined value returns E_INVALIDARG.</param>
    /// <param name="percentage">The current volume, 0 to 100, on success; zero on failure.</param>
    /// <param name="muted">The current mute state, nonzero when muted, on success; zero on failure.</param>
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
            static (volume, ref state)
                => ReadVolume(volume, out state.Percentage, out state.Muted));
        percentage = call.Percentage;
        muted = call.Muted;
        return result;
    }

    /// <summary>Sets the default playback endpoint's master volume.</summary>
    /// <param name="percentage">
    ///     The volume to set, 0 to 100. Values outside that range are
    ///     clamped.
    /// </param>
    /// <param name="muted">
    ///     The mute state afterwards, nonzero when muted, on success; ignore on failure.
    ///     A positive volume also unmutes the endpoint.
    /// </param>
    /// <returns>Zero on success, otherwise the HRESULT Core Audio returned.</returns>
    /// <remarks>
    ///     Writes volume, unmutes when the clamped volume is positive, then reads mute for the output.
    ///     A later failure does not roll back an earlier write. Do not infer that an error means the
    ///     endpoint stayed unchanged.
    /// </remarks>
    public static int SetVolume(int percentage, out int muted)
    {
        return SetVolume(AudioDirection.Render, percentage, out muted);
    }

    /// <summary>Sets the default endpoint's master volume in one direction.</summary>
    /// <param name="direction">Playback or recording endpoint; an undefined value returns E_INVALIDARG.</param>
    /// <param name="percentage">
    ///     The volume to set, 0 to 100. Values outside that range are
    ///     clamped.
    /// </param>
    /// <param name="muted">
    ///     The mute state afterwards, nonzero when muted, on success; ignore on failure.
    ///     A positive volume also unmutes the endpoint.
    /// </param>
    /// <returns>Zero on success, otherwise the HRESULT Core Audio returned.</returns>
    /// <remarks>
    ///     Writes volume, unmutes when the clamped volume is positive, then reads mute for the output.
    ///     A later failure does not roll back an earlier write. Do not infer that an error means the
    ///     endpoint stayed unchanged.
    /// </remarks>
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
    /// <param name="muted">
    ///     True to mute, false to unmute.
    /// </param>
    /// <returns>Zero on success, otherwise the HRESULT Core Audio returned.</returns>
    /// <remarks>Issues one write without a confirming read.</remarks>
    public static int SetMuted(bool muted)
    {
        var call = new VolumeCall { Mute = muted };
        return WithDefaultVolume(AudioDirection.Render, ref call,
            static (volume, ref state) => volume.SetMute(state.Mute, 0));
    }

    /// <summary>Reads volume and mute from a specific endpoint without following the default.</summary>
    /// <param name="endpointId">An endpoint ID; null, empty or whitespace returns E_INVALIDARG.</param>
    /// <param name="percentage">Volume percentage, 0 to 100, on success; zero on failure.</param>
    /// <param name="muted">Nonzero when muted on success; zero on failure.</param>
    /// <returns>Zero on success, otherwise the HRESULT.</returns>
    public static int GetVolume(string endpointId, out int percentage, out int muted)
    {
        var call = new VolumeCall();
        var result = WithEndpointVolume(endpointId, ref call,
            static (volume, ref state) => ReadVolume(volume, out state.Percentage, out state.Muted));
        percentage = call.Percentage;
        muted = call.Muted;
        return result;
    }

    /// <summary>Sets one endpoint's mute state without changing another default endpoint.</summary>
    /// <param name="endpointId">An endpoint ID; null, empty or whitespace returns E_INVALIDARG.</param>
    /// <param name="muted">The state to write.</param>
    /// <returns>Zero on success, otherwise the HRESULT.</returns>
    /// <remarks>Issues one write to this endpoint without following later default changes or reading back.</remarks>
    public static int SetMuted(string endpointId, bool muted)
    {
        var call = new VolumeCall { Mute = muted };
        return WithEndpointVolume(endpointId, ref call,
            static (volume, ref state) => volume.SetMute(state.Mute, 0));
    }

    /// <summary>Lists the active audio endpoints in one direction.</summary>
    /// <param name="direction">Playback or recording endpoints; an undefined value returns E_INVALIDARG.</param>
    /// <param name="endpoints">
    ///     Active endpoints, default first then ordered by name and ID; empty on failure. An unreadable
    ///     endpoint ID omits that endpoint. An unreadable friendly name is empty. A failed default lookup
    ///     leaves every IsDefault false without failing enumeration.
    /// </param>
    /// <returns>Zero on success, otherwise the HRESULT Core Audio returned.</returns>
    public static int ListEndpoints(
        AudioDirection direction,
        out IReadOnlyList<AudioEndpoint> endpoints)
    {
        endpoints = [];
        var records = new List<AudioEndpoint>();
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
            var result = ForgetEnumeratorIfGone(enumerator, enumerator.EnumAudioEndpoints(
                dataFlow,
                DeviceStateActive,
                out collection));
            string? defaultId = null;
            if (result >= 0
                && ForgetEnumeratorIfGone(enumerator, enumerator.GetDefaultAudioEndpoint(
                    dataFlow,
                    AudioRole.Console,
                    out defaultDevice)) >= 0
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
                                                               || device.GetId(out var id) < 0 ||
                                                               string.IsNullOrEmpty(id))
                    {
                        continue;
                    }

                    var name = ReadStringProperty(device, DeviceFriendlyNameKey, static value => value.StringValue)
                               ?? string.Empty;
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
            endpoints = records;
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

    /// <summary>Makes one render or capture endpoint the default for all three roles in its direction.</summary>
    /// <param name="endpointId">
    ///     The endpoint identifier from
    ///     <see cref="ListEndpoints(AudioDirection, out IReadOnlyList{AudioEndpoint})" />.
    /// </param>
    /// <returns>Zero on success, otherwise the HRESULT the policy interface returned.</returns>
    /// <remarks>
    ///     Captures Console, Multimedia and Communications defaults before writing. On failure, attempts
    ///     reverse-order rollback of every changed role without retry. Use the overload with role results
    ///     to inspect rollback failures. Accepted writes are not read back.
    /// </remarks>
    public static int SetDefaultEndpoint(string endpointId)
    {
        return SetDefaultEndpoint(endpointId, out _);
    }

    /// <summary>Makes one endpoint the default for every role and reports each role operation.</summary>
    /// <param name="endpointId">
    ///     The endpoint identifier from
    ///     <see cref="ListEndpoints(AudioDirection, out IReadOnlyList{AudioEndpoint})" />.
    /// </param>
    /// <param name="roleResults">
    ///     Every attempted role's apply HRESULT and any rollback HRESULT. Empty if validation or
    ///     capture failed before the first write. Contains partial results on an apply failure.
    /// </param>
    /// <returns>
    ///     Zero on success; E_INVALIDARG for an empty ID; otherwise the lookup, capture or apply
    ///     failure HRESULT. Inspect <paramref name="roleResults" /> for additional rollback failures.
    /// </returns>
    /// <remarks>
    ///     Captures all three defaults in the selected endpoint's direction before writing. An apply
    ///     failure attempts every changed role's rollback in reverse order, even after a rollback fails.
    ///     There is no process-wide transaction lock; callers serialize competing default changes.
    /// </remarks>
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
            // Capture the selected endpoint's flow so a microphone rollback cannot select speakers.
            var flowResult = ReadEndpointFlow(enumerator, endpointId, out var flow);
            if (flowResult < 0)
            {
                return flowResult;
            }

            var previous = new Dictionary<AudioRole, string>();
            foreach (var role in DefaultRoles)
            {
                var snapshot = ReadDefaultEndpointId(enumerator, flow, role, out var previousId);
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

    /// <summary>Orders endpoints by default status, display name and stable endpoint ID.</summary>
    /// <param name="left">First endpoint.</param>
    /// <param name="right">Second endpoint.</param>
    /// <returns>A comparison result placing default endpoints first and breaking name ties ordinally.</returns>
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

    private static int ReadEndpointFlow(
        IMMDeviceEnumerator enumerator,
        string endpointId,
        out DataFlow flow)
    {
        flow = DataFlow.Render;
        IMMDevice? device = null;
        try
        {
            var result = ForgetEnumeratorIfGone(enumerator, enumerator.GetDevice(endpointId, out device));
            if (result < 0)
            {
                return result;
            }

            return device is IMMEndpoint endpoint ? endpoint.GetDataFlow(out flow) : Failure;
        }
        finally
        {
            Release(device);
        }
    }

    private static int ReadDefaultEndpointId(
        IMMDeviceEnumerator enumerator,
        DataFlow flow,
        AudioRole role,
        out string? endpointId)
    {
        endpointId = null;
        IMMDevice? device = null;
        try
        {
            var result = ForgetEnumeratorIfGone(enumerator, enumerator.GetDefaultAudioEndpoint(flow, role, out device));
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

    /// <summary>Writes the three default roles and attempts reverse rollback after a negative HRESULT.</summary>
    /// <param name="endpointId">Endpoint to assign to every role.</param>
    /// <param name="previous">Complete pre-write role-to-endpoint snapshot, required for rollback.</param>
    /// <param name="setDefault">Synchronous single-write callback returning HRESULT; thrown exceptions propagate.</param>
    /// <param name="updates">Every attempted role and any rollback HRESULT, in apply order.</param>
    /// <returns>Zero after all roles accept, otherwise the first apply failure; rollback failures remain in updates.</returns>
    /// <remarks>The caller serializes competing transactions. No state is read back and rollback attempts continue after a negative rollback HRESULT.</remarks>
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
    {
        return direction is AudioDirection.Render or AudioDirection.Capture;
    }

    /// <summary>
    ///     Opens the named endpoint's volume, runs one action on it and
    ///     releases everything. A COM failure is returned as its HRESULT.
    /// </summary>
    private static int WithEndpointVolume(string endpointId, ref VolumeCall call, VolumeAction action)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            return InvalidArgument;
        }

        IMMDevice? device = null;
        IAudioEndpointVolume? volume = null;
        try
        {
            var enumerator = Enumerator();
            var result = ForgetEnumeratorIfGone(enumerator, enumerator.GetDevice(endpointId, out device));
            if (result < 0 || device is null)
            {
                return result < 0 ? result : Failure;
            }

            result = Activate(device, AudioEndpointVolumeId, out volume);
            if (result < 0 || volume is null)
            {
                return result < 0 ? result : Failure;
            }

            return action(volume, ref call);
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
        var enumerator = Enumerator();
        var result = ForgetEnumeratorIfGone(enumerator, enumerator.GetDefaultAudioEndpoint(
            (DataFlow)direction,
            AudioRole.Console,
            out device));
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
                percentage = (int)(scalar * 100.0f + 0.5f);
                muted = isMuted ? 1 : 0;
            }
        }

        return result;
    }

    /// <summary>One active Core Audio endpoint.</summary>
    /// <param name="Id">The opaque endpoint identifier used when selecting it.</param>
    /// <param name="Name">
    ///     The friendly name Windows reports, for display; empty when Windows has none, so the
    ///     caller chooses its own fallback wording.
    /// </param>
    /// <param name="IsDefault">Whether it is the current console default.</param>
    public readonly record struct AudioEndpoint(string Id, string Name, bool IsDefault);

    /// <summary>The apply and optional rollback result for one default-endpoint role.</summary>
    /// <param name="Role">The role updated.</param>
    /// <param name="ApplyHResult">The HRESULT returned while assigning the requested endpoint.</param>
    /// <param name="RollbackHResult">
    ///     The HRESULT returned while restoring the previous endpoint;
    ///     <see langword="null" /> when no rollback was needed.
    /// </param>
    public readonly record struct DefaultEndpointRoleResult(
        AudioRole Role,
        int ApplyHResult,
        int? RollbackHResult);

    /// <summary>The inputs and results of one call on the default endpoint's volume.</summary>
    private struct VolumeCall
    {
        internal VolumeCommand Command;
        internal bool Mute;
        internal int Percentage;
        internal int Muted;
    }

    private delegate int VolumeAction(IAudioEndpointVolume volume, ref VolumeCall call);
}
