using System;
using System.Linq;
using System.Net;
using System.Text;

namespace WindowsDeviceControl;

/// <summary>Pure WLAN profile authoring and parsing helpers.</summary>
public static class WifiProfile
{
    /// <summary>The profile shape used for a pre-shared key network.</summary>
    public enum PskFlavor
    {
        /// <summary>
        ///     WPA3-SAE with WPA2 fallback. Try this first: it joins both WPA3 and
        ///     WPA2 networks, so one profile covers modern and older access points.
        /// </summary>
        Wpa3Transition,

        /// <summary>
        ///     WPA2-Personal with AES. The fallback when the transition profile is
        ///     refused, which older access points do.
        /// </summary>
        Wpa2Aes,

        /// <summary>
        ///     WPA-Personal with TKIP. Last resort for legacy equipment; TKIP is
        ///     deprecated and should not be chosen unless the network requires it.
        /// </summary>
        WpaTkip
    }

    /// <summary>Builds a profile for a network that needs no passphrase.</summary>
    /// <param name="profileName">
    ///     The name Windows stores the profile under. Conventionally the
    ///     SSID.
    /// </param>
    /// <param name="ssid">The network name as text, written as the SSID's display name.</param>
    /// <param name="rawSsid">
    ///     The SSID's exact bytes. When known they are written as hex ahead of the name, as Windows
    ///     itself exports a profile, so Windows matches the exact bytes rather than its own encoding
    ///     of the name. Empty writes the name alone.
    /// </param>
    /// <param name="enhancedOpen">
    ///     True for Opportunistic Wireless Encryption (OWE), false for a
    ///     genuinely unencrypted network.
    /// </param>
    /// <returns>The profile XML, in the form this library writes through <c>WlanSetProfile</c>.</returns>
    public static string CreateOpen(
        string profileName,
        string ssid,
        byte[] rawSsid,
        bool enhancedOpen)
    {
        return Document(
            profileName,
            ssid,
            rawSsid,
            enhancedOpen ? "OWE" : "open",
            enhancedOpen ? "AES" : "none",
            string.Empty,
            string.Empty);
    }

    /// <summary>Builds a profile for a pre-shared-key network.</summary>
    /// <param name="profileName">
    ///     The name Windows stores the profile under. Conventionally the
    ///     SSID.
    /// </param>
    /// <param name="ssid">The network name as text, written as the SSID's display name.</param>
    /// <param name="rawSsid">
    ///     The SSID's exact bytes, written as hex ahead of the name when known. Empty writes the name
    ///     alone.
    /// </param>
    /// <param name="passphrase">
    ///     The passphrase, or a 64-character hex key. Validate it first with
    ///     <see cref="PassphraseIsValid" />; a raw key is detected and declared as one.
    /// </param>
    /// <param name="flavor">
    ///     Which WPA shape to write. The profile must match what the access point
    ///     advertises, so try <see cref="PskFlavor.Wpa3Transition" /> and fall back if it is
    ///     refused.
    /// </param>
    /// <returns>The profile XML, in the form this library writes through <c>WlanSetProfile</c>.</returns>
    public static string CreatePsk(
        string profileName,
        string ssid,
        byte[] rawSsid,
        string passphrase,
        PskFlavor flavor)
    {
        var (authentication, encryption, transition) = flavor switch
        {
            PskFlavor.Wpa3Transition => (
                "WPA3SAE",
                "AES",
                "\n      <transitionMode xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v4\">true</transitionMode>"),
            PskFlavor.Wpa2Aes => ("WPA2PSK", "AES", string.Empty),
            _ => ("WPAPSK", "TKIP", string.Empty)
        };
        var keyType = IsRawKey(passphrase) ? "networkKey" : "passPhrase";
        return Document(profileName, ssid, rawSsid, authentication, encryption, transition, $$"""

                  <sharedKey>
                    <keyType>{{keyType}}</keyType>
                    <protected>false</protected>
                    <keyMaterial>{{Escape(passphrase)}}</keyMaterial>
                  </sharedKey>
              """);
    }

    /// <summary>
    ///     The profile document both shapes share. <paramref name="transition" /> follows the
    ///     <c>useOneX</c> element and <paramref name="sharedKey" /> follows <c>authEncryption</c>; each
    ///     carries its own leading line break when present.
    /// </summary>
    private static string Document(
        string profileName,
        string ssid,
        byte[] rawSsid,
        string authentication,
        string encryption,
        string transition,
        string sharedKey)
    {
        return $$"""
                 <?xml version="1.0"?>
                 <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
                   <name>{{Escape(profileName)}}</name>
                   <SSIDConfig><SSID>{{SsidElement(ssid, rawSsid)}}</SSID></SSIDConfig>
                   <connectionType>ESS</connectionType>
                   <connectionMode>auto</connectionMode>
                   <MSM><security>
                     <authEncryption>
                       <authentication>{{authentication}}</authentication>
                       <encryption>{{encryption}}</encryption>
                       <useOneX>false</useOneX>{{transition}}
                     </authEncryption>{{sharedKey}}
                   </security></MSM>
                 </WLANProfile>
                 """;
    }

    /// <summary>Reads the SSID out of a profile document Windows produced.</summary>
    /// <param name="xml">The profile XML, as returned by WLANAPI when a saved profile is read.</param>
    /// <returns>
    ///     The SSID's exact bytes, or <see langword="null" /> when the document carries no
    ///     readable SSID. Bytes rather than a string, because an SSID need not be valid UTF-8.
    /// </returns>
    /// <remarks>
    ///     The first <c>SSID</c> element of <c>SSIDConfig</c> is read. Its <c>hex</c> form wins, because
    ///     it carries the exact bytes; the <c>name</c> form is decoded and encoded as UTF-8 otherwise.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="xml" /> is null.</exception>
    public static byte[]? TryReadSsid(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        // A profile may list several SSID elements; reading inside the first keeps one element's
        // hex and name together.
        var element = Between(xml, "<SSIDConfig>", "</SSIDConfig>") is { } section
            ? Between(section, "<SSID>", "</SSID>")
            : null;
        if (element is null)
        {
            return null;
        }

        if (Between(element, "<hex>", "</hex>") is { } hex)
        {
            var trimmed = hex.Trim();
            if ((trimmed.Length & 1) != 0)
            {
                return null;
            }

            try
            {
                return Convert.FromHexString(trimmed);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        return Between(element, "<name>", "</name>") is { } name
            ? Encoding.UTF8.GetBytes(WebUtility.HtmlDecode(name))
            : null;
    }

    /// <summary>Checks a passphrase against the 802.11 bounds before WLANAPI sees it.</summary>
    /// <param name="passphrase">The passphrase or raw key to check.</param>
    /// <returns>
    ///     <see langword="true" /> for a 64-character hex key, or for 8 to 63 printable ASCII
    ///     characters. Checking here turns an unhelpful driver-level refusal into a message you can
    ///     show the user before anything is attempted.
    /// </returns>
    public static bool PassphraseIsValid(string passphrase)
    {
        if (IsRawKey(passphrase))
        {
            return true;
        }

        return passphrase.Length is >= 8 and <= 63
               && passphrase.All(character => character is >= ' ' and <= '~');
    }

    private static bool IsRawKey(string value)
    {
        return value.Length == 64 && value.All(Uri.IsHexDigit);
    }

    private static string SsidElement(string ssid, byte[] rawSsid)
    {
        return rawSsid.Length > 0
            ? $"<hex>{Convert.ToHexString(rawSsid)}</hex><name>{Escape(ssid)}</name>"
            : $"<name>{Escape(ssid)}</name>";
    }

    private static string Escape(string value)
    {
        return WebUtility.HtmlEncode(value)
            .Replace("&#39;", "&apos;", StringComparison.Ordinal);
    }

    private static string? Between(string source, string open, string close)
    {
        var start = source.IndexOf(open, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += open.Length;
        var end = source.IndexOf(close, start, StringComparison.Ordinal);
        return end < 0 ? null : source[start..end];
    }
}
