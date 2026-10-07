using System;
using System.Collections.Generic;
using System.Text;

namespace WindowsDeviceControl;

/// <summary>Windows radio control: adapter power, Bluetooth discovery and pairing, and Wi-Fi.</summary>
/// <remarks>
///     Uses WinRT for radio power and Bluetooth, and native WLANAPI for Wi-Fi in unpackaged processes.
///     The process must match the Windows architecture for radio enumeration; an x86 process on
///     x64 Windows may receive an empty adapter list.
///     <para>
///         Native operations are synchronous except <see cref="PairBluetoothAsync" />; call blocking
///         operations from a worker thread. Watch callbacks arrive on Windows threads. Callers should
///         serialize competing mutations of the same device or saved profile. Windows controls access:
///         <see cref="RequestAccess" /> asks for radio-power permission, while <see cref="GetConsent" />
///         reads diagnostic registry values only.
///     </para>
/// </remarks>
public static partial class WindowsRadio
{
    /// <summary>Whether Windows permits radio power changes.</summary>
    public enum Access
    {
        /// <summary>This process may change radio power.</summary>
        Allowed,

        /// <summary>
        ///     The user has denied radio control to this application in privacy settings.
        ///     No retry will succeed until the user changes that.
        /// </summary>
        DeniedByUser,

        /// <summary>
        ///     Policy or device configuration denies radio control to every application —
        ///     commonly a managed or kiosk-provisioned machine.
        /// </summary>
        DeniedBySystem,

        /// <summary>Windows answered without a reason. Treat as denied, but not permanently.</summary>
        Unspecified
    }

    /// <summary>What happened to a device seen by <see cref="StartBluetoothWatch" />.</summary>
    public enum BluetoothChangeKind
    {
        /// <summary>The device appeared.</summary>
        Added,

        /// <summary>A property of a known device changed — typically its connected state.</summary>
        Updated,

        /// <summary>The device disappeared. Only its identifier is meaningful.</summary>
        Removed,

        /// <summary>
        ///     The initial sweep finished; everything already present has been reported.
        ///     The watch stays active and keeps reporting later changes.
        /// </summary>
        EnumerationCompleted,

        /// <summary>
        ///     The watch ended on its own, for example because Windows aborted the device watcher.
        ///     It is the last change the registration reports, and the device is default. Dispose the
        ///     registration and start a new one to resume.
        /// </summary>
        Stopped
    }

    /// <summary>The privacy consent value reported by the diagnostic registry store.</summary>
    public enum Consent
    {
        /// <summary>Consent is recorded as granted.</summary>
        Allow,

        /// <summary>Consent is recorded as refused.</summary>
        Deny,

        /// <summary>No value is recorded; this does not establish whether Windows has asked for consent.</summary>
        Unset,

        /// <summary>The consent store could not be read, or its value was unrecognized.</summary>
        Unknown
    }

    /// <summary>What a pairing ceremony is asking the user to do.</summary>
    /// <remarks>
    ///     The value decides what your UI must show and what
    ///     <see cref="RespondToPairing" /> needs back: only <see cref="ProvidePin" /> requires a PIN
    ///     argument, and the others are answered with accept or reject alone.
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

        /// <summary>
        ///     A ceremony this library does not classify. Windows still waits for an answer; the
        ///     caller decides whether to accept or reject it.
        /// </summary>
        Unknown
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

        /// <summary>
        ///     The attempt failed: rejected, timed out, out of connections, or a hardware
        ///     or authentication failure.
        /// </summary>
        Failed,

        /// <summary>Windows refused this process permission to pair.</summary>
        AccessDenied,

        /// <summary>
        ///     Windows reported a status this library does not classify. Consult
        ///     <see cref="PairingResult.RawStatus" />.
        /// </summary>
        Unknown,

        /// <summary>Another pairing attempt for this device is already running.</summary>
        AlreadyInProgress
    }

    /// <summary>The result of a radio power query.</summary>
    public enum Power
    {
        /// <summary>At least one adapter of this kind is on.</summary>
        On,

        /// <summary>At least one adapter is off and none reports On or Disabled; access to enable it is separate.</summary>
        Off,

        /// <summary>
        ///     Blocked by the system — typically a hardware switch or airplane mode. Turning
        ///     it on will not succeed until whatever disabled it is reversed.
        /// </summary>
        Disabled,

        /// <summary>Present, but Windows did not report a state this API recognizes.</summary>
        Unknown,

        /// <summary>Windows enumerated no adapter of this kind; this does not prove hardware is absent.</summary>
        Absent
    }

    /// <summary>Which family of radio adapter an operation applies to.</summary>
    public enum RadioKind
    {
        /// <summary>Every Wi-Fi adapter.</summary>
        WiFi,

        /// <summary>Every Bluetooth adapter.</summary>
        Bluetooth
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
        Unknown
    }

    /// <summary>How a <see cref="ConnectWifi" /> attempt ended.</summary>
    public enum WifiConnectOutcome
    {
        /// <summary>The adapter joined the network.</summary>
        Joined,

        /// <summary>
        ///     WLAN reported a definite failure; <see cref="WifiConnectResult.ReasonCode" /> says
        ///     which. A replaced profile has its previous XML back, and a profile created for this
        ///     attempt was removed when the key or the security settings were refused.
        /// </summary>
        Failed,

        /// <summary>
        ///     No verdict arrived before the wait ended and the adapter is not on the network yet. The
        ///     attempt may still complete, so the profile it uses was left in place; a watch or the
        ///     next <see cref="GetWifiStatus" /> reports how it ends.
        /// </summary>
        Pending,

        /// <summary>
        ///     The request was refused before anything was written;
        ///     <see cref="WifiConnectResult.Refusal" /> says why.
        /// </summary>
        Refused
    }

    /// <summary>Why <see cref="ConnectWifi" /> refused a request before writing anything.</summary>
    public enum WifiConnectRefusal
    {
        /// <summary>
        ///     The passphrase is neither 8 to 63 printable ASCII characters nor 64 hex digits; see
        ///     <see cref="WifiProfile.PassphraseIsValid" />.
        /// </summary>
        InvalidPassphrase,

        /// <summary>
        ///     A passphrase was given, but the network is not visible with a personal-key
        ///     authentication method this library can build a profile for.
        /// </summary>
        UnsupportedAuthentication,

        /// <summary>The network is protected and has no saved profile, so it needs a passphrase.</summary>
        NeedsPassword,

        /// <summary>
        ///     The network has no saved profile, and its authentication is one this library cannot
        ///     build a profile for, or it is not visible to read the authentication from.
        /// </summary>
        UnsupportedSecurity
    }

    /// <summary>
    ///     Why a join attempt failed, reduced to the outcomes a caller acts on
    ///     differently.
    /// </summary>
    /// <remarks>
    ///     Produced by <see cref="GetReasonVerdict" /> from a raw WLAN reason code. Use
    ///     <see cref="ReasonText" /> when you want Windows' own wording for the specific code.
    /// </remarks>
    public enum WifiFailureKind
    {
        /// <summary>Not a failure — the join succeeded.</summary>
        None,

        /// <summary>
        ///     The access point rejected the key. This is the one case where re-prompting
        ///     for the passphrase is the right response.
        /// </summary>
        KeyRejected,

        /// <summary>
        ///     The profile's security settings do not match what the access point offers,
        ///     so the key was never tried. Reported before association completes.
        /// </summary>
        SecurityMismatch,

        /// <summary>
        ///     Association or the connection manager gave up: the network was out of range,
        ///     too weak, or stopped responding. Nothing about the passphrase is implied.
        /// </summary>
        Unreachable,

        /// <summary>
        ///     A reason code outside the ranges this library classifies. Show
        ///     <see cref="ReasonText" /> rather than guessing at a cause.
        /// </summary>
        Unknown
    }

    /// <summary>The security a Wi-Fi network requires to join.</summary>
    public enum WifiSecurity
    {
        /// <summary>No authentication.</summary>
        Open,

        /// <summary>A pre-shared key — the ordinary home and small-office network.</summary>
        PersonalPsk,

        /// <summary>
        ///     802.1X enterprise authentication. This library does not build enterprise
        ///     profiles; join these with a profile provisioned by other means.
        /// </summary>
        Enterprise,

        /// <summary>Opportunistic Wireless Encryption: no passphrase, but encrypted.</summary>
        EnhancedOpen,

        /// <summary>An authentication algorithm this library cannot build a profile for.</summary>
        Unsupported
    }

    /// <summary>What changed, as reported to a <see cref="StartWifiWatch" /> callback.</summary>
    /// <remarks>
    ///     Each value says which query is now worth repeating; neither carries the new data
    ///     itself. Windows raises many more notification codes than these, and the rest describe
    ///     internal state transitions that change nothing a caller can observe, so they are dropped
    ///     rather than passed on as callbacks that lead to identical results.
    /// </remarks>
    public enum WifiWatchEvent
    {
        /// <summary>
        ///     A scan finished or the visible-network list changed. Call
        ///     <see cref="ListWifiNetworks" /> for the new results.
        /// </summary>
        ScanCompleted,

        /// <summary>
        ///     The adapter connected or disconnected. Call <see cref="GetWifiStatus" /> for
        ///     the new state. This is also raised when a connection attempt fails.
        /// </summary>
        ConnectionChanged
    }

    /// <summary>One visible Wi-Fi network.</summary>
    /// <param name="Key">
    ///     The network's identity: its SSID bytes and security class. Pass it to
    ///     <see cref="ConnectWifi" /> and <see cref="ForgetWifi" />. Never default, because networks
    ///     that hide their name are not listed.
    /// </param>
    /// <param name="Ssid">
    ///     The network name as text, for display. Never empty: networks that hide their name are not
    ///     listed. Two networks can show the same text, so identify one by <paramref name="Key" />.
    /// </param>
    /// <param name="Signal">Signal quality, 0 to 100, as Windows reports it.</param>
    /// <param name="Security">What joining it requires; the same class as the key's.</param>
    /// <param name="Saved">
    ///     Whether a profile for it already exists on this machine, in which case
    ///     <see cref="ConnectWifi" /> needs no passphrase.
    /// </param>
    /// <param name="Connectable">Whether Windows currently considers it joinable.</param>
    /// <param name="Connected">Whether this is the network the adapter is joined to.</param>
    public readonly record struct WifiNetwork(
        WifiNetworkKey Key,
        string Ssid,
        int Signal,
        WifiSecurity Security,
        bool Saved,
        bool Connectable,
        bool Connected);

    /// <summary>The Wi-Fi adapter's current state.</summary>
    /// <param name="State">
    ///     Whether the adapter is joined, joining, or neither;
    ///     <see cref="WifiConnectionState.Unknown" /> on a machine without a WLAN interface.
    /// </param>
    /// <param name="Signal">Signal quality of the joined network, 0 to 100; zero when not joined.</param>
    /// <param name="Ssid">The joined network's name as text; empty when not joined.</param>
    /// <param name="Key">
    ///     The joined network's identity, built from its SSID bytes and the security of the
    ///     connection; <see langword="null" /> when not joined.
    /// </param>
    public readonly record struct WifiStatus(
        WifiConnectionState State,
        int Signal,
        string Ssid,
        WifiNetworkKey? Key);

    /// <summary>How a <see cref="ConnectWifi" /> attempt ended.</summary>
    /// <param name="Outcome">The result a caller acts on.</param>
    /// <param name="ReasonCode">
    ///     For <see cref="WifiConnectOutcome.Failed" />, the Windows WLAN reason code: pass it to
    ///     <see cref="ReasonText" /> and <see cref="GetReasonVerdict" />. A failure WLAN reported
    ///     without a reason carries <c>ERROR_NOT_FOUND</c> (1168). Zero for every other outcome.
    /// </param>
    /// <param name="Refusal">
    ///     For <see cref="WifiConnectOutcome.Refused" />, why; <see langword="null" /> otherwise.
    /// </param>
    public readonly record struct WifiConnectResult(
        WifiConnectOutcome Outcome,
        uint ReasonCode,
        WifiConnectRefusal? Refusal);

    /// <summary>One profile deletion made by <see cref="ForgetWifi" />.</summary>
    /// <param name="ProfileName">The saved profile that was deleted, or that could not be.</param>
    /// <param name="Status">
    ///     The Win32 status <c>WlanDeleteProfile</c> returned: zero when the profile is gone,
    ///     otherwise why Windows kept it.
    /// </param>
    public readonly record struct WifiForgetResult(string ProfileName, uint Status);

    /// <summary>Identifies one Wi-Fi network: its exact SSID bytes and the security class it advertises.</summary>
    /// <remarks>
    ///     Equality compares both. Two names that decode to the same text are told apart by their
    ///     bytes, and the same name advertised with different security is two networks. The bytes are
    ///     copied on construction, so a key never changes. <see langword="default" /> names no network
    ///     and is refused by every member that takes a key.
    /// </remarks>
    public readonly struct WifiNetworkKey : IEquatable<WifiNetworkKey>
    {
        private readonly byte[]? _ssid;

        /// <summary>Creates a key from a network's SSID bytes and security class.</summary>
        /// <param name="ssid">The SSID's exact bytes. They are copied.</param>
        /// <param name="security">The security class the network advertises.</param>
        /// <exception cref="ArgumentException"><paramref name="ssid" /> is empty.</exception>
        public WifiNetworkKey(ReadOnlySpan<byte> ssid, WifiSecurity security)
        {
            if (ssid.IsEmpty)
            {
                throw new ArgumentException("A Wi-Fi network key needs the SSID's bytes.", nameof(ssid));
            }

            _ssid = ssid.ToArray();
            Security = security;
        }

        /// <summary>Gets the SSID's exact bytes. Empty only for <see langword="default" />.</summary>
        public ReadOnlySpan<byte> Ssid => _ssid;

        /// <summary>Gets the security class the network advertises.</summary>
        public WifiSecurity Security { get; }

        /// <summary>Gets the SSID bytes as upper-case hex, two digits per byte.</summary>
        public string Hex => Convert.ToHexString(Ssid);

        /// <summary>
        ///     Gets the SSID decoded as UTF-8, for display. Invalid sequences become U+FFFD, which is
        ///     why two keys can show the same text.
        /// </summary>
        public string DisplayText => Encoding.UTF8.GetString(Ssid);

        /// <summary>Whether this is <see langword="default" />, which names no network.</summary>
        internal bool IsDefault => _ssid is null;

        /// <summary>Compares two keys by SSID bytes and security class.</summary>
        /// <param name="left">The first key.</param>
        /// <param name="right">The second key.</param>
        /// <returns>Whether both name the same network.</returns>
        public static bool operator ==(WifiNetworkKey left, WifiNetworkKey right)
        {
            return left.Equals(right);
        }

        /// <summary>Compares two keys by SSID bytes and security class.</summary>
        /// <param name="left">The first key.</param>
        /// <param name="right">The second key.</param>
        /// <returns>Whether the keys name different networks.</returns>
        public static bool operator !=(WifiNetworkKey left, WifiNetworkKey right)
        {
            return !left.Equals(right);
        }

        /// <inheritdoc />
        public bool Equals(WifiNetworkKey other)
        {
            return Security == other.Security && Ssid.SequenceEqual(other.Ssid);
        }

        /// <inheritdoc />
        public override bool Equals(object? obj)
        {
            return obj is WifiNetworkKey other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Security);
            hash.AddBytes(Ssid);
            return hash.ToHashCode();
        }

        /// <summary>Returns the display text and security class, for logs.</summary>
        /// <returns>The SSID text followed by the security class.</returns>
        public override string ToString()
        {
            return $"{DisplayText} ({Security})";
        }
    }

    /// <summary>One Bluetooth association endpoint.</summary>
    /// <param name="Id">The device identifier, and the handle for every other operation here.</param>
    /// <param name="Name">The friendly name, for display.</param>
    /// <param name="Paired">Whether the device is already paired with this machine.</param>
    /// <param name="CanPair">Whether Windows considers it pairable right now.</param>
    /// <param name="Connected">
    ///     Whether it is currently connected. A device can be paired without
    ///     being connected — a headset that is switched off, for instance.
    /// </param>
    /// <param name="Container">
    ///     The container identifier, which is what ties this device to its
    ///     audio endpoints in <see cref="CoreAudio.ListBluetoothAudioContainers" />. Always the
    ///     lower-case hyphenated GUID form, or empty when Windows reports no container or the empty
    ///     GUID, so endpoints without a real container are never grouped together.
    /// </param>
    public readonly record struct BluetoothDevice(
        string Id,
        string Name,
        bool Paired,
        bool CanPair,
        bool Connected,
        string Container);

    /// <summary>A Bluetooth discovery change.</summary>
    /// <param name="Kind">What happened to the device.</param>
    /// <param name="Device">
    ///     The endpoint it happened to. Only <see cref="BluetoothDevice.Id" /> is meaningful for
    ///     <see cref="BluetoothChangeKind.Removed" />. The whole value is default for
    ///     <see cref="BluetoothChangeKind.EnumerationCompleted" /> and <see cref="BluetoothChangeKind.Stopped" />.
    /// </param>
    public readonly record struct BluetoothChange(BluetoothChangeKind Kind, BluetoothDevice Device);

    /// <summary>A pairing question that must be answered before its deferral expires.</summary>
    /// <param name="Token">Identifies this question; pass it to <see cref="RespondToPairing" />.</param>
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
    /// <param name="RawStatus">
    ///     Windows' own <c>DevicePairingResultStatus</c> value, kept so an
    ///     unclassified outcome can still be diagnosed.
    /// </param>
    public readonly record struct PairingResult(PairingOutcome Outcome, int RawStatus);

    /// <summary>How an unpairing request ended.</summary>
    /// <param name="Unpaired">
    ///     True when the device is no longer paired, including when it was not paired to
    ///     begin with.
    /// </param>
    /// <param name="NativeStatus">
    ///     Windows' own <c>DeviceUnpairingResultStatus</c> value, kept so a refusal can be
    ///     diagnosed.
    /// </param>
    public readonly record struct BluetoothUnpairResult(bool Unpaired, int NativeStatus);

    /// <summary>How a radio power change went, adapter by adapter.</summary>
    /// <param name="Access">
    ///     <see cref="WindowsRadio.Access.Allowed" /> only when every adapter accepted the state.
    ///     Otherwise the first refusal an adapter reported, or
    ///     <see cref="WindowsRadio.Access.Unspecified" /> when an adapter failed without a refusal.
    /// </param>
    /// <param name="Adapters">
    ///     One entry per adapter, in the order they were written. Empty when Windows refused
    ///     access before any adapter was written; <paramref name="Access" /> then says why.
    /// </param>
    public sealed record RadioPowerResult(Access Access, IReadOnlyList<RadioAdapterResult> Adapters);

    /// <summary>One adapter's answer to a radio power change.</summary>
    /// <param name="Name">The adapter's name as Windows reports it; empty when it could not be read.</param>
    /// <param name="Access">
    ///     The adapter's answer, or <see langword="null" /> when the write failed instead of
    ///     answering. The answer is the write's own status; nothing is read back.
    /// </param>
    /// <param name="HResult">The failure's HRESULT when <paramref name="Access" /> is null; otherwise zero.</param>
    public readonly record struct RadioAdapterResult(string Name, Access? Access, int HResult);
}
