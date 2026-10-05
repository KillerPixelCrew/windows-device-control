using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace WindowsDeviceControl;

/// <summary>
///     Reads and writes one display's Windows scaling percentage.
///     Windows stores scaling as a step relative to the value it recommends for that display, not as a
///     percentage, and the available steps differ per display. The undocumented CCD packets are the
///     only way to reach it without a user opening the Settings app, so the relative step is converted
///     to and from a percentage here and callers work in percentages alone.
/// </summary>
public static partial class DisplayScaling
{
    private const int GetDpiScale = -3;
    private const int SetDpiScale = -4;

    /// <summary>The scaling steps Windows offers, in order. Index zero is 100 percent.</summary>
    private static readonly int[] Steps = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];

    /// <summary>Reads a display's current scaling percentage.</summary>
    /// <param name="target">Monitor identity.</param>
    /// <param name="percent">Set to the current percentage.</param>
    /// <returns>True when the value could be read.</returns>
    public static bool TryRead(DisplayTargetIdentity target, out int percent)
    {
        ArgumentNullException.ThrowIfNull(target);
        percent = 0;
        return TryFindSource(target, out var adapter, out var source)
               && TryRead(adapter, source, out percent, out _, out _);
    }

    /// <summary>Reads the scaling percentages a display supports.</summary>
    /// <param name="target">Monitor identity.</param>
    /// <param name="current">Set to the current percentage.</param>
    /// <param name="recommended">Set to the percentage Windows recommends.</param>
    /// <param name="maximum">Set to the highest percentage this display offers.</param>
    /// <returns>True when the values could be read.</returns>
    public static bool TryReadRange(DisplayTargetIdentity target, out int current, out int recommended, out int maximum)
    {
        ArgumentNullException.ThrowIfNull(target);
        current = recommended = maximum = 0;
        return TryFindSource(target, out var adapter, out var source)
               && TryRead(adapter, source, out current, out recommended, out maximum);
    }

    /// <summary>Sets a display's scaling percentage with one write.</summary>
    /// <param name="target">Monitor identity.</param>
    /// <param name="percent">Requested percentage; snapped to the nearest step this display offers.</param>
    /// <returns>
    ///     <see cref="DisplaySetOutcome.Written" /> when Windows accepted the write, which is not read back;
    ///     <see cref="DisplaySetOutcome.AlreadySet" /> when the display already ran at that step;
    ///     <see cref="DisplaySetOutcome.Unsupported" /> when the display's recommended step is not one this
    ///     library knows; otherwise why nothing was written. <see cref="DisplayScaleResult.Percent" /> is the
    ///     snapped step.
    /// </returns>
    /// <remarks>
    ///     The current and recommended steps are read first because the write is relative to the
    ///     recommended one. Serialized with every other display write in the process; a refusal is reported
    ///     with its native status and never retried here.
    /// </remarks>
    public static DisplayScaleResult Set(DisplayTargetIdentity target, int percent)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (DisplayTopology.WriteGate)
        {
            return DisplayTopology.FindActive(target, out var path) switch
            {
                ActiveLookup.Found => Set(path.SourceInfo.AdapterId, path.SourceInfo.Id, percent),
                ActiveLookup.NotActive => new DisplayScaleResult(DisplaySetOutcome.NotActive, 0, Snap(percent)),
                _ => new DisplayScaleResult(DisplaySetOutcome.Unreadable, 0, Snap(percent))
            };
        }
    }

    /// <summary>Sets the scaling of a source route the caller already resolved.</summary>
    internal static DisplayScaleResult Set(DisplayTopology.Luid adapter, uint source, int percent)
    {
        lock (DisplayTopology.WriteGate)
        {
            if (!TryRead(adapter, source, out var current, out var recommended, out var maximum))
            {
                return new DisplayScaleResult(DisplaySetOutcome.Unreadable, 0, Snap(percent));
            }

            var wanted = Snap(Math.Min(percent, maximum));
            if (wanted == current)
            {
                return new DisplayScaleResult(DisplaySetOutcome.AlreadySet, 0, wanted);
            }

            var index = Array.IndexOf(Steps, wanted);
            var recommendedIndex = Array.IndexOf(Steps, recommended);
            if (index < 0 || recommendedIndex < 0)
            {
                return new DisplayScaleResult(DisplaySetOutcome.Unsupported, 0, wanted);
            }

            DpiScaleSet packet = new()
            {
                Header = DisplayTopology.Header<DpiScaleSet>(SetDpiScale, adapter, source),
                ScaleRelative = index - recommendedIndex
            };
            var status = DisplayConfigSetDeviceInfo(ref packet);
            return new DisplayScaleResult(status == 0 ? DisplaySetOutcome.Written : DisplaySetOutcome.Refused,
                status, wanted);
        }
    }

    /// <summary>Snaps a percentage to the nearest step Windows offers.</summary>
    /// <param name="percent">Requested percentage.</param>
    /// <returns>The nearest supported step.</returns>
    public static int Snap(int percent)
    {
        return Steps.MinBy(step => Math.Abs(step - percent));
    }

    internal static bool TryRead(
        DisplayTopology.Luid adapter, uint source, out int current, out int recommended, out int maximum)
    {
        current = recommended = maximum = 0;
        DpiScaleGet packet = new()
        {
            Header = DisplayTopology.Header<DpiScaleGet>(GetDpiScale, adapter, source)
        };
        if (DisplayConfigGetDeviceInfo(ref packet) != 0)
        {
            return false;
        }

        var relative = Math.Clamp(packet.CurrentScaleRelative, packet.MinScaleRelative, packet.MaxScaleRelative);
        var recommendedIndex = Math.Abs(packet.MinScaleRelative);
        if (recommendedIndex + packet.MaxScaleRelative + 1 > Steps.Length
            || recommendedIndex + relative < 0 || recommendedIndex + relative >= Steps.Length)
        {
            return false;
        }

        current = Steps[recommendedIndex + relative];
        recommended = Steps[recommendedIndex];
        maximum = Steps[recommendedIndex + packet.MaxScaleRelative];
        return true;
    }

    /// <summary>
    ///     Finds the CCD source currently driving one monitor, taken from the same path that
    ///     matched it. Scaling is a property of the source, so an inactive monitor has none.
    /// </summary>
    private static bool TryFindSource(DisplayTargetIdentity target, out DisplayTopology.Luid adapter, out uint source)
    {
        var found = DisplayTopology.FindActive(target, out var path) == ActiveLookup.Found;
        adapter = path.SourceInfo.AdapterId;
        source = path.SourceInfo.Id;
        return found;
    }

    [LibraryImport("user32.dll")]
    private static partial int DisplayConfigGetDeviceInfo(ref DpiScaleGet packet);

    [LibraryImport("user32.dll")]
    private static partial int DisplayConfigSetDeviceInfo(ref DpiScaleSet packet);

    [StructLayout(LayoutKind.Sequential)]
    private struct DpiScaleGet
    {
        public DisplayTopology.DeviceInfoHeader Header;
        public int MinScaleRelative;
        public int CurrentScaleRelative;
        public int MaxScaleRelative;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DpiScaleSet
    {
        public DisplayTopology.DeviceInfoHeader Header;
        public int ScaleRelative;
    }
}
