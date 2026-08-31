using System;
using System.Linq;
using System.Net;
using System.Text;

namespace WSGM.Interop;

/// <summary>Pure WLAN profile authoring and parsing helpers.</summary>
internal static class WifiProfile
{
    /// <summary>The profile shape used for a pre-shared key network.</summary>
    internal enum PskFlavor
    {
        Wpa3Transition,
        Wpa2Aes,
        WpaTkip,
    }

    /// <summary>Builds a passwordless profile without losing a non-UTF8 SSID.</summary>
    internal static string CreateOpen(
        string profileName,
        string ssid,
        byte[] rawSsid,
        bool enhancedOpen)
    {
        var authentication = enhancedOpen ? "OWE" : "open";
        var encryption = enhancedOpen ? "AES" : "none";
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
                  <useOneX>false</useOneX>
                </authEncryption>
              </security></MSM>
            </WLANProfile>
            """;
    }

    /// <summary>Builds the precise WPA profile advertised by the access point.</summary>
    internal static string CreatePsk(
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
            _ => ("WPAPSK", "TKIP", string.Empty),
        };
        var keyType = IsRawKey(passphrase) ? "networkKey" : "passPhrase";
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
                </authEncryption>
                <sharedKey>
                  <keyType>{{keyType}}</keyType>
                  <protected>false</protected>
                  <keyMaterial>{{Escape(passphrase)}}</keyMaterial>
                </sharedKey>
              </security></MSM>
            </WLANProfile>
            """;
    }

    /// <summary>Reads the SSID identity from a Windows-produced profile document.</summary>
    internal static byte[]? TryReadSsid(string xml)
    {
        var config = Between(xml, "<SSIDConfig>", "</SSIDConfig>");
        if (config is null)
        {
            return null;
        }
        if (Between(config, "<hex>", "</hex>") is { } hex)
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
        return Between(config, "<name>", "</name>") is { } name
            ? Encoding.UTF8.GetBytes(WebUtility.HtmlDecode(name))
            : null;
    }

    /// <summary>Checks the 802.11 passphrase and raw-key bounds before WLANAPI sees it.</summary>
    internal static bool PassphraseIsValid(string passphrase)
    {
        if (IsRawKey(passphrase))
        {
            return true;
        }
        return passphrase.Length is >= 8 and <= 63
            && passphrase.All(character => character is >= ' ' and <= '~');
    }

    private static bool IsRawKey(string value)
        => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string SsidElement(string ssid, byte[] rawSsid)
        => rawSsid.Length > 0 && !IsUtf8(rawSsid)
            ? $"<hex>{Convert.ToHexString(rawSsid)}</hex>"
            : $"<name>{Escape(ssid)}</name>";

    private static bool IsUtf8(byte[] bytes)
    {
        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static string Escape(string value) => WebUtility.HtmlEncode(value)
        .Replace("&#39;", "&apos;", StringComparison.Ordinal);

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
