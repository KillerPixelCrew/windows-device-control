using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;

namespace WindowsDeviceControl;

/// <summary>A refresh rate as the adapter expresses it, so a captured 59.94 Hz survives a round trip.</summary>
/// <param name="Numerator">Refresh numerator.</param>
/// <param name="Denominator">Refresh denominator; zero means "let Windows choose".</param>
public sealed record DisplayRefresh(uint Numerator, uint Denominator)
{
    /// <summary>Let Windows pick the rate for the requested mode.</summary>
    public static DisplayRefresh Default { get; } = new(0, 0);

    /// <summary>The rate in hertz, or zero when Windows is to choose.</summary>
    public double Hertz => Denominator == 0 ? 0 : (double)Numerator / Denominator;

    /// <summary>Builds a whole-hertz rate.</summary>
    /// <param name="hertz">Whole hertz; zero or negative selects the default rate.</param>
    /// <returns>The rate with denominator one, or Default for a nonpositive request.</returns>
    public static DisplayRefresh FromHertz(int hertz)
    {
        return hertz <= 0 ? Default : new DisplayRefresh((uint)hertz, 1);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Denominator == 0 ? "default" : Hertz.ToString("0.###", CultureInfo.InvariantCulture) + " Hz";
    }
}

/// <summary>One display in a layout. The output at (0,0) is the primary display, as it is in Windows.</summary>
/// <param name="Target">Which monitor this describes.</param>
/// <param name="X">Desktop x position of its top-left corner.</param>
/// <param name="Y">Desktop y position of its top-left corner.</param>
/// <param name="Width">Horizontal resolution in pixels.</param>
/// <param name="Height">Vertical resolution in pixels.</param>
/// <param name="Refresh">Refresh rate, or <see cref="DisplayRefresh.Default" />.</param>
/// <param name="Rotation">
///     Raw DISPLAYCONFIG_ROTATION value; 1 is landscape. Zero keeps the rotation the display runs at when
///     the layout is applied (landscape for a display that is off) and is never compared.
/// </param>
/// <param name="DpiPercent">Scaling request from 100 to 500, snapped to a supported step; null leaves it alone.</param>
/// <param name="Hdr">Advanced colour state to apply, or null to leave it alone.</param>
public sealed record DisplayLayoutOutput(
    DisplayTargetIdentity Target,
    int X,
    int Y,
    int Width,
    int Height,
    DisplayRefresh Refresh,
    uint Rotation = 1,
    int? DpiPercent = null,
    bool? Hdr = null)
{
    /// <summary>Whether this output is the primary display.</summary>
    public bool IsPrimary => X == 0 && Y == 0;
}

/// <summary>A complete desktop arrangement: exactly the displays that are on, and where.</summary>
/// <param name="Outputs">The active displays. Any target not listed is switched off.</param>
public sealed record DisplayLayout(IReadOnlyList<DisplayLayoutOutput> Outputs);

/// <summary>What one monitor looks like right now.</summary>
/// <param name="Target">Monitor identity.</param>
/// <param name="Available">Whether the adapter reports the monitor as connected.</param>
/// <param name="Active">Whether it is part of the current desktop.</param>
/// <param name="Current">Its current placement and mode, when it is active.</param>
public sealed record DisplayTargetObservation(
    DisplayTargetIdentity Target,
    bool Available,
    bool Active,
    DisplayLayoutOutput? Current);

/// <summary>Every monitor the adapter can see, plus a fingerprint of that observation.</summary>
/// <param name="Targets">One entry per known monitor.</param>
/// <param name="Fingerprint">
///     Stable text of monitor identity, availability, active state, position, resolution and refresh.
///     Rotation, scaling and HDR are not included. Equal fingerprints a moment apart help a caller
///     wait for an arrival to settle; they do not guarantee that every display property is unchanged.
/// </param>
/// <param name="CapturedAt">When the observation completed.</param>
public sealed record DisplayArrangement(
    IReadOnlyList<DisplayTargetObservation> Targets,
    string Fingerprint,
    DateTimeOffset CapturedAt);

/// <summary>How a layout application ended.</summary>
public enum DisplayLayoutOutcome
{
    /// <summary>Validation passed, or Windows accepted an apply; the calling operation determines which.</summary>
    Applied,

    /// <summary>The topology already matched; requested scaling and HDR were still applied separately.</summary>
    AlreadyActive,

    /// <summary>One or more requested monitors are not connected. A waiting state, not a failure.</summary>
    TargetsAbsent,

    /// <summary>The layout itself does not describe a usable desktop.</summary>
    Invalid,

    /// <summary>Windows refused the configuration, or it could not be read, before anything changed.</summary>
    Rejected,

    /// <summary>Windows refused the apply; restoring the captured arrangement was attempted once, never retried.</summary>
    Refused
}

/// <summary>Why a layout was not applied.</summary>
public enum DisplayLayoutProblem
{
    /// <summary>The layout lists no display.</summary>
    NoDisplays,

    /// <summary>A resolution is outside 320x200 to 32768x32768.</summary>
    ResolutionOutOfRange,

    /// <summary>Not exactly one display sits at 0,0 as the primary.</summary>
    PrimaryNotAtOrigin,

    /// <summary>The same display is listed twice.</summary>
    DuplicateDisplay,

    /// <summary>Two displays overlap.</summary>
    Overlap,

    /// <summary>A scaling percentage is outside 100 to 500.</summary>
    ScalingOutOfRange,

    /// <summary>A display does not touch the others; Windows snaps a detached desktop.</summary>
    Detached,

    /// <summary>No path on the adapter reaches a display; see <see cref="DisplayLayoutResult.ProblemTarget" />.</summary>
    NoDisplayPath,

    /// <summary>No free source is left for a display; see <see cref="DisplayLayoutResult.ProblemTarget" />.</summary>
    NoFreeSource,

    /// <summary>The current configuration could not be read; see <see cref="DisplayLayoutResult.FailureMessage" />.</summary>
    ReadFailed,

    /// <summary>Windows rejected the layout during validation; see <see cref="DisplayLayoutResult.NativeStatus" />.</summary>
    ValidationRejected,

    /// <summary>The current arrangement could not be captured for rollback, so nothing was applied.</summary>
    RollbackCaptureFailed
}

/// <summary>Which per-display setting a layout warning is about.</summary>
public enum DisplayOutputWarningKind
{
    /// <summary>Advanced colour (HDR).</summary>
    Hdr,

    /// <summary>Scaling percentage.</summary>
    Scaling
}

/// <summary>A per-display setting that was not written after the arrangement itself was.</summary>
/// <param name="Target">The display.</param>
/// <param name="Kind">Which setting.</param>
/// <param name="Outcome">Why it was not written.</param>
/// <param name="NativeStatus">The native status of a refused write, or zero.</param>
public sealed record DisplayOutputWarning(
    DisplayTargetIdentity Target,
    DisplayOutputWarningKind Kind,
    DisplaySetOutcome Outcome,
    int NativeStatus);

/// <summary>Result of validating or applying a layout.</summary>
/// <param name="Outcome">Validation or apply disposition; Applied from Validate means no write occurred.</param>
/// <param name="Absent">The requested monitors that are not connected.</param>
/// <param name="NativeStatus">Win32/SetDisplayConfig status for a failed native stage, or zero.</param>
/// <param name="RollbackAttempted">Whether a refused application was rolled back.</param>
/// <param name="RollbackStatus">Topology rollback's SetDisplayConfig status; meaningful only when RollbackAttempted.</param>
/// <param name="Warnings">
///     Per-display settings not written after an Applied or AlreadyActive arrangement. Rollback extras
///     are best effort and their failures are not included in this list.
/// </param>
/// <param name="Problem">Why the layout was not applied, when the library knows a specific reason.</param>
/// <param name="ProblemTarget">The display <paramref name="Problem" /> concerns, when it concerns one.</param>
/// <param name="FailureMessage">The native failure text, when the current configuration could not be read.</param>
public sealed record DisplayLayoutResult(
    DisplayLayoutOutcome Outcome,
    IReadOnlyList<DisplayTargetIdentity> Absent,
    int NativeStatus,
    bool RollbackAttempted,
    int RollbackStatus,
    IReadOnlyList<DisplayOutputWarning> Warnings,
    DisplayLayoutProblem? Problem = null,
    DisplayTargetIdentity? ProblemTarget = null,
    string? FailureMessage = null)
{
    /// <summary>
    ///     Whether validation passed, an apply was accepted or topology already matched; depends on the calling
    ///     operation.
    /// </summary>
    public bool Applied => Outcome is DisplayLayoutOutcome.Applied or DisplayLayoutOutcome.AlreadyActive;

    /// <summary>Whether the topology rollback returned success; does not confirm scaling, HDR or physical visibility.</summary>
    public bool RollbackSucceeded => RollbackAttempted && RollbackStatus == 0;
}

/// <summary>
///     Captures, validates and applies complete desktop arrangements, including optional scaling and HDR.
/// </summary>
/// <remarks>
///     These calls block on display drivers; run them on a worker. Applying rearranges or blanks
///     displays. A requested monitor that is not connected is reported as absent. An accepted apply is
///     not read back; one Windows refuses gets
///     exactly one rollback, and nothing is retried automatically.
/// </remarks>
public static partial class DisplayLayouts
{
    internal const uint PathActiveFlag = 0x1;
    internal const uint InvalidModeIndex = 0xffffffff;
    internal const uint SourceModeType = 1;
    internal const uint Pixel32Bpp = 4;

    /// <summary>Observes every monitor the adapter can see, without changing anything.</summary>
    /// <returns>Readable monitor identities and their topology fingerprint; unreadable identities are omitted.</returns>
    /// <exception cref="Win32Exception">A CCD query failed.</exception>
    public static DisplayArrangement Observe()
    {
        return Observe([], out _);
    }

    /// <summary>
    ///     Observes every monitor, remembering target identities in <paramref name="read" /> and
    ///     returning the paths the observation came from, so a caller can plan on the same query.
    /// </summary>
    /// <param name="read">Per-observation cache of identities by target route, populated by this call.</param>
    /// <param name="paths">The complete native path array used to build the observation.</param>
    /// <returns>Readable target observations and their fingerprint; targets with unreadable identity are omitted.</returns>
    internal static DisplayArrangement Observe(
        Dictionary<DisplayTopology.RouteKey, DisplayTargetIdentity> read, out DisplayTopology.PathInfo[] paths)
    {
        (paths, var modes) = DisplayTopology.Query(DisplayTopology.AllPaths);
        List<DisplayTargetObservation> targets = [];
        foreach (var path in paths)
        {
            DisplayTargetIdentity identity;
            try
            {
                identity = DisplayTopology.ReadTarget(path, read);
            }
            catch (Win32Exception)
            {
                continue;
            }

            var active = (path.Flags & PathActiveFlag) != 0;
            AddObservation(targets, new DisplayTargetObservation(identity, path.TargetInfo.TargetAvailable != 0, active,
                active ? ReadOutput(path, modes, identity) : null));
        }

        return new DisplayArrangement(targets, Fingerprint(targets), DateTimeOffset.UtcNow);
    }

    /// <summary>
    ///     Combines alternative source routes to the same monitor. Connection is a target fact:
    ///     an unavailable inactive route must not hide another route reporting that monitor connected.
    /// </summary>
    /// <param name="targets">Observations accumulated from this query only.</param>
    /// <param name="observation">One route's observation; the active route supplies the current layout.</param>
    internal static void AddObservation(List<DisplayTargetObservation> targets, DisplayTargetObservation observation)
    {
        var index = targets.FindIndex(other => Same(other.Target, observation.Target));
        if (index < 0)
        {
            targets.Add(observation);
            return;
        }

        var previous = targets[index];
        var selected = observation.Active && !previous.Active ? observation : previous;
        targets[index] = selected with { Available = previous.Available || observation.Available };
    }

    /// <summary>
    ///     Whether two paths observe the same current monitor interface. Equal EDID serials on
    ///     distinct interfaces stay separate, so duplicated OEM serials cannot hide ambiguity.
    ///     Unidentified monitors fold by their current target route instead.
    /// </summary>
    private static bool Same(DisplayTargetIdentity first, DisplayTargetIdentity second)
    {
        return (first.DevicePath.Length > 0 && string.Equals(first.DevicePath, second.DevicePath,
                   StringComparison.OrdinalIgnoreCase))
               || (first.DevicePath.Length == 0 && second.DevicePath.Length == 0
                                                && first.AdapterLowPart == second.AdapterLowPart
                                                && first.AdapterHighPart == second.AdapterHighPart
                                                && first.TargetId == second.TargetId);
    }

    /// <summary>Captures the current desktop as an editable layout.</summary>
    /// <returns>
    ///     Active outputs with readable placement/mode data. Unreadable outputs are omitted; unavailable
    ///     scaling/HDR values are null. Capture can therefore be partial or empty.
    /// </returns>
    /// <exception cref="Win32Exception">A CCD query failed.</exception>
    public static DisplayLayout Capture()
    {
        return new DisplayLayout(
        [
            .. Observe().Targets.Where(target => target is { Active: true, Current: not null })
                .Select(target => target.Current!)
        ]);
    }

    /// <summary>Checks pure layout rules without accessing Windows or requiring connected monitors.</summary>
    /// <param name="layout">The layout to check.</param>
    /// <returns>The first rule the layout breaks, or null when the layout is well formed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layout" /> is null.</exception>
    /// <remarks>A well-formed value can still fail the native validation performed by Validate.</remarks>
    public static DisplayLayoutProblem? Describe(DisplayLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return DisplayLayoutPlanner.Describe(layout);
    }

    /// <summary>Checks a layout against Windows without changing anything.</summary>
    /// <param name="layout">The layout to check.</param>
    /// <returns>Invalid, TargetsAbsent, Rejected, or Applied meaning "would apply".</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layout" /> is null.</exception>
    /// <remarks>Blocks on the driver. Validates topology only; optional scaling/HDR writes are not tested.</remarks>
    public static DisplayLayoutResult Validate(DisplayLayout layout)
    {
        return Run(layout, false);
    }

    /// <summary>Applies a layout once. Windows' acceptance is the result; nothing is read back.</summary>
    /// <param name="layout">The layout to apply.</param>
    /// <returns>The topology disposition, native failure/rollback statuses and any forward extras warnings.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layout" /> is null.</exception>
    /// <remarks>
    ///     Serialized with every other display write in the process. The arrangement and any rollback are
    ///     saved to the Windows display database, so they survive a reboot; a caller that needs to undo an
    ///     apply applies the layout it captured before. This call blocks on the driver, so keep it off the
    ///     UI thread. AlreadyActive still applies requested scaling/HDR. A refusal triggers at most one
    ///     topology rollback; restored extras are best effort and their failures are not returned.
    /// </remarks>
    public static DisplayLayoutResult Apply(DisplayLayout layout)
    {
        lock (DisplayTopology.WriteGate)
        {
            return Run(layout, true);
        }
    }

    private static DisplayLayoutResult Run(DisplayLayout layout, bool apply)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (DisplayLayoutPlanner.Describe(layout) is { } invalid)
        {
            return Failed(DisplayLayoutOutcome.Invalid, invalid);
        }

        Dictionary<DisplayTopology.RouteKey, DisplayTargetIdentity> read = [];
        DisplayArrangement arrangement;
        DisplayTopology.PathInfo[] paths;
        try
        {
            arrangement = Observe(read, out paths);
        }
        catch (Win32Exception ex)
        {
            return Failed(DisplayLayoutOutcome.Rejected, DisplayLayoutProblem.ReadFailed, ex.NativeErrorCode,
                failureMessage: ex.Message);
        }

        IReadOnlyList<DisplayTargetIdentity> absent =
        [
            .. layout.Outputs.Select(output => output.Target)
                .Where(target => !arrangement.Targets.Any(other => other.Available && target.Matches(other.Target)))
        ];
        if (absent.Count != 0)
        {
            return new DisplayLayoutResult(DisplayLayoutOutcome.TargetsAbsent, absent, 0, false, 0, []);
        }

        if (apply && Matches(arrangement, layout))
        {
            var active = paths.Where(path => (path.Flags & PathActiveFlag) != 0).ToArray();
            return new DisplayLayoutResult(DisplayLayoutOutcome.AlreadyActive, [], 0, false, 0,
                ApplyExtras(layout.Outputs.Select(output => (output, Driving(active, read, output.Target)))));
        }

        var (planned, plannedModes, problem, problemTarget) =
            DisplayLayoutPlanner.Plan(paths, layout, path => DisplayTopology.ReadTarget(path, read));
        if (problem is { } unplanned)
        {
            return Failed(DisplayLayoutOutcome.Invalid, unplanned, problemTarget: problemTarget);
        }

        var status = DisplayTopology.Supply(planned, plannedModes, DisplayTopology.SdcValidate);
        if (status != 0)
        {
            return Failed(DisplayLayoutOutcome.Rejected, DisplayLayoutProblem.ValidationRejected, status);
        }

        if (!apply)
        {
            return new DisplayLayoutResult(DisplayLayoutOutcome.Applied, [], 0, false, 0, []);
        }

        // Capture topology and extras before mutation for the same rollback baseline.
        DisplayTopology.NativeSnapshot rollback;
        try
        {
            rollback = DisplayTopology.Query(DisplayTopology.OnlyActivePaths);
        }
        catch (Win32Exception ex)
        {
            return Failed(DisplayLayoutOutcome.Rejected, DisplayLayoutProblem.RollbackCaptureFailed,
                ex.NativeErrorCode);
        }

        List<(DisplayLayoutOutput Output, DisplayTopology.PathInfo? Path)> rollbackExtras = [];
        foreach (var path in rollback.Paths)
        {
            try
            {
                if (ReadOutput(path, rollback.Modes, DisplayTopology.ReadTarget(path)) is { } output)
                {
                    rollbackExtras.Add((output, path));
                }
            }
            catch (Win32Exception)
            {
                // A display that cannot be read gets its topology back but not its scaling or colour.
            }
        }

        status = DisplayTopology.Supply(planned, plannedModes,
            DisplayTopology.SdcApply | DisplayTopology.SaveToDatabase);
        if (status == 0)
        {
            // The planner put the outputs' paths first and in order, and they are the active paths now.
            return new DisplayLayoutResult(DisplayLayoutOutcome.Applied, [], 0, false, 0,
                ApplyExtras(layout.Outputs.Select((output, index) =>
                    (output, (DisplayTopology.PathInfo?)planned[index]))));
        }

        // A refusal can follow a partial driver change, so the captured arrangement goes back once.
        var rollbackStatus = DisplayTopology.Supply(rollback.Paths, rollback.Modes,
            DisplayTopology.SdcApply | DisplayTopology.SaveToDatabase);
        if (rollbackStatus == 0)
        {
            ApplyExtras(rollbackExtras);
        }

        return new DisplayLayoutResult(DisplayLayoutOutcome.Refused, [], status, true, rollbackStatus, []);
    }

    private static DisplayLayoutResult Failed(DisplayLayoutOutcome outcome, DisplayLayoutProblem problem,
        int nativeStatus = 0, DisplayTargetIdentity? problemTarget = null, string? failureMessage = null)
    {
        return new DisplayLayoutResult(outcome, [], nativeStatus, false, 0, [], problem, problemTarget,
            failureMessage);
    }

    /// <summary>The active path among <paramref name="active" /> that drives this monitor, if any.</summary>
    private static DisplayTopology.PathInfo? Driving(DisplayTopology.PathInfo[] active,
        Dictionary<DisplayTopology.RouteKey, DisplayTargetIdentity> read, DisplayTargetIdentity target)
    {
        DisplayTopology.PathInfo? found = null;
        foreach (var path in active)
        {
            try
            {
                if (target.Matches(DisplayTopology.ReadTarget(path, read)))
                {
                    if (found is not null)
                    {
                        return null;
                    }

                    found = path;
                }
            }
            catch (Win32Exception)
            {
            }
        }

        return found;
    }

    /// <summary>
    ///     Applies the per-display settings that are not part of the topology, on the path already
    ///     resolved for each display. A refusal here is a warning: the desktop is already arranged, and
    ///     undoing that would be worse.
    /// </summary>
    private static IReadOnlyList<DisplayOutputWarning> ApplyExtras(
        IEnumerable<(DisplayLayoutOutput Output, DisplayTopology.PathInfo? Path)> outputs)
    {
        List<DisplayOutputWarning> warnings = [];
        foreach (var (output, path) in outputs)
        {
            if (output.Hdr is { } hdr)
            {
                var result = path is { } colourPath
                    ? DisplayColor.SetHdr(colourPath.TargetInfo.AdapterId, colourPath.TargetInfo.Id, hdr)
                    : new DisplaySetResult(DisplaySetOutcome.NotActive, 0);
                if (!result.Succeeded)
                {
                    warnings.Add(new DisplayOutputWarning(output.Target, DisplayOutputWarningKind.Hdr, result.Outcome,
                        result.NativeStatus));
                }
            }

            if (output.DpiPercent is { } percent)
            {
                var result = path is { } scalePath
                    ? DisplayScaling.Set(scalePath.SourceInfo.AdapterId, scalePath.SourceInfo.Id, percent)
                    : new DisplayScaleResult(DisplaySetOutcome.NotActive, 0, DisplayScaling.Snap(percent));
                if (!result.Succeeded)
                {
                    warnings.Add(new DisplayOutputWarning(output.Target, DisplayOutputWarningKind.Scaling,
                        result.Outcome, result.NativeStatus));
                }
            }
        }

        return warnings;
    }

    /// <summary>
    ///     Whether the current arrangement already is this layout, within the tolerance a
    ///     captured rational refresh needs.
    /// </summary>
    /// <param name="arrangement">Observed active outputs with readable current modes.</param>
    /// <param name="layout">Requested complete desktop topology.</param>
    /// <returns>
    ///     Whether every active output matches placement, size, refresh tolerance and any requested rotation; HDR and DPI
    ///     are ignored.
    /// </returns>
    internal static bool Matches(DisplayArrangement arrangement, DisplayLayout layout)
    {
        var active = arrangement.Targets.Where(target => target is { Active: true, Current: not null }).ToArray();
        if (active.Length != layout.Outputs.Count)
        {
            return false;
        }

        foreach (var output in layout.Outputs)
        {
            var match = active.FirstOrDefault(target => output.Target.Matches(target.Target));
            if (match?.Current is not { } current
                || current.X != output.X || current.Y != output.Y
                || current.Width != output.Width || current.Height != output.Height
                || !SameRefresh(current.Refresh, output.Refresh)
                || (output.Rotation != 0 && current.Rotation != output.Rotation))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     A requested rate of "default" matches whatever is running; otherwise the observed rate
    ///     must land within half a hertz, because the adapter reports the exact rational it chose.
    /// </summary>
    /// <param name="observed">Actual rational refresh rate.</param>
    /// <param name="requested">Requested rate; a zero denominator accepts any observed rate.</param>
    /// <returns>True for the default request or an absolute difference strictly below 0.5 Hz.</returns>
    internal static bool SameRefresh(DisplayRefresh observed, DisplayRefresh requested)
    {
        return requested.Denominator == 0 || Math.Abs(observed.Hertz - requested.Hertz) < 0.5;
    }

    /// <summary>
    ///     Stable text of identity, availability, active state, placement, resolution and refresh.
    ///     Sorted by identity; deliberately excludes rotation, scaling and advanced colour.
    /// </summary>
    /// <param name="targets">Targets from one observation.</param>
    /// <returns>Deterministic topology text for change detection, not a complete layout equality test.</returns>
    internal static string Fingerprint(IEnumerable<DisplayTargetObservation> targets)
    {
        StringBuilder text = new();
        foreach (var target in targets
                     .OrderBy(target => Key(target.Target), StringComparer.OrdinalIgnoreCase))
        {
            text.Append(Key(target.Target)).Append(target.Available ? "|available" : "|absent")
                .Append(target.Active ? "|active" : "|off");
            if (target.Current is { } current)
            {
                text.Append(CultureInfo.InvariantCulture, $"|{current.X},{current.Y},{current.Width}x{current.Height}")
                    .Append(CultureInfo.InvariantCulture,
                        $"@{current.Refresh.Numerator}/{current.Refresh.Denominator}");
            }

            text.Append(';');
        }

        return text.ToString();
    }

    private static string Key(DisplayTargetIdentity target)
    {
        return target.DevicePath.Length != 0
            ? target.DevicePath
            : $"{target.EdidManufacturerId}-{target.EdidProductCodeId}-{target.FriendlyName}";
    }

    private static DisplayLayoutOutput? ReadOutput(
        DisplayTopology.PathInfo path, DisplayTopology.ModeInfo[] modes, DisplayTargetIdentity identity)
    {
        if (path.SourceInfo.ModeInfoIdx >= modes.Length)
        {
            return null;
        }

        var mode = modes[path.SourceInfo.ModeInfoIdx];
        if (mode.InfoType != SourceModeType)
        {
            return null;
        }

        var source = mode.Mode.Source;
        return new DisplayLayoutOutput(identity, source.X, source.Y, (int)source.Width, (int)source.Height,
            new DisplayRefresh(path.TargetInfo.RefreshRate.Numerator, path.TargetInfo.RefreshRate.Denominator),
            path.TargetInfo.Rotation,
            DisplayScaling.TryRead(path.SourceInfo.AdapterId, path.SourceInfo.Id, out var percent, out _, out _)
                ? percent
                : null,
            DisplayColor.TryRead(path.TargetInfo.AdapterId, path.TargetInfo.Id, out var enabled, out var supported) &&
            supported
                ? enabled
                : null);
    }
}
