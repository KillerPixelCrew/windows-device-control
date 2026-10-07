using System;
using System.Runtime.InteropServices;

namespace WindowsDeviceControl;

/// <summary>Reads and writes advanced colour (HDR) on the active CCD target matching a monitor identity.</summary>
/// <remarks>Calls block on Windows; use a worker thread. Support/current state are write inputs, not readback.</remarks>
public static partial class DisplayColor
{
    private const int GetAdvancedColorInfo = 9;
    private const int SetAdvancedColorState = 10;
    private const uint SupportedBit = 0x1;
    private const uint EnabledBit = 0x2;

    /// <summary>Reads whether a monitor supports advanced colour and whether it is on.</summary>
    /// <param name="target">Monitor identity.</param>
    /// <param name="enabled">Whether advanced colour is on; false on a failed read.</param>
    /// <param name="supported">Whether the monitor reports support; false on a failed read.</param>
    /// <returns>True for a readable active target, including unsupported HDR; false for an absent/unreadable target.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target" /> is null.</exception>
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
    /// <exception cref="ArgumentNullException"><paramref name="target" /> is null.</exception>
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
    /// <param name="adapter">Adapter LUID for the resolved target route.</param>
    /// <param name="id">Target ID on that adapter.</param>
    /// <param name="enabled">Requested advanced-color state.</param>
    /// <returns>The preflight outcome or single write status; Written is acceptance without readback.</returns>
    internal static DisplaySetResult SetHdr(DisplayTopology.Luid adapter, uint id, bool enabled)
    {
        lock (DisplayTopology.WriteGate)
        {
            if (!TryRead(adapter, id, out var current, out var supported))
            {
                return new DisplaySetResult(DisplaySetOutcome.Unreadable, 0);
            }

            if (!supported)
            {
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

    /// <summary>Reads advanced-color state on an already-resolved target route.</summary>
    /// <param name="adapter">Target adapter LUID.</param>
    /// <param name="id">Target ID.</param>
    /// <param name="enabled">Current enabled flag; false when the read fails.</param>
    /// <param name="supported">Current supported flag; false when the read fails.</param>
    /// <returns>Whether the native packet was read; unsupported is a successful read with supported false.</returns>
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
