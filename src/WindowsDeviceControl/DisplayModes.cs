using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace WindowsDeviceControl;

/// <summary>A driver-advertised display mode, using the integer GDI refresh rate.</summary>
/// <param name="Width">Horizontal pixel count.</param>
/// <param name="Height">Vertical pixel count.</param>
/// <param name="RefreshHz">Refresh rate in hertz.</param>
public sealed record DisplayMode(int Width, int Height, int RefreshHz);

/// <summary>A fresh mode observation for one active, unambiguous display source.</summary>
/// <param name="Path">Target identity and source route.</param>
/// <param name="Current">Current mode.</param>
/// <param name="Supported">Driver modes passing validation at observation time.</param>
public sealed record DisplayModeSnapshot(ActiveDisplayPath Path, DisplayMode Current,
    IReadOnlyList<DisplayMode> Supported);

/// <summary>Enumerates and applies validated modes to an explicitly identified active display.</summary>
/// <remarks>Calls block on display drivers; use a worker thread. No registry settings are persisted.
/// Fresh identity checks reject disconnected, rerouted and cloned sources. Driver validation does
/// not prove physical visibility. An unconfirmed write gets one rollback to the captured mode,
/// only while the original route remains present; callers must not automatically retry.</remarks>
public static unsafe partial class DisplayModes
{
    private static readonly object Gate = new();
    private const uint ModeFields = 0x00040000 | 0x00080000 | 0x00100000 | 0x00400000;

    /// <summary>Reads current and supported modes from a fresh topology observation.</summary>
    /// <param name="target">Active target to query.</param>
    /// <returns>Null when the target is absent, ambiguous or unreadable.</returns>
    public static DisplayModeSnapshot? Read(DisplayTargetIdentity target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (Gate)
        {
            var path = Find(target);
            if (path is null || !ReadNative(path.SourceName, uint.MaxValue, out var current)) { return null; }
            List<DisplayMode> supported = [];
            for (uint index = 0; index < 4096 && ReadNative(path.SourceName, index, out var mode); index++)
            {
                if (mode.Width == 0 || mode.Height == 0 || mode.Frequency < 2 || mode.Bits != current.Bits) { continue; }
                mode.Fields = ModeFields;
                if (Change(path.SourceName, ref mode, 2) == 0) { supported.Add(Project(mode)); }
            }
            if (!SameRoute(path, Find(target))) { return null; }
            return new(path, Project(current), supported.Distinct().OrderBy(mode => mode.Width)
                .ThenBy(mode => mode.Height).ThenBy(mode => mode.RefreshHz).ToArray());
        }
    }

    /// <summary>Revalidates a selected mode, applies it once and confirms readback.</summary>
    /// <param name="observation">Observation from which the user selected the mode.</param>
    /// <param name="requested">Mode explicitly selected by the user.</param>
    /// <returns>Application and rollback results. No automatic retry is performed.</returns>
    public static DisplayProfileResult Apply(DisplayModeSnapshot observation, DisplayMode requested)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(requested);
        lock (Gate)
        {
            var path = Find(observation.Path.Target);
            if (!SameRoute(observation.Path, path) || !observation.Supported.Contains(requested))
            { return new(false, -2, false, false, "Display changed or the selected mode was not offered. Refresh and select again."); }
            if (!ReadNative(path!.SourceName, uint.MaxValue, out var original))
            { return new(false, -2, false, false, "Current display mode is unavailable."); }
            for (uint index = 0; index < 4096 && ReadNative(path.SourceName, index, out var mode); index++)
            {
                if (Project(mode) != requested || mode.Bits != original.Bits) { continue; }
                mode.Fields = ModeFields;
                int test = Change(path.SourceName, ref mode, 2);
                if (test != 0) { return new(false, test, false, false, "The display rejected mode validation."); }
                if (!SameRoute(path, Find(path.Target)))
                { return new(false, -2, false, false, "Display route changed before application."); }
                int status = Change(path.SourceName, ref mode, 0);
                bool same = SameRoute(path, Find(path.Target));
                if (status == 0 && same && ReadNative(path.SourceName, uint.MaxValue, out var readback)
                    && Project(readback) == requested)
                { return new(true, 0, false, false, "Display mode confirmed."); }
                original.Fields = ModeFields;
                bool rollback = same && Change(path.SourceName, ref original, 0) == 0
                    && ReadNative(path.SourceName, uint.MaxValue, out var restored) && Project(restored) == Project(original);
                return new(false, status, same, rollback, rollback
                    ? "Mode was not confirmed; the original mode was restored."
                    : "Mode was not confirmed; display recovery could not be verified.");
            }
            return new(false, -2, false, false, "The selected mode is no longer advertised.");
        }
    }

    private static ActiveDisplayPath? Find(DisplayTargetIdentity target)
    {
        var paths = DisplayTopology.CaptureActive().Paths;
        var matches = paths.Where(path => target.Matches(path.Target)).ToArray();
        return matches.Length == 1 && paths.Count(path => path.SourceName == matches[0].SourceName) == 1
            ? matches[0] : null;
    }

    private static bool SameRoute(ActiveDisplayPath expected, ActiveDisplayPath? current) =>
        current is not null && expected.Target == current.Target && expected.SourceName == current.SourceName;

    private static DisplayMode Project(NativeMode mode) => new((int)mode.Width, (int)mode.Height, (int)mode.Frequency);
    private static bool ReadNative(string source, uint index, out NativeMode mode)
    {
        mode = new() { Size = 220 };
        return EnumDisplaySettingsEx(source, index, ref mode, 0);
    }

    [StructLayout(LayoutKind.Explicit, Size = 220)]
    private struct NativeMode
    {
        [FieldOffset(68)] internal ushort Size;
        [FieldOffset(72)] internal uint Fields;
        [FieldOffset(168)] internal uint Bits;
        [FieldOffset(172)] internal uint Width;
        [FieldOffset(176)] internal uint Height;
        [FieldOffset(184)] internal uint Frequency;
    }

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplaySettingsExW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplaySettingsEx(string source, uint index, ref NativeMode mode, uint flags);

    private static int Change(string source, ref NativeMode mode, uint flags) =>
        ChangeDisplaySettingsEx(source, ref mode, 0, flags, 0);

    [LibraryImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ChangeDisplaySettingsEx(string source, ref NativeMode mode, nint window, uint flags, nint parameter);
}
