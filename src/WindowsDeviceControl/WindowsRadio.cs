using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Foundation;

namespace WindowsDeviceControl;

/// <summary>Windows radio control: adapter power, Bluetooth discovery and pairing, and Wi-Fi.</summary>
/// <remarks>
/// WinRT owns radio power and Bluetooth. Wi-Fi goes through WLANAPI rather than WinRT's
/// <c>WiFiAdapter</c>, because an unpackaged process cannot declare the <c>wiFiControl</c>
/// capability WinRT requires — which is why an unpackaged desktop, kiosk or service application
/// cannot use the WinRT Wi-Fi surface at all.
/// <para>
/// Every member is synchronous and safe to call from any thread. Windows itself decides what a
/// given process may do: <see cref="RequestAccess"/> reports whether radio power may be changed,
/// and <see cref="GetConsent"/> reports the privacy consent recorded for a capability.
/// </para>
/// </remarks>
public static unsafe partial class WindowsRadio
{
    private const string BluetoothAqs =
        "(System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\""
        + " OR System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\")";
    private const string AepConnected = "System.Devices.Aep.IsConnected";
    private const string AepContainer = "System.Devices.Aep.ContainerId";
    private const uint ErrorSuccess = 0;
    private const uint ErrorNotFound = 1168;
    private const uint WlanNotificationSourceNone = 0;
    private const uint WlanNotificationSourceAcm = 0x00000008;
    private const uint WlanNotificationSourceMsm = 0x00000010;
    private const uint AcmScanComplete = 7;
    private const uint AcmConnectionComplete = 10;
    private const uint AcmConnectionAttemptFail = 11;
    private const uint AcmDisconnected = 21;
    private const uint AcmScanListRefresh = 26;
    private const int WlanInterfaceStateConnected = 1;
    private const int WlanInterfaceStateAdHocFormed = 2;
    private const int WlanInterfaceStateDisconnecting = 3;
    private const int WlanInterfaceStateDisconnected = 4;
    private const int WlanInterfaceStateAssociating = 5;
    private const int WlanInterfaceStateDiscovering = 6;
    private const int WlanInterfaceStateAuthenticating = 7;
    private const int WlanConnectionModeProfile = 0;
    private const int Dot11BssTypeInfrastructure = 1;
    private const int WlanIntfOpcodeCurrentConnection = 7;
    private const int Dot11AuthOpen = 1;
    private const int Dot11AuthSharedKey = 2;
    private const int Dot11AuthWpa = 3;
    private const int Dot11AuthWpaPsk = 4;
    private const int Dot11AuthRsna = 6;
    private const int Dot11AuthRsnaPsk = 7;
    private const int Dot11AuthWpa3 = 8;
    private const int Dot11AuthWpa3Sae = 9;
    private const int Dot11AuthOwe = 10;
    private const int Dot11AuthWpa3Enterprise192 = 11;
    private const int Dot11AuthWpa3Enterprise = 12;
    private const uint ReasonMsmsecBase = 0x40000;
    private const uint ReasonMsmsecConnectBase = 0x48000;
    private const uint ReasonMsmsecEnd = 0x4FFFF;
    private const uint ReasonMsmBase = 0x30000;
    private const uint ReasonMsmEnd = 0x3FFFF;
    private const uint ReasonAcBase = 0x20000;
    private const uint ReasonAcEnd = 0x2FFFF;
    private static readonly TimeSpan RadioCacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan PairingTimeout = TimeSpan.FromSeconds(90);
    private static readonly object RadioCacheLock = new();
    private static (DateTime Taken, IReadOnlyList<Radio> Radios)? _radioCache;
    private static readonly object BluetoothWatchLock = new();
    private static BluetoothWatch? _bluetoothWatch;
    private static readonly object WifiWatchLock = new();
    private static nint _wifiWatchHandle;
    private static WlanNotificationCallback? _wifiWatchCallback;
    private static Action<int>? _wifiEvents;
    private static int _nextPairingToken;
    private static readonly ConcurrentDictionary<uint, PendingPairing> PendingPairings = new();

    /// <summary>The result of a radio power query.</summary>
    public enum Power
    {
        /// <summary>At least one adapter of this kind is on.</summary>
        On,

        /// <summary>Every adapter of this kind is off, and can be turned back on.</summary>
        Off,

        /// <summary>Blocked by the system — typically a hardware switch or airplane mode. Turning
        /// it on will not succeed until whatever disabled it is reversed.</summary>
        Disabled,

        /// <summary>Present, but Windows did not report a state this API recognizes.</summary>
        Unknown,

        /// <summary>No adapter of this kind exists on the machine.</summary>
        Absent,
    }

    /// <summary>Whether Windows permits radio power changes.</summary>
    public enum Access
    {
        /// <summary>This process may change radio power.</summary>
        Allowed,

        /// <summary>The user has denied radio control to this application in privacy settings.
        /// No retry will succeed until the user changes that.</summary>
        DeniedByUser,

        /// <summary>Policy or device configuration denies radio control to every application —
        /// commonly a managed or kiosk-provisioned machine.</summary>
        DeniedBySystem,

        /// <summary>Windows answered without a reason. Treat as denied, but not permanently.</summary>
        Unspecified,
    }

    /// <summary>The privacy consent value reported by the diagnostic registry store.</summary>
    public enum Consent
    {
        /// <summary>Consent is recorded as granted.</summary>
        Allow,

        /// <summary>Consent is recorded as refused.</summary>
        Deny,

        /// <summary>No value is recorded — Windows has not asked yet.</summary>
        Unset,

        /// <summary>The consent store could not be read.</summary>
        Unknown,
    }

    /// <summary>One visible Wi-Fi network.</summary>
    public readonly record struct WifiNetwork(
        string Ssid,
        int Signal,
        int Security,
        bool Saved,
        bool Connectable,
        bool Connected);

    /// <summary>The joined Wi-Fi interface state used by shell surfaces.</summary>
    public readonly record struct WifiStatus(int State, int Signal, string Ssid);

    /// <summary>One Bluetooth association endpoint.</summary>
    public readonly record struct BluetoothDevice(
        string Id,
        string Name,
        bool Paired,
        bool CanPair,
        bool Connected,
        string Container);

    /// <summary>A Bluetooth discovery change.</summary>
    public readonly record struct BluetoothChange(int Kind, BluetoothDevice Device);

    /// <summary>A pairing question that must be answered before its deferral expires.</summary>
    public readonly record struct PairingRequest(uint Token, int Kind, string Pin, string DeviceName);

    /// <summary>How a pairing attempt ended, matching the manager's stable result vocabulary.</summary>
    public readonly record struct PairingResult(int Outcome, int RawStatus);

    /// <summary>Reads one radio kind: zero for Wi-Fi, one for Bluetooth.</summary>
    public static Power GetPower(int kind)
    {
        var radios = GetRadios(kind);
        return radios.Count == 0
            ? Power.Absent
            : AggregatePower(radios.Select(radio => MapPower(radio.State)));
    }

    /// <summary>Requests the process' radio-control access from Windows.</summary>
    public static Access RequestAccess() => MapAccess(
        Radio.RequestAccessAsync().AsTask().GetAwaiter().GetResult());

    /// <summary>Changes every adapter of one radio kind.</summary>
    public static Access SetPower(int kind, bool on)
    {
        var access = RequestAccess();
        if (access != Access.Allowed)
        {
            return access;
        }
        var radios = GetRadios(kind);
        if (radios.Count == 0)
        {
            throw new InvalidOperationException("Windows reported no radio of the requested kind.");
        }
        Access? refusal = null;
        Exception? lastFailure = null;
        foreach (var radio in radios)
        {
            try
            {
                var result = MapAccess(radio.SetStateAsync(on ? RadioState.On : RadioState.Off)
                    .AsTask().GetAwaiter().GetResult());
                if (result != Access.Allowed)
                {
                    refusal ??= result;
                }
            }
            catch (Exception ex)
            {
                lastFailure = ex;
            }
        }
        if (lastFailure is not null)
        {
            throw new InvalidOperationException(
                "At least one radio did not accept the requested power state.", lastFailure);
        }
        return refusal ?? Access.Allowed;
    }

    /// <summary>Reads a capability consent value for the user and machine.
    /// This is diagnostic only; callers still ask the owning API what it permits.</summary>
    public static (Consent User, Consent Machine) GetConsent(string capability) => (
        ReadConsent(Registry.CurrentUser, capability),
        ReadConsent(Registry.LocalMachine, capability));

    private static IReadOnlyList<Radio> GetRadios(int kind)
    {
        IReadOnlyList<Radio> all;
        lock (RadioCacheLock)
        {
            if (_radioCache is { } cached
                && DateTime.UtcNow - cached.Taken < RadioCacheTtl
                && (cached.Radios.Count == 0 || cached.Radios.All(CanReadRadio)))
            {
                all = cached.Radios;
            }
            else
            {
                all = Radio.GetRadiosAsync().AsTask().GetAwaiter().GetResult().ToArray();
                _radioCache = (DateTime.UtcNow, all);
            }
        }
        var expected = kind == 0 ? RadioKind.WiFi : RadioKind.Bluetooth;
        return all.Where(radio => radio.Kind == expected).ToArray();
    }

    private static bool CanReadRadio(Radio radio)
    {
        try
        {
            _ = radio.State;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Reduces several adapters' power states to the one a caller should act on.</summary>
    /// <param name="states">The individual adapter states.</param>
    /// <returns>
    /// The state that represents the group: any adapter on means on, and a machine-wide block is
    /// reported ahead of a merely-off adapter, so a caller does not offer to enable a radio that
    /// airplane mode or a hardware switch will refuse.
    /// </returns>
    public static Power AggregatePower(IEnumerable<Power> states)
    {
        var materialized = states.ToArray();
        foreach (var preferred in new[] { Power.On, Power.Off, Power.Disabled, Power.Unknown })
        {
            if (materialized.Contains(preferred))
            {
                return preferred;
            }
        }
        return Power.Absent;
    }

    private static Power MapPower(RadioState state) => state switch
    {
        RadioState.On => Power.On,
        RadioState.Off => Power.Off,
        RadioState.Disabled => Power.Disabled,
        _ => Power.Unknown,
    };

    private static Access MapAccess(RadioAccessStatus status) => status switch
    {
        RadioAccessStatus.Allowed => Access.Allowed,
        RadioAccessStatus.DeniedByUser => Access.DeniedByUser,
        RadioAccessStatus.DeniedBySystem => Access.DeniedBySystem,
        _ => Access.Unspecified,
    };

    private static Consent ReadConsent(RegistryKey root, string capability)
    {
        try
        {
            using var key = root.OpenSubKey(
                $"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\CapabilityAccessManager\\ConsentStore\\{capability}",
                writable: false);
            if (key is null)
            {
                return Consent.Unset;
            }
            return key.GetValue("Value") switch
            {
                string value when value.Trim().Equals("Allow", StringComparison.OrdinalIgnoreCase)
                    => Consent.Allow,
                string value when value.Trim().Equals("Deny", StringComparison.OrdinalIgnoreCase)
                    => Consent.Deny,
                string value when string.IsNullOrWhiteSpace(value) => Consent.Unset,
                null => Consent.Unset,
                _ => Consent.Unknown,
            };
        }
        catch
        {
            return Consent.Unknown;
        }
    }

    /// <summary>Lists Bluetooth classic and LE association endpoints.</summary>
    public static IReadOnlyList<BluetoothDevice> ListBluetoothDevices(bool pairedOnly)
    {
        var filter = pairedOnly
            ? $"{BluetoothAqs} AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True"
            : BluetoothAqs;
        var found = DeviceInformation.FindAllAsync(
                filter,
                new[] { AepConnected, AepContainer },
                DeviceInformationKind.AssociationEndpoint)
            .AsTask().GetAwaiter().GetResult();
        return found.Select(ReadBluetoothDevice)
            .Where(device => device.Id.Length > 0)
            .OrderBy(device => device.Name.Length == 0)
            .ThenBy(device => device.Name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Counts connected classic and LE Bluetooth interfaces from PnP state.</summary>
    public static int ConnectedBluetoothCount()
    {
        var selectors = new[]
        {
            Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromConnectionStatus(
                BluetoothConnectionStatus.Connected),
            BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
        };
        return selectors.Sum(selector => DeviceInformation.FindAllAsync(selector)
            .AsTask().GetAwaiter().GetResult().Count);
    }

    /// <summary>Starts the live association-endpoint feed. Restarting replaces the old feed.</summary>
    public static void StartBluetoothWatch(Action<BluetoothChange> onChange)
    {
        StopBluetoothWatch();
        var watcher = DeviceInformation.CreateWatcher(
            BluetoothAqs,
            new[] { AepConnected, AepContainer },
            DeviceInformationKind.AssociationEndpoint);
        var records = new Dictionary<string, DeviceInformation>(StringComparer.Ordinal);
        TypedEventHandler<DeviceWatcher, DeviceInformation> added = (_, info) =>
        {
            if (info.Id.Length == 0)
            {
                return;
            }
            lock (records)
            {
                records[info.Id] = info;
            }
            onChange(new BluetoothChange(0, ReadBluetoothDevice(info)));
        };
        TypedEventHandler<DeviceWatcher, DeviceInformationUpdate> updated = (_, update) =>
        {
            DeviceInformation? info;
            lock (records)
            {
                records.TryGetValue(update.Id, out info);
                info?.Update(update);
            }
            if (info is not null)
            {
                onChange(new BluetoothChange(1, ReadBluetoothDevice(info)));
                return;
            }
            try
            {
                var resolved = DeviceInformation.CreateFromIdAsync(
                        update.Id,
                        new[] { AepConnected, AepContainer },
                        DeviceInformationKind.AssociationEndpoint)
                    .AsTask().GetAwaiter().GetResult();
                lock (records)
                {
                    records[resolved.Id] = resolved;
                }
                onChange(new BluetoothChange(1, ReadBluetoothDevice(resolved)));
            }
            catch
            {
                // A disappearing endpoint is followed by Removed; it has no update to publish.
            }
        };
        TypedEventHandler<DeviceWatcher, DeviceInformationUpdate> removed = (_, update) =>
        {
            lock (records)
            {
                records.Remove(update.Id);
            }
            onChange(new BluetoothChange(2, new BluetoothDevice(
                update.Id, string.Empty, false, false, false, string.Empty)));
        };
        TypedEventHandler<DeviceWatcher, object> completed = (_, _) =>
            onChange(new BluetoothChange(3, default));
        watcher.Added += added;
        watcher.Updated += updated;
        watcher.Removed += removed;
        watcher.EnumerationCompleted += completed;
        watcher.Start();
        lock (BluetoothWatchLock)
        {
            _bluetoothWatch = new BluetoothWatch(watcher, added, updated, removed, completed);
        }
    }

    /// <summary>Stops the Bluetooth feed and revokes every callback before returning.</summary>
    public static void StopBluetoothWatch()
    {
        BluetoothWatch? watch;
        lock (BluetoothWatchLock)
        {
            watch = _bluetoothWatch;
            _bluetoothWatch = null;
        }
        if (watch is null)
        {
            return;
        }
        watch.Watcher.Added -= watch.Added;
        watch.Watcher.Updated -= watch.Updated;
        watch.Watcher.Removed -= watch.Removed;
        watch.Watcher.EnumerationCompleted -= watch.Completed;
        watch.Watcher.Stop();
    }

    /// <summary>Begins one bounded custom pairing ceremony on a worker thread.</summary>
    public static void PairBluetooth(
        string deviceId,
        Action<PairingRequest> onRequest,
        Action<PairingResult?, Exception?> onFinished)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        _ = Task.Run(() =>
        {
            try
            {
                var info = DeviceInformation.CreateFromIdAsync(
                        deviceId,
                        new[] { AepConnected, AepContainer },
                        DeviceInformationKind.AssociationEndpoint)
                    .AsTask().GetAwaiter().GetResult();
                var custom = info.Pairing.Custom;
                TypedEventHandler<DeviceInformationCustomPairing, DevicePairingRequestedEventArgs>
                    requested = (_, args) =>
                    {
                        var deferral = args.GetDeferral();
                        var token = unchecked((uint)Interlocked.Increment(ref _nextPairingToken));
                        PendingPairings[token] = new PendingPairing(args, deferral);
                        onRequest(new PairingRequest(
                            token,
                            MapPairingKind(args.PairingKind),
                            args.Pin ?? string.Empty,
                            info.Name ?? string.Empty));
                    };
                custom.PairingRequested += requested;
                try
                {
                    var result = Pair(
                        custom,
                        DevicePairingKinds.ConfirmOnly
                            | DevicePairingKinds.ProvidePin
                            | DevicePairingKinds.ConfirmPinMatch);
                    if (result.Status == DevicePairingResultStatus.RequiredHandlerNotRegistered)
                    {
                        result = Pair(custom, DevicePairingKinds.DisplayPin);
                    }
                    onFinished(new PairingResult(
                        MapPairingOutcome(result.Status),
                        (int)result.Status), null);
                }
                finally
                {
                    custom.PairingRequested -= requested;
                }
            }
            catch (Exception ex)
            {
                Exception failure = ex;
                try
                {
                    CompletePendingPairings();
                }
                catch (Exception cleanupFailure)
                {
                    failure = new AggregateException(ex, cleanupFailure);
                }
                onFinished(null, failure);
            }
        });
    }

    /// <summary>Answers one custom-pairing deferral. Unknown tokens are failures.</summary>
    public static void RespondToPairing(uint token, bool accept, string? pin)
    {
        if (!PendingPairings.TryRemove(token, out var pending))
        {
            throw new InvalidOperationException($"Pairing request {token} is no longer pending.");
        }
        try
        {
            if (accept)
            {
                if (string.IsNullOrEmpty(pin))
                {
                    pending.Args.Accept();
                }
                else
                {
                    pending.Args.Accept(pin);
                }
            }
        }
        finally
        {
            // This method is always called by RadioManager's Task.Run worker. Completing a
            // pairing deferral on Avalonia's STA thread can wedge Device Association service.
            pending.Deferral.Complete();
        }
    }

    /// <summary>Removes a Bluetooth pairing.</summary>
    public static bool UnpairBluetooth(string deviceId)
    {
        var info = DeviceInformation.CreateFromIdAsync(
                deviceId,
                new[] { AepConnected, AepContainer },
                DeviceInformationKind.AssociationEndpoint)
            .AsTask().GetAwaiter().GetResult();
        var result = info.Pairing.UnpairAsync().AsTask().GetAwaiter().GetResult();
        return result.Status is DeviceUnpairingResultStatus.Unpaired
            or DeviceUnpairingResultStatus.AlreadyUnpaired;
    }

    private static DevicePairingResult Pair(
        DeviceInformationCustomPairing pairing,
        DevicePairingKinds kinds)
    {
        using var timeout = new CancellationTokenSource(PairingTimeout);
        try
        {
            return pairing.PairAsync(kinds, DevicePairingProtectionLevel.Default)
                .AsTask(timeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            CompletePendingPairings();
            throw new TimeoutException("Bluetooth pairing timed out.");
        }
    }

    private static void CompletePendingPairings()
    {
        List<Exception>? failures = null;
        foreach (var token in PendingPairings.Keys)
        {
            if (PendingPairings.TryRemove(token, out var request))
            {
                try
                {
                    request.Deferral.Complete();
                }
                catch (Exception ex)
                {
                    (failures ??= []).Add(ex);
                }
            }
        }
        if (failures is not null)
        {
            throw new AggregateException("Pending pairing deferrals could not be completed.", failures);
        }
    }

    private static BluetoothDevice ReadBluetoothDevice(DeviceInformation info)
    {
        var connected = info.Properties.TryGetValue(AepConnected, out var connectedValue)
            && connectedValue is bool isConnected
            && isConnected;
        var container = info.Properties.TryGetValue(AepContainer, out var containerValue)
            ? containerValue switch
            {
                Guid id => id.ToString("D"),
                string text => text.Trim('{', '}').ToLowerInvariant(),
                _ => string.Empty,
            }
            : string.Empty;
        return new BluetoothDevice(
            info.Id ?? string.Empty,
            info.Name ?? string.Empty,
            info.Pairing.IsPaired,
            info.Pairing.CanPair,
            connected,
            container);
    }

    private static int MapPairingKind(DevicePairingKinds kind) => kind switch
    {
        DevicePairingKinds.ConfirmOnly => 0,
        DevicePairingKinds.DisplayPin => 1,
        DevicePairingKinds.ProvidePin => 2,
        DevicePairingKinds.ConfirmPinMatch => 3,
        _ => 4,
    };

    private static int MapPairingOutcome(DevicePairingResultStatus status) => status switch
    {
        DevicePairingResultStatus.Paired => 0,
        DevicePairingResultStatus.AlreadyPaired => 1,
        DevicePairingResultStatus.RejectedByHandler or DevicePairingResultStatus.PairingCanceled => 2,
        DevicePairingResultStatus.AccessDenied => 4,
        DevicePairingResultStatus.OperationAlreadyInProgress => 6,
        DevicePairingResultStatus.Failed
            or DevicePairingResultStatus.ConnectionRejected
            or DevicePairingResultStatus.TooManyConnections
            or DevicePairingResultStatus.HardwareFailure
            or DevicePairingResultStatus.AuthenticationTimeout
            or DevicePairingResultStatus.AuthenticationNotAllowed
            or DevicePairingResultStatus.AuthenticationFailure
            or DevicePairingResultStatus.NoSupportedProfiles => 3,
        _ => 5,
    };

    private sealed record BluetoothWatch(
        DeviceWatcher Watcher,
        TypedEventHandler<DeviceWatcher, DeviceInformation> Added,
        TypedEventHandler<DeviceWatcher, DeviceInformationUpdate> Updated,
        TypedEventHandler<DeviceWatcher, DeviceInformationUpdate> Removed,
        TypedEventHandler<DeviceWatcher, object> Completed);

    /// <summary>Reads the best current WLAN interface and joined network.</summary>
    public static WifiStatus GetWifiStatus()
    {
        using var client = WlanClient.Open();
        var selected = SelectInterface(client.Interfaces());
        var current = TryCurrentConnection(client.Handle, selected.Id);
        return new WifiStatus(
            MapInterfaceState(selected.State),
            current?.Signal ?? 0,
            current?.Ssid ?? string.Empty);
    }

    /// <summary>Asks every WLAN adapter for a fresh scan.</summary>
    public static void RequestWifiScan()
    {
        using var client = WlanClient.Open();
        Exception? last = null;
        var succeeded = false;
        foreach (var adapter in client.Interfaces())
        {
            var status = WlanScan(client.Handle, in adapter.Id, 0, 0, 0);
            if (status == ErrorSuccess)
            {
                succeeded = true;
            }
            else
            {
                last = WlanFailure("WlanScan", status);
            }
        }
        if (!succeeded)
        {
            throw last ?? new InvalidOperationException("Windows reported no WLAN interface.");
        }
    }

    /// <summary>Lists visible networks from every adapter, merged by raw SSID.</summary>
    public static IReadOnlyList<WifiNetwork> ListWifiNetworks()
    {
        using var client = WlanClient.Open();
        var merged = new Dictionary<string, WifiNetworkFacts>(StringComparer.Ordinal);
        Exception? last = null;
        var succeeded = false;
        foreach (var adapter in client.Interfaces())
        {
            try
            {
                MergeNetworks(client.Handle, adapter.Id, merged);
                succeeded = true;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        if (!succeeded && last is not null)
        {
            throw last;
        }
        return merged.Values
            .OrderByDescending(network => network.Signal)
            .ThenBy(network => network.Ssid, StringComparer.Ordinal)
            .Select(network => new WifiNetwork(
                network.Ssid,
                network.Signal,
                (int)network.Security,
                network.Saved,
                network.Connectable,
                network.Connected))
            .ToArray();
    }

    /// <summary>Installs or reuses a WLAN profile and waits for the association verdict.</summary>
    /// <returns>Zero on success or the WLAN reason code reported for failure.</returns>
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
        var interfaces = client.Interfaces();
        var choice = ChooseInterface(client.Handle, interfaces, ssid, passphrase is null);
        var facts = choice.Facts;
        if (facts.Ambiguous)
        {
            throw new InvalidOperationException(
                "More than one network advertises this display name; it cannot be identified safely.");
        }

        var targetSsid = facts.RawSsid.Length == 0 ? Encoding.UTF8.GetBytes(ssid) : facts.RawSsid;
        var profiles = ReadProfileSsids(client.Handle, choice.Adapter.Id, failOnListError: true);
        var profileName = facts.ProfileName;
        string? authored = null;
        var authoredOverExisting = false;

        if (passphrase is not null)
        {
            (authored, authoredOverExisting) = FindFreeProfileName(profiles, ssid, targetSsid);
            var flavors = facts.Authentication == Dot11AuthWpaPsk
                ? new[] { WifiProfile.PskFlavor.WpaTkip, WifiProfile.PskFlavor.Wpa2Aes,
                    WifiProfile.PskFlavor.Wpa3Transition }
                : new[] { WifiProfile.PskFlavor.Wpa3Transition, WifiProfile.PskFlavor.Wpa2Aes };
            Exception? last = null;
            foreach (var flavor in flavors)
            {
                try
                {
                    SetProfile(client.Handle, choice.Adapter.Id, WifiProfile.CreatePsk(
                        authored, ssid, facts.RawSsid, passphrase, flavor));
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
                if (last is WlanReasonException reason
                    && GetReasonVerdict(reason.ReasonCode) != 4)
                {
                    return reason.ReasonCode;
                }
                throw last;
            }
            profileName = authored;
        }
        else if (profileName is null)
        {
            if (facts.Security is not WifiSecurity.Open and not WifiSecurity.EnhancedOpen)
            {
                throw new InvalidOperationException(
                    facts.Security == WifiSecurity.Unsupported
                        ? "This network's WEP security is not supported."
                        : "This network needs a password and has no saved profile.");
            }
            (authored, authoredOverExisting) = FindFreeProfileName(profiles, ssid, targetSsid);
            try
            {
                SetProfile(client.Handle, choice.Adapter.Id, WifiProfile.CreateOpen(
                    authored,
                    ssid,
                    facts.RawSsid,
                    facts.Security == WifiSecurity.EnhancedOpen));
            }
            catch (WlanReasonException reason) when (GetReasonVerdict(reason.ReasonCode) != 4)
            {
                return reason.ReasonCode;
            }
            profileName = authored;
        }

        profileName ??= ssid;
        using var verdict = ConnectionVerdict.TryStart(
            choice.Adapter.Id,
            profileName,
            out var verdictRegistrationStatus);
        var parameters = new WlanConnectionParameters
        {
            Mode = WlanConnectionModeProfile,
            Profile = Marshal.StringToCoTaskMemUni(profileName),
            BssType = Dot11BssTypeInfrastructure,
        };
        try
        {
            var adapterId = choice.Adapter.Id;
            var accepted = WlanConnect(client.Handle, in adapterId, in parameters, 0);
            if (accepted != ErrorSuccess)
            {
                RollBackProfile(client.Handle, choice.Adapter.Id, authored, authoredOverExisting);
                throw WlanFailure("WlanConnect", accepted);
            }
            if (verdict is null)
            {
                if (PollForConnection(client.Handle, choice.Adapter.Id, ssid, ConnectTimeout))
                {
                    return 0;
                }
                RollBackProfile(client.Handle, choice.Adapter.Id, authored, authoredOverExisting);
                throw new TimeoutException(
                    "The Wi-Fi connection attempt did not complete; "
                    + $"WLAN notification registration failed (Win32 {verdictRegistrationStatus}).");
            }

            var outcome = verdict.Wait(ConnectTimeout);
            if (outcome is { Succeeded: true })
            {
                return 0;
            }
            if (outcome is { } failed)
            {
                if (GetReasonVerdict(failed.Reason) is 1 or 2)
                {
                    RollBackProfile(client.Handle, choice.Adapter.Id, authored, authoredOverExisting);
                }
                return failed.Reason == 0 ? ErrorNotFound : failed.Reason;
            }
            if (TryCurrentConnection(client.Handle, choice.Adapter.Id)?.Ssid != ssid)
            {
                RollBackProfile(client.Handle, choice.Adapter.Id, authored, authoredOverExisting);
            }
            throw new TimeoutException("The Wi-Fi connection attempt did not complete.");
        }
        finally
        {
            Marshal.FreeCoTaskMem(parameters.Profile);
        }
    }

    /// <summary>Disconnects every connected or connecting WLAN interface.</summary>
    public static void DisconnectWifi()
    {
        using var client = WlanClient.Open();
        Exception? last = null;
        foreach (var adapter in client.Interfaces())
        {
            if (MapInterfaceState(adapter.State) is not 0 and not 1)
            {
                continue;
            }
            var status = WlanDisconnect(client.Handle, in adapter.Id, 0);
            if (status != ErrorSuccess)
            {
                last = WlanFailure("WlanDisconnect", status);
            }
        }
        if (last is not null)
        {
            throw last;
        }
    }

    /// <summary>Deletes every saved profile whose document names the SSID.</summary>
    public static void ForgetWifi(string ssid)
    {
        using var client = WlanClient.Open();
        Exception? last = null;
        foreach (var adapter in client.Interfaces())
        {
            try
            {
                var facts = ReadScanFacts(client.Handle, adapter.Id, ssid);
                if (facts.Ambiguous)
                {
                    throw new InvalidOperationException(
                        "More than one network advertises this display name; it cannot be identified safely.");
                }
                var target = facts.RawSsid.Length == 0 ? Encoding.UTF8.GetBytes(ssid) : facts.RawSsid;
                var names = ReadProfileSsids(client.Handle, adapter.Id, failOnListError: true)
                    .Where(profile => profile.Ssid.AsSpan().SequenceEqual(target))
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
                    if (status != ErrorSuccess)
                    {
                        throw WlanFailure("WlanDeleteProfile", status);
                    }
                }
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        if (last is not null)
        {
            throw last;
        }
    }

    /// <summary>Maps a WLAN reason code to the manager's stable verdict.</summary>
    public static int GetReasonVerdict(uint code)
    {
        if (code == 0)
        {
            return 0;
        }
        if (code >= ReasonMsmsecBase && code < ReasonMsmsecConnectBase)
        {
            return 2;
        }
        if (code >= ReasonMsmBase && code <= ReasonMsmEnd
            || code >= ReasonAcBase && code <= ReasonAcEnd)
        {
            return 3;
        }
        if (code >= ReasonMsmsecConnectBase && code <= ReasonMsmsecEnd)
        {
            return 1;
        }
        return 4;
    }

    private static bool PollForConnection(
        nint client,
        Guid adapter,
        string ssid,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (TryCurrentConnection(client, adapter)?.Ssid == ssid)
            {
                return true;
            }
            Thread.Sleep(500);
        }
        return false;
    }

    /// <summary>Returns Windows' localized text for one WLAN reason code.</summary>
    public static string ReasonText(uint code)
    {
        var buffer = new char[1024];
        fixed (char* text = buffer)
        {
            var status = WlanReasonCodeToString(code, (uint)buffer.Length, text, 0);
            if (status != ErrorSuccess)
            {
                return $"Wi-Fi reason code {code}";
            }
        }
        var result = new string(buffer, 0, Array.IndexOf(buffer, '\0') is var end && end >= 0
            ? end
            : buffer.Length).Trim();
        return result.Length == 0 ? $"Wi-Fi reason code {code}" : result;
    }

    /// <summary>Starts process-wide WLAN change notifications. Restarting replaces the old feed.</summary>
    public static void StartWifiWatch(Action<int> onEvent)
    {
        StopWifiWatch();
        var status = WlanOpenHandle(2, 0, out _, out var handle);
        ThrowIfWlanFailed("WlanOpenHandle", status);
        _wifiWatchCallback = OnWifiNotification;
        _wifiEvents = onEvent;
        var callback = Marshal.GetFunctionPointerForDelegate(_wifiWatchCallback);
        var sources = WlanNotificationSourceAcm | WlanNotificationSourceMsm;
        status = WlanRegisterNotification(handle, sources, 1, callback, 0, 0, 0);
        if (status != ErrorSuccess)
        {
            status = WlanRegisterNotification(
                handle, WlanNotificationSourceAcm, 1, callback, 0, 0, 0);
        }
        if (status != ErrorSuccess)
        {
            WlanCloseHandle(handle, 0);
            _wifiEvents = null;
            _wifiWatchCallback = null;
            throw WlanFailure("WlanRegisterNotification", status);
        }
        lock (WifiWatchLock)
        {
            _wifiWatchHandle = handle;
        }
    }

    /// <summary>Stops the WLAN notification feed.</summary>
    public static void StopWifiWatch()
    {
        nint handle;
        lock (WifiWatchLock)
        {
            handle = _wifiWatchHandle;
            _wifiWatchHandle = 0;
        }
        if (handle != 0)
        {
            WlanRegisterNotification(handle, WlanNotificationSourceNone, 0, 0, 0, 0, 0);
            WlanCloseHandle(handle, 0);
        }
        _wifiEvents = null;
        _wifiWatchCallback = null;
    }

    private static void OnWifiNotification(nint data, nint context)
    {
        try
        {
            if (data == 0)
            {
                return;
            }
            var notification = Marshal.PtrToStructure<WlanNotificationData>(data);
            if (notification.Source != WlanNotificationSourceAcm)
            {
                return;
            }
            var eventCode = notification.Code switch
            {
                AcmScanComplete or AcmScanListRefresh => 0,
                AcmConnectionComplete or AcmDisconnected => 1,
                _ => -1,
            };
            if (eventCode >= 0)
            {
                _wifiEvents?.Invoke(eventCode);
            }
        }
        catch
        {
            // A native service callback cannot propagate managed failures.
        }
    }

    private static WlanInterfaceInfo SelectInterface(IReadOnlyList<WlanInterfaceInfo> interfaces)
        => interfaces.FirstOrDefault(adapter => MapInterfaceState(adapter.State) == 0) is var connected
            && connected.Id != Guid.Empty
                ? connected
                : interfaces[0];

    private static int MapInterfaceState(int state) => state switch
    {
        WlanInterfaceStateConnected or WlanInterfaceStateAdHocFormed => 0,
        WlanInterfaceStateAssociating or WlanInterfaceStateDiscovering
            or WlanInterfaceStateAuthenticating => 1,
        WlanInterfaceStateDisconnecting or WlanInterfaceStateDisconnected => 2,
        _ => 3,
    };

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
            if (MapInterfaceState(current.State) != 0)
            {
                return null;
            }
            return new CurrentConnection(
                DecodeSsid(current.Association.Ssid),
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
        var status = WlanGetAvailableNetworkList(client, in adapter, 0, 0, out var list);
        ThrowIfWlanFailed("WlanGetAvailableNetworkList", status);
        if (list == 0)
        {
            return;
        }
        try
        {
            var count = (uint)Marshal.ReadInt32(list);
            if (count > 4096)
            {
                throw new InvalidOperationException($"WLANAPI reported an invalid network count ({count}).");
            }
            var profiles = ReadProfileSsids(client, adapter, failOnListError: false);
            var connected = TryCurrentConnection(client, adapter)?.Ssid;
            var start = list + 8;
            var stride = Marshal.SizeOf<WlanAvailableNetwork>();
            for (var index = 0u; index < count; index++)
            {
                var item = Marshal.PtrToStructure<WlanAvailableNetwork>(
                    start + checked((int)index * stride));
                var raw = ReadSsidBytes(item.Ssid);
                var ssid = Encoding.UTF8.GetString(raw);
                if (ssid.Length == 0)
                {
                    continue;
                }
                var key = Convert.ToHexString(raw);
                var saved = item.ProfileName[0] != '\0'
                    || profiles.Any(profile => profile.Ssid.AsSpan().SequenceEqual(raw));
                var facts = new WifiNetworkFacts(
                    ssid,
                    raw,
                    (int)item.SignalQuality,
                    ClassifySecurity(item.SecurityEnabled != 0, item.DefaultAuthAlgorithm),
                    item.DefaultAuthAlgorithm,
                    saved,
                    item.Connectable != 0,
                    string.Equals(connected, ssid, StringComparison.Ordinal),
                    ReadFixed(item.ProfileName, 256),
                    false);
                if (merged.TryGetValue(key, out var existing))
                {
                    merged[key] = existing with
                    {
                        Signal = Math.Max(existing.Signal, facts.Signal),
                        Saved = existing.Saved || facts.Saved,
                        Connectable = existing.Connectable || facts.Connectable,
                        Connected = existing.Connected || facts.Connected,
                    };
                }
                else
                {
                    merged.Add(key, facts);
                }
            }
        }
        finally
        {
            WlanFreeMemory(list);
        }
    }

    private static InterfaceChoice ChooseInterface(
        nint client,
        IReadOnlyList<WlanInterfaceInfo> interfaces,
        string ssid,
        bool needsSavedProfile)
    {
        InterfaceChoice? best = null;
        var bestRank = -1;
        foreach (var adapter in interfaces)
        {
            var facts = ReadScanFacts(client, adapter.Id, ssid);
            var visible = facts.RawSsid.Length > 0;
            var saved = facts.ProfileName is not null;
            var rank = (visible, saved, needsSavedProfile) switch
            {
                (true, true, _) => 4,
                (false, true, true) => 3,
                (true, false, _) => 2,
                (false, true, false) => 1,
                _ => 0,
            };
            if (rank > bestRank)
            {
                bestRank = rank;
                best = new InterfaceChoice(adapter, facts);
            }
        }
        return best ?? new InterfaceChoice(interfaces[0], WifiNetworkFacts.Empty(ssid));
    }

    private static WifiNetworkFacts ReadScanFacts(nint client, Guid adapter, string ssid)
    {
        var facts = WifiNetworkFacts.Empty(ssid);
        var status = WlanGetAvailableNetworkList(client, in adapter, 0, 0, out var list);
        if (status == ErrorSuccess && list != 0)
        {
            try
            {
                var count = (uint)Marshal.ReadInt32(list);
                var start = list + 8;
                var stride = Marshal.SizeOf<WlanAvailableNetwork>();
                for (var index = 0u; index < Math.Min(count, 4096u); index++)
                {
                    var item = Marshal.PtrToStructure<WlanAvailableNetwork>(
                        start + checked((int)index * stride));
                    var raw = ReadSsidBytes(item.Ssid);
                    if (!string.Equals(Encoding.UTF8.GetString(raw), ssid, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    facts = facts with
                    {
                        RawSsid = facts.RawSsid.Length == 0 ? raw : facts.RawSsid,
                        Ambiguous = facts.RawSsid.Length > 0 && !facts.RawSsid.AsSpan().SequenceEqual(raw),
                        Security = ClassifySecurity(item.SecurityEnabled != 0, item.DefaultAuthAlgorithm),
                        Authentication = item.DefaultAuthAlgorithm,
                        ProfileName = ReadFixed(item.ProfileName, 256) is { Length: > 0 } name
                            ? name
                            : facts.ProfileName,
                    };
                }
            }
            finally
            {
                WlanFreeMemory(list);
            }
        }
        if (facts.ProfileName is null)
        {
            var target = facts.RawSsid.Length == 0 ? Encoding.UTF8.GetBytes(ssid) : facts.RawSsid;
            facts = facts with
            {
                ProfileName = ReadProfileSsids(client, adapter, failOnListError: false)
                    .FirstOrDefault(profile => profile.Ssid.AsSpan().SequenceEqual(target)).Name,
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
        try
        {
            var count = (uint)Marshal.ReadInt32(list);
            var start = list + 8;
            var stride = Marshal.SizeOf<WlanProfileInfo>();
            var profiles = new List<SavedProfile>(checked((int)Math.Min(count, 4096)));
            for (var index = 0u; index < Math.Min(count, 4096u); index++)
            {
                var record = Marshal.PtrToStructure<WlanProfileInfo>(
                    start + checked((int)index * stride));
                var name = ReadFixed(record.Name, 256);
                if (name.Length == 0)
                {
                    continue;
                }
                var rawSsid = TryReadProfileXml(client, adapter, name) is { } xml
                    ? WifiProfile.TryReadSsid(xml)
                    : null;
                profiles.Add(new SavedProfile(name, rawSsid ?? Encoding.UTF8.GetBytes(name)));
            }
            return profiles;
        }
        finally
        {
            WlanFreeMemory(list);
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

    private static (string Name, bool Existed) FindFreeProfileName(
        IReadOnlyList<SavedProfile> profiles,
        string ssid,
        byte[] target)
    {
        var exact = profiles.FirstOrDefault(profile => profile.Name == ssid);
        if (exact.Name is null)
        {
            return (ssid, false);
        }
        if (exact.Ssid.AsSpan().SequenceEqual(target))
        {
            return (ssid, true);
        }
        for (var suffix = 2; suffix <= 64; suffix++)
        {
            var candidate = $"{ssid} {suffix}";
            var owner = profiles.FirstOrDefault(profile => profile.Name == candidate);
            if (owner.Name is null)
            {
                return (candidate, false);
            }
            if (owner.Ssid.AsSpan().SequenceEqual(target))
            {
                return (candidate, true);
            }
        }
        return (ssid, true);
    }

    private static void SetProfile(nint client, Guid adapter, string xml)
    {
        var status = WlanSetProfile(client, in adapter, 0, xml, null, 1, 0, out var reason);
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
        string? authored,
        bool existed)
    {
        if (authored is not null && !existed)
        {
            WlanDeleteProfile(client, in adapter, authored, 0);
        }
    }

    private static WifiSecurity ClassifySecurity(bool secured, int auth)
    {
        if (!secured)
        {
            return WifiSecurity.Open;
        }
        return auth switch
        {
            Dot11AuthOwe => WifiSecurity.EnhancedOpen,
            Dot11AuthOpen or Dot11AuthSharedKey => WifiSecurity.Unsupported,
            Dot11AuthWpaPsk or Dot11AuthRsnaPsk or Dot11AuthWpa3Sae
                => WifiSecurity.PersonalPsk,
            Dot11AuthWpa or Dot11AuthRsna or Dot11AuthWpa3
                or Dot11AuthWpa3Enterprise or Dot11AuthWpa3Enterprise192
                => WifiSecurity.Enterprise,
            _ => WifiSecurity.PersonalPsk,
        };
    }

    private static byte[] ReadSsidBytes(Dot11Ssid ssid)
    {
        var length = Math.Min((int)ssid.Length, 32);
        var bytes = new byte[length];
        byte* source = ssid.Value;
        Marshal.Copy((nint)source, bytes, 0, length);
        return bytes;
    }

    private static string DecodeSsid(Dot11Ssid ssid) => Encoding.UTF8.GetString(ReadSsidBytes(ssid));

    private static string ReadFixed(char* value, int length)
    {
        var end = 0;
        while (end < length && value[end] != '\0')
        {
            end++;
        }
        return new string(value, 0, end);
    }

    private static Exception WlanFailure(string operation, uint status)
        => new Win32Exception((int)status, $"{operation} failed (Win32 {status}).");

    private static void ThrowIfWlanFailed(string operation, uint status)
    {
        if (status != ErrorSuccess)
        {
            throw WlanFailure(operation, status);
        }
    }

    private sealed class WlanClient : IDisposable
    {
        private WlanClient(nint handle) => Handle = handle;

        internal nint Handle { get; }

        public static WlanClient Open()
        {
            var status = WlanOpenHandle(2, 0, out _, out var handle);
            ThrowIfWlanFailed("WlanOpenHandle", status);
            return new WlanClient(handle);
        }

        internal IReadOnlyList<WlanInterfaceInfo> Interfaces()
        {
            var status = WlanEnumInterfaces(Handle, 0, out var list);
            ThrowIfWlanFailed("WlanEnumInterfaces", status);
            if (list == 0)
            {
                throw new InvalidOperationException("Windows reported no WLAN interface.");
            }
            try
            {
                var count = (uint)Marshal.ReadInt32(list);
                if (count == 0 || count > 64)
                {
                    throw new InvalidOperationException($"Windows reported {count} WLAN interfaces.");
                }
                var start = list + 8;
                var stride = Marshal.SizeOf<WlanInterfaceInfo>();
                var adapters = new WlanInterfaceInfo[count];
                for (var index = 0u; index < count; index++)
                {
                    adapters[index] = Marshal.PtrToStructure<WlanInterfaceInfo>(
                        start + checked((int)index * stride));
                }
                return adapters;
            }
            finally
            {
                WlanFreeMemory(list);
            }
        }

        public void Dispose() => WlanCloseHandle(Handle, 0);
    }

    private sealed class ConnectionVerdict : IDisposable
    {
        private readonly nint _handle;
        private readonly WlanNotificationCallback _callback;
        private readonly Guid _adapter;
        private readonly string _profile;
        private readonly ManualResetEventSlim _ready = new(false);
        private ConnectionOutcome? _outcome;

        private ConnectionVerdict(nint handle, Guid adapter, string profile)
        {
            _handle = handle;
            _adapter = adapter;
            _profile = profile;
            _callback = OnNotification;
        }

        public static ConnectionVerdict? TryStart(
            Guid adapter,
            string profile,
            out uint status)
        {
            status = WlanOpenHandle(2, 0, out _, out var handle);
            if (status != ErrorSuccess)
            {
                return null;
            }
            var verdict = new ConnectionVerdict(handle, adapter, profile);
            status = WlanRegisterNotification(
                handle,
                WlanNotificationSourceAcm,
                1,
                Marshal.GetFunctionPointerForDelegate(verdict._callback),
                0,
                0,
                0);
            if (status != ErrorSuccess)
            {
                WlanCloseHandle(handle, 0);
                return null;
            }
            return verdict;
        }

        internal ConnectionOutcome? Wait(TimeSpan timeout)
            => _ready.Wait(timeout) ? _outcome : null;

        private void OnNotification(nint data, nint context)
        {
            try
            {
                var notification = Marshal.PtrToStructure<WlanNotificationData>(data);
                if (notification.Source != WlanNotificationSourceAcm
                    || notification.InterfaceId != _adapter
                    || notification.Code is not AcmConnectionComplete and not AcmConnectionAttemptFail)
                {
                    return;
                }
                var reason = 0u;
                var profile = string.Empty;
                if (notification.Data != 0
                    && notification.DataSize >= (uint)Marshal.SizeOf<WlanConnectionNotificationData>())
                {
                    var payload = Marshal.PtrToStructure<WlanConnectionNotificationData>(
                        notification.Data);
                    reason = payload.ReasonCode;
                    profile = ReadFixed(payload.ProfileName, 256);
                }
                if (profile.Length > 0 && !string.Equals(profile, _profile, StringComparison.Ordinal))
                {
                    return;
                }
                _outcome = new ConnectionOutcome(
                    notification.Code == AcmConnectionComplete && reason == 0,
                    reason);
                _ready.Set();
            }
            catch
            {
                // Native callback failures cannot cross WLANAPI.
            }
        }

        public void Dispose()
        {
            WlanRegisterNotification(_handle, WlanNotificationSourceNone, 0, 0, 0, 0, 0);
            WlanCloseHandle(_handle, 0);
            _ready.Dispose();
            GC.KeepAlive(_callback);
        }
    }

    private enum WifiSecurity
    {
        Open,
        PersonalPsk,
        Enterprise,
        EnhancedOpen,
        Unsupported,
    }

    private readonly record struct CurrentConnection(string Ssid, int Signal);
    private readonly record struct ConnectionOutcome(bool Succeeded, uint Reason);
    private readonly record struct SavedProfile(string? Name, byte[] Ssid);
    private readonly record struct InterfaceChoice(WlanInterfaceInfo Adapter, WifiNetworkFacts Facts);
    private sealed class WlanReasonException(uint reasonCode, string message)
        : Win32Exception((int)reasonCode, message)
    {
        internal uint ReasonCode { get; } = reasonCode;
    }

    private readonly record struct WifiNetworkFacts(
        string Ssid,
        byte[] RawSsid,
        int Signal,
        WifiSecurity Security,
        int Authentication,
        bool Saved,
        bool Connectable,
        bool Connected,
        string? ProfileName,
        bool Ambiguous)
    {
        public static WifiNetworkFacts Empty(string ssid)
            => new(ssid, [], 0, WifiSecurity.PersonalPsk, 0, false, false, false, null, false);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WlanNotificationCallback(nint data, nint context);

    [StructLayout(LayoutKind.Sequential)]
    private struct Dot11Ssid
    {
        internal uint Length;
        internal fixed byte Value[32];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanInterfaceInfo
    {
        internal Guid Id;
        internal fixed char Description[256];
        internal int State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanProfileInfo
    {
        internal fixed char Name[256];
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanAvailableNetwork
    {
        internal fixed char ProfileName[256];
        internal Dot11Ssid Ssid;
        internal int BssType;
        internal uint BssidCount;
        internal int Connectable;
        internal uint NotConnectableReason;
        internal uint PhyTypeCount;
        internal fixed int PhyTypes[8];
        internal int MorePhyTypes;
        internal uint SignalQuality;
        internal int SecurityEnabled;
        internal int DefaultAuthAlgorithm;
        internal int DefaultCipherAlgorithm;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanAssociationAttributes
    {
        internal Dot11Ssid Ssid;
        internal int BssType;
        internal fixed byte Bssid[6];
        internal int PhyType;
        internal uint PhyIndex;
        internal uint SignalQuality;
        internal uint RxRate;
        internal uint TxRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanSecurityAttributes
    {
        internal int SecurityEnabled;
        internal int OneXEnabled;
        internal int AuthAlgorithm;
        internal int CipherAlgorithm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanConnectionAttributes
    {
        internal int State;
        internal int Mode;
        internal fixed char ProfileName[256];
        internal WlanAssociationAttributes Association;
        internal WlanSecurityAttributes Security;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanConnectionParameters
    {
        internal int Mode;
        internal nint Profile;
        internal nint Ssid;
        internal nint DesiredBssidList;
        internal int BssType;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanNotificationData
    {
        internal uint Source;
        internal uint Code;
        internal Guid InterfaceId;
        internal uint DataSize;
        internal nint Data;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanConnectionNotificationData
    {
        internal int Mode;
        internal fixed char ProfileName[256];
        internal Dot11Ssid Ssid;
        internal int BssType;
        internal int SecurityEnabled;
        internal uint ReasonCode;
        internal uint Flags;
        internal char ProfileXml;
    }

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanOpenHandle(
        uint clientVersion, nint reserved, out uint negotiatedVersion, out nint clientHandle);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanCloseHandle(nint clientHandle, nint reserved);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanEnumInterfaces(nint clientHandle, nint reserved, out nint list);

    [LibraryImport("wlanapi.dll")]
    private static partial void WlanFreeMemory(nint memory);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanScan(
        nint clientHandle, in Guid interfaceId, nint ssid, nint ies, nint reserved);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanGetAvailableNetworkList(
        nint clientHandle, in Guid interfaceId, uint flags, nint reserved, out nint list);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanGetProfileList(
        nint clientHandle, in Guid interfaceId, nint reserved, out nint list);

    [LibraryImport("wlanapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint WlanGetProfile(
        nint clientHandle,
        in Guid interfaceId,
        string profileName,
        nint reserved,
        out nint profileXml,
        out uint flags,
        out uint access);

    [LibraryImport("wlanapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint WlanSetProfile(
        nint clientHandle,
        in Guid interfaceId,
        uint flags,
        string profileXml,
        string? securityDescriptor,
        int overwrite,
        nint reserved,
        out uint reasonCode);

    [LibraryImport("wlanapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint WlanDeleteProfile(
        nint clientHandle, in Guid interfaceId, string profileName, nint reserved);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanConnect(
        nint clientHandle,
        in Guid interfaceId,
        in WlanConnectionParameters parameters,
        nint reserved);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanDisconnect(nint clientHandle, in Guid interfaceId, nint reserved);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanQueryInterface(
        nint clientHandle,
        in Guid interfaceId,
        int opcode,
        nint reserved,
        out uint dataSize,
        out nint data,
        out int opcodeValueType);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanRegisterNotification(
        nint clientHandle,
        uint notificationSource,
        int ignoreDuplicate,
        nint callback,
        nint callbackContext,
        nint reserved,
        nint previousSource);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanReasonCodeToString(
        uint reasonCode, uint bufferSize, char* buffer, nint reserved);

    private sealed record PendingPairing(DevicePairingRequestedEventArgs Args, Deferral Deferral);
}
