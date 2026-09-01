using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
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
    private const string DeviceContainer = "System.Devices.ContainerId";
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
    private static (long Taken, IReadOnlyList<Radio> Radios)? _radioCache;
    private static readonly object BluetoothWatchLock = new();
    private static BluetoothWatch? _bluetoothWatch;
    private static readonly object WifiWatchLock = new();
    private static WifiWatch? _wifiWatch;
    private static int _nextPairingToken;
    private static long _nextPairingAttempt;
    private static readonly ConcurrentDictionary<uint, PendingPairing> PendingPairings = new();
    private static readonly ConcurrentDictionary<long, byte> ActivePairingAttempts = new();

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

    /// <summary>Which family of radio adapter an operation applies to.</summary>
    public enum RadioKind
    {
        /// <summary>Every Wi-Fi adapter.</summary>
        WiFi,

        /// <summary>Every Bluetooth adapter.</summary>
        Bluetooth,
    }

    /// <summary>What happened to a device seen by <see cref="StartBluetoothWatch"/>.</summary>
    public enum BluetoothChangeKind
    {
        /// <summary>The device appeared.</summary>
        Added,

        /// <summary>A property of a known device changed — typically its connected state.</summary>
        Updated,

        /// <summary>The device disappeared. Only its identifier is meaningful.</summary>
        Removed,

        /// <summary>The initial sweep finished; everything already present has been reported.
        /// The watch stays active and keeps reporting later changes.</summary>
        EnumerationCompleted,
    }

    /// <summary>What a pairing ceremony is asking the user to do.</summary>
    /// <remarks>
    /// The value decides what your UI must show and what
    /// <see cref="RespondToPairing"/> needs back: only <see cref="ProvidePin"/> requires a PIN
    /// argument, and the others are answered with accept or reject alone.
    /// </remarks>
    public enum PairingKind
    {
        /// <summary>Confirm that pairing should proceed. No PIN is involved.</summary>
        ConfirmOnly,

        /// <summary>Show the PIN carried by the request so the user can type it on the device.</summary>
        DisplayPin,

        /// <summary>Ask the user for the PIN shown on the device, and pass it back.</summary>
        ProvidePin,

        /// <summary>Show the PIN and confirm it matches the one on the device.</summary>
        ConfirmPinMatch,

        /// <summary>A ceremony this library does not recognize. Reject it.</summary>
        Unknown,
    }

    /// <summary>How a pairing attempt ended.</summary>
    public enum PairingOutcome
    {
        /// <summary>Paired successfully.</summary>
        Paired,

        /// <summary>The device was already paired; nothing changed.</summary>
        AlreadyPaired,

        /// <summary>Cancelled — by your handler rejecting it, or by the user.</summary>
        Cancelled,

        /// <summary>The attempt failed: rejected, timed out, out of connections, or a hardware
        /// or authentication failure.</summary>
        Failed,

        /// <summary>Windows refused this process permission to pair.</summary>
        AccessDenied,

        /// <summary>Windows reported a status this library does not classify. Consult
        /// <see cref="PairingResult.RawStatus"/>.</summary>
        Unknown,

        /// <summary>Another pairing attempt for this device is already running.</summary>
        AlreadyInProgress,
    }

    /// <summary>The security a Wi-Fi network requires to join.</summary>
    public enum WifiSecurity
    {
        /// <summary>No authentication.</summary>
        Open,

        /// <summary>A pre-shared key — the ordinary home and small-office network.</summary>
        PersonalPsk,

        /// <summary>802.1X enterprise authentication. This library does not build enterprise
        /// profiles; join these with a profile provisioned by other means.</summary>
        Enterprise,

        /// <summary>Opportunistic Wireless Encryption: no passphrase, but encrypted.</summary>
        EnhancedOpen,

        /// <summary>An authentication algorithm this library cannot build a profile for.</summary>
        Unsupported,
    }

    /// <summary>The Wi-Fi adapter's connection state.</summary>
    public enum WifiConnectionState
    {
        /// <summary>Joined to a network.</summary>
        Connected,

        /// <summary>Associating, discovering or authenticating.</summary>
        Connecting,

        /// <summary>Not joined, and not attempting to join.</summary>
        Disconnected,

        /// <summary>No adapter, or a state this library does not recognize.</summary>
        Unknown,
    }

    /// <summary>Why a join attempt failed, reduced to the outcomes a caller acts on
    /// differently.</summary>
    /// <remarks>Produced by <see cref="GetReasonVerdict"/> from a raw WLAN reason code. Use
    /// <see cref="ReasonText"/> when you want Windows' own wording for the specific code.</remarks>
    public enum WifiFailureKind
    {
        /// <summary>Not a failure — the join succeeded.</summary>
        None,

        /// <summary>The access point rejected the key. This is the one case where re-prompting
        /// for the passphrase is the right response.</summary>
        KeyRejected,

        /// <summary>The profile's security settings do not match what the access point offers,
        /// so the key was never tried. Reported before association completes.</summary>
        SecurityMismatch,

        /// <summary>Association or the connection manager gave up: the network was out of range,
        /// too weak, or stopped responding. Nothing about the passphrase is implied.</summary>
        Unreachable,

        /// <summary>A reason code outside the ranges this library classifies. Show
        /// <see cref="ReasonText"/> rather than guessing at a cause.</summary>
        Unknown,
    }

    /// <summary>What changed, as reported to a <see cref="StartWifiWatch"/> callback.</summary>
    /// <remarks>Each value says which query is now worth repeating; neither carries the new data
    /// itself. Windows raises many more notification codes than these, and the rest describe
    /// internal state transitions that change nothing a caller can observe, so they are dropped
    /// rather than passed on as callbacks that lead to identical results.</remarks>
    public enum WifiWatchEvent
    {
        /// <summary>A scan finished or the visible-network list changed. Call
        /// <see cref="ListWifiNetworks"/> for the new results.</summary>
        ScanCompleted,

        /// <summary>The adapter connected or disconnected. Call <see cref="GetWifiStatus"/> for
        /// the new state. This is also raised when a connection attempt fails.</summary>
        ConnectionChanged,
    }

    /// <summary>One visible Wi-Fi network.</summary>
    /// <param name="Ssid">The network name. Empty for a hidden network that advertises none.</param>
    /// <param name="Signal">Signal quality, 0 to 100, as Windows reports it.</param>
    /// <param name="Security">What joining it requires.</param>
    /// <param name="Saved">Whether a profile for it already exists on this machine, in which case
    /// <see cref="ConnectWifi"/> needs no passphrase.</param>
    /// <param name="Connectable">Whether Windows currently considers it joinable.</param>
    /// <param name="Connected">Whether this is the network the adapter is joined to.</param>
    public readonly record struct WifiNetwork(
        string Ssid,
        int Signal,
        WifiSecurity Security,
        bool Saved,
        bool Connectable,
        bool Connected);

    /// <summary>The Wi-Fi adapter's current state.</summary>
    /// <param name="State">Whether the adapter is joined, joining, or neither.</param>
    /// <param name="Signal">Signal quality of the joined network, 0 to 100; zero when not joined.</param>
    /// <param name="Ssid">The joined network's name; empty when not joined.</param>
    public readonly record struct WifiStatus(WifiConnectionState State, int Signal, string Ssid);

    /// <summary>One Bluetooth association endpoint.</summary>
    /// <param name="Id">The device identifier, and the handle for every other operation here.</param>
    /// <param name="Name">The friendly name, for display.</param>
    /// <param name="Paired">Whether the device is already paired with this machine.</param>
    /// <param name="CanPair">Whether Windows considers it pairable right now.</param>
    /// <param name="Connected">Whether it is currently connected. A device can be paired without
    /// being connected — a headset that is switched off, for instance.</param>
    /// <param name="Container">The container identifier, which is what ties this device to its
    /// audio endpoints in <see cref="CoreAudio.ListBluetoothAudioContainers"/>.</param>
    public readonly record struct BluetoothDevice(
        string Id,
        string Name,
        bool Paired,
        bool CanPair,
        bool Connected,
        string Container);

    /// <summary>A Bluetooth discovery change.</summary>
    /// <param name="Kind">What happened to the device.</param>
    /// <param name="Device">The device it happened to. Only <see cref="BluetoothDevice.Id"/> is
    /// meaningful when <paramref name="Kind"/> is <see cref="BluetoothChangeKind.Removed"/>, and
    /// the whole value is default for
    /// <see cref="BluetoothChangeKind.EnumerationCompleted"/>.</param>
    public readonly record struct BluetoothChange(BluetoothChangeKind Kind, BluetoothDevice Device);

    /// <summary>A pairing question that must be answered before its deferral expires.</summary>
    /// <param name="Token">Identifies this question; pass it to <see cref="RespondToPairing"/>.</param>
    /// <param name="Kind">What the user is being asked, and therefore what your UI must show.</param>
    /// <param name="Pin">The PIN to display, when the ceremony carries one; otherwise empty.</param>
    /// <param name="DeviceName">The device's friendly name, for your prompt.</param>
    public readonly record struct PairingRequest(
        uint Token,
        PairingKind Kind,
        string Pin,
        string DeviceName);

    /// <summary>How a pairing attempt ended.</summary>
    /// <param name="Outcome">The classified result.</param>
    /// <param name="RawStatus">Windows' own <c>DevicePairingResultStatus</c> value, kept so an
    /// unclassified outcome can still be diagnosed.</param>
    public readonly record struct PairingResult(PairingOutcome Outcome, int RawStatus);

    /// <summary>Reads the combined power state of every adapter of one kind.</summary>
    /// <param name="kind">Which radio family to read.</param>
    /// <returns>The aggregate state; <see cref="Power.Absent"/> when the machine has no such
    /// adapter.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a defined
    /// <see cref="RadioKind"/> value.</exception>
    public static Power GetPower(RadioKind kind)
    {
        var radios = GetRadios(kind);
        return radios.Count == 0
            ? Power.Absent
            : AggregatePower(radios.Select(radio => MapPower(radio.State)));
    }

    /// <summary>Asks Windows whether this process may change radio power.</summary>
    /// <returns>Whether radio control is permitted, and if not, why.</returns>
    /// <remarks>Called for you by <see cref="SetPower"/>. Call it directly to decide whether to
    /// show a radio toggle at all — a denied toggle that silently does nothing is worse than an
    /// absent one.</remarks>
    public static Access RequestAccess() => MapAccess(
        Radio.RequestAccessAsync().AsTask().GetAwaiter().GetResult());

    /// <summary>Turns every adapter of one kind on or off.</summary>
    /// <param name="kind">Which radio family to change.</param>
    /// <param name="on">True to turn the radios on, false to turn them off.</param>
    /// <returns><see cref="Access.Allowed"/> when the change was permitted; otherwise why Windows
    /// refused. A refusal is reported, not thrown.</returns>
    /// <exception cref="InvalidOperationException">The machine has no adapter of this kind, or no
    /// adapter accepted the requested state.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a defined
    /// <see cref="RadioKind"/> value.</exception>
    public static Access SetPower(RadioKind kind, bool on)
    {
        ValidateRadioKind(kind);
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

    /// <summary>Reads the privacy consent recorded for a capability.</summary>
    /// <param name="capability">The capability name, as the privacy store spells it — for example
    /// <c>location</c> or <c>radios</c>.</param>
    /// <returns>The user-scope and machine-scope consent values.</returns>
    /// <remarks>
    /// Diagnostic only: the owning API remains the authority on what is permitted, and this can
    /// disagree with it. It exists to answer "why did enumeration return nothing" — on a
    /// provisioned kiosk or signage machine, location consent is commonly off, and Wi-Fi
    /// enumeration then returns an empty list rather than an error.
    /// </remarks>
    public static (Consent User, Consent Machine) GetConsent(string capability) => (
        ReadConsent(Registry.CurrentUser, capability),
        ReadConsent(Registry.LocalMachine, capability));

    private static IReadOnlyList<Radio> GetRadios(RadioKind kind)
    {
        ValidateRadioKind(kind);
        IReadOnlyList<Radio> all;
        lock (RadioCacheLock)
        {
            if (_radioCache is { } cached
                && Stopwatch.GetElapsedTime(cached.Taken) < RadioCacheTtl
                && (cached.Radios.Count == 0 || cached.Radios.All(CanReadRadio)))
            {
                all = cached.Radios;
            }
            else
            {
                all = Radio.GetRadiosAsync().AsTask().GetAwaiter().GetResult().ToArray();
                _radioCache = (Stopwatch.GetTimestamp(), all);
            }
        }
        // Fully qualified: this type declares its own RadioKind, so the WinRT one needs naming.
        var expected = kind == RadioKind.WiFi
            ? Windows.Devices.Radios.RadioKind.WiFi
            : Windows.Devices.Radios.RadioKind.Bluetooth;
        return all.Where(radio => radio.Kind == expected).ToArray();
    }

    private static void ValidateRadioKind(RadioKind kind)
    {
        if (kind is not RadioKind.WiFi and not RadioKind.Bluetooth)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown radio kind.");
        }
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
        foreach (var preferred in new[] { Power.On, Power.Disabled, Power.Off, Power.Unknown })
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

    /// <summary>Lists Bluetooth devices, classic and Low Energy alike.</summary>
    /// <param name="pairedOnly">True to list only already-paired devices; false to include every
    /// device currently visible, which is what a "add a device" screen shows.</param>
    /// <returns>The distinct devices found. Classic and Low Energy endpoints that share a device
    /// container are combined. This is a point-in-time snapshot — use
    /// <see cref="StartBluetoothWatch"/> to follow changes instead of polling this.</returns>
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
            .GroupBy(
                device => device.Container.Length > 0
                    ? $"container:{device.Container}"
                    : $"endpoint:{device.Id}",
                StringComparer.OrdinalIgnoreCase)
            .Select(MergeBluetoothEndpoints)
            .OrderBy(device => device.Name.Length == 0)
            .ThenBy(device => device.Name, StringComparer.Ordinal)
            .ThenBy(device => device.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static BluetoothDevice MergeBluetoothEndpoints(
        IGrouping<string, BluetoothDevice> endpoints)
    {
        var preferred = endpoints
            .OrderByDescending(device => device.Paired)
            .ThenByDescending(device => device.Connected)
            .ThenByDescending(device => device.CanPair)
            .ThenBy(device => device.Id, StringComparer.Ordinal)
            .First();
        var name = endpoints.Select(device => device.Name)
            .Where(value => value.Length > 0)
            .OrderBy(value => value, StringComparer.Ordinal)
            .FirstOrDefault() ?? string.Empty;
        return preferred with
        {
            Name = name,
            Paired = endpoints.Any(device => device.Paired),
            CanPair = endpoints.Any(device => device.CanPair),
            Connected = endpoints.Any(device => device.Connected),
        };
    }

    /// <summary>Counts the currently connected Bluetooth devices.</summary>
    /// <returns>How many distinct classic or Low Energy devices PnP reports as connected. A device
    /// exposed through both transports is counted once by its device-container identity. Cheaper
    /// than <see cref="ListBluetoothDevices"/> when all you need is whether anything is connected
    /// — for a status icon, say.</returns>
    public static int ConnectedBluetoothCount()
    {
        var selectors = new[]
        {
            Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromConnectionStatus(
                BluetoothConnectionStatus.Connected),
            BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
        };
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var selector in selectors)
        {
            var devices = DeviceInformation.FindAllAsync(
                    selector,
                    new[] { AepContainer, DeviceContainer })
                .AsTask().GetAwaiter().GetResult();
            foreach (var device in devices)
            {
                identities.Add(BluetoothIdentity(device.Id, device.Properties));
            }
        }
        return identities.Count;
    }

    internal static string BluetoothIdentity(
        string id,
        IReadOnlyDictionary<string, object> properties)
    {
        if (properties.TryGetValue(AepContainer, out var value)
            || properties.TryGetValue(DeviceContainer, out value))
        {
            var container = value switch
            {
                Guid guid => guid.ToString("D"),
                string text when Guid.TryParse(text, out var guid) => guid.ToString("D"),
                _ => null,
            };
            if (container is not null)
            {
                return $"container:{container}";
            }
        }
        return $"endpoint:{id}";
    }

    /// <summary>Starts a live feed of Bluetooth device changes.</summary>
    /// <param name="onChange">Called for each change. Raised on a Windows device-watcher thread,
    /// not the caller's — marshal to your UI thread before touching UI state.</param>
    /// <remarks>
    /// Starting again replaces the previous feed rather than adding a second one, so this is safe
    /// to call on every screen entry. The initial sweep reports everything already present as
    /// <see cref="BluetoothChangeKind.Added"/> and then one
    /// <see cref="BluetoothChangeKind.EnumerationCompleted"/>; the feed stays live afterwards.
    /// Always pair with <see cref="StopBluetoothWatch"/> — the watcher holds callbacks alive.
    /// </remarks>
    public static void StartBluetoothWatch(Action<BluetoothChange> onChange)
    {
        ArgumentNullException.ThrowIfNull(onChange);
        lock (BluetoothWatchLock)
        {
            StopBluetoothWatchCore();
            var watcher = DeviceInformation.CreateWatcher(
                BluetoothAqs,
                new[] { AepConnected, AepContainer },
                DeviceInformationKind.AssociationEndpoint);
            var watch = new BluetoothWatch(watcher, onChange);
            watch.Added = (_, info) => OnBluetoothAdded(watch, info);
            watch.Updated = (_, update) => OnBluetoothUpdated(watch, update);
            watch.Removed = (_, update) => OnBluetoothRemoved(watch, update);
            watch.Completed = (_, _) => PublishBluetoothChange(
                watch,
                new BluetoothChange(BluetoothChangeKind.EnumerationCompleted, default));
            _bluetoothWatch = watch;
            try
            {
                watcher.Added += watch.Added;
                watcher.Updated += watch.Updated;
                watcher.Removed += watch.Removed;
                watcher.EnumerationCompleted += watch.Completed;
                watcher.Start();
            }
            catch
            {
                StopBluetoothWatchCore();
                throw;
            }
        }
    }

    /// <summary>Stops reporting Bluetooth device changes.</summary>
    /// <remarks>Safe to call when no watch is running. Every WinRT event handler is revoked before
    /// this returns, so once it has, no further callback can arrive — which is what makes it safe
    /// to tear down whatever state the callback touched.</remarks>
    public static void StopBluetoothWatch()
    {
        lock (BluetoothWatchLock)
        {
            StopBluetoothWatchCore();
        }
    }

    private static void StopBluetoothWatchCore()
    {
        var watch = _bluetoothWatch;
        _bluetoothWatch = null;
        if (watch is null)
        {
            return;
        }
        if (watch.Added is not null)
        {
            watch.Watcher.Added -= watch.Added;
        }
        if (watch.Updated is not null)
        {
            watch.Watcher.Updated -= watch.Updated;
        }
        if (watch.Removed is not null)
        {
            watch.Watcher.Removed -= watch.Removed;
        }
        if (watch.Completed is not null)
        {
            watch.Watcher.EnumerationCompleted -= watch.Completed;
        }
        try
        {
            watch.Watcher.Stop();
        }
        catch (InvalidOperationException)
        {
            // A watcher that failed during Start can already be stopped or aborted.
        }
    }

    private static void OnBluetoothAdded(BluetoothWatch watch, DeviceInformation info)
    {
        lock (BluetoothWatchLock)
        {
            if (!ReferenceEquals(_bluetoothWatch, watch) || info.Id.Length == 0)
            {
                return;
            }
            watch.Records[info.Id] = info;
            watch.Callback(new BluetoothChange(
                BluetoothChangeKind.Added,
                ReadBluetoothDevice(info)));
        }
    }

    private static void OnBluetoothUpdated(
        BluetoothWatch watch,
        DeviceInformationUpdate update)
    {
        lock (BluetoothWatchLock)
        {
            if (!ReferenceEquals(_bluetoothWatch, watch))
            {
                return;
            }
            if (watch.Records.TryGetValue(update.Id, out var info))
            {
                info.Update(update);
                watch.Callback(new BluetoothChange(
                    BluetoothChangeKind.Updated,
                    ReadBluetoothDevice(info)));
                return;
            }
            try
            {
                var resolved = DeviceInformation.CreateFromIdAsync(
                        update.Id,
                        new[] { AepConnected, AepContainer },
                        DeviceInformationKind.AssociationEndpoint)
                    .AsTask().GetAwaiter().GetResult();
                if (!ReferenceEquals(_bluetoothWatch, watch))
                {
                    return;
                }
                watch.Records[resolved.Id] = resolved;
                watch.Callback(new BluetoothChange(
                    BluetoothChangeKind.Updated,
                    ReadBluetoothDevice(resolved)));
            }
            catch
            {
                // A disappearing endpoint is followed by Removed; it has no update to publish.
            }
        }
    }

    private static void OnBluetoothRemoved(
        BluetoothWatch watch,
        DeviceInformationUpdate update)
    {
        lock (BluetoothWatchLock)
        {
            if (!ReferenceEquals(_bluetoothWatch, watch))
            {
                return;
            }
            watch.Records.Remove(update.Id);
            watch.Callback(new BluetoothChange(BluetoothChangeKind.Removed, new BluetoothDevice(
                update.Id, string.Empty, false, false, false, string.Empty)));
        }
    }

    private static void PublishBluetoothChange(BluetoothWatch watch, BluetoothChange change)
    {
        lock (BluetoothWatchLock)
        {
            if (ReferenceEquals(_bluetoothWatch, watch))
            {
                watch.Callback(change);
            }
        }
    }

    /// <summary>Pairs a Bluetooth device, running the ceremony through your own UI.</summary>
    /// <param name="deviceId">The device's <see cref="BluetoothDevice.Id"/>.</param>
    /// <param name="onRequest">Called when Windows asks something — show it, then answer with
    /// <see cref="RespondToPairing"/>. <b>You must answer</b>: the ceremony holds a deferral that
    /// expires, and an unanswered request fails the pairing.</param>
    /// <param name="onFinished">Called once when the attempt ends, with the result, or with an
    /// exception if one escaped. Both arguments are null only if the attempt was abandoned.</param>
    /// <remarks>
    /// Returns immediately; the ceremony runs on a worker thread and both callbacks are raised
    /// there. This is the piece that is hard to find elsewhere — Windows supports several pairing
    /// ceremonies, and the right one depends on the device, so
    /// <see cref="PairingRequest.Kind"/> tells you which prompt to show.
    /// </remarks>
    public static void PairBluetooth(
        string deviceId,
        Action<PairingRequest> onRequest,
        Action<PairingResult?, Exception?> onFinished)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        ArgumentNullException.ThrowIfNull(onRequest);
        ArgumentNullException.ThrowIfNull(onFinished);
        var attempt = Interlocked.Increment(ref _nextPairingAttempt);
        _ = Task.Run(() =>
        {
            ActivePairingAttempts.TryAdd(attempt, 0);
            PairingResult? completed = null;
            Exception? failure = null;
            DeviceInformationCustomPairing? custom = null;
            TypedEventHandler<DeviceInformationCustomPairing, DevicePairingRequestedEventArgs>?
                requested = null;
            try
            {
                var info = DeviceInformation.CreateFromIdAsync(
                        deviceId,
                        new[] { AepConnected, AepContainer },
                        DeviceInformationKind.AssociationEndpoint)
                    .AsTask().GetAwaiter().GetResult();
                custom = info.Pairing.Custom;
                requested = (_, args) =>
                    {
                        var deferral = args.GetDeferral();
                        var pending = new PendingPairing(attempt, args, deferral);
                        if (!ActivePairingAttempts.ContainsKey(attempt))
                        {
                            deferral.Complete();
                            return;
                        }
                        var token = AddPendingPairing(pending);
                        if (!ActivePairingAttempts.ContainsKey(attempt)
                            && PendingPairings.TryRemove(
                                new KeyValuePair<uint, PendingPairing>(token, pending)))
                        {
                            deferral.Complete();
                            return;
                        }
                        onRequest(new PairingRequest(
                            token,
                            MapPairingKind(args.PairingKind),
                            args.Pin ?? string.Empty,
                            info.Name ?? string.Empty));
                    };
                custom.PairingRequested += requested;
                var result = Pair(
                    custom,
                    DevicePairingKinds.ConfirmOnly
                        | DevicePairingKinds.ProvidePin
                        | DevicePairingKinds.ConfirmPinMatch,
                    attempt);
                if (result.Status == DevicePairingResultStatus.RequiredHandlerNotRegistered)
                {
                    result = Pair(custom, DevicePairingKinds.DisplayPin, attempt);
                }
                completed = new PairingResult(
                    MapPairingOutcome(result.Status),
                    (int)result.Status);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                ActivePairingAttempts.TryRemove(attempt, out _);
                if (custom is not null && requested is not null)
                {
                    custom.PairingRequested -= requested;
                }
                try
                {
                    CompletePendingPairings(attempt);
                }
                catch (Exception cleanupFailure)
                {
                    failure = failure is null
                        ? cleanupFailure
                        : new AggregateException(failure, cleanupFailure);
                }
            }
            onFinished(failure is null ? completed : null, failure);
        });
    }

    /// <summary>Answers a pairing question raised by <see cref="PairBluetooth"/>.</summary>
    /// <param name="token">The <see cref="PairingRequest.Token"/> being answered. A token that is
    /// unknown or already answered is ignored.</param>
    /// <param name="accept">True to proceed with pairing, false to reject it.</param>
    /// <param name="pin">The PIN the user entered. Required when
    /// <see cref="PairingRequest.Kind"/> is <see cref="PairingKind.ProvidePin"/>, and ignored
    /// otherwise.</param>
    /// <remarks>Safe to call from any thread, including directly from the request callback.</remarks>
    public static void RespondToPairing(uint token, bool accept, string? pin)
    {
        if (!PendingPairings.TryRemove(token, out var pending))
        {
            return;
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
            pending.Deferral.Complete();
        }
    }

    /// <summary>Removes a Bluetooth pairing.</summary>
    /// <param name="deviceId">The device's <see cref="BluetoothDevice.Id"/>.</param>
    /// <returns><see langword="true"/> when the device is no longer paired, including when it was
    /// not paired to begin with.</returns>
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
        DevicePairingKinds kinds,
        long attempt)
    {
        using var timeout = new CancellationTokenSource(PairingTimeout);
        try
        {
            return pairing.PairAsync(kinds, DevicePairingProtectionLevel.Default)
                .AsTask(timeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            CompletePendingPairings(attempt);
            throw new TimeoutException("Bluetooth pairing timed out.");
        }
    }

    private static uint AddPendingPairing(PendingPairing pending)
    {
        while (true)
        {
            var token = unchecked((uint)Interlocked.Increment(ref _nextPairingToken));
            if (PendingPairings.TryAdd(token, pending))
            {
                return token;
            }
        }
    }

    private static void CompletePendingPairings(long attempt)
    {
        List<Exception>? failures = null;
        foreach (var entry in PendingPairings)
        {
            if (entry.Value.Attempt == attempt
                && PendingPairings.TryRemove(
                    new KeyValuePair<uint, PendingPairing>(entry.Key, entry.Value)))
            {
                try
                {
                    entry.Value.Deferral.Complete();
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

    private static PairingKind MapPairingKind(DevicePairingKinds kind) => kind switch
    {
        DevicePairingKinds.ConfirmOnly => PairingKind.ConfirmOnly,
        DevicePairingKinds.DisplayPin => PairingKind.DisplayPin,
        DevicePairingKinds.ProvidePin => PairingKind.ProvidePin,
        DevicePairingKinds.ConfirmPinMatch => PairingKind.ConfirmPinMatch,
        _ => PairingKind.Unknown,
    };

    private static PairingOutcome MapPairingOutcome(DevicePairingResultStatus status) => status switch
    {
        DevicePairingResultStatus.Paired => PairingOutcome.Paired,
        DevicePairingResultStatus.AlreadyPaired => PairingOutcome.AlreadyPaired,
        DevicePairingResultStatus.RejectedByHandler or DevicePairingResultStatus.PairingCanceled =>
            PairingOutcome.Cancelled,
        DevicePairingResultStatus.AccessDenied => PairingOutcome.AccessDenied,
        DevicePairingResultStatus.OperationAlreadyInProgress => PairingOutcome.AlreadyInProgress,
        DevicePairingResultStatus.Failed
            or DevicePairingResultStatus.ConnectionRejected
            or DevicePairingResultStatus.TooManyConnections
            or DevicePairingResultStatus.HardwareFailure
            or DevicePairingResultStatus.AuthenticationTimeout
            or DevicePairingResultStatus.AuthenticationNotAllowed
            or DevicePairingResultStatus.AuthenticationFailure
            or DevicePairingResultStatus.NoSupportedProfiles => PairingOutcome.Failed,
        _ => PairingOutcome.Unknown,
    };

    private sealed class BluetoothWatch(
        DeviceWatcher watcher,
        Action<BluetoothChange> callback)
    {
        internal DeviceWatcher Watcher { get; } = watcher;
        internal Action<BluetoothChange> Callback { get; } = callback;
        internal Dictionary<string, DeviceInformation> Records { get; } =
            new(StringComparer.Ordinal);
        internal TypedEventHandler<DeviceWatcher, DeviceInformation>? Added { get; set; }
        internal TypedEventHandler<DeviceWatcher, DeviceInformationUpdate>? Updated { get; set; }
        internal TypedEventHandler<DeviceWatcher, DeviceInformationUpdate>? Removed { get; set; }
        internal TypedEventHandler<DeviceWatcher, object>? Completed { get; set; }
    }

    /// <summary>Reads the Wi-Fi adapter's current state and joined network.</summary>
    /// <returns>The state, signal and network name. On a machine with several adapters this
    /// reports the one Windows is actually using.</returns>
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

    /// <summary>Asks every Wi-Fi adapter to scan for networks.</summary>
    /// <remarks>Returns as soon as the request is made, not when scanning finishes — results
    /// arrive seconds later. Watch for completion with <see cref="StartWifiWatch"/> rather than
    /// calling <see cref="ListWifiNetworks"/> immediately, which would return the previous
    /// results.</remarks>
    /// <exception cref="InvalidOperationException">No adapter accepted the scan request.</exception>
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

    /// <summary>Lists the Wi-Fi networks currently visible.</summary>
    /// <returns>Networks from every adapter, merged by SSID and ordered strongest first.</returns>
    /// <remarks>Returns the last scan's results rather than scanning — call
    /// <see cref="RequestWifiScan"/> first for fresh ones. An empty list on a machine that clearly
    /// has networks nearby usually means location consent is denied; see
    /// <see cref="GetConsent"/>.</remarks>
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
                network.Ambiguous ? WifiSecurity.Unsupported : network.Security,
                network.Saved,
                network.Connectable && !network.Ambiguous,
                network.Connected))
            .ToArray();
    }

    /// <summary>Joins a Wi-Fi network, creating or reusing its profile, and waits for the result.</summary>
    /// <param name="ssid">The network name to join.</param>
    /// <param name="passphrase">The passphrase, or <see langword="null"/> to use the saved profile
    /// — which is what <see cref="WifiNetwork.Saved"/> tells you exists. Required for a protected
    /// network with no saved profile.</param>
    /// <returns>Zero when joined; otherwise the WLAN reason code. Pass it to
    /// <see cref="ReasonText"/> for a message and <see cref="GetReasonVerdict"/> to decide whether
    /// retrying or re-prompting for the passphrase is worthwhile.</returns>
    /// <exception cref="ArgumentException"><paramref name="ssid"/> is null or empty.</exception>
    /// <remarks>
    /// Blocks until the association succeeds or fails, up to an internal timeout. Windows requires
    /// a stored profile before joining a protected network, so one is written first when needed.
    /// If an existing profile must be overwritten, its exact XML is restored when the connection
    /// fails; a failed key or security negotiation also removes a newly-created profile.
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
        var profiles = ReadProfileSsids(client.Handle, choice.Adapter.Id, failOnListError: true);
        var profileName = facts.ProfileName;
        ProfileMutation? mutation = null;

        if (passphrase is not null)
        {
            mutation = FindFreeProfileName(profiles, ssid, targetSsid);
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
                throw CombineFailure(
                    ex,
                    TryRollBackProfile(client.Handle, choice.Adapter.Id, mutation));
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
            throw CombineFailure(
                ex,
                TryRollBackProfile(client.Handle, choice.Adapter.Id, mutation));
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
                throw CombineFailure(
                    ex,
                    TryRollBackProfile(client.Handle, choice.Adapter.Id, mutation));
            }
            var parameters = new WlanConnectionParameters
            {
                Mode = WlanConnectionModeProfile,
                Profile = profilePointer,
                BssType = Dot11BssTypeInfrastructure,
            };
            try
            {
                var adapterId = choice.Adapter.Id;
                var accepted = WlanConnect(client.Handle, in adapterId, in parameters, 0);
                if (accepted != ErrorSuccess)
                {
                    var failure = WlanFailure("WlanConnect", accepted);
                    throw CombineFailure(
                        failure,
                        TryRollBackProfile(client.Handle, choice.Adapter.Id, mutation));
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
                    var failure = new TimeoutException(
                        "The Wi-Fi connection attempt did not complete; "
                        + $"WLAN notification registration failed (Win32 {verdictRegistrationStatus}).");
                    throw CombineFailure(
                        failure,
                        TryRollBackProfile(client.Handle, choice.Adapter.Id, mutation));
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
                var timeout = new TimeoutException(
                    "The Wi-Fi connection attempt did not complete.");
                throw CombineFailure(
                    timeout,
                    TryRollBackProfile(client.Handle, choice.Adapter.Id, mutation));
            }
            finally
            {
                Marshal.FreeCoTaskMem(parameters.Profile);
            }
        }
    }

    /// <summary>Disconnects every Wi-Fi adapter that is connected or connecting.</summary>
    /// <remarks>Leaves saved profiles in place, so Windows may reconnect automatically. Use
    /// <see cref="ForgetWifi"/> to stop that.</remarks>
    /// <exception cref="Win32Exception">An adapter refused to disconnect.</exception>
    public static void DisconnectWifi()
    {
        using var client = WlanClient.Open();
        Exception? last = null;
        foreach (var adapter in client.Interfaces())
        {
            if (MapInterfaceState(adapter.State)
                is not WifiConnectionState.Connected and not WifiConnectionState.Connecting)
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

    /// <summary>Forgets a network by deleting every saved profile for it.</summary>
    /// <param name="ssid">The network name to forget.</param>
    /// <remarks>Matches on the SSID inside each profile document rather than on the profile's
    /// name, so a profile Windows saved under a different name is still removed. Doing nothing
    /// because no profile matched is success, not an error.</remarks>
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

    /// <summary>Classifies a WLAN reason code into the kind of failure it represents.</summary>
    /// <param name="code">A reason code, as returned by
    /// <see cref="ConnectWifi(string, string?)"/> or carried on a
    /// <see cref="WlanReasonException"/>.</param>
    /// <returns>Which of the few outcomes a caller can act on differently.</returns>
    /// <remarks>Windows defines hundreds of reason codes across four numbering ranges, and the
    /// exact code is only useful as text. What a caller needs to decide is narrower: whether to
    /// re-prompt for the passphrase, or to say the network could not be reached. Blaming a wrong
    /// passphrase for an association timeout is the worse mistake, because the user retypes a
    /// passphrase that was already correct.</remarks>
    public static WifiFailureKind GetReasonVerdict(uint code)
    {
        if (code == 0)
        {
            return WifiFailureKind.None;
        }
        if (code >= ReasonMsmsecBase && code < ReasonMsmsecConnectBase)
        {
            return WifiFailureKind.SecurityMismatch;
        }
        if (code >= ReasonMsmBase && code <= ReasonMsmEnd
            || code >= ReasonAcBase && code <= ReasonAcEnd)
        {
            return WifiFailureKind.Unreachable;
        }
        if (code >= ReasonMsmsecConnectBase && code <= ReasonMsmsecEnd)
        {
            return WifiFailureKind.KeyRejected;
        }
        return WifiFailureKind.Unknown;
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
        => TryCurrentConnection(client, adapter) is { } current
            && current.RawSsid.AsSpan().SequenceEqual(targetSsid);

    /// <summary>Looks up Windows' own description of a WLAN reason code.</summary>
    /// <param name="code">The reason code to describe.</param>
    /// <returns>The description in the user's display language, or <c>"Wi-Fi reason code N"</c>
    /// when Windows has no text for the code. Never empty, so it is always safe to show.</returns>
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

    /// <summary>Starts reporting Wi-Fi scan and connection changes.</summary>
    /// <param name="onEvent">Called on a Windows service thread, not the caller's. Marshal to your
    /// UI thread before touching UI state, and keep the callback short — it runs inside the WLAN
    /// notification path. Exceptions it throws are swallowed, because a native callback cannot
    /// propagate them.</param>
    /// <exception cref="Win32Exception">The WLAN handle could not be opened, or Windows refused to
    /// register for notifications.</exception>
    /// <remarks>There is one feed per process: calling this again replaces the previous callback
    /// rather than adding a second one. Pair it with <see cref="StopWifiWatch"/>.</remarks>
    public static void StartWifiWatch(Action<WifiWatchEvent> onEvent)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        lock (WifiWatchLock)
        {
            StopWifiWatchCore();
            var status = WlanOpenHandle(2, 0, out _, out var handle);
            ThrowIfWlanFailed("WlanOpenHandle", status);
            WifiWatch watch;
            nint callback;
            try
            {
                watch = new WifiWatch(handle, onEvent);
                watch.Callback = (data, context) => OnWifiNotification(watch, data, context);
                callback = Marshal.GetFunctionPointerForDelegate(watch.Callback);
            }
            catch
            {
                WlanCloseHandle(handle, 0);
                throw;
            }
            _wifiWatch = watch;
            var sources = WlanNotificationSourceAcm | WlanNotificationSourceMsm;
            status = WlanRegisterNotification(handle, sources, 1, callback, 0, 0, 0);
            if (status != ErrorSuccess)
            {
                status = WlanRegisterNotification(
                    handle, WlanNotificationSourceAcm, 1, callback, 0, 0, 0);
            }
            if (status != ErrorSuccess)
            {
                _wifiWatch = null;
                WlanCloseHandle(handle, 0);
                GC.KeepAlive(watch.Callback);
                throw WlanFailure("WlanRegisterNotification", status);
            }
        }
    }

    /// <summary>Stops reporting Wi-Fi changes and releases the notification handle.</summary>
    /// <remarks>Safe to call when no watch is running. Call it before the process exits: the
    /// callback is held by native code, and leaving it registered risks a call into an unloaded
    /// delegate.</remarks>
    public static void StopWifiWatch()
    {
        lock (WifiWatchLock)
        {
            StopWifiWatchCore();
        }
    }

    private static void StopWifiWatchCore()
    {
        var watch = _wifiWatch;
        _wifiWatch = null;
        if (watch is null)
        {
            return;
        }
        WlanRegisterNotification(
            watch.Handle,
            WlanNotificationSourceNone,
            0,
            0,
            0,
            0,
            0);
        WlanCloseHandle(watch.Handle, 0);
        GC.KeepAlive(watch.Callback);
    }

    private static void OnWifiNotification(WifiWatch watch, nint data, nint context)
    {
        try
        {
            lock (WifiWatchLock)
            {
                if (!ReferenceEquals(_wifiWatch, watch) || data == 0)
                {
                    return;
                }
                var notification = Marshal.PtrToStructure<WlanNotificationData>(data);
                if (notification.Source != WlanNotificationSourceAcm)
                {
                    return;
                }
                WifiWatchEvent? change = notification.Code switch
                {
                    AcmScanComplete or AcmScanListRefresh => WifiWatchEvent.ScanCompleted,
                    AcmConnectionComplete or AcmConnectionAttemptFail or AcmDisconnected =>
                        WifiWatchEvent.ConnectionChanged,
                    _ => null,
                };
                if (change is { } raised)
                {
                    watch.Events(raised);
                }
            }
        }
        catch
        {
            // A native service callback cannot propagate managed failures.
        }
    }

    private static WlanInterfaceInfo SelectInterface(IReadOnlyList<WlanInterfaceInfo> interfaces)
        => interfaces.FirstOrDefault(adapter =>
                MapInterfaceState(adapter.State) == WifiConnectionState.Connected) is var connected
            && connected.Id != Guid.Empty
                ? connected
                : interfaces[0];

    private static WifiConnectionState MapInterfaceState(int state) => state switch
    {
        WlanInterfaceStateConnected or WlanInterfaceStateAdHocFormed =>
            WifiConnectionState.Connected,
        WlanInterfaceStateAssociating or WlanInterfaceStateDiscovering
            or WlanInterfaceStateAuthenticating => WifiConnectionState.Connecting,
        WlanInterfaceStateDisconnecting or WlanInterfaceStateDisconnected =>
            WifiConnectionState.Disconnected,
        _ => WifiConnectionState.Unknown,
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
            var connected = TryCurrentConnection(client, adapter)?.RawSsid;
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
                    || profiles.Any(profile => profile.Ssid is { } profileSsid
                        && profileSsid.AsSpan().SequenceEqual(raw));
                var profileName = ReadFixed(item.ProfileName, 256);
                var facts = new WifiNetworkFacts(
                    ssid,
                    raw,
                    (int)item.SignalQuality,
                    ClassifySecurity(item.SecurityEnabled != 0, item.DefaultAuthAlgorithm),
                    item.DefaultAuthAlgorithm,
                    saved,
                    item.Connectable != 0,
                    connected is not null && connected.AsSpan().SequenceEqual(raw),
                    profileName.Length == 0 ? null : profileName,
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
                _ => 0,
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
                || visibleObservations[0].ProfileName is { Length: > 0 } firstProfile
                    && item.ProfileName is { Length: > 0 } itemProfile
                    && !string.Equals(firstProfile, itemProfile, StringComparison.Ordinal)))
        {
            selected = selected with
            {
                Facts = selected.Facts with
                {
                    Ambiguous = true,
                    Security = WifiSecurity.Unsupported,
                    Authentication = 0,
                    Connectable = false,
                    ProfileName = null,
                },
            };
        }
        return selected;
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
                    var security = ClassifySecurity(
                        item.SecurityEnabled != 0,
                        item.DefaultAuthAlgorithm);
                    var readProfile = ReadFixed(item.ProfileName, 256);
                    var profileName = readProfile.Length == 0 ? null : readProfile;
                    var sameRaw = facts.RawSsid.Length == 0
                        || facts.RawSsid.AsSpan().SequenceEqual(raw);
                    var conflictingIdentity = facts.RawSsid.Length > 0
                        && (!sameRaw
                            || facts.Security != security
                            || facts.Authentication != item.DefaultAuthAlgorithm
                            || facts.ProfileName is { Length: > 0 } existingProfile
                                && profileName is { Length: > 0 }
                                && !string.Equals(
                                    existingProfile,
                                    profileName,
                                    StringComparison.Ordinal));
                    facts = facts with
                    {
                        RawSsid = facts.RawSsid.Length == 0 ? raw : facts.RawSsid,
                        Ambiguous = facts.Ambiguous || conflictingIdentity,
                        Security = conflictingIdentity ? WifiSecurity.Unsupported : security,
                        Authentication = conflictingIdentity ? 0 : item.DefaultAuthAlgorithm,
                        ProfileName = conflictingIdentity ? null : profileName ?? facts.ProfileName,
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
                    .FirstOrDefault(profile => profile.Ssid is { } profileSsid
                        && profileSsid.AsSpan().SequenceEqual(target)).Name,
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
                var xml = TryReadProfileXml(client, adapter, name);
                var rawSsid = xml is null ? null : WifiProfile.TryReadSsid(xml);
                profiles.Add(new SavedProfile(name, rawSsid, xml));
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

    internal static ProfileMutation FindFreeProfileName(
        IReadOnlyList<SavedProfile> profiles,
        string ssid,
        byte[] target)
    {
        for (var suffix = 1; suffix <= 64; suffix++)
        {
            var candidate = suffix == 1 ? ssid : $"{ssid} {suffix}";
            var owner = profiles.FirstOrDefault(profile => profile.Name == candidate);
            if (owner == default)
            {
                return new ProfileMutation(candidate, false, null);
            }
            if (owner.Ssid is { } ownerSsid && ownerSsid.AsSpan().SequenceEqual(target))
            {
                if (owner.Xml is null)
                {
                    throw new InvalidOperationException(
                        $"The existing Wi-Fi profile '{candidate}' could not be read, so it cannot be overwritten safely.");
                }
                return new ProfileMutation(candidate, true, owner.Xml);
            }
        }
        throw new InvalidOperationException(
            "No collision-free Wi-Fi profile name is available for this network.");
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
        if (status is not ErrorSuccess and not ErrorNotFound)
        {
            throw WlanFailure("WlanDeleteProfile", status);
        }
    }

    internal static WifiNetworkFacts MergeNetworkFacts(
        WifiNetworkFacts existing,
        WifiNetworkFacts observed)
    {
        var conflictingIdentity = existing.Ambiguous
            || observed.Ambiguous
            || existing.Security != observed.Security
            || existing.Authentication != observed.Authentication
            || existing.ProfileName is { Length: > 0 } existingProfile
                && observed.ProfileName is { Length: > 0 } observedProfile
                && !string.Equals(existingProfile, observedProfile, StringComparison.Ordinal);
        var observedIsPrimary = observed.Signal > existing.Signal;
        var primary = observedIsPrimary ? observed : existing;
        var secondary = observedIsPrimary ? existing : observed;
        return primary with
        {
            Signal = Math.Max(existing.Signal, observed.Signal),
            Security = conflictingIdentity ? WifiSecurity.Unsupported : primary.Security,
            Authentication = conflictingIdentity ? 0 : primary.Authentication,
            Saved = existing.Saved || observed.Saved,
            Connectable = !conflictingIdentity && (existing.Connectable || observed.Connectable),
            Connected = existing.Connected || observed.Connected,
            ProfileName = conflictingIdentity ? null : primary.ProfileName ?? secondary.ProfileName,
            Ambiguous = conflictingIdentity,
        };
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
        => cleanupFailure is null ? failure : new AggregateException(failure, cleanupFailure);

    internal static WifiSecurity ClassifySecurity(bool secured, int auth)
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
            _ => WifiSecurity.Unsupported,
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
            try
            {
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
                    verdict.Dispose();
                    return null;
                }
                return verdict;
            }
            catch
            {
                verdict.Dispose();
                throw;
            }
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

    private sealed class WifiWatch(nint handle, Action<WifiWatchEvent> events)
    {
        internal nint Handle { get; } = handle;
        internal Action<WifiWatchEvent> Events { get; } = events;
        internal WlanNotificationCallback Callback { get; set; } = null!;
    }

    private readonly record struct CurrentConnection(string Ssid, byte[] RawSsid, int Signal);
    private readonly record struct ConnectionOutcome(bool Succeeded, uint Reason);
    internal readonly record struct SavedProfile(string Name, byte[]? Ssid, string? Xml);
    internal readonly record struct ProfileMutation(string Name, bool Existed, string? PreviousXml);
    private readonly record struct InterfaceChoice(WlanInterfaceInfo Adapter, WifiNetworkFacts Facts);
    private sealed class WlanReasonException(uint reasonCode, string message)
        : Win32Exception((int)reasonCode, message)
    {
        internal uint ReasonCode { get; } = reasonCode;
    }

    internal readonly record struct WifiNetworkFacts(
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
            => new(ssid, [], 0, WifiSecurity.Unsupported, 0, false, false, false, null, false);
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

    private sealed record PendingPairing(
        long Attempt,
        DevicePairingRequestedEventArgs Args,
        Deferral Deferral);
}
