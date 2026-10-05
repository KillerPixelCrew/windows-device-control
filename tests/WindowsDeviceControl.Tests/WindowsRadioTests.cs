using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace WindowsDeviceControl.Tests;

public sealed class WindowsRadioTests
{
    [Fact]
    public void AggregatePowerReportsDisabledBeforeOff()
    {
        var result = WindowsRadio.AggregatePower(
            [WindowsRadio.Power.Off, WindowsRadio.Power.Disabled]);

        Assert.Equal(WindowsRadio.Power.Disabled, result);
    }

    [Fact]
    public void OneLiveRadioWinsTheAggregateState()
    {
        Assert.Equal(
            WindowsRadio.Power.On,
            WindowsRadio.AggregatePower([WindowsRadio.Power.Off, WindowsRadio.Power.On]));
    }

    [Fact]
    public void NoRadioIsReportedAsAbsent()
    {
        Assert.Equal(WindowsRadio.Power.Absent, WindowsRadio.AggregatePower([]));
    }

    [Fact]
    public void AnUndefinedRadioKindIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WindowsRadio.ValidateRadioKind((WindowsRadio.RadioKind)int.MaxValue));
        WindowsRadio.ValidateRadioKind(WindowsRadio.RadioKind.WiFi);
        WindowsRadio.ValidateRadioKind(WindowsRadio.RadioKind.Bluetooth);
    }

    [Fact]
    public void UnknownAuthenticationIsUnsupported()
    {
        Assert.Equal(
            WindowsRadio.WifiSecurity.Unsupported,
            WindowsRadio.ClassifySecurity(true, int.MaxValue));
    }

    [Theory]
    [InlineData(294932u, WindowsRadio.WifiFailureKind.KeyRejected)] // MSMSEC_PSK_MISMATCH_SUSPECTED
    [InlineData(262148u, WindowsRadio.WifiFailureKind.SecurityMismatch)] // MSMSEC_PROFILE_PSK_LENGTH
    [InlineData(196614u, WindowsRadio.WifiFailureKind.Unreachable)] // any MSM association failure
    [InlineData(1u, WindowsRadio.WifiFailureKind.Unknown)]
    public void WlanReasonFamiliesKeepPasswordAndReachabilityFailuresDistinct(
        uint reason,
        WindowsRadio.WifiFailureKind expected)
    {
        Assert.Equal(expected, WindowsRadio.GetReasonVerdict(reason));
    }

    [Fact]
    public void ANetworkKeyIsTheSsidBytesAndTheSecurity()
    {
        var home = new WindowsRadio.WifiNetworkKey("Home"u8, WindowsRadio.WifiSecurity.PersonalPsk);

        Assert.Equal(home, new WindowsRadio.WifiNetworkKey("Home"u8, WindowsRadio.WifiSecurity.PersonalPsk));
        Assert.Equal(
            home.GetHashCode(),
            new WindowsRadio.WifiNetworkKey("Home"u8, WindowsRadio.WifiSecurity.PersonalPsk).GetHashCode());
        // The same name advertised open is a different network.
        Assert.NotEqual(home, new WindowsRadio.WifiNetworkKey("Home"u8, WindowsRadio.WifiSecurity.Open));
        Assert.Equal("486F6D65", home.Hex);
        Assert.Equal("Home", home.DisplayText);
    }

    [Fact]
    public void TwoSsidsThatShowTheSameTextAreStillTwoNetworks()
    {
        var first = new WindowsRadio.WifiNetworkKey([0x41, 0xFF], WindowsRadio.WifiSecurity.Open);
        var second = new WindowsRadio.WifiNetworkKey([0x41, 0xFE], WindowsRadio.WifiSecurity.Open);

        Assert.Equal(first.DisplayText, second.DisplayText);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ANetworkKeyCopiesItsBytesAndNeedsSome()
    {
        var bytes = "Cafe"u8.ToArray();
        var key = new WindowsRadio.WifiNetworkKey(bytes, WindowsRadio.WifiSecurity.Open);
        bytes[0] = (byte)'X';

        Assert.Equal("Cafe", key.DisplayText);
        Assert.Throws<ArgumentException>(() =>
            new WindowsRadio.WifiNetworkKey([], WindowsRadio.WifiSecurity.Open));
        Assert.True(default(WindowsRadio.WifiNetworkKey).IsDefault);
        Assert.False(key.IsDefault);
    }

    [Fact]
    public void ProfileMutationKeepsExactPreviousXml()
    {
        var target = new byte[] { 1, 2, 3 };
        const string xml = "<WLANProfile><SSIDConfig /></WLANProfile>";
        var profiles = new[]
        {
            new WindowsRadio.SavedProfile("network", target, xml)
        };

        var mutation = WindowsRadio.FindFreeProfileName(profiles, "network", target);

        Assert.True(mutation.Existed);
        Assert.Equal("network", mutation.Name);
        Assert.Equal(xml, mutation.PreviousXml);
    }

    [Fact]
    public void ProfileMutationDoesNotOverwriteUnreadableProfile()
    {
        var target = new byte[] { 1, 2, 3 };
        var profiles = new[]
        {
            new WindowsRadio.SavedProfile("network", null, null)
        };

        var mutation = WindowsRadio.FindFreeProfileName(profiles, "network", target);

        Assert.False(mutation.Existed);
        Assert.Equal("network 2", mutation.Name);
    }

    [Fact]
    public void ProfileNamingSkipsEveryNameAnotherNetworkHolds()
    {
        var profiles = Enumerable.Range(1, 64)
            .Select(index => new WindowsRadio.SavedProfile(
                index == 1 ? "network" : $"network {index}",
                new[] { checked((byte)index) },
                $"<profile>{index}</profile>"))
            .ToArray();

        var mutation = WindowsRadio.FindFreeProfileName(profiles, "network", [0]);

        Assert.False(mutation.Existed);
        Assert.Equal("network 65", mutation.Name);
    }

    [Fact]
    public void MergedObservationsTakeTheStrongerSignalAndEitherFlag()
    {
        var key = new WindowsRadio.WifiNetworkKey("network"u8, WindowsRadio.WifiSecurity.PersonalPsk);
        var weak = new WindowsRadio.WifiNetworkFacts(key, 20, true, false, true);
        var strong = new WindowsRadio.WifiNetworkFacts(key, 70, false, true, false);

        var merged = WindowsRadio.MergeNetworkFacts(weak, strong);

        Assert.Equal(70, merged.Signal);
        Assert.True(merged.Saved);
        Assert.True(merged.Connectable);
        Assert.True(merged.Connected);
        Assert.Equal(merged, WindowsRadio.MergeNetworkFacts(strong, weak));
    }

    [Theory]
    [InlineData("Home", "Home", true)]
    [InlineData("Home 2", "Home", false)]
    // A completion without a profile name could be any connection on the adapter.
    [InlineData("", "Home", false)]
    public void OnlyACompletionNamingTheProfileDecidesAJoin(string profile, string expected, bool decides)
    {
        Assert.Equal(decides, WindowsRadio.IsCompletionForProfile(profile, expected));
    }

    [Fact]
    public void BluetoothTransportEndpointsShareContainerIdentity()
    {
        const string property = "System.Devices.Aep.ContainerId";
        var container = Guid.NewGuid();
        var fromClassic = WindowsRadio.BluetoothIdentity(
            "classic-id",
            new Dictionary<string, object> { [property] = container });
        var fromLowEnergy = WindowsRadio.BluetoothIdentity(
            "le-id",
            new Dictionary<string, object> { [property] = container.ToString("B") });

        Assert.Equal(fromClassic, fromLowEnergy, true);
    }

    [Fact]
    public void AnAnswerToAnUnknownPairingTokenIsIgnored()
    {
        // An accept without a PIN is refused only for a live provide-PIN question; for a token
        // nobody holds, every answer, repeated or not, returns without effect.
        Assert.Null(Record.Exception(() => WindowsRadio.RespondToPairing(uint.MaxValue, true, null)));
        Assert.Null(Record.Exception(() => WindowsRadio.RespondToPairing(uint.MaxValue, false, null)));
        Assert.Null(Record.Exception(() => WindowsRadio.RespondToPairing(uint.MaxValue, false, null)));
    }

    // ---- WLAN native layouts, pinned by the offsets wlanapi.h and windot11.h give ----

    [Fact]
    public void AnAvailableNetworkListDecodesEveryRecordAtItsNativeStride()
    {
        // WLAN_AVAILABLE_NETWORK_LIST: dwNumberOfItems, dwIndex, then 628-byte records.
        // WLAN_AVAILABLE_NETWORK: strProfileName[256] at 0, DOT11_SSID at 512 (length, then 32
        // bytes), bNetworkConnectable at 556, wlanSignalQuality at 604, bSecurityEnabled at 608,
        // dot11DefaultAuthAlgorithm at 612, dwFlags at 620 and dwReserved at 624.
        const int record = 628;
        var bytes = new byte[8 + 2 * record];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 2);
        WriteAvailableNetwork(bytes.AsSpan(8, record), "Home"u8, connectable: true, signal: 81, secured: true, auth: 7);
        WriteAvailableNetwork(bytes.AsSpan(8 + record, record), [0x41, 0xFF], connectable: false, signal: 12,
            secured: false, auth: 1);

        var decoded = WithNative(bytes, list => WindowsRadio.ReadWlanList(list,
            static (WindowsRadio.WlanAvailableNetwork network) => (
                Ssid: WindowsRadio.ReadSsidBytes(network.Ssid),
                network.Connectable,
                network.SignalQuality,
                network.SecurityEnabled,
                network.DefaultAuthAlgorithm)));

        Assert.Equal(2, decoded.Length);
        Assert.Equal("Home"u8.ToArray(), decoded[0].Ssid);
        Assert.Equal((1, 81u, 1, 7), (decoded[0].Connectable, decoded[0].SignalQuality,
            decoded[0].SecurityEnabled, decoded[0].DefaultAuthAlgorithm));
        Assert.Equal(new byte[] { 0x41, 0xFF }, decoded[1].Ssid);
        Assert.Equal((0, 12u, 0, 1), (decoded[1].Connectable, decoded[1].SignalQuality,
            decoded[1].SecurityEnabled, decoded[1].DefaultAuthAlgorithm));
    }

    [Fact]
    public void InterfaceAndProfileListsDecodeAtTheirNativeStrides()
    {
        // WLAN_INTERFACE_INFO: GUID at 0, strInterfaceDescription[256] at 16, isState at 528; 532
        // bytes. WLAN_PROFILE_INFO: strProfileName[256] at 0, dwFlags at 512; 516 bytes.
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var interfaces = new byte[8 + 2 * 532];
        BinaryPrimitives.WriteUInt32LittleEndian(interfaces, 2);
        first.TryWriteBytes(interfaces.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(interfaces.AsSpan(8 + 528), 1);
        second.TryWriteBytes(interfaces.AsSpan(8 + 532));
        BinaryPrimitives.WriteInt32LittleEndian(interfaces.AsSpan(8 + 532 + 528), 4);

        var adapters = WithNative(interfaces, list => WindowsRadio.ReadWlanList(list,
            static (WindowsRadio.WlanInterfaceInfo adapter) => (adapter.Id, adapter.State)));

        Assert.Equal(new[] { (first, 1), (second, 4) }, adapters);

        var profiles = new byte[8 + 2 * 516];
        BinaryPrimitives.WriteUInt32LittleEndian(profiles, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(profiles.AsSpan(8 + 512), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(profiles.AsSpan(8 + 516 + 512), 5);

        var flags = WithNative(profiles, list => WindowsRadio.ReadWlanList(list,
            static (WindowsRadio.WlanProfileInfo profile) => profile.Flags));

        Assert.Equal(new[] { 3u, 5u }, flags);
    }

    [Fact]
    public void ConnectionAttributesDecodeAtTheirNativeOffsets()
    {
        // WLAN_CONNECTION_ATTRIBUTES: isState at 0, wlanConnectionMode at 4, strProfileName[256] at 8,
        // WLAN_ASSOCIATION_ATTRIBUTES at 520 (DOT11_SSID, BSS type at 556, BSSID at 560, PHY type at
        // 568, PHY index at 572, wlanSignalQuality at 576, rates at 580 and 584) and
        // WLAN_SECURITY_ATTRIBUTES at 588 (enabled, 802.1X, auth at 596, cipher at 600); 604 bytes.
        var bytes = new byte[604];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 1);
        WriteSsid(bytes.AsSpan(520), "Cafe"u8);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(576), 64);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(588), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(596), 9);

        var attributes = MemoryMarshal.Read<WindowsRadio.WlanConnectionAttributes>(bytes);

        Assert.Equal(604, Marshal.SizeOf<WindowsRadio.WlanConnectionAttributes>());
        Assert.Equal(1, attributes.State);
        Assert.Equal("Cafe"u8.ToArray(), WindowsRadio.ReadSsidBytes(attributes.Association.Ssid));
        Assert.Equal(64u, attributes.Association.SignalQuality);
        Assert.Equal(1, attributes.Security.SecurityEnabled);
        Assert.Equal(9, attributes.Security.AuthAlgorithm);
    }

    [Fact]
    public void NotificationsDecodeAtTheirNativeOffsets()
    {
        // WLAN_NOTIFICATION_DATA: source, code, interface GUID at 8, dwDataSize at 24, then the
        // pointer at the next pointer-aligned offset.
        var pointerOffset = IntPtr.Size == 8 ? 32 : 28;
        var adapter = Guid.NewGuid();
        var notification = new byte[pointerOffset + IntPtr.Size];
        BinaryPrimitives.WriteUInt32LittleEndian(notification, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(notification.AsSpan(4), 11);
        adapter.TryWriteBytes(notification.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(notification.AsSpan(24), 572);
        notification[pointerOffset] = 0x5A;

        var data = MemoryMarshal.Read<WindowsRadio.WlanNotificationData>(notification);

        Assert.Equal(notification.Length, Marshal.SizeOf<WindowsRadio.WlanNotificationData>());
        Assert.Equal((8u, 11u, adapter, 572u, (nint)0x5A),
            (data.Source, data.Code, data.InterfaceId, data.DataSize, data.Data));

        // WLAN_CONNECTION_NOTIFICATION_DATA: mode, strProfileName[256] at 4, DOT11_SSID at 516,
        // BSS type at 552, security at 556, wlanReasonCode at 560, dwFlags at 564 and the first
        // character of the profile XML at 568; 572 bytes with padding.
        var connection = new byte[572];
        WriteSsid(connection.AsSpan(516), "Home"u8);
        BinaryPrimitives.WriteUInt32LittleEndian(connection.AsSpan(560), 0x48001);

        var payload = MemoryMarshal.Read<WindowsRadio.WlanConnectionNotificationData>(connection);

        Assert.Equal(572, Marshal.SizeOf<WindowsRadio.WlanConnectionNotificationData>());
        Assert.Equal(0x48001u, payload.ReasonCode);
        Assert.Equal("Home"u8.ToArray(), WindowsRadio.ReadSsidBytes(payload.Ssid));
    }

    // ---- WLAN profile documents ----

    [Fact]
    public void AProfileAsWindowsExportsItYieldsTheHexBytes()
    {
        // The shape netsh wlan export writes: a profile name of its own, then hex and name.
        const string xml = """
                           <?xml version="1.0"?>
                           <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
                               <name>Home network</name>
                               <SSIDConfig>
                                   <SSID>
                                       <hex>436166C3A9</hex>
                                       <name>Café</name>
                                   </SSID>
                                   <nonBroadcast>false</nonBroadcast>
                               </SSIDConfig>
                               <connectionType>ESS</connectionType>
                           </WLANProfile>
                           """;

        Assert.Equal(Encoding.UTF8.GetBytes("Café"), WifiProfile.TryReadSsid(xml));
    }

    [Theory]
    [InlineData("<SSIDConfig><SSID><hex>41FF42</hex></SSID></SSIDConfig>", new byte[] { 0x41, 0xFF, 0x42 })]
    [InlineData("<SSIDConfig><SSID><hex> 616263 </hex></SSID></SSIDConfig>", new byte[] { 0x61, 0x62, 0x63 })]
    [InlineData("<SSIDConfig><SSID><name>Tom &amp; Jerry&apos;s</name></SSID></SSIDConfig>",
        new byte[] { 0x54, 0x6F, 0x6D, 0x20, 0x26, 0x20, 0x4A, 0x65, 0x72, 0x72, 0x79, 0x27, 0x73 })]
    // Several SSID elements: the first one's own form is read, never the next one's hex.
    [InlineData(
        "<SSIDConfig><SSID><name>Home</name></SSID><SSID><hex>4F74686572</hex><name>Other</name></SSID></SSIDConfig>",
        new byte[] { 0x48, 0x6F, 0x6D, 0x65 })]
    public void EverySsidElementFormWindowsWritesIsRead(string xml, byte[] expected)
    {
        Assert.Equal(expected, WifiProfile.TryReadSsid(xml));
    }

    [Fact]
    public void ADocumentWithoutAReadableSsidYieldsNothing()
    {
        Assert.Null(WifiProfile.TryReadSsid("<WLANProfile />"));
        Assert.Null(WifiProfile.TryReadSsid("<SSIDConfig><SSID><hex>ABC</hex></SSID></SSIDConfig>"));
        Assert.Null(WifiProfile.TryReadSsid("<SSIDConfig><SSID><hex>ZZ</hex></SSID></SSIDConfig>"));
        Assert.Throws<ArgumentNullException>(() => WifiProfile.TryReadSsid(null!));
    }

    [Fact]
    public void AnAuthoredProfileCarriesHexAheadOfTheName()
    {
        var raw = new byte[] { 0x41, 0xFF, 0x42 };

        var xml = WifiProfile.CreateOpen("A?B", "A?B", raw, false);

        Assert.Contains("<SSID><hex>41FF42</hex><name>A?B</name></SSID>", xml);
        Assert.Equal(raw, WifiProfile.TryReadSsid(xml));
    }

    [Fact]
    public void ARawPskUsesTheNetworkKeyProfileShape()
    {
        var xml = WifiProfile.CreatePsk(
            "Cafe", "Cafe", [.. "Cafe"u8], string.Concat(Enumerable.Repeat("a1B2", 16)),
            WifiProfile.PskFlavor.Wpa3Transition);
        Assert.Contains("<keyType>networkKey</keyType>", xml);
        Assert.Contains("profile/v4", xml);
    }

    [Fact]
    public void AProfileRoundTripsEscapedAndHexSsids()
    {
        var escaped = WifiProfile.CreateOpen("A&B", "A&B", [.. "A&B"u8], false);
        Assert.Equal("A&B"u8.ToArray(), WifiProfile.TryReadSsid(escaped));

        var raw = new byte[] { 0x41, 0xff, 0x42 };
        var hex = WifiProfile.CreatePsk(
            "A?B", "A?B", raw, "password1", WifiProfile.PskFlavor.Wpa2Aes);
        Assert.Equal(raw, WifiProfile.TryReadSsid(hex));
    }

    [Fact]
    public void ProfileAuthoringPreservesEveryWindowsSpecificShape()
    {
        var escaped = WifiProfile.CreatePsk(
            "A&B<C>",
            "A&B<C>",
            [.. "A&B<C>"u8],
            "pw\"&<>'x",
            WifiProfile.PskFlavor.Wpa3Transition);
        Assert.Contains("<name>A&amp;B&lt;C&gt;</name>", escaped);
        Assert.Contains("<keyMaterial>pw&quot;&amp;&lt;&gt;&apos;x</keyMaterial>", escaped);
        Assert.Contains(
            "<transitionMode xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v4\">true</transitionMode>",
            escaped);

        var enhancedOpen = WifiProfile.CreateOpen("Cafe", "Cafe", [.. "Cafe"u8], true);
        Assert.Contains("<authentication>OWE</authentication>", enhancedOpen);
        Assert.DoesNotContain("<encryption>none</encryption>", enhancedOpen);

        var legacy = WifiProfile.CreatePsk(
            "Old", "Old", [.. "Old"u8], "password1", WifiProfile.PskFlavor.WpaTkip);
        Assert.Contains("<authentication>WPAPSK</authentication>", legacy);
        Assert.Contains("<encryption>TKIP</encryption>", legacy);
    }

    [Fact]
    public void AProfileNameNeverReplacesTheNetworkIdentity()
    {
        var xml = WifiProfile.CreatePsk(
            "Cafe 2", " Cafe ", [.. " Cafe "u8], "password1",
            WifiProfile.PskFlavor.Wpa2Aes);
        Assert.Contains("<name>Cafe 2</name>", xml);
        Assert.Equal(" Cafe "u8.ToArray(), WifiProfile.TryReadSsid(xml));
    }

    [Theory]
    [InlineData("short", false)]
    [InlineData("12345678", true)]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", false)]
    [InlineData("pass\tword", false)]
    [InlineData("pässword", false)]
    public void PassphraseValidationUses80211Bounds(string passphrase, bool expected)
    {
        Assert.Equal(expected, WifiProfile.PassphraseIsValid(passphrase));
    }

    private static void WriteAvailableNetwork(
        Span<byte> record,
        ReadOnlySpan<byte> ssid,
        bool connectable,
        uint signal,
        bool secured,
        int auth)
    {
        WriteSsid(record[512..], ssid);
        BinaryPrimitives.WriteInt32LittleEndian(record[556..], connectable ? 1 : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(record[604..], signal);
        BinaryPrimitives.WriteInt32LittleEndian(record[608..], secured ? 1 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(record[612..], auth);
        // dwReserved, the last word: a decoder short of it strides every later record too early.
        BinaryPrimitives.WriteUInt32LittleEndian(record[624..], 0xDEADBEEF);
    }

    private static void WriteSsid(Span<byte> destination, ReadOnlySpan<byte> ssid)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)ssid.Length);
        ssid.CopyTo(destination[4..]);
    }

    private static T WithNative<T>(byte[] bytes, Func<nint, T> read)
    {
        var memory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            return read(memory);
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }
}
