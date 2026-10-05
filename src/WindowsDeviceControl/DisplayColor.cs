using System;
using System.Runtime.InteropServices;

namespace WindowsDeviceControl;

/// <summary>
///     Reads and writes a monitor's advanced colour (HDR) state.
///     Advanced colour belongs to the CCD target, not to its GDI source name, so it is addressed by the
///     same monitor identity the rest of this library uses. Support is read immediately before a write as
///     its input: a monitor that reports no support must never be written to, and support can change when a
///     cable or a mode changes. Nothing is read after a write to confirm it.
/// </summary>
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
        return DisplayTopology.FindActive(target, out var path) == ActiveLookup.Found
               && TryRead(path.TargetInfo.AdapterId, path.TargetInfo.Id, out enabled, out supported);
    }

    /// <summary>Sets a monitor's advanced colour state with one write.</summary>
    /// <param name="target">Monitor identity.</param>
    /// <param name="enabled">The state to apply.</param>
    /// <returns>
    ///     <see cref="DisplaySetOutcome.Written" /> when Windows accepted the write, which is not read back;
    ///     <see cref="DisplaySetOutcome.AlreadySet" /> when the monitor already had that state, or has no
    ///     advanced colour and off was requested; <see cref="DisplaySetOutcome.Unsupported" /> when on was
    ///     requested for a monitor without it; otherwise why nothing was written.
    /// </returns>
    /// <remarks>
    ///     Serialized with every other display write in the process. A refusal is reported with its
    ///     native status and never retried here.
    /// </remarks>
    public static DisplaySetResult SetHdr(DisplayTargetIdentity target, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (DisplayTopology.WriteGate)
        {
            return DisplayTopology.FindActive(target, out var path) switch
            {
                ActiveLookup.Found => SetHdr(path.TargetInfo.AdapterId, path.TargetInfo.Id, enabled),
                ActiveLookup.NotActive => new DisplaySetResult(DisplaySetOutcome.NotActive, 0),
                _ => new DisplaySetResult(DisplaySetOutcome.Unreadable, 0)
            };
        }
    }

    /// <summary>Sets the advanced colour state of a target route the caller already resolved.</summary>
    internal static DisplaySetResult SetHdr(DisplayTopology.Luid adapter, uint id, bool enabled)
    {
        lock (DisplayTopology.WriteGate)
        {
            // The support bit and current state are the write's input, not a confirmation of it.
            if (!TryRead(adapter, id, out var current, out var supported))
            {
                return new DisplaySetResult(DisplaySetOutcome.Unreadable, 0);
            }

            if (!supported)
            {
                // Not a failure of the caller: the display simply has no HDR to turn on.
                return new DisplaySetResult(enabled ? DisplaySetOutcome.Unsupported : DisplaySetOutcome.AlreadySet, 0);
            }

            if (current == enabled)
            {
                return new DisplaySetResult(DisplaySetOutcome.AlreadySet, 0);
            }

            AdvancedColorState packet = new()
            {
                Header = DisplayTopology.Header<AdvancedColorState>(SetAdvancedColorState, adapter, id),
                EnableAdvancedColor = enabled ? 1u : 0u
            };
            var status = DisplayConfigSetDeviceInfo(ref packet);
            return new DisplaySetResult(status == 0 ? DisplaySetOutcome.Written : DisplaySetOutcome.Refused, status);
        }
    }

    internal static bool TryRead(DisplayTopology.Luid adapter, uint id, out bool enabled, out bool supported)
    {
        AdvancedColorInfo packet = new()
        {
            Header = DisplayTopology.Header<AdvancedColorInfo>(GetAdvancedColorInfo, adapter, id)
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

    [LibraryImport("user32.dll")]
    private static partial int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo packet);

    [LibraryImport("user32.dll")]
    private static partial int DisplayConfigSetDeviceInfo(ref AdvancedColorState packet);

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
}
