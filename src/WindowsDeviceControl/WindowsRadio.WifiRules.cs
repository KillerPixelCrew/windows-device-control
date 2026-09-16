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
    ///     A reason code, as returned by
    ///     <see cref="ConnectWifi(string, string?)" /> or carried on a
    ///     <see cref="WlanReasonException" />.
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

    internal static WifiNetworkFacts MergeNetworkFacts(
        WifiNetworkFacts existing,
        WifiNetworkFacts observed)
    {
        var conflictingIdentity = existing.Ambiguous
                                  || observed.Ambiguous
                                  || existing.Security != observed.Security
                                  || existing.Authentication != observed.Authentication
                                  || (existing.ProfileName is { Length: > 0 } existingProfile
                                      && observed.ProfileName is { Length: > 0 } observedProfile
                                      && !string.Equals(existingProfile, observedProfile, StringComparison.Ordinal));
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
            Ambiguous = conflictingIdentity
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

    internal readonly record struct ProfileMutation(string Name, bool Existed, string? PreviousXml);

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
        {
            return new WifiNetworkFacts(ssid, [], 0, WifiSecurity.Unsupported, 0, false, false, false, null, false);
        }
    }
}
