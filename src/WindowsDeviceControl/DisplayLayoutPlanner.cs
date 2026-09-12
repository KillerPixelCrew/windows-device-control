using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace WindowsDeviceControl;

/// <summary>Turns an editable layout into the native configuration Windows is asked to apply.
///
/// Kept pure and separate from the CCD calls: choosing which path drives which monitor, which
/// source each takes and where the desktop rectangles sit is the part with rules worth testing, and
/// it can be tested on synthetic path arrays without a second monitor.</summary>
internal static class DisplayLayoutPlanner
{
    /// <summary>Why this layout cannot describe a desktop, or null when it can.</summary>
    /// <param name="layout">The layout to check.</param>
    /// <returns>A user-facing reason, or null.</returns>
    internal static string? Describe(DisplayLayout layout)
    {
        if (layout.Outputs is not { Count: > 0 })
        {
            return "A layout needs at least one display.";
        }
        if (layout.Outputs.Count > 16)
        {
            return "A layout cannot hold more than 16 displays.";
        }
        if (layout.Outputs.Any(output => output.Width < 320 || output.Height < 200
            || output.Width > 32768 || output.Height > 32768))
        {
            return "Every display needs a resolution between 320x200 and 32768x32768.";
        }
        if (layout.Outputs.Count(output => output.IsPrimary) != 1)
        {
            // Windows puts the primary display at the origin, so exactly one output must sit there.
            return "Exactly one display must sit at 0,0 as the primary display.";
        }
        for (int index = 0; index < layout.Outputs.Count; index++)
        {
            for (int other = index + 1; other < layout.Outputs.Count; other++)
            {
                if (layout.Outputs[index].Target.Matches(layout.Outputs[other].Target))
                {
                    return "A layout cannot list the same display twice.";
                }
                if (Overlaps(layout.Outputs[index], layout.Outputs[other]))
                {
                    return "Displays cannot overlap.";
                }
            }
        }
        if (layout.Outputs.Any(output => output.DpiPercent is { } percent && (percent < 100 || percent > 500)))
        {
            return "Display scaling must be between 100 and 500 percent.";
        }
        return Connected(layout) ? null : "Every display must touch another one; Windows snaps a detached desktop.";
    }

    private static bool Overlaps(DisplayLayoutOutput first, DisplayLayoutOutput second) =>
        first.X < second.X + second.Width && second.X < first.X + first.Width
        && first.Y < second.Y + second.Height && second.Y < first.Y + first.Height;

    /// <summary>Whether every display touches the arrangement built from the primary outwards.</summary>
    private static bool Connected(DisplayLayout layout)
    {
        List<DisplayLayoutOutput> reached = [layout.Outputs.First(output => output.IsPrimary)];
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (DisplayLayoutOutput candidate in layout.Outputs.Except(reached))
            {
                if (reached.Any(other => Touches(candidate, other)))
                {
                    reached.Add(candidate);
                    grew = true;
                }
            }
        }
        return reached.Count == layout.Outputs.Count;
    }

    private static bool Touches(DisplayLayoutOutput first, DisplayLayoutOutput second) =>
        first.X <= second.X + second.Width && second.X <= first.X + first.Width
        && first.Y <= second.Y + second.Height && second.Y <= first.Y + first.Height;

    /// <summary>Builds the paths and modes for one layout from the adapter's own path list.</summary>
    /// <param name="paths">Every path the adapter advertises, from a QDC_ALL_PATHS query.</param>
    /// <param name="modes">The modes that query returned, used for the current source of a target.</param>
    /// <param name="layout">The layout to express.</param>
    /// <param name="readTarget">Reads a path's monitor identity.</param>
    /// <returns>The configuration to supply to Windows.</returns>
    /// <exception cref="InvalidOperationException">No usable path or source exists for a display.</exception>
    internal static (DisplayTopology.PathInfo[] Paths, DisplayTopology.ModeInfo[] Modes) Plan(
        DisplayTopology.PathInfo[] paths,
        DisplayTopology.ModeInfo[] modes,
        DisplayLayout layout,
        Func<DisplayTopology.PathInfo, DisplayTargetIdentity> readTarget)
    {
        var candidates = paths
            .Select((path, index) => (Path: path, Index: index, Target: Identity(path, readTarget)))
            .Where(candidate => candidate.Target is not null)
            .ToArray();

        List<DisplayTopology.PathInfo> plannedPaths = [];
        List<DisplayTopology.ModeInfo> plannedModes = [];
        HashSet<(uint Adapter, int High, uint Source)> takenSources = [];

        foreach (DisplayLayoutOutput output in layout.Outputs)
        {
            var matching = candidates.Where(candidate => output.Target.Matches(candidate.Target!)).ToArray();
            if (matching.Length == 0)
            {
                throw new InvalidOperationException(
                    $"No display path reaches {Name(output.Target)} on this adapter.");
            }
            // The path that already drives this monitor first: keeping its source avoids asking the
            // driver to re-route a display that is only moving or changing mode.
            var chosen = matching.FirstOrDefault(candidate =>
                (candidate.Path.Flags & DisplayLayouts.PathActiveFlag) != 0
                && Free(candidate.Path, takenSources));
            if (chosen.Target is null)
            {
                chosen = matching.Where(candidate => Free(candidate.Path, takenSources))
                    .OrderBy(candidate => candidate.Path.SourceInfo.Id)
                    .FirstOrDefault();
            }
            if (chosen.Target is null)
            {
                throw new InvalidOperationException(
                    $"No free display source is available for {Name(output.Target)}.");
            }
            takenSources.Add(SourceKey(chosen.Path));

            DisplayTopology.PathInfo path = chosen.Path;
            path.Flags |= DisplayLayouts.PathActiveFlag;
            path.SourceInfo.ModeInfoIdx = (uint)plannedModes.Count;
            // The target mode is left to Windows: the requested refresh plus ALLOW_CHANGES is what
            // lets the driver pick a signal it can actually produce for this resolution.
            path.TargetInfo.ModeInfoIdx = DisplayLayouts.InvalidModeIndex;
            path.TargetInfo.RefreshRate = new()
            {
                Numerator = output.Refresh.Numerator,
                Denominator = output.Refresh.Denominator,
            };
            path.TargetInfo.Rotation = output.Rotation == 0 ? 1 : output.Rotation;
            plannedPaths.Add(path);

            plannedModes.Add(new()
            {
                InfoType = DisplayLayouts.SourceModeType,
                Id = path.SourceInfo.Id,
                AdapterId = path.SourceInfo.AdapterId,
                Mode = new()
                {
                    Source = new()
                    {
                        Width = (uint)output.Width,
                        Height = (uint)output.Height,
                        PixelFormat = DisplayLayouts.Pixel32Bpp,
                        X = output.X,
                        Y = output.Y,
                    },
                },
            });
        }

        // Every other path is supplied inactive, which is how a supplied configuration says
        // "and switch these off" rather than leaving the previous desktop half in place.
        foreach (var candidate in candidates)
        {
            if (plannedPaths.Exists(planned => Same(planned, candidate.Path))) { continue; }
            DisplayTopology.PathInfo path = candidate.Path;
            path.Flags &= ~DisplayLayouts.PathActiveFlag;
            path.SourceInfo.ModeInfoIdx = DisplayLayouts.InvalidModeIndex;
            path.TargetInfo.ModeInfoIdx = DisplayLayouts.InvalidModeIndex;
            plannedPaths.Add(path);
        }

        _ = modes;
        return ([.. plannedPaths], [.. plannedModes]);
    }

    private static bool Free(DisplayTopology.PathInfo path, HashSet<(uint, int, uint)> taken) =>
        !taken.Contains(SourceKey(path));

    private static (uint, int, uint) SourceKey(DisplayTopology.PathInfo path) =>
        (path.SourceInfo.AdapterId.LowPart, path.SourceInfo.AdapterId.HighPart, path.SourceInfo.Id);

    private static bool Same(DisplayTopology.PathInfo first, DisplayTopology.PathInfo second) =>
        first.TargetInfo.AdapterId.LowPart == second.TargetInfo.AdapterId.LowPart
        && first.TargetInfo.AdapterId.HighPart == second.TargetInfo.AdapterId.HighPart
        && first.TargetInfo.Id == second.TargetInfo.Id
        && SourceKey(first) == SourceKey(second);

    private static DisplayTargetIdentity? Identity(
        DisplayTopology.PathInfo path, Func<DisplayTopology.PathInfo, DisplayTargetIdentity> readTarget)
    {
        try { return readTarget(path); }
        // A path whose name cannot be read is not one this layout can be built on, but it must not
        // stop the layout being built on the others.
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    private static string Name(DisplayTargetIdentity target) => target.FriendlyName.Length != 0
        ? target.FriendlyName
        : target.DevicePath.Length != 0
            ? target.DevicePath
            : string.Create(CultureInfo.InvariantCulture, $"target {target.TargetId}");
}
