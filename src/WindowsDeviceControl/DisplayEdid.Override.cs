using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace WindowsDeviceControl;

/// <summary>What one monitor's <c>EDID_OVERRIDE</c> registry key holds.</summary>
/// <param name="Present">Whether the key exists at all.</param>
/// <param name="Edid">
///     The overriding EDID when the key holds exactly the binary values <c>0</c> to <c>n-1</c>, one 128-byte
///     block each, in that order; null when the key is absent or holds anything else.
/// </param>
public sealed record DisplayEdidOverride(bool Present, byte[]? Edid)
{
    /// <summary>No override key.</summary>
    public static DisplayEdidOverride None { get; } = new(false, null);
}

public static partial class DisplayEdid
{
    private const int BlockLength = 128;
    private const string OverrideKey = "EDID_OVERRIDE";

    /// <summary>Reads the EDID Windows reports for an exact monitor interface, overrides included.</summary>
    /// <param name="devicePath">Monitor interface path from CCD discovery.</param>
    /// <returns>The raw bytes of the <c>Device Parameters\EDID</c> value, or null when absent or unreadable.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="devicePath" /> is null.</exception>
    /// <remarks>
    ///     Read-only registry access. While an <c>EDID_OVERRIDE</c> is loaded, Windows reports the overriding
    ///     EDID here; an override written since the last start is reported only after the next one.
    /// </remarks>
    public static byte[]? ReadReported(string devicePath)
    {
        ArgumentNullException.ThrowIfNull(devicePath);
        if (DeviceParameters(devicePath) is not { } path)
        {
            return null;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue("EDID") as byte[];
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>Reads the EDID override of an exact monitor interface.</summary>
    /// <param name="devicePath">Monitor interface path from CCD discovery.</param>
    /// <returns>What the override key holds; <see cref="DisplayEdidOverride.None" /> when there is no key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="devicePath" /> is null.</exception>
    /// <exception cref="ArgumentException">The path is not a monitor interface path.</exception>
    /// <remarks>Read-only. Registry access failures propagate, so an unreadable key is never reported as absent.</remarks>
    public static DisplayEdidOverride ReadOverride(string devicePath)
    {
        using var key = Registry.LocalMachine.OpenSubKey(OverridePath(devicePath));
        if (key is null)
        {
            return DisplayEdidOverride.None;
        }

        Dictionary<string, object?> values = new(StringComparer.Ordinal);
        foreach (var name in key.GetValueNames())
        {
            values[name] = key.GetValueKind(name) is RegistryValueKind.Binary ? key.GetValue(name) : null;
        }

        return new DisplayEdidOverride(true, JoinBlocks(values));
    }

    /// <summary>Writes an EDID override for an exact monitor interface, one 128-byte block per value.</summary>
    /// <param name="devicePath">Monitor interface path from CCD discovery.</param>
    /// <param name="edid">The complete EDID, a whole number of 128-byte blocks.</param>
    /// <exception cref="ArgumentNullException"><paramref name="devicePath" /> is null.</exception>
    /// <exception cref="ArgumentException">The path or the EDID length is invalid.</exception>
    /// <remarks>
    ///     Needs administrator rights; access failures propagate. Values <c>0</c> to <c>n-1</c> are written in
    ///     <c>Device Parameters\EDID_OVERRIDE</c>, the key Windows reads at the next start, and any other value
    ///     already in that key is removed so the key holds exactly this EDID. The caller decides whether an existing
    ///     override may be replaced. Nothing changes on screen before the next start.
    /// </remarks>
    public static void WriteOverride(string devicePath, ReadOnlySpan<byte> edid)
    {
        if (edid.Length == 0 || edid.Length % BlockLength != 0)
        {
            throw new ArgumentException("An EDID override is a whole number of 128-byte blocks.", nameof(edid));
        }

        using var key = Registry.LocalMachine.CreateSubKey(OverridePath(devicePath), true);
        HashSet<string> written = [];
        for (var block = 0; block < edid.Length / BlockLength; block++)
        {
            var name = block.ToString(CultureInfo.InvariantCulture);
            key.SetValue(name, edid.Slice(block * BlockLength, BlockLength).ToArray(), RegistryValueKind.Binary);
            written.Add(name);
        }

        foreach (var name in key.GetValueNames())
        {
            if (!written.Contains(name))
            {
                key.DeleteValue(name, false);
            }
        }
    }

    /// <summary>Removes the EDID override key of an exact monitor interface.</summary>
    /// <param name="devicePath">Monitor interface path from CCD discovery.</param>
    /// <returns>Whether a key was removed; false when there was none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="devicePath" /> is null.</exception>
    /// <exception cref="ArgumentException">The path is not a monitor interface path.</exception>
    /// <remarks>Needs administrator rights; access failures propagate. The reported EDID returns after the next start.</remarks>
    public static bool RemoveOverride(string devicePath)
    {
        var path = OverridePath(devicePath);
        using (var key = Registry.LocalMachine.OpenSubKey(path))
        {
            if (key is null)
            {
                return false;
            }
        }

        Registry.LocalMachine.DeleteSubKeyTree(path, false);
        return true;
    }

    /// <summary>Joins override values named <c>0</c> to <c>n-1</c>, each a 128-byte binary block, in order.</summary>
    /// <param name="values">Every value in the key by name; a non-binary value is null.</param>
    /// <returns>The joined EDID, or null when the key holds no values or any other shape.</returns>
    internal static byte[]? JoinBlocks(IReadOnlyDictionary<string, object?> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var edid = new byte[values.Count * BlockLength];
        for (var block = 0; block < values.Count; block++)
        {
            if (!values.TryGetValue(block.ToString(CultureInfo.InvariantCulture), out var value)
                || value is not byte[] { Length: BlockLength } bytes)
            {
                return null;
            }

            bytes.CopyTo(edid, block * BlockLength);
        }

        return edid;
    }

    /// <summary>The <c>Device Parameters</c> key under <c>HKLM</c> for a monitor interface path.</summary>
    /// <param name="devicePath">The path, such as <c>\\?\DISPLAY#BOE0CFF#4&amp;...#{guid}</c>.</param>
    /// <returns>The key path, or null when the path is not a monitor interface path.</returns>
    internal static string? DeviceParameters(string devicePath)
    {
        var parts = devicePath.Split('#');
        if (parts.Length != 4 || !string.Equals(parts[0], @"\\?\DISPLAY", StringComparison.OrdinalIgnoreCase)
                              || parts[1].Length == 0 || parts[2].Length == 0
                              || parts[1].Contains('\\') || parts[2].Contains('\\'))
        {
            return null;
        }

        return @"SYSTEM\CurrentControlSet\Enum\DISPLAY\" + parts[1] + "\\" + parts[2] + @"\Device Parameters";
    }

    private static string OverridePath(string devicePath)
    {
        ArgumentNullException.ThrowIfNull(devicePath);
        return DeviceParameters(devicePath) is { } path
            ? path + "\\" + OverrideKey
            : throw new ArgumentException("Not a monitor interface path.", nameof(devicePath));
    }
}
