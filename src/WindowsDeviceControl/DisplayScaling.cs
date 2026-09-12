using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace WindowsDeviceControl;

/// <summary>Reads and writes one display's Windows scaling percentage.
///
/// Windows stores scaling as a step relative to the value it recommends for that display, not as a
/// percentage, and the available steps differ per display. The undocumented CCD packets are the
/// only way to reach it without a user opening the Settings app, so the relative step is converted
/// to and from a percentage here and callers work in percentages alone.</summary>
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
        return TryFindSource(target, out DisplayTopology.Luid adapter, out uint source)
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
        return TryFindSource(target, out DisplayTopology.Luid adapter, out uint source)
            && TryRead(adapter, source, out current, out recommended, out maximum);
    }

    /// <summary>Sets a display's scaling percentage and confirms it by readback.</summary>
    /// <param name="target">Monitor identity.</param>
    /// <param name="percent">Requested percentage; snapped to the nearest step this display offers.</param>
    /// <param name="detail">Set to why the write did not happen, when it did not.</param>
    /// <returns>True when the display now reports the requested step, or already did.</returns>
    public static bool TrySet(DisplayTargetIdentity target, int percent, out string detail)
    {
        ArgumentNullException.ThrowIfNull(target);
        detail = "";
        if (!TryFindSource(target, out DisplayTopology.Luid adapter, out uint source))
        {
            detail = "the display is not active, so its scaling was left alone";
            return false;
        }
        if (!TryRead(adapter, source, out int current, out int recommended, out int maximum))
        {
            detail = "its scaling could not be read";
            return false;
        }
        int wanted = Snap(Math.Min(percent, maximum));
        if (wanted == current) { return true; }

        int index = Array.IndexOf(Steps, wanted);
        int recommendedIndex = Array.IndexOf(Steps, recommended);
        if (index < 0 || recommendedIndex < 0)
        {
            detail = $"{percent}% is not a scaling step this display offers";
            return false;
        }
        DpiScaleSet packet = new()
        {
            Header = new()
            {
                Type = SetDpiScale,
                Size = (uint)Marshal.SizeOf<DpiScaleSet>(),
                AdapterId = adapter,
                Id = source,
            },
            ScaleRelative = index - recommendedIndex,
        };
        if (DisplayConfigSetDeviceInfo(ref packet) != 0)
        {
            detail = $"Windows refused the {wanted}% scaling change";
            return false;
        }
        if (TryRead(adapter, source, out int readback, out _, out _) && readback == wanted) { return true; }
        detail = "the scaling change was not confirmed";
        return false;
    }

    /// <summary>Snaps a percentage to the nearest step Windows offers.</summary>
    /// <param name="percent">Requested percentage.</param>
    /// <returns>The nearest supported step.</returns>
    public static int Snap(int percent) => Steps.MinBy(step => Math.Abs(step - percent));

    private static bool TryRead(
        DisplayTopology.Luid adapter, uint source, out int current, out int recommended, out int maximum)
    {
        current = recommended = maximum = 0;
        DpiScaleGet packet = new()
        {
            Header = new()
            {
                Type = GetDpiScale,
                Size = (uint)Marshal.SizeOf<DpiScaleGet>(),
                AdapterId = adapter,
                Id = source,
            },
        };
        if (DisplayConfigGetDeviceInfo(ref packet) != 0) { return false; }
        int relative = Math.Clamp(packet.CurrentScaleRelative, packet.MinScaleRelative, packet.MaxScaleRelative);
        int recommendedIndex = Math.Abs(packet.MinScaleRelative);
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

    /// <summary>Finds the CCD source currently driving one monitor. Scaling is a property of the
    /// source, so an inactive monitor has none.</summary>
    private static bool TryFindSource(DisplayTargetIdentity target, out DisplayTopology.Luid adapter, out uint source)
    {
        adapter = default;
        source = 0;
        try
        {
            DisplayTopologySnapshot snapshot = DisplayTopology.CaptureActive();
            ActiveDisplayPath? path = snapshot.Paths.FirstOrDefault(candidate => target.Matches(candidate.Target));
            if (path is null) { return false; }
            return TryFindSourceId(path.SourceName, out adapter, out source);
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private static unsafe bool TryFindSourceId(string sourceName, out DisplayTopology.Luid adapter, out uint source)
    {
        adapter = default;
        source = 0;
        int status = DisplayTopology.GetDisplayConfigBufferSizes(0x2, out uint pathCount, out uint modeCount);
        if (status != 0 || pathCount > 256 || modeCount > 1024) { return false; }
        DisplayTopology.PathInfo[] paths = new DisplayTopology.PathInfo[pathCount];
        DisplayTopology.ModeInfo[] modes = new DisplayTopology.ModeInfo[modeCount];
        if (DisplayTopology.QueryDisplayConfig(0x2, ref pathCount, paths, ref modeCount, modes, 0) != 0) { return false; }
        for (int index = 0; index < pathCount; index++)
        {
            DisplayTopology.SourceDeviceName name = new()
            {
                Header = new()
                {
                    Type = 1,
                    Size = (uint)Marshal.SizeOf<DisplayTopology.SourceDeviceName>(),
                    AdapterId = paths[index].SourceInfo.AdapterId,
                    Id = paths[index].SourceInfo.Id,
                },
            };
            if (DisplayTopology.DisplayConfigGetDeviceInfo(ref name) != 0) { continue; }
            if (!string.Equals(DisplayTopology.ReadNativeString(name.ViewGdiDeviceName, 32), sourceName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            adapter = paths[index].SourceInfo.AdapterId;
            source = paths[index].SourceInfo.Id;
            return true;
        }
        return false;
    }

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

    [LibraryImport("user32.dll")] private static partial int DisplayConfigGetDeviceInfo(ref DpiScaleGet packet);
    [LibraryImport("user32.dll")] private static partial int DisplayConfigSetDeviceInfo(ref DpiScaleSet packet);
}
