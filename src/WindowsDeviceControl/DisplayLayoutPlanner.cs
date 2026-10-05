using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace WindowsDeviceControl;

/// <summary>
///     Turns an editable layout into the native configuration Windows is asked to apply.
///     Kept pure and separate from the CCD calls: choosing which path drives which monitor, which
///     source each takes and where the desktop rectangles sit is the part with rules worth testing, and
///     it can be tested on synthetic path arrays without a second monitor.
/// </summary>
internal static class DisplayLayoutPlanner
{
    /// <summary>Why this layout cannot describe a desktop, or null when it can.</summary>
    /// <param name="layout">The layout to check.</param>
    /// <returns>The first rule the layout breaks, or null.</returns>
    internal static DisplayLayoutProblem? Describe(DisplayLayout layout)
    {
        if (layout.Outputs is not { Count: > 0 })
        {
            return DisplayLayoutProblem.NoDisplays;
        }

        if (layout.Outputs.Any(output => output.Width < 320 || output.Height < 200
                                                            || output.Width > 32768 || output.Height > 32768))
        {
            return DisplayLayoutProblem.ResolutionOutOfRange;
        }

        if (layout.Outputs.Count(output => output.IsPrimary) != 1)
        {
            // Windows puts the primary display at the origin, so exactly one output must sit there.
            return DisplayLayoutProblem.PrimaryNotAtOrigin;
        }

        for (var index = 0; index < layout.Outputs.Count; index++)
        {
            for (var other = index + 1; other < layout.Outputs.Count; other++)
            {
                if (layout.Outputs[index].Target.Matches(layout.Outputs[other].Target))
                {
                    return DisplayLayoutProblem.DuplicateDisplay;
                }

                if (Overlaps(layout.Outputs[index], layout.Outputs[other]))
                {
                    return DisplayLayoutProblem.Overlap;
                }
            }
        }

        if (layout.Outputs.Any(output => output.DpiPercent is { } percent && (percent < 100 || percent > 500)))
        {
            return DisplayLayoutProblem.ScalingOutOfRange;
        }

        return Connected(layout) ? null : DisplayLayoutProblem.Detached;
    }

    private static bool Overlaps(DisplayLayoutOutput first, DisplayLayoutOutput second)
    {
        return first.X < second.X + second.Width && second.X < first.X + first.Width
                                                 && first.Y < second.Y + second.Height &&
                                                 second.Y < first.Y + first.Height;
    }

    /// <summary>Whether every display touches the arrangement built from the primary outwards.</summary>
    private static bool Connected(DisplayLayout layout)
    {
        List<DisplayLayoutOutput> reached = [layout.Outputs.First(output => output.IsPrimary)];
        var grew = true;
        while (grew)
        {
            grew = false;
            foreach (var candidate in layout.Outputs.Except(reached))
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

    private static bool Touches(DisplayLayoutOutput first, DisplayLayoutOutput second)
    {
        return first.X <= second.X + second.Width && second.X <= first.X + first.Width
                                                  && first.Y <= second.Y + second.Height &&
                                                  second.Y <= first.Y + first.Height;
    }

    /// <summary>Builds the paths and modes for one layout from the adapter's own path list.</summary>
    /// <param name="paths">Every path the adapter advertises, from a QDC_ALL_PATHS query.</param>
    /// <param name="layout">The layout to express.</param>
    /// <param name="readTarget">Reads a path's monitor identity.</param>
    /// <returns>
    ///     The configuration to supply to Windows, whose first paths follow the layout's outputs in order;
    ///     or, with empty arrays, the problem (<see cref="DisplayLayoutProblem.NoDisplayPath" /> or
    ///     <see cref="DisplayLayoutProblem.NoFreeSource" />) and the display it concerns.
    /// </returns>
    internal static (DisplayTopology.PathInfo[] Paths, DisplayTopology.ModeInfo[] Modes, DisplayLayoutProblem? Problem,
        DisplayTargetIdentity? ProblemTarget) Plan(
        DisplayTopology.PathInfo[] paths,
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

        foreach (var output in layout.Outputs)
        {
            var matching = candidates.Where(candidate => output.Target.Matches(candidate.Target!)).ToArray();
            if (matching.Length == 0)
            {
                return ([], [], DisplayLayoutProblem.NoDisplayPath, output.Target);
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
                return ([], [], DisplayLayoutProblem.NoFreeSource, output.Target);
            }

            takenSources.Add(SourceKey(chosen.Path));
            // Rotation 0 keeps the rotation the display runs at now. It is read from the path that drives it
            // before any flag below changes, as input to the write; a display that is off gets landscape.
            var current = matching.FirstOrDefault(candidate =>
                (candidate.Path.Flags & DisplayLayouts.PathActiveFlag) != 0);
            var rotation = output.Rotation != 0 ? output.Rotation
                : current.Target is not null && current.Path.TargetInfo.Rotation != 0
                    ? current.Path.TargetInfo.Rotation
                    : 1;

            var path = chosen.Path;
            path.Flags |= DisplayLayouts.PathActiveFlag;
            path.SourceInfo.ModeInfoIdx = (uint)plannedModes.Count;
            // The target mode is left to Windows: the requested refresh plus ALLOW_CHANGES is what
            // lets the driver pick a signal it can actually produce for this resolution.
            path.TargetInfo.ModeInfoIdx = DisplayLayouts.InvalidModeIndex;
            path.TargetInfo.RefreshRate = new DisplayTopology.Rational
            {
                Numerator = output.Refresh.Numerator,
                Denominator = output.Refresh.Denominator
            };
            // Inactive CCD paths have unspecified scan ordering. Pair the requested refresh with
            // progressive scan instead of replaying that placeholder (Windows rejects it with 87).
            path.TargetInfo.ScanLineOrdering = 1;
            path.TargetInfo.Rotation = rotation;
            plannedPaths.Add(path);

            plannedModes.Add(new DisplayTopology.ModeInfo
            {
                InfoType = DisplayLayouts.SourceModeType,
                Id = path.SourceInfo.Id,
                AdapterId = path.SourceInfo.AdapterId,
                Mode = new DisplayTopology.ModeUnion
                {
                    Source = new DisplayTopology.SourceMode
                    {
                        Width = (uint)output.Width,
                        Height = (uint)output.Height,
                        PixelFormat = DisplayLayouts.Pixel32Bpp,
                        X = output.X,
                        Y = output.Y
                    }
                }
            });
        }

        // Every other path is supplied inactive, which is how a supplied configuration says
        // "and switch these off" rather than leaving the previous desktop half in place.
        foreach (var candidate in candidates)
        {
            if (plannedPaths.Exists(planned => Same(planned, candidate.Path)))
            {
                continue;
            }

            var path = candidate.Path;
            path.Flags &= ~DisplayLayouts.PathActiveFlag;
            path.SourceInfo.ModeInfoIdx = DisplayLayouts.InvalidModeIndex;
            path.TargetInfo.ModeInfoIdx = DisplayLayouts.InvalidModeIndex;
            plannedPaths.Add(path);
        }

        return ([.. plannedPaths], [.. plannedModes], null, null);
    }

    private static bool Free(DisplayTopology.PathInfo path, HashSet<(uint, int, uint)> taken)
    {
        return !taken.Contains(SourceKey(path));
    }

    private static (uint, int, uint) SourceKey(DisplayTopology.PathInfo path)
    {
        return (path.SourceInfo.AdapterId.LowPart, path.SourceInfo.AdapterId.HighPart, path.SourceInfo.Id);
    }

    private static bool Same(DisplayTopology.PathInfo first, DisplayTopology.PathInfo second)
    {
        return first.TargetInfo.AdapterId.LowPart == second.TargetInfo.AdapterId.LowPart
               && first.TargetInfo.AdapterId.HighPart == second.TargetInfo.AdapterId.HighPart
               && first.TargetInfo.Id == second.TargetInfo.Id
               && SourceKey(first) == SourceKey(second);
    }

    private static DisplayTargetIdentity? Identity(
        DisplayTopology.PathInfo path, Func<DisplayTopology.PathInfo, DisplayTargetIdentity> readTarget)
    {
        try
        {
            return readTarget(path);
        }
        // A path whose name cannot be read is not one this layout can be built on, but it must not
        // stop the layout being built on the others.
        catch (Win32Exception)
        {
            return null;
        }
    }
}
