using System;
using System.Runtime.InteropServices;

namespace WindowsDeviceControl;

/// <summary>Reads and writes a monitor's advanced colour (HDR) state.
///
/// Advanced colour belongs to the CCD target, not to its GDI source name, so it is addressed by the
/// same monitor identity the rest of this library uses. Support is re-read immediately before a
/// write: a monitor that reports no support must never be written to, and support can change when a
/// cable or a mode changes.</summary>
public static partial class DisplayColor
{
    private const int GetAdvancedColorInfo = 9;
    private const int SetAdvancedColorState = 10;
    private const uint SupportedBit = 0x1;
    private const uint EnabledBit = 0x2;

    /// <summary>Reads whether a monitor supports advanced colour and whether it is on.</summary>
    /// <param name="target">Monitor identity.</param>
    /// <param name="enabled">Set to whether advanced colour is currently on.</param>
    /// <param name="supported">Set to whether the monitor supports it at all.</param>
    /// <returns>True when the state could be read.</returns>
    public static bool TryReadHdr(DisplayTargetIdentity target, out bool enabled, out bool supported)
    {
        ArgumentNullException.ThrowIfNull(target);
        enabled = supported = false;
        return DisplayTopology.TryFindActive(target, out DisplayTopology.PathInfo path)
            && TryRead(path.TargetInfo.AdapterId, path.TargetInfo.Id, out enabled, out supported);
    }

    /// <summary>Sets a monitor's advanced colour state and confirms it by readback.</summary>
    /// <param name="target">Monitor identity.</param>
    /// <param name="enabled">The state to apply.</param>
    /// <param name="detail">Set to why the write did not happen, when it did not.</param>
    /// <returns>True when the monitor now reports the requested state, or already did.</returns>
    /// <remarks>A monitor that does not support advanced colour is left alone and reported, not
    /// written to. An uncertain write is never retried here.</remarks>
    public static bool TrySetHdr(DisplayTargetIdentity target, bool enabled, out string detail)
    {
        ArgumentNullException.ThrowIfNull(target);
        detail = "";
        if (!DisplayTopology.TryFindActive(target, out DisplayTopology.PathInfo path))
        {
            detail = "the display is not active, so its colour state was left alone";
            return false;
        }
        DisplayTopology.Luid adapter = path.TargetInfo.AdapterId;
        uint id = path.TargetInfo.Id;
        if (!TryRead(adapter, id, out bool current, out bool supported))
        {
            detail = "its colour state could not be read";
            return false;
        }
        if (!supported)
        {
            // Not a failure of the layout: the display simply has no HDR to turn on.
            detail = enabled ? "this display does not support HDR" : "";
            return !enabled;
        }
        if (current == enabled) { return true; }

        AdvancedColorState packet = new()
        {
            Header = DisplayTopology.Header<AdvancedColorState>(SetAdvancedColorState, adapter, id),
            EnableAdvancedColor = enabled ? 1u : 0u,
        };
        int status = DisplayConfigSetDeviceInfo(ref packet);
        if (status != 0)
        {
            detail = $"Windows refused the HDR change (status {status})";
            return false;
        }
        if (TryRead(adapter, id, out bool readback, out _) && readback == enabled) { return true; }
        detail = "the HDR change was not confirmed";
        return false;
    }

    internal static bool TryRead(DisplayTopology.Luid adapter, uint id, out bool enabled, out bool supported)
    {
        AdvancedColorInfo packet = new()
        {
            Header = DisplayTopology.Header<AdvancedColorInfo>(GetAdvancedColorInfo, adapter, id),
        };
        if (DisplayConfigGetDeviceInfo(ref packet) != 0)
        {
            enabled = supported = false;
            return false;
        }
        supported = (packet.Value & SupportedBit) != 0;
        enabled = (packet.Value & EnabledBit) != 0;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdvancedColorInfo
    {
        public DisplayTopology.DeviceInfoHeader Header;
        public uint Value;
        public uint ColorEncoding;
        public uint BitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdvancedColorState
    {
        public DisplayTopology.DeviceInfoHeader Header;
        public uint EnableAdvancedColor;
    }

    [LibraryImport("user32.dll")] private static partial int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo packet);
    [LibraryImport("user32.dll")] private static partial int DisplayConfigSetDeviceInfo(ref AdvancedColorState packet);
}
