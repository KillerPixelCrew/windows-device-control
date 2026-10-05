using System;
using System.Collections.Generic;
using System.Linq;

namespace WindowsDeviceControl;

public static partial class WindowsRadio
{
    private const int WlanInterfaceStateConnected = 1;
    private const int WlanInterfaceStateAdHocFormed = 2;
    private const int WlanInterfaceStateDisconnecting = 3;
    private const int WlanInterfaceStateDisconnected = 4;
    private const int WlanInterfaceStateAssociating = 5;
    private const int WlanInterfaceStateDiscovering = 6;
    private const int WlanInterfaceStateAuthenticating = 7;
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

    /// <summary>Classifies a WLAN reason code into the kind of failure it represents.</summary>
    /// <param name="code">
    ///     A WLAN reason code, as carried by <see cref="WifiConnectResult.ReasonCode" /> or by
    ///     <see cref="System.ComponentModel.Win32Exception.NativeErrorCode" /> after a profile write
    ///     failed.
    /// </param>
    /// <returns>Which of the few outcomes a caller can act on differently.</returns>
    /// <remarks>
    ///     Windows defines hundreds of reason codes across four numbering ranges, and the
    ///     exact code is only useful as text. What a caller needs to decide is narrower: whether to
    ///     re-prompt for the passphrase, or to say the network could not be reached. Blaming a wrong
    ///     passphrase for an association timeout is the worse mistake, because the user retypes a
    ///     passphrase that was already correct.
    /// </remarks>
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

        if ((code >= ReasonMsmBase && code <= ReasonMsmEnd)
            || (code >= ReasonAcBase && code <= ReasonAcEnd))
        {
            return WifiFailureKind.Unreachable;
        }

        if (code >= ReasonMsmsecConnectBase && code <= ReasonMsmsecEnd)
        {
            return WifiFailureKind.KeyRejected;
        }

        return WifiFailureKind.Unknown;
    }

    private static WifiConnectionState MapInterfaceState(int state)
    {
        return state switch
        {
            WlanInterfaceStateConnected or WlanInterfaceStateAdHocFormed =>
                WifiConnectionState.Connected,
            WlanInterfaceStateAssociating or WlanInterfaceStateDiscovering
                or WlanInterfaceStateAuthenticating => WifiConnectionState.Connecting,
            WlanInterfaceStateDisconnecting or WlanInterfaceStateDisconnected =>
                WifiConnectionState.Disconnected,
            _ => WifiConnectionState.Unknown
        };
    }

    /// <summary>
    ///     The profile name a new or replaced profile is written under: the network's own name,
    ///     then "name 2", "name 3" and so on.
    /// </summary>
    /// <remarks>
    ///     A name held by a readable profile for the same SSID bytes is reused, and its XML kept for
    ///     the rollback. A name held by anything else, including a profile whose XML cannot be read,
    ///     is skipped and never overwritten. Each saved profile blocks at most one candidate, so the
    ///     search always ends within one more suffix than there are profiles.
    /// </remarks>
    internal static ProfileMutation FindFreeProfileName(
        IReadOnlyList<SavedProfile> profiles,
        string ssid,
        byte[] target)
    {
        for (var suffix = 1;; suffix++)
        {
            var candidate = suffix == 1 ? ssid : $"{ssid} {suffix}";
            var owner = profiles.FirstOrDefault(profile => profile.Name == candidate);
            if (owner == default)
            {
                return new ProfileMutation(candidate, null);
            }

            if (owner is { Ssid: { } ownerSsid, Xml: { } ownerXml } && ownerSsid.AsSpan().SequenceEqual(target))
            {
                return new ProfileMutation(candidate, ownerXml);
            }
        }
    }

    /// <summary>
    ///     Merges two observations of one network, from two adapters or two profile entries. The
    ///     caller only merges observations with the same key, so they never conflict: the stronger
    ///     one supplies the signal, and either one's flags count.
    /// </summary>
    internal static WifiNetworkFacts MergeNetworkFacts(
        WifiNetworkFacts existing,
        WifiNetworkFacts observed)
    {
        var observedIsPrimary = observed.Signal > existing.Signal;
        var primary = observedIsPrimary ? observed : existing;
        return primary with
        {
            Saved = existing.Saved || observed.Saved,
            Connectable = existing.Connectable || observed.Connectable,
            Connected = existing.Connected || observed.Connected
        };
    }

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
            _ => WifiSecurity.Unsupported
        };
    }

    internal readonly record struct SavedProfile(string Name, byte[]? Ssid, string? Xml);

    /// <summary>The profile one connection attempt writes, and what to put back if it must.</summary>
    /// <param name="Name">The profile name written.</param>
    /// <param name="PreviousXml">The replaced profile's exact XML, or null when the name was free.</param>
    internal readonly record struct ProfileMutation(string Name, string? PreviousXml)
    {
        /// <summary>Whether the attempt replaced an existing profile rather than creating one.</summary>
        internal bool Existed => PreviousXml is not null;
    }

    /// <summary>One listed network as observed on one or more adapters.</summary>
    internal readonly record struct WifiNetworkFacts(
        WifiNetworkKey Key,
        int Signal,
        bool Saved,
        bool Connectable,
        bool Connected);
}
