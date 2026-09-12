using System;
using System.Linq;
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
        if (!TryFind(target, out DisplayTopology.Luid adapter, out uint id)) { return false; }
        return TryRead(adapter, id, out enabled, out supported);
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
        if (!TryFind(target, out DisplayTopology.Luid adapter, out uint id))
        {
            detail = "the display is not active, so its colour state was left alone";
            return false;
        }
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
            Header = new()
            {
                Type = SetAdvancedColorState,
                Size = (uint)Marshal.SizeOf<AdvancedColorState>(),
                AdapterId = adapter,
                Id = id,
            },
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

    private static bool TryRead(DisplayTopology.Luid adapter, uint id, out bool enabled, out bool supported)
    {
        AdvancedColorInfo packet = new()
        {
            Header = new()
            {
                Type = GetAdvancedColorInfo,
                Size = (uint)Marshal.SizeOf<AdvancedColorInfo>(),
                AdapterId = adapter,
                Id = id,
            },
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

    /// <summary>Finds the current CCD route to one monitor. The route changes on hotplug, so it is
    /// resolved per call rather than stored.</summary>
    internal static bool TryFind(DisplayTargetIdentity target, out DisplayTopology.Luid adapter, out uint id)
    {
        adapter = default;
        id = 0;
        try
        {
            DisplayTopologySnapshot snapshot = DisplayTopology.CaptureActive();
            ActiveDisplayPath? path = snapshot.Paths.FirstOrDefault(candidate => target.Matches(candidate.Target));
            if (path is null) { return false; }
            adapter = new() { LowPart = path.Target.AdapterLowPart, HighPart = path.Target.AdapterHighPart };
            id = path.Target.TargetId;
            return true;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
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
