using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
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
    ///     The first connected adapter in Windows enumeration order, or the first adapter when none
    ///     is connected. No WLAN interfaces yields Unknown. If connection
    ///     details cannot be read, the adapter state remains available with zero signal and no network key.
    /// </returns>
    /// <exception cref="Win32Exception">The WLAN service could not be opened or enumerated.</exception>
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

            if (interfaces.Count == 0)
            {
                return new WifiStatus(WifiConnectionState.Unknown, 0, string.Empty, null);
            }

            var selected = SelectInterface(interfaces);
            var current = TryCurrentConnection(_statusClient.Handle, selected.Id);
            return new WifiStatus(
                MapInterfaceState(selected.State),
                current?.Signal ?? 0,
                current?.Key.DisplayText ?? string.Empty,
                current?.Key);
        }
    }

    /// <summary>Asks every Wi-Fi adapter to scan for networks.</summary>
    /// <remarks>
    ///     Returns as soon as the request is made, not when scanning finishes — results
    ///     arrive seconds later through <see cref="StartWifiWatch" />. All adapters are attempted;
    ///     the call succeeds if any accepts, without reporting refusals from the others.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Windows reported no WLAN interface.</exception>
    /// <exception cref="Win32Exception">
    ///     The WLAN service could not be opened, or no adapter accepted the scan request; the native
    ///     status is <see cref="Win32Exception.NativeErrorCode" />.
    /// </exception>
    public static void RequestWifiScan()
    {
        using var client = WlanClient.Open();
        ForEachAdapter(client, false,
            adapter => CheckWlan("WlanScan", WlanScan(client.Handle, in adapter.Id, 0, 0, 0)));
    }

    /// <summary>Lists the Wi-Fi networks currently visible.</summary>
    /// <returns>
    ///     Networks from every adapter, one per <see cref="WifiNetworkKey" />, ordered strongest
    ///     first. Networks that hide their name are not listed.
    /// </returns>
    /// <remarks>
    ///     Returns the last scan's results rather than scanning — call
    ///     <see cref="RequestWifiScan" /> and wait for its notification before reading fresh results.
    ///     Results from an adapter that succeeds are retained when another adapter fails; a failure
    ///     is thrown when none succeeds. An empty successful list does not diagnose location consent;
    ///     <see cref="GetConsent" /> is diagnostic only and the WLAN status is authoritative.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Windows reported no WLAN interface.</exception>
    /// <exception cref="Win32Exception">
    ///     The WLAN service could not be opened, or no adapter's list could be read; the native
    ///     status is <see cref="Win32Exception.NativeErrorCode" />. Windows 11 24H2 answers
    ///     <c>ERROR_ACCESS_DENIED</c> (5) while location access is off.
    /// </exception>
    public static IReadOnlyList<WifiNetwork> ListWifiNetworks()
    {
        using var client = WlanClient.Open();
        var merged = new Dictionary<WifiNetworkKey, WifiNetworkFacts>();
        ForEachAdapter(client, false,
            adapter => MergeNetworks(client.Handle, adapter.Id, merged));
        return merged.Values
            .OrderByDescending(network => network.Signal)
            .ThenBy(network => network.Key.DisplayText, StringComparer.Ordinal)
            .ThenBy(network => network.Key.Security)
            .ThenBy(network => network.Key.Hex, StringComparer.Ordinal)
            .Select(network => new WifiNetwork(
                network.Key,
                network.Key.DisplayText,
                network.Signal,
                network.Key.Security,
                network.Saved,
                network.Connectable,
                network.Connected))
            .ToArray();
    }

    /// <summary>Joins a Wi-Fi network, creating or reusing its profile, and waits for the result.</summary>
    /// <param name="network">The network to join, as a listed <see cref="WifiNetwork.Key" />.</param>
    /// <param name="passphrase">
    ///     The passphrase, or <see langword="null" /> to use the saved profile
    ///     — which is what <see cref="WifiNetwork.Saved" /> tells you exists. Required for a protected
    ///     network with no saved profile.
    /// </param>
    /// <returns>
    ///     How the attempt ended. For <see cref="WifiConnectOutcome.Failed" /> pass
    ///     <see cref="WifiConnectResult.ReasonCode" /> to <see cref="ReasonText" /> for a message and to
    ///     <see cref="GetReasonVerdict" /> to decide whether re-prompting for the passphrase is
    ///     worthwhile.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="network" /> is <see langword="default" />.</exception>
    /// <exception cref="InvalidOperationException">Windows reported no WLAN interface.</exception>
    /// <exception cref="Win32Exception">
    ///     A WLAN call failed: the service could not be opened, the available-network or profile list
    ///     could not be read (<c>ERROR_ACCESS_DENIED</c> while location access is off on Windows 11
    ///     24H2), a profile could not be written, or the connection request was refused. Nothing was
    ///     left changed: a profile written for the attempt was rolled back first.
    /// </exception>
    /// <exception cref="AggregateException">
    ///     The attempt failed and rolling its profile back failed too; both failures are inside.
    ///     The rollback is not retried.
    /// </exception>
    /// <remarks>
    ///     Registers a scoped completion callback before dispatch, then waits up to 25 seconds for it;
    ///     profile preparation and native calls add to that duration. A successful event or a current
    ///     connection with the requested SSID bytes yields Joined. Otherwise a definite failure restores
    ///     replaced profile XML once; a new profile is deleted only for key or security refusal.
    ///     Without a verdict the result is Pending. Profiles written for Joined or Pending remain
    ///     stored as all-user profiles. A saved match prefers the current connection, then Windows'
    ///     profile priority. Serialize competing changes to the same network/profile.
    /// </remarks>
    public static WifiConnectResult ConnectWifi(WifiNetworkKey network, string? passphrase)
    {
        ThrowIfDefault(network);
        if (passphrase is not null && !WifiProfile.PassphraseIsValid(passphrase))
        {
            return Refused(WifiConnectRefusal.InvalidPassphrase);
        }

        using var client = WlanClient.Open();
        // Profile collision checks and rollback need the current stored XML.
        InvalidateSavedProfiles(null);
        var choice = ChooseInterface(client.Handle, client.RequireInterfaces(), network, passphrase is null);
        var adapterId = choice.Adapter.Id;
        var targetSsid = network.Ssid.ToArray();
        var displayName = network.DisplayText;
        if (passphrase is not null
            && (!choice.Facts.Visible || network.Security != WifiSecurity.PersonalPsk))
        {
            return Refused(WifiConnectRefusal.UnsupportedAuthentication);
        }

        var profileName = choice.ProfileName;
        ProfileMutation? mutation = null;

        Exception Fail(Exception failure)
        {
            return CombineFailure(failure, TryRollBackProfile(client.Handle, adapterId, mutation));
        }

        if (passphrase is not null)
        {
            mutation = FindFreeProfileName(choice.Profiles, displayName, targetSsid);
            var flavors = choice.Facts.Authentication == Dot11AuthWpaPsk
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
                    SetProfile(client.Handle, adapterId, WifiProfile.CreatePsk(
                        mutation.Value.Name, displayName, targetSsid, passphrase, flavor));
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
                var cleanup = TryRollBackProfile(client.Handle, adapterId, mutation);
                if (cleanup is null)
                {
                    if (last is WlanReasonException reason
                        && GetReasonVerdict(reason.ReasonCode) != WifiFailureKind.Unknown)
                    {
                        return Failed(reason.ReasonCode);
                    }

                    ExceptionDispatchInfo.Throw(last);
                }

                throw CombineFailure(last, cleanup);
            }

            profileName = mutation.Value.Name;
        }
        else if (profileName is null)
        {
            // Without a visible network there is no advertised authentication to build a profile for.
            var security = choice.Facts.Visible ? network.Security : WifiSecurity.Unsupported;
            if (security is not WifiSecurity.Open and not WifiSecurity.EnhancedOpen)
            {
                return Refused(security == WifiSecurity.Unsupported
                    ? WifiConnectRefusal.UnsupportedSecurity
                    : WifiConnectRefusal.NeedsPassword);
            }

            mutation = FindFreeProfileName(choice.Profiles, displayName, targetSsid);
            try
            {
                SetProfile(client.Handle, adapterId, WifiProfile.CreateOpen(
                    mutation.Value.Name,
                    displayName,
                    targetSsid,
                    security == WifiSecurity.EnhancedOpen));
            }
            catch (WlanReasonException reason)
                when (GetReasonVerdict(reason.ReasonCode) != WifiFailureKind.Unknown)
            {
                var cleanup = TryRollBackProfile(client.Handle, adapterId, mutation);
                if (cleanup is null)
                {
                    return Failed(reason.ReasonCode);
                }

                throw CombineFailure(reason, cleanup);
            }
            catch (Exception ex)
            {
                throw Fail(ex);
            }

            profileName = mutation.Value.Name;
        }

        ConnectionVerdict? verdict;
        uint verdictRegistrationStatus;
        try
        {
            verdict = ConnectionVerdict.TryStart(adapterId, profileName, out verdictRegistrationStatus);
        }
        catch (Exception ex)
        {
            throw Fail(ex);
        }

        // The verdict is how the outcome is known, so without it the request is never sent.
        if (verdict is null)
        {
            throw Fail(new Win32Exception((int)verdictRegistrationStatus,
                "WLAN notification registration failed; the Wi-Fi connection was not attempted."));
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
                var accepted = WlanConnect(client.Handle, in adapterId, in parameters, 0);
                if (accepted != ErrorSuccess)
                {
                    throw Fail(WlanFailure("WlanConnect", accepted));
                }

                var outcome = verdict.Wait(ConnectTimeout);
                if (outcome is { Succeeded: true } || IsConnectedTo(client.Handle, adapterId, targetSsid))
                {
                    return new WifiConnectResult(WifiConnectOutcome.Joined, 0, null);
                }

                if (outcome is not { } failed)
                {
                    // No verdict is not a failure: the attempt may still complete, so the profile
                    // it is using stays and nothing is undone.
                    return new WifiConnectResult(WifiConnectOutcome.Pending, 0, null);
                }

                var reason = failed.Reason == 0 ? ErrorNotFound : failed.Reason;
                var kind = GetReasonVerdict(reason);
                var mustRestore = mutation?.Existed == true
                                  || kind is WifiFailureKind.KeyRejected or WifiFailureKind.SecurityMismatch;
                var cleanup = mustRestore
                    ? TryRollBackProfile(client.Handle, adapterId, mutation)
                    : null;
                if (cleanup is not null)
                {
                    throw CombineFailure(
                        new WlanReasonException(reason, ReasonText(reason)),
                        cleanup);
                }

                return Failed(reason);
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
    /// <exception cref="InvalidOperationException">Windows reported no WLAN interface.</exception>
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
    /// <param name="network">The network to forget, as a listed <see cref="WifiNetwork.Key" />.</param>
    /// <returns>
    ///     One entry per profile whose deletion was attempted, with Windows' status. Empty when no
    ///     profile matched, which is success, not an error.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="network" /> is <see langword="default" />.</exception>
    /// <exception cref="InvalidOperationException">Windows reported no WLAN interface.</exception>
    /// <exception cref="Win32Exception">
    ///     The WLAN service could not be opened, or an adapter's profile list could not be read.
    /// </exception>
    /// <remarks>
    ///     Matches on the SSID bytes inside each profile document rather than on the profile's name,
    ///     so a profile Windows saved under a different name is still removed. Every matching profile
    ///     on every adapter is deleted once, and a failed deletion does not stop the others. A profile
    ///     whose XML cannot be read is never deleted, because nothing proves which network it is for.
    /// </remarks>
    public static IReadOnlyList<WifiForgetResult> ForgetWifi(WifiNetworkKey network)
    {
        ThrowIfDefault(network);
        var target = network.Ssid.ToArray();
        using var client = WlanClient.Open();
        // Deletion is chosen from the SSIDs inside stored profiles, so those are read fresh.
        InvalidateSavedProfiles(null);
        var results = new List<WifiForgetResult>();
        ForEachAdapter(client, true, adapter =>
        {
            foreach (var profile in ReadProfileSsids(client.Handle, adapter.Id, true))
            {
                if (profile.Ssid is not { } ssid || !ssid.AsSpan().SequenceEqual(target))
                {
                    continue;
                }

                var status = WlanDeleteProfile(client.Handle, in adapter.Id, profile.Name, 0);
                InvalidateSavedProfiles(adapter.Id);
                results.Add(new WifiForgetResult(profile.Name, status));
            }
        });
        return results;
    }

    private static void ThrowIfDefault(WifiNetworkKey network)
    {
        if (network.IsDefault)
        {
            throw new ArgumentException("The Wi-Fi network key names no network.", nameof(network));
        }
    }

    private static WifiConnectResult Refused(WifiConnectRefusal refusal)
    {
        return new WifiConnectResult(WifiConnectOutcome.Refused, 0, refusal);
    }

    private static WifiConnectResult Failed(uint reason)
    {
        return new WifiConnectResult(WifiConnectOutcome.Failed, reason, null);
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
        foreach (var adapter in client.RequireInterfaces())
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
            ExceptionDispatchInfo.Throw(last);
        }
    }

    private static bool IsConnectedTo(nint client, Guid adapter, byte[] targetSsid)
    {
        return TryCurrentConnection(client, adapter) is { } current
               && current.Key.Ssid.SequenceEqual(targetSsid);
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

    /// <summary>The adapter's joined network, or null when it is not joined to a named one.</summary>
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
            if (rawSsid.Length == 0)
            {
                return null;
            }

            var security = ClassifySecurity(
                current.Security.SecurityEnabled != 0,
                current.Security.AuthAlgorithm);
            return new CurrentConnection(
                new WifiNetworkKey(rawSsid, security),
                (int)current.Association.SignalQuality,
                NativeText.ReadFixed(current.ProfileName, 256));
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    private static void MergeNetworks(
        nint client,
        Guid adapter,
        IDictionary<WifiNetworkKey, WifiNetworkFacts> merged)
    {
        CheckWlan("WlanGetAvailableNetworkList", ReadAvailableNetworks(client, adapter, out var networks));
        if (networks.Length == 0)
        {
            return;
        }

        var savedSsids = ReadProfileSsids(client, adapter, false)
            .Where(profile => profile.Ssid is not null)
            .Select(profile => Convert.ToHexString(profile.Ssid!))
            .ToHashSet(StringComparer.Ordinal);
        var connected = TryCurrentConnection(client, adapter)?.Key;
        foreach (var network in networks)
        {
            if (network.RawSsid.Length == 0)
            {
                continue;
            }

            var key = new WifiNetworkKey(network.RawSsid, network.Security);
            var facts = new WifiNetworkFacts(
                key,
                network.Signal,
                network.ProfileName is not null || savedSsids.Contains(key.Hex),
                network.Connectable,
                // By bytes: the connection's own security attributes can classify a transition
                // network differently from its advertisement, and it is still the joined network.
                connected is { } joined && joined.Ssid.SequenceEqual(network.RawSsid));
            merged[key] = merged.TryGetValue(key, out var existing)
                ? MergeNetworkFacts(existing, facts)
                : facts;
        }
    }

    /// <summary>
    ///     Reads and decodes one adapter's available networks. A failed status comes back with an
    ///     empty list, for the caller to report.
    /// </summary>
    private static uint ReadAvailableNetworks(
        nint client,
        Guid adapter,
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
            networks = ReadWlanList(list, static (WlanAvailableNetwork item) =>
            {
                var profileName = NativeText.ReadFixed(item.ProfileName, 256);
                return new AvailableNetwork(
                    ReadSsidBytes(item.Ssid),
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

    /// <summary>
    ///     Picks the adapter a join runs on and the saved profile it uses. An adapter that sees the
    ///     network and has a profile for it wins, then one with a profile when no passphrase was
    ///     given, then one that sees it.
    /// </summary>
    private static InterfaceChoice ChooseInterface(
        nint client,
        IReadOnlyList<WlanInterfaceInfo> interfaces,
        WifiNetworkKey network,
        bool needsSavedProfile)
    {
        InterfaceChoice? best = null;
        var bestRank = -1;
        foreach (var adapter in interfaces)
        {
            var facts = ReadScanFacts(client, adapter.Id, network);
            var profiles = ReadProfileSsids(client, adapter.Id, true);
            var profileName = ChooseProfile(client, adapter.Id, network, facts, profiles);
            var rank = (facts.Visible, profileName is not null, needsSavedProfile) switch
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
                best = new InterfaceChoice(adapter, facts, profiles, profileName);
            }
        }

        // RequireInterfaces gave at least one adapter, and every rank beats -1.
        return best!.Value;
    }

    /// <summary>What one adapter's scan says about one network.</summary>
    private static ScanFacts ReadScanFacts(nint client, Guid adapter, WifiNetworkKey network)
    {
        // A failed list is reported, not read as "not visible": a scan blocked by location
        // consent must not turn into a security refusal or a write.
        CheckWlan("WlanGetAvailableNetworkList", ReadAvailableNetworks(client, adapter, out var networks));
        var visible = false;
        var authentication = 0;
        var strongest = -1;
        List<string> boundProfiles = [];
        foreach (var entry in networks)
        {
            if (entry.Security != network.Security || !network.Ssid.SequenceEqual(entry.RawSsid))
            {
                continue;
            }

            visible = true;
            if (entry.Signal > strongest)
            {
                strongest = entry.Signal;
                authentication = entry.Authentication;
            }

            if (entry.ProfileName is { } bound && !boundProfiles.Contains(bound))
            {
                boundProfiles.Add(bound);
            }
        }

        return new ScanFacts(visible, authentication, boundProfiles);
    }

    /// <summary>
    ///     The saved profile a join uses: the one the adapter is connected with when it is joined to
    ///     this network, else the first in Windows' own priority order that Windows matched to the
    ///     network or that carries its SSID bytes.
    /// </summary>
    private static string? ChooseProfile(
        nint client,
        Guid adapter,
        WifiNetworkKey network,
        ScanFacts facts,
        IReadOnlyList<SavedProfile> profiles)
    {
        if (TryCurrentConnection(client, adapter) is { } current
            && current.Key == network
            && current.ProfileName.Length > 0)
        {
            return current.ProfileName;
        }

        foreach (var profile in profiles)
        {
            if (facts.BoundProfiles.Contains(profile.Name))
            {
                return profile.Name;
            }
        }

        foreach (var profile in profiles)
        {
            if (profile.Ssid is { } ssid && network.Ssid.SequenceEqual(ssid))
            {
                return profile.Name;
            }
        }

        return facts.BoundProfiles.Count > 0 ? facts.BoundProfiles[0] : null;
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
            names = ReadWlanList(list, static (WlanProfileInfo record) => NativeText.ReadFixed(record.Name, 256));
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
        // In and out: zero asks for the key protected, never in plain text.
        var flags = 0u;
        var status = WlanGetProfile(client, in adapter, name, 0, out var xml, ref flags, out _);
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

    /// <summary>Puts a replaced profile's XML back, or deletes a profile the attempt created.</summary>
    private static void RollBackProfile(
        nint client,
        Guid adapter,
        ProfileMutation? mutation)
    {
        if (mutation is not { } authored)
        {
            return;
        }

        if (authored.PreviousXml is { } previous)
        {
            SetProfile(client, adapter, previous);
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

    private readonly record struct CurrentConnection(WifiNetworkKey Key, int Signal, string ProfileName);

    private readonly record struct AvailableNetwork(
        byte[] RawSsid,
        int Signal,
        WifiSecurity Security,
        int Authentication,
        bool Connectable,
        string? ProfileName);

    /// <summary>What one adapter's scan says about one network.</summary>
    /// <param name="Visible">Whether the adapter sees the network now.</param>
    /// <param name="Authentication">The strongest observation's default authentication algorithm.</param>
    /// <param name="BoundProfiles">Profile names Windows matched to the network, in list order.</param>
    private readonly record struct ScanFacts(bool Visible, int Authentication, IReadOnlyList<string> BoundProfiles);

    private readonly record struct InterfaceChoice(
        WlanInterfaceInfo Adapter,
        ScanFacts Facts,
        IReadOnlyList<SavedProfile> Profiles,
        string? ProfileName);

    private sealed class WlanReasonException(uint reasonCode, string message)
        : Win32Exception((int)reasonCode, message)
    {
        internal uint ReasonCode { get; } = reasonCode;
    }
}
