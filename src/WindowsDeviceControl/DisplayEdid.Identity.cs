using System;
using System.Buffers.Binary;
using System.IO;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace WindowsDeviceControl;

public static partial class DisplayEdid
{
    /// <summary>Reads the cached EDID serial identity for an exact Windows monitor interface.</summary>
    /// <param name="devicePath">Monitor interface path from CCD discovery, including a remembered interface.</param>
    /// <returns>Manufacturer/product and serial identity, or null when unavailable or invalid.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="devicePath" /> is null.</exception>
    /// <remarks>Read-only registry access. Does not activate a monitor or use its connector as physical identity.</remarks>
    public static string? ReadIdentity(string devicePath)
    {
        ArgumentNullException.ThrowIfNull(devicePath);
        var parts = devicePath.Split('#');
        if (parts.Length != 4 || !string.Equals(parts[0], @"\\?\DISPLAY", StringComparison.OrdinalIgnoreCase)
                              || parts[1].Length == 0 || parts[2].Length == 0
                              || parts[1].Contains('\\') || parts[2].Contains('\\'))
        {
            return null;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\DISPLAY\" + parts[1] + "\\" + parts[2] + @"\Device Parameters");
            return key?.GetValue("EDID") is byte[] edid ? ParseIdentity(edid) : null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>Extracts serial identity only from a complete, checksum-valid EDID base block.</summary>
    /// <param name="edid">Raw monitor EDID bytes from Windows or a graphics driver; the base block must be complete.</param>
    /// <returns>
    ///     Manufacturer/product, numeric serial and optional textual serial, or null when the base block is invalid
    ///     or supplies no usable serial. Equal identities can still be duplicated by monitor firmware.
    /// </returns>
    /// <remarks>Pure parsing; performs no Windows query, native driver call or hardware write.</remarks>
    public static string? ParseIdentity(ReadOnlySpan<byte> edid)
    {
        if (edid.Length < 128 || edid[18] != 1 || !edid[..8].SequenceEqual(Magic) || !Checksum(edid[..128]))
        {
            return null;
        }

        var serial = BinaryPrimitives.ReadUInt32LittleEndian(edid.Slice(12, 4));
        string? text = null;
        for (var offset = 54; offset <= 108; offset += 18)
        {
            var descriptor = edid.Slice(offset, 18);
            if (descriptor[0] == 0 && descriptor[1] == 0 && descriptor[2] == 0 && descriptor[3] == 0xff)
            {
                text = Encoding.ASCII.GetString(descriptor.Slice(5, 13)).Trim('\0', '\n', '\r', ' ');
                if (text.Length == 0 || text.Trim('0').Length == 0)
                {
                    text = null;
                }

                break;
            }
        }

        return serial is 0 or uint.MaxValue && text is null
            ? null
            : Convert.ToHexString(edid.Slice(8, 4)) + ":" + serial.ToString("X8") + ":" + text;
    }
}
