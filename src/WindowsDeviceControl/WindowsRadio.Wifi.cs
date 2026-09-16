using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using static WindowsDeviceControl.Win32Error;

namespace WindowsDeviceControl;

public static unsafe partial class WindowsRadio
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(25);
    private static readonly object StatusClientLock = new();
    private static WlanClient? _statusClient;
    private static readonly object SavedProfileLock = new();
    private static readonly Dictionary<(Guid Adapter, string Name), SavedProfile> SavedProfiles = [];
    private static readonly Dictionary<Guid, string[]> SavedProfileNames = [];
    private static long _savedProfileGeneration;

    /// <summary>Reads the Wi-Fi adapter's current state and joined network.</summary>
    /// <returns>
    ///     The state, signal and network name. On a machine with several adapters this
    ///     reports the one Windows is actually using.
    /// </returns>
    public static WifiStatus GetWifiStatus()
    {
        lock (StatusClientLock)
        {
            // A status read keeps one client handle. It only reads, so a handle the WLAN service
            // invalidated is reopened once and the read repeated; writes open a handle of their own.
            _statusClient ??= WlanClient.Open();
            IReadOnlyList<WlanInterfaceInfo> interfaces;
            try
            {
                interfaces = _statusClient.Interfaces();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == (int)ErrorInvalidHandle)
            {
                _statusClient.Dispose();
                _statusClient = null;
                _statusClient = WlanClient.Open();
                interfaces = _statusClient.Interfaces();
            }

            var selected = SelectInterface(interfaces);
            var current = TryCurrentConnection(_statusClient.Handle, selected.Id);
            return new WifiStatus(
                MapInterfaceState(selected.State),
                current?.Signal ?? 0,
                current?.Ssid ?? string.Empty);
        }
    }

    /// <summary>Asks every Wi-Fi adapter to scan for networks.</summary>
    /// <remarks>
    ///     Returns as soon as the request is made, not when scanning finishes — results
    ///     arrive seconds later. Watch for completion with <see cref="StartWifiWatch" /> rather than
    ///     calling <see cref="ListWifiNetworks" /> immediately, which would return the previous
    ///     results.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No adapter accepted the scan request.</exception>
    public static void RequestWifiScan()
    {
        using var client = WlanClient.Open();
        ForEachAdapter(client, false,
            adapter => CheckWlan("WlanScan", WlanScan(client.Handle, in adapter.Id, 0, 0, 0)));
    }

    /// <summary>Lists the Wi-Fi networks currently visible.</summary>
    /// <returns>Networks from every adapter, merged by SSID and ordered strongest first.</returns>
    /// <remarks>
    ///     Returns the last scan's results rather than scanning — call
    ///     <see cref="RequestWifiScan" /> first for fresh ones. An empty list on a machine that clearly
    ///     has networks nearby usually means location consent is denied; see
    ///     <see cref="GetConsent" />.
    /// </remarks>
    public static IReadOnlyList<WifiNetwork> ListWifiNetworks()
    {
        using var client = WlanClient.Open();
        var merged = new Dictionary<string, WifiNetworkFacts>(StringComparer.Ordinal);
        ForEachAdapter(client, false,
            adapter => MergeNetworks(client.Handle, adapter.Id, merged));
        return merged.Values
            .OrderByDescending(network => network.Signal)
            .ThenBy(network => network.Ssid, StringComparer.Ordinal)
            .Select(network => new WifiNetwork(
                network.Ssid,
                network.Signal,
                network.Ambiguous ? WifiSecurity.Unsupported : network.Security,
                network.Saved,
                network.Connectable && !network.Ambiguous,
                network.Connected))
            .ToArray();
    }

    /// <summary>Joins a Wi-Fi network, creating or reusing its profile, and waits for the result.</summary>
    /// <param name="ssid">The network name to join.</param>
    /// <param name="passphrase">
    ///     The passphrase, or <see langword="null" /> to use the saved profile
    ///     — which is what <see cref="WifiNetwork.Saved" /> tells you exists. Required for a protected
    ///     network with no saved profile.
    /// </param>
    /// <returns>
    ///     Zero when joined; otherwise the WLAN reason code. Pass it to
    ///     <see cref="ReasonText" /> for a message and <see cref="GetReasonVerdict" /> to decide whether
    ///     retrying or re-prompting for the passphrase is worthwhile.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="ssid" /> is null or empty.</exception>
    /// <remarks>
    ///     Blocks until the association succeeds or fails, up to an internal timeout. Windows requires
    ///     a stored profile before joining a protected network, so one is written first when needed.
    ///     If an existing profile must be overwritten, its exact XML is restored when the connection
    ///     fails; a failed key or security negotiation also removes a newly-created profile.
    /// </remarks>
    public static uint ConnectWifi(string ssid, string? passphrase)
    {
        ArgumentException.ThrowIfNullOrEmpty(ssid);
        if (passphrase is not null && !WifiProfile.PassphraseIsValid(passphrase))
        {
            throw new ArgumentException(
                "The password must be 8-63 printable ASCII characters, or 64 hex digits.",
                nameof(passphrase));
        }

        using var client = WlanClient.Open();
        // This call writes profiles, so its name checks and rollback snapshot come from what is
        // stored now. Reads within the call then share one parse of each profile.
        InvalidateSavedProfiles(null);
        var interfaces = client.Interfaces();
        var choice = ChooseInterface(client.Handle, interfaces, ssid, passphrase is null);
        var facts = choice.Facts;
        if (facts.Ambiguous)
        {
            throw new InvalidOperationException(
                "More than one network advertises this display name; it cannot be identified safely.");
        }

        if (passphrase is not null && facts.Security != WifiSecurity.PersonalPsk)
        {
            throw new InvalidOperationException(
                "This network does not advertise a supported personal-key authentication method.");
        }

        var targetSsid = facts.RawSsid.Length == 0 ? Encoding.UTF8.GetBytes(ssid) : facts.RawSsid;
        var profiles = ReadProfileSsids(client.Handle, choice.Adapter.Id, true);
        var profileName = facts.ProfileName;
        ProfileMutation? mutation = null;

        // Every failure below rolls the profile back once before it is reported.
        Exception Fail(Exception failure)
        {
            return CombineFailure(failure, TryRollBackProfile(client.Handle, choice.Adapter.Id, mutation));
        }

        if (passphrase is not null)
        {
            mutation = FindFreeProfileName(profiles, ssid, targetSsid);
            var flavors = facts.Authentication == Dot11AuthWpaPsk
                ? new[]
                {
                    WifiProfile.PskFlavor.WpaTkip, WifiProfile.PskFlavor.Wpa2Aes,
                    WifiProfile.PskFlavor.Wpa3Transition
                }
                : new[] { WifiProfile.PskFlavor.Wpa3Transition, WifiProfile.PskFlavor.Wpa2Aes };
            Exception? last = null;
            foreach (var flavor in flavors)
            {
                try
                {
                    SetProfile(client.Handle, choice.Adapter.Id, WifiProfile.CreatePsk(
                        mutation.Value.Name, ssid, facts.RawSsid, passphrase, flavor));
                    last = null;
                    break;
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }

            if (last is not null)
            {
                var cleanup = TryRollBackProfile(client.Handle, choice.Adapter.Id, mutation);
                if (last is WlanReasonException reason
                    && GetReasonVerdict(reason.ReasonCode) != WifiFailureKind.Unknown
                    && cleanup is null)
                {
                    return reason.ReasonCode;
                }

                throw CombineFailure(last, cleanup);
            }

            profileName = mutation.Value.Name;
        }
        else if (profileName is null)
        {
            if (facts.Security is not WifiSecurity.Open and not WifiSecurity.EnhancedOpen)
            {
                throw new InvalidOperationException(
                    facts.Security == WifiSecurity.Unsupported
                        ? "This network's authentication method is not supported."
                        : "This network needs a password and has no saved profile.");
            }

            mutation = FindFreeProfileName(profiles, ssid, targetSsid);
            try
            {
                SetProfile(client.Handle, choice.Adapter.Id, WifiProfile.CreateOpen(
                    mutation.Value.Name,
                    ssid,
                    facts.RawSsid,
                    facts.Security == WifiSecurity.EnhancedOpen));
            }
            catch (WlanReasonException reason)
                when (GetReasonVerdict(reason.ReasonCode) != WifiFailureKind.Unknown)
            {
                var cleanup = TryRollBackProfile(client.Handle, choice.Adapter.Id, mutation);
                if (cleanup is null)
                {
                    return reason.ReasonCode;
                }

                throw CombineFailure(reason, cleanup);
            }
            catch (Exception ex)
            {
                throw Fail(ex);
            }

            profileName = mutation.Value.Name;
        }

        profileName ??= ssid;
        ConnectionVerdict? verdict;
        uint verdictRegistrationStatus;
        try
        {
            verdict = ConnectionVerdict.TryStart(
                choice.Adapter.Id,
                profileName,
                out verdictRegistrationStatus);
        }
        catch (Exception ex)
        {
            throw Fail(ex);
        }

        using (verdict)
        {
            nint profilePointer;
            try
            {
                profilePointer = Marshal.StringToCoTaskMemUni(profileName);
            }
            catch (Exception ex)
            {
                throw Fail(ex);
            }

            var parameters = new WlanConnectionParameters
            {
                Mode = WlanConnectionModeProfile,
                Profile = profilePointer,
                BssType = Dot11BssTypeInfrastructure
            };
            try
            {
                var adapterId = choice.Adapter.Id;
                var accepted = WlanConnect(client.Handle, in adapterId, in parameters, 0);
                if (accepted != ErrorSuccess)
                {
                    throw Fail(WlanFailure("WlanConnect", accepted));
                }

                if (verdict is null)
                {
                    if (PollForConnection(
                            client.Handle,
                            choice.Adapter.Id,
                            targetSsid,
                            ConnectTimeout))
                    {
                        return 0;
                    }

                    throw Fail(new TimeoutException(
                        "The Wi-Fi connection attempt did not complete; "
                        + $"WLAN notification registration failed (Win32 {verdictRegistrationStatus})."));
                }

                var outcome = verdict.Wait(ConnectTimeout);
                if (outcome is { Succeeded: true })
                {
                    return 0;
                }

                if (outcome is { } failed)
                {
                    if (IsConnectedTo(client.Handle, choice.Adapter.Id, targetSsid))
                    {
                        return 0;
                    }

                    var reason = failed.Reason == 0 ? ErrorNotFound : failed.Reason;
                    var kind = GetReasonVerdict(reason);
                    var mustRestore = mutation?.Existed == true
                                      || kind is WifiFailureKind.KeyRejected or WifiFailureKind.SecurityMismatch;
                    var cleanup = mustRestore
                        ? TryRollBackProfile(client.Handle, choice.Adapter.Id, mutation)
                        : null;
                    if (cleanup is not null)
                    {
                        throw CombineFailure(
                            new WlanReasonException(reason, ReasonText(reason)),
                            cleanup);
                    }

                    return reason;
                }

                if (IsConnectedTo(client.Handle, choice.Adapter.Id, targetSsid))
                {
                    return 0;
                }

                throw Fail(new TimeoutException("The Wi-Fi connection attempt did not complete."));
            }
            finally
            {
                Marshal.FreeCoTaskMem(parameters.Profile);
            }
        }
    }

    /// <summary>Disconnects every Wi-Fi adapter that is connected or connecting.</summary>
    /// <remarks>
    ///     Leaves saved profiles in place, so Windows may reconnect automatically. Use
    ///     <see cref="ForgetWifi" /> to stop that.
    /// </remarks>
    /// <exception cref="Win32Exception">An adapter refused to disconnect.</exception>
    public static void DisconnectWifi()
    {
        using var client = WlanClient.Open();
        ForEachAdapter(client, true, adapter =>
        {
            if (MapInterfaceState(adapter.State)
                is WifiConnectionState.Connected or WifiConnectionState.Connecting)
            {
                CheckWlan("WlanDisconnect", WlanDisconnect(client.Handle, in adapter.Id, 0));
            }
        });
    }

    /// <summary>Forgets a network by deleting every saved profile for it.</summary>
    /// <param name="ssid">The network name to forget.</param>
    /// <remarks>
    ///     Matches on the SSID inside each profile document rather than on the profile's
    ///     name, so a profile Windows saved under a different name is still removed. Doing nothing
    ///     because no profile matched is success, not an error.
    /// </remarks>
    public static void ForgetWifi(string ssid)
    {
        using var client = WlanClient.Open();
        // Deletion is chosen from the SSIDs inside stored profiles, so those are read fresh.
        InvalidateSavedProfiles(null);
        ForEachAdapter(client, true, adapter =>
        {
            var facts = ReadScanFacts(client.Handle, adapter.Id, ssid);
            if (facts.Ambiguous)
            {
                throw new InvalidOperationException(
                    "More than one network advertises this display name; it cannot be identified safely.");
            }

            var target = facts.RawSsid.Length == 0 ? Encoding.UTF8.GetBytes(ssid) : facts.RawSsid;
            var names = ReadProfileSsids(client.Handle, adapter.Id, true)
                .Where(profile => profile.Ssid is { } profileSsid
                                  && profileSsid.AsSpan().SequenceEqual(target))
                .Select(profile => profile.Name)
                .Where(name => name is not null)
                .Select(name => name!)
                .ToHashSet(StringComparer.Ordinal);
            if (facts.ProfileName is { Length: > 0 } bound)
            {
                names.Add(bound);
            }

            foreach (var name in names)
            {
                var status = WlanDeleteProfile(client.Handle, in adapter.Id, name, 0);
                InvalidateSavedProfiles(adapter.Id);
                CheckWlan("WlanDeleteProfile", status);
            }
        });
    }

    /// <summary>
    ///     Runs one operation on every WLAN interface. A failure on one interface does not
    ///     stop the others; the last failure is thrown afterwards when every interface had to succeed,
    ///     or when none did.
    /// </summary>
    private static void ForEachAdapter(
        WlanClient client,
        bool requireEvery,
        Action<WlanInterfaceInfo> operation)
    {
        Exception? last = null;
        var succeeded = false;
        foreach (var adapter in client.Interfaces())
        {
            try
            {
                operation(adapter);
                succeeded = true;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        if (last is not null && (requireEvery || !succeeded))
        {
            throw last;
        }
    }

    private static bool PollForConnection(
        nint client,
        Guid adapter,
        byte[] targetSsid,
        TimeSpan timeout)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            if (IsConnectedTo(client, adapter, targetSsid))
            {
                return true;
            }

            Thread.Sleep(500);
        }

        return false;
    }

    private static bool IsConnectedTo(nint client, Guid adapter, byte[] targetSsid)
    {
        return TryCurrentConnection(client, adapter) is { } current
               && current.RawSsid.AsSpan().SequenceEqual(targetSsid);
    }

    /// <summary>Looks up Windows' own description of a WLAN reason code.</summary>
    /// <param name="code">The reason code to describe.</param>
    /// <returns>
    ///     The description in the user's display language, or <c>"Wi-Fi reason code N"</c>
    ///     when Windows has no text for the code. Never empty, so it is always safe to show.
    /// </returns>
    public static string ReasonText(uint code)
    {
        Span<char> buffer = stackalloc char[1024];
        fixed (char* text = buffer)
        {
            var status = WlanReasonCodeToString(code, (uint)buffer.Length, text, 0);
            if (status != ErrorSuccess)
            {
                return $"Wi-Fi reason code {code}";
            }
        }

        var end = buffer.IndexOf('\0');
        var result = (end >= 0 ? buffer[..end] : buffer).Trim();
        return result.IsEmpty ? $"Wi-Fi reason code {code}" : new string(result);
    }

    private static WlanInterfaceInfo SelectInterface(IReadOnlyList<WlanInterfaceInfo> interfaces)
    {
        return interfaces.FirstOrDefault(adapter =>
                   MapInterfaceState(adapter.State) == WifiConnectionState.Connected) is var connected
               && connected.Id != Guid.Empty
            ? connected
            : interfaces[0];
    }

    private static CurrentConnection? TryCurrentConnection(nint client, Guid adapter)
    {
        var status = WlanQueryInterface(
            client,
            in adapter,
            WlanIntfOpcodeCurrentConnection,
            0,
            out _,
            out var data,
            out _);
        if (status != ErrorSuccess || data == 0)
        {
            return null;
        }

        try
        {
            var current = Marshal.PtrToStructure<WlanConnectionAttributes>(data);
            if (MapInterfaceState(current.State) != WifiConnectionState.Connected)
            {
                return null;
            }

            var rawSsid = ReadSsidBytes(current.Association.Ssid);
            return new CurrentConnection(
                Encoding.UTF8.GetString(rawSsid),
                rawSsid,
                (int)current.Association.SignalQuality);
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    private static void MergeNetworks(
        nint client,
        Guid adapter,
        IDictionary<string, WifiNetworkFacts> merged)
    {
        CheckWlan("WlanGetAvailableNetworkList", ReadAvailableNetworks(
            client,
            adapter,
            static count => $"WLANAPI reported an invalid network count ({count}).",
            out var networks));
        if (networks.Length == 0)
        {
            return;
        }

        var savedSsids = ReadProfileSsids(client, adapter, false)
            .Where(profile => profile.Ssid is not null)
            .Select(profile => Convert.ToHexString(profile.Ssid!))
            .ToHashSet(StringComparer.Ordinal);
        var connected = TryCurrentConnection(client, adapter)?.RawSsid;
        foreach (var network in networks)
        {
            if (network.Ssid.Length == 0)
            {
                continue;
            }

            var raw = network.RawSsid;
            var key = Convert.ToHexString(raw);
            var saved = network.ProfileName is not null || savedSsids.Contains(key);
            var facts = new WifiNetworkFacts(
                network.Ssid,
                raw,
                network.Signal,
                network.Security,
                network.Authentication,
                saved,
                network.Connectable,
                connected is not null && connected.AsSpan().SequenceEqual(raw),
                network.ProfileName,
                false);
            if (merged.TryGetValue(key, out var existing))
            {
                merged[key] = MergeNetworkFacts(existing, facts);
            }
            else
            {
                merged.Add(key, facts);
            }
        }
    }

    /// <summary>
    ///     Reads and decodes one adapter's available networks. Failure handling stays with
    ///     the caller: a failed status comes back with an empty list, and
    ///     <paramref name="rejection" /> decides whether an oversized count throws or is cut to the
    ///     bound.
    /// </summary>
    private static uint ReadAvailableNetworks(
        nint client,
        Guid adapter,
        Func<uint, string>? rejection,
        out AvailableNetwork[] networks)
    {
        networks = [];
        var status = WlanGetAvailableNetworkList(client, in adapter, 0, 0, out var list);
        if (status != ErrorSuccess || list == 0)
        {
            return status;
        }

        try
        {
            networks = ReadWlanList(list, 4096, rejection, static (WlanAvailableNetwork item) =>
            {
                var raw = ReadSsidBytes(item.Ssid);
                var profileName = NativeText.ReadFixed(item.ProfileName, 256);
                return new AvailableNetwork(
                    raw,
                    Encoding.UTF8.GetString(raw),
                    (int)item.SignalQuality,
                    ClassifySecurity(item.SecurityEnabled != 0, item.DefaultAuthAlgorithm),
                    item.DefaultAuthAlgorithm,
                    item.Connectable != 0,
                    profileName.Length == 0 ? null : profileName);
            });
        }
        finally
        {
            WlanFreeMemory(list);
        }

        return status;
    }

    private static InterfaceChoice ChooseInterface(
        nint client,
        IReadOnlyList<WlanInterfaceInfo> interfaces,
        string ssid,
        bool needsSavedProfile)
    {
        InterfaceChoice? best = null;
        var bestRank = -1;
        var observations = new List<WifiNetworkFacts>(interfaces.Count);
        foreach (var adapter in interfaces)
        {
            var facts = ReadScanFacts(client, adapter.Id, ssid);
            observations.Add(facts);
            var visible = facts.RawSsid.Length > 0;
            var saved = facts.ProfileName is not null;
            var rank = (visible, saved, needsSavedProfile) switch
            {
                (true, true, _) => 4,
                (false, true, true) => 3,
                (true, false, _) => 2,
                (false, true, false) => 1,
                _ => 0
            };
            if (rank > bestRank)
            {
                bestRank = rank;
                best = new InterfaceChoice(adapter, facts);
            }
        }

        var selected = best ?? new InterfaceChoice(interfaces[0], WifiNetworkFacts.Empty(ssid));
        var visibleObservations = observations.Where(item => item.RawSsid.Length > 0).ToArray();
        if (visibleObservations.Any(item => item.Ambiguous)
            || visibleObservations.Skip(1).Any(item =>
                !item.RawSsid.AsSpan().SequenceEqual(visibleObservations[0].RawSsid)
                || item.Security != visibleObservations[0].Security
                || item.Authentication != visibleObservations[0].Authentication
                || (visibleObservations[0].ProfileName is { Length: > 0 } firstProfile
                    && item.ProfileName is { Length: > 0 } itemProfile
                    && !string.Equals(firstProfile, itemProfile, StringComparison.Ordinal))))
        {
            selected = selected with
            {
                Facts = selected.Facts with
                {
                    Ambiguous = true,
                    Security = WifiSecurity.Unsupported,
                    Authentication = 0,
                    Connectable = false,
                    ProfileName = null
                }
            };
        }

        return selected;
    }

    private static WifiNetworkFacts ReadScanFacts(nint client, Guid adapter, string ssid)
    {
        var facts = WifiNetworkFacts.Empty(ssid);
        ReadAvailableNetworks(client, adapter, null, out var networks);
        foreach (var network in networks)
        {
            if (!string.Equals(network.Ssid, ssid, StringComparison.Ordinal))
            {
                continue;
            }

            var raw = network.RawSsid;
            var profileName = network.ProfileName;
            var sameRaw = facts.RawSsid.Length == 0
                          || facts.RawSsid.AsSpan().SequenceEqual(raw);
            var conflictingIdentity = facts.RawSsid.Length > 0
                                      && (!sameRaw
                                          || facts.Security != network.Security
                                          || facts.Authentication != network.Authentication
                                          || (facts.ProfileName is { Length: > 0 } existingProfile
                                              && profileName is { Length: > 0 }
                                              && !string.Equals(
                                                  existingProfile,
                                                  profileName,
                                                  StringComparison.Ordinal)));
            facts = facts with
            {
                RawSsid = facts.RawSsid.Length == 0 ? raw : facts.RawSsid,
                Ambiguous = facts.Ambiguous || conflictingIdentity,
                Security = conflictingIdentity ? WifiSecurity.Unsupported : network.Security,
                Authentication = conflictingIdentity ? 0 : network.Authentication,
                ProfileName = conflictingIdentity ? null : profileName ?? facts.ProfileName
            };
        }

        if (facts.ProfileName is null)
        {
            var target = facts.RawSsid.Length == 0 ? Encoding.UTF8.GetBytes(ssid) : facts.RawSsid;
            facts = facts with
            {
                ProfileName = ReadProfileSsids(client, adapter, false)
                    .FirstOrDefault(profile => profile.Ssid is { } profileSsid
                                               && profileSsid.AsSpan().SequenceEqual(target)).Name
            };
        }

        return facts;
    }

    private static IReadOnlyList<SavedProfile> ReadProfileSsids(
        nint client,
        Guid adapter,
        bool failOnListError)
    {
        var status = WlanGetProfileList(client, in adapter, 0, out var list);
        if (status != ErrorSuccess)
        {
            if (failOnListError)
            {
                throw WlanFailure("WlanGetProfileList", status);
            }

            return [];
        }

        if (list == 0)
        {
            return [];
        }

        string[] names;
        try
        {
            names = ReadWlanList(list, 4096, null,
                static (WlanProfileInfo record) => NativeText.ReadFixed(record.Name, 256));
        }
        finally
        {
            WlanFreeMemory(list);
        }

        long generation;
        lock (SavedProfileLock)
        {
            if (!SavedProfileNames.TryGetValue(adapter, out var known) || !known.AsSpan().SequenceEqual(names))
            {
                InvalidateSavedProfiles(adapter);
                SavedProfileNames[adapter] = names;
            }

            generation = _savedProfileGeneration;
        }

        var profiles = new List<SavedProfile>(names.Length);
        foreach (var name in names)
        {
            if (name.Length == 0)
            {
                continue;
            }

            SavedProfile? cached;
            lock (SavedProfileLock)
            {
                cached = SavedProfiles.TryGetValue((adapter, name), out var entry) ? entry : null;
            }

            if (cached is { } known)
            {
                profiles.Add(known);
                continue;
            }

            var xml = TryReadProfileXml(client, adapter, name);
            var profile = new SavedProfile(name, xml is null ? null : WifiProfile.TryReadSsid(xml), xml);
            // Unreadable XML is not remembered, so it is asked for again on the next read.
            if (xml is not null)
            {
                lock (SavedProfileLock)
                {
                    if (generation == _savedProfileGeneration)
                    {
                        SavedProfiles[(adapter, name)] = profile;
                    }
                }
            }

            profiles.Add(profile);
        }

        return profiles;
    }

    /// <summary>
    ///     Forgets the parsed profiles of one adapter, or of every adapter when null, and
    ///     stops reads already in progress from storing what they found.
    /// </summary>
    /// <remarks>
    ///     A cached profile stays valid while its adapter's profile-name list is unchanged and
    ///     this library has not written or deleted a profile since.
    /// </remarks>
    private static void InvalidateSavedProfiles(Guid? adapter)
    {
        lock (SavedProfileLock)
        {
            _savedProfileGeneration++;
            if (adapter is not { } only)
            {
                SavedProfiles.Clear();
                SavedProfileNames.Clear();
                return;
            }

            SavedProfileNames.Remove(only);
            foreach (var key in SavedProfiles.Keys.Where(key => key.Adapter == only).ToArray())
            {
                SavedProfiles.Remove(key);
            }
        }
    }

    private static string? TryReadProfileXml(nint client, Guid adapter, string name)
    {
        var status = WlanGetProfile(client, in adapter, name, 0, out var xml, out _, out _);
        if (status != ErrorSuccess || xml == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(xml);
        }
        finally
        {
            WlanFreeMemory(xml);
        }
    }

    private static void SetProfile(nint client, Guid adapter, string xml)
    {
        var status = WlanSetProfile(client, in adapter, 0, xml, null, 1, 0, out var reason);
        InvalidateSavedProfiles(adapter);
        if (status == ErrorSuccess)
        {
            return;
        }

        throw reason != 0
            ? new WlanReasonException(reason, $"WlanSetProfile failed: {ReasonText(reason)}")
            : WlanFailure("WlanSetProfile", status);
    }

    private static void RollBackProfile(
        nint client,
        Guid adapter,
        ProfileMutation? mutation)
    {
        if (mutation is not { } authored)
        {
            return;
        }

        if (authored.Existed)
        {
            if (authored.PreviousXml is null)
            {
                throw new InvalidOperationException(
                    $"The previous Wi-Fi profile '{authored.Name}' was not available for rollback.");
            }

            SetProfile(client, adapter, authored.PreviousXml);
            return;
        }

        var status = WlanDeleteProfile(client, in adapter, authored.Name, 0);
        InvalidateSavedProfiles(adapter);
        if (status is not ErrorSuccess and not ErrorNotFound)
        {
            throw WlanFailure("WlanDeleteProfile", status);
        }
    }

    private static Exception? TryRollBackProfile(
        nint client,
        Guid adapter,
        ProfileMutation? mutation)
    {
        try
        {
            RollBackProfile(client, adapter, mutation);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static Exception CombineFailure(Exception failure, Exception? cleanupFailure)
    {
        return cleanupFailure is null ? failure : new AggregateException(failure, cleanupFailure);
    }

    private readonly record struct CurrentConnection(string Ssid, byte[] RawSsid, int Signal);

    private readonly record struct AvailableNetwork(
        byte[] RawSsid,
        string Ssid,
        int Signal,
        WifiSecurity Security,
        int Authentication,
        bool Connectable,
        string? ProfileName);

    private readonly record struct InterfaceChoice(WlanInterfaceInfo Adapter, WifiNetworkFacts Facts);

    private sealed class WlanReasonException(uint reasonCode, string message)
        : Win32Exception((int)reasonCode, message)
    {
        internal uint ReasonCode { get; } = reasonCode;
    }
}
