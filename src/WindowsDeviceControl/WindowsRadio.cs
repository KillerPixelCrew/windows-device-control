
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
public static partial class WindowsRadio
{
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
}
