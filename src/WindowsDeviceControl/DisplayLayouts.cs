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

    /// <summary>Builds a whole-hertz rate.</summary>
    /// <param name="hertz">Refresh rate in hertz.</param>
    /// <returns>The rate as a rational.</returns>
    public static DisplayRefresh FromHertz(int hertz) =>
        hertz <= 0 ? Default : new((uint)hertz, 1);

    /// <summary>The rate in hertz, or zero when Windows is to choose.</summary>
    public double Hertz => Denominator == 0 ? 0 : (double)Numerator / Denominator;

    /// <inheritdoc />
    public override string ToString() =>
        Denominator == 0 ? "default" : Hertz.ToString("0.###", CultureInfo.InvariantCulture) + " Hz";
}

/// <summary>One display in a layout. The output at (0,0) is the primary display, as it is in Windows.</summary>
/// <param name="Target">Which monitor this describes.</param>
/// <param name="X">Desktop x position of its top-left corner.</param>
/// <param name="Y">Desktop y position of its top-left corner.</param>
/// <param name="Width">Horizontal resolution in pixels.</param>
/// <param name="Height">Vertical resolution in pixels.</param>
/// <param name="Refresh">Refresh rate, or <see cref="DisplayRefresh.Default"/>.</param>
/// <param name="Rotation">Raw DISPLAYCONFIG_ROTATION value; 1 is landscape.</param>
/// <param name="DpiPercent">Scaling percentage to apply, or null to leave it alone.</param>
/// <param name="Hdr">Advanced colour state to apply, or null to leave it alone.</param>
public sealed record DisplayLayoutOutput(
    DisplayTargetIdentity Target, int X, int Y, int Width, int Height, DisplayRefresh Refresh,
    uint Rotation = 1, int? DpiPercent = null, bool? Hdr = null)
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
    DisplayTargetIdentity Target, bool Available, bool Active, DisplayLayoutOutput? Current);

/// <summary>Every monitor the adapter can see, plus a fingerprint of that observation.</summary>
/// <param name="Targets">One entry per known monitor.</param>
/// <param name="Fingerprint">Stable text that changes whenever the observation changes. Two equal
/// fingerprints a moment apart are what a caller waits for before acting on an arrival.</param>
/// <param name="CapturedAt">When the observation completed.</param>
public sealed record DisplayArrangement(
    IReadOnlyList<DisplayTargetObservation> Targets, string Fingerprint, DateTimeOffset CapturedAt);

/// <summary>How a layout application ended.</summary>
public enum DisplayLayoutOutcome
{
    /// <summary>Applied and confirmed by readback.</summary>
    Applied,
    /// <summary>The arrangement already matched; nothing was written.</summary>
    AlreadyActive,
    /// <summary>One or more requested monitors are not connected. A waiting state, not a failure.</summary>
    TargetsAbsent,
    /// <summary>The layout itself does not describe a usable desktop.</summary>
    Invalid,
    /// <summary>Windows refused the configuration before anything changed.</summary>
    Rejected,
    /// <summary>Windows accepted it but the readback did not match. Rolled back once, never retried.</summary>
    Unconfirmed,
}

/// <summary>Result of validating or applying a layout.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Absent">The requested monitors that are not connected.</param>
/// <param name="NativeStatus">SetDisplayConfig status for the failed stage, or zero.</param>
/// <param name="RollbackAttempted">Whether an unconfirmed application was rolled back.</param>
/// <param name="RollbackSucceeded">Whether that rollback returned success.</param>
/// <param name="Warnings">Non-fatal problems, such as a refused HDR or scaling write.</param>
/// <param name="Detail">Bounded diagnostic suitable for a log or UI.</param>
public sealed record DisplayLayoutResult(
    DisplayLayoutOutcome Outcome,
    IReadOnlyList<DisplayTargetIdentity> Absent,
    int NativeStatus,
    bool RollbackAttempted,
    bool RollbackSucceeded,
    IReadOnlyList<string> Warnings,
    string Detail)
{
    /// <summary>Whether the desktop now matches the layout.</summary>
    public bool Applied => Outcome is DisplayLayoutOutcome.Applied or DisplayLayoutOutcome.AlreadyActive;
}

/// <summary>Captures and applies complete desktop arrangements by value.
///
/// <see cref="DisplayTopology"/> replays a captured native configuration, which is enough to restore
/// what was there and nothing else. This is the editable form: which monitors are on, which is
/// primary, where each sits, its mode, its scaling and its advanced colour state, all as values a
/// person can be shown and change. Nobody hand-authors a <c>DISPLAYCONFIG_*</c> record.</summary>
/// <remarks>
/// These calls block on display drivers; run them on a worker. Applying rearranges or blanks
/// displays. A requested monitor that is not connected is reported as absent rather than thrown, so
/// a caller can wait for it. An unconfirmed application gets exactly one rollback and is never
/// retried automatically.
/// </remarks>
public static class DisplayLayouts
{
    private const uint OnlyActivePaths = 0x2;
    private const uint AllPaths = 0x1;
    private const uint UseSupplied = 0x20;
    private const uint SdcValidate = 0x40;
    private const uint SdcApply = 0x80;
    private const uint SaveToDatabase = 0x200;
    private const uint AllowChanges = 0x400;
    private const uint PathActive = 0x1;
    private const uint ModeInfoIdxInvalid = 0xffffffff;
    private const uint SourceModeInfo = 1;
    private const uint TargetModeInfo = 2;
    private const uint PixelFormat32Bpp = 5;
    private const int GetSourceName = 1;
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>Observes every monitor the adapter can see, without changing anything.</summary>
    /// <returns>The observation and its fingerprint.</returns>
    /// <exception cref="Win32Exception">A CCD query failed.</exception>
    public static DisplayArrangement Observe()
    {
        (DisplayTopology.PathInfo[] paths, DisplayTopology.ModeInfo[] modes) = Query(AllPaths);
        List<DisplayTargetObservation> targets = [];
        foreach (DisplayTopology.PathInfo path in paths)
        {
            DisplayTargetIdentity identity;
            try { identity = ReadTarget(path); }
            // One unreadable target must not hide the rest: a monitor can drop out between the
            // query and the name read, and the caller is often waiting for a different one.
            catch (Win32Exception) { continue; }
            bool active = (path.Flags & PathActive) != 0;
            if (targets.Exists(other => other.Target.Matches(identity) && (other.Active || !active))) { continue; }
            targets.RemoveAll(other => other.Target.Matches(identity));
            targets.Add(new(identity, path.TargetInfo.TargetAvailable != 0, active,
                active ? ReadOutput(path, modes, identity) : null));
        }
        return new(targets, Fingerprint(targets), DateTimeOffset.UtcNow);
    }

    /// <summary>Captures the current desktop as an editable layout.</summary>
    /// <returns>The active displays, their placement, modes, scaling and advanced colour state.</returns>
    /// <exception cref="Win32Exception">A CCD query failed.</exception>
    public static DisplayLayout Capture() => new(
        [.. Observe().Targets.Where(target => target is { Active: true, Current: not null })
            .Select(target => target.Current!)]);

    /// <summary>Why this layout could never describe a desktop, or null when it could.
    ///
    /// Pure, and it touches no display, so an editor can refuse a layout as it is typed and a
    /// stored layout can be checked while the monitors it names are unplugged. <see
    /// cref="Validate"/> answers the separate question of whether Windows would accept it now.
    /// </summary>
    /// <param name="layout">The layout to check.</param>
    /// <returns>A user-facing reason, or null when the layout is well formed.</returns>
    public static string? Describe(DisplayLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return DisplayLayoutPlanner.Describe(layout);
    }

    /// <summary>Reads a profile captured by <see cref="DisplayTopology.CaptureProfile"/> back as an
    /// editable layout.
    ///
    /// A profile is a native configuration meant to be replayed, not read; this is the one-way trip
    /// out of that form, for callers migrating stored profiles to layouts. Scaling and advanced
    /// colour are left unset because the profile never recorded them, and reading them now would
    /// describe today's desktop rather than the captured one.</summary>
    /// <param name="profile">A previously captured profile.</param>
    /// <returns>The layout, or null when the profile cannot be read as one.</returns>
    public static DisplayLayout? FromProfile(DisplayProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        DisplayTopology.PathInfo[] paths;
        DisplayTopology.ModeInfo[] modes;
        try
        {
            paths = DisplayTopology.Decode<DisplayTopology.PathInfo>(profile.PathData);
            modes = DisplayTopology.Decode<DisplayTopology.ModeInfo>(profile.ModeData);
        }
        catch (ArgumentException) { return null; }
        if (paths.Length != profile.Targets.Count) { return null; }

        List<DisplayLayoutOutput> outputs = [];
        for (int index = 0; index < paths.Length; index++)
        {
            DisplayTopology.PathInfo path = paths[index];
            if ((path.Flags & PathActive) == 0 || path.SourceInfo.ModeInfoIdx >= modes.Length) { continue; }
            DisplayTopology.ModeInfo mode = modes[path.SourceInfo.ModeInfoIdx];
            if (mode.InfoType != SourceModeInfo) { continue; }
            DisplayTargetIdentity identity = profile.Targets[index];
            if (outputs.Exists(other => other.Target.Matches(identity))) { continue; }
            DisplayTopology.SourceMode source = mode.Mode.Source;
            outputs.Add(new(identity, source.X, source.Y, (int)source.Width, (int)source.Height,
                new(path.TargetInfo.RefreshRate.Numerator, path.TargetInfo.RefreshRate.Denominator),
                path.TargetInfo.Rotation));
        }
        DisplayLayout layout = new(outputs);
        return Describe(layout) is null ? layout : null;
    }

    /// <summary>Checks a layout against Windows without changing anything.</summary>
    /// <param name="layout">The layout to check.</param>
    /// <returns>Invalid, TargetsAbsent, Rejected, or Applied meaning "would apply".</returns>
    public static DisplayLayoutResult Validate(DisplayLayout layout) => Run(layout, apply: false);

    /// <summary>Applies a layout and confirms it by readback.</summary>
    /// <param name="layout">The layout to apply.</param>
    /// <returns>What happened, including any rollback.</returns>
    public static DisplayLayoutResult Apply(DisplayLayout layout) => Run(layout, apply: true);

    private static DisplayLayoutResult Run(DisplayLayout layout, bool apply)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (DisplayLayoutPlanner.Describe(layout) is { } invalid)
        {
            return new(DisplayLayoutOutcome.Invalid, [], 0, false, false, [], invalid);
        }

        DisplayArrangement arrangement;
        DisplayTopology.PathInfo[] paths;
        DisplayTopology.ModeInfo[] modes;
        try
        {
            arrangement = Observe();
            (paths, modes) = Query(AllPaths);
        }
        catch (Win32Exception ex)
        {
            return new(DisplayLayoutOutcome.Rejected, [], ex.NativeErrorCode, false, false, [],
                Bound("The current display configuration could not be read: " + ex.Message));
        }

        IReadOnlyList<DisplayTargetIdentity> absent =
            [.. layout.Outputs.Select(output => output.Target)
                .Where(target => !arrangement.Targets.Any(other => other.Available && target.Matches(other.Target)))];
        if (absent.Count != 0)
        {
            return new(DisplayLayoutOutcome.TargetsAbsent, absent, 0, false, false, [],
                "Waiting for " + string.Join(", ", absent.Select(Describe)) + ".");
        }

        if (apply && Matches(arrangement, layout))
        {
            // Nothing to change. Reported rather than written, so compensating twice is harmless.
            return ApplyPerTarget(layout, DisplayLayoutOutcome.AlreadyActive, [],
                "The desktop already matches this layout.");
        }

        DisplayTopology.PathInfo[] planned;
        DisplayTopology.ModeInfo[] plannedModes;
        try
        {
            (planned, plannedModes) = DisplayLayoutPlanner.Plan(paths, modes, layout, ReadTarget);
        }
        catch (InvalidOperationException ex)
        {
            return new(DisplayLayoutOutcome.Invalid, [], 0, false, false, [], Bound(ex.Message));
        }

        int status = DisplayTopology.SetDisplayConfig((uint)planned.Length, planned,
            (uint)plannedModes.Length, plannedModes, UseSupplied | SdcValidate | AllowChanges);
        if (status != 0)
        {
            return new(DisplayLayoutOutcome.Rejected, [], status, false, false, [],
                $"Windows rejected this layout during validation (status {status}).");
        }
        if (!apply)
        {
            return new(DisplayLayoutOutcome.Applied, [], 0, false, false, [], "The layout is valid for this hardware.");
        }

        (DisplayTopology.PathInfo[] Paths, DisplayTopology.ModeInfo[] Modes) rollback;
        DisplayLayout rollbackLayout;
        try
        {
            rollback = Query(OnlyActivePaths);
            rollbackLayout = Capture();
        }
        catch (Win32Exception ex)
        {
            return new(DisplayLayoutOutcome.Rejected, [], ex.NativeErrorCode, false, false, [],
                "The current arrangement could not be captured for rollback; nothing was applied.");
        }

        status = DisplayTopology.SetDisplayConfig((uint)planned.Length, planned,
            (uint)plannedModes.Length, plannedModes, UseSupplied | SdcApply | SaveToDatabase | AllowChanges);
        if (status == 0 && Confirm(layout))
        {
            return ApplyPerTarget(layout, DisplayLayoutOutcome.Applied, [], "Layout applied and confirmed.");
        }

        int rollbackStatus = DisplayTopology.SetDisplayConfig((uint)rollback.Paths.Length, rollback.Paths,
            (uint)rollback.Modes.Length, rollback.Modes, UseSupplied | SdcApply | SaveToDatabase | AllowChanges);
        if (rollbackStatus == 0)
        {
            // Scaling and colour follow the topology back, so the desktop is left as it was found.
            foreach (DisplayLayoutOutput output in rollbackLayout.Outputs) { ApplyOutputExtras(output); }
        }
        return new(DisplayLayoutOutcome.Unconfirmed, [], status, true, rollbackStatus == 0, [],
            rollbackStatus == 0
                ? "The layout was not confirmed; the previous arrangement was restored."
                : $"The layout was not confirmed and the rollback failed with status {rollbackStatus}.");
    }

    /// <summary>Applies the per-display settings that are not part of the topology. A refusal here
    /// is a warning: the desktop is already arranged, and undoing that would be worse.</summary>
    private static DisplayLayoutResult ApplyPerTarget(
        DisplayLayout layout, DisplayLayoutOutcome outcome, IReadOnlyList<DisplayTargetIdentity> absent, string detail)
    {
        List<string> warnings = [];
        foreach (DisplayLayoutOutput output in layout.Outputs)
        {
            warnings.AddRange(ApplyOutputExtras(output));
        }
        return new(outcome, absent, 0, false, false, warnings, detail);
    }

    private static IReadOnlyList<string> ApplyOutputExtras(DisplayLayoutOutput output)
    {
        List<string> warnings = [];
        if (output.Hdr is { } hdr && !DisplayColor.TrySetHdr(output.Target, hdr, out string colourDetail))
        {
            warnings.Add($"{Describe(output.Target)}: {colourDetail}");
        }
        if (output.DpiPercent is { } percent && !DisplayScaling.TrySet(output.Target, percent, out string scaleDetail))
        {
            warnings.Add($"{Describe(output.Target)}: {scaleDetail}");
        }
        return warnings;
    }

    /// <summary>Whether the current arrangement already is this layout, within the tolerance a
    /// captured rational refresh needs.</summary>
    internal static bool Matches(DisplayArrangement arrangement, DisplayLayout layout)
    {
        var active = arrangement.Targets.Where(target => target is { Active: true, Current: not null }).ToArray();
        if (active.Length != layout.Outputs.Count) { return false; }
        foreach (DisplayLayoutOutput output in layout.Outputs)
        {
            DisplayTargetObservation? match = active.FirstOrDefault(target => output.Target.Matches(target.Target));
            if (match?.Current is not { } current
                || current.X != output.X || current.Y != output.Y
                || current.Width != output.Width || current.Height != output.Height
                || !SameRefresh(current.Refresh, output.Refresh))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>A requested rate of "default" matches whatever is running; otherwise the readback
    /// must land within half a hertz, because the adapter reports the exact rational it chose.</summary>
    internal static bool SameRefresh(DisplayRefresh observed, DisplayRefresh requested) =>
        requested.Denominator == 0 || Math.Abs(observed.Hertz - requested.Hertz) < 0.5;

    private static bool Confirm(DisplayLayout layout)
    {
        try { return Matches(Observe(), layout); }
        catch (Win32Exception) { return false; }
    }

    /// <summary>Stable text for one observation. Sorted by identity so two equal observations of a
    /// settled topology produce the same string.</summary>
    internal static string Fingerprint(IEnumerable<DisplayTargetObservation> targets)
    {
        StringBuilder text = new();
        foreach (DisplayTargetObservation target in targets
            .OrderBy(target => Key(target.Target), StringComparer.OrdinalIgnoreCase))
        {
            text.Append(Key(target.Target)).Append(target.Available ? "|available" : "|absent")
                .Append(target.Active ? "|active" : "|off");
            if (target.Current is { } current)
            {
                text.Append(CultureInfo.InvariantCulture, $"|{current.X},{current.Y},{current.Width}x{current.Height}")
                    .Append(CultureInfo.InvariantCulture, $"@{current.Refresh.Numerator}/{current.Refresh.Denominator}");
            }
            text.Append(';');
        }
        return text.ToString();
    }

    private static string Key(DisplayTargetIdentity target) => target.DevicePath.Length != 0
        ? target.DevicePath
        : $"{target.EdidManufacturerId}-{target.EdidProductCodeId}-{target.FriendlyName}";

    private static string Describe(DisplayTargetIdentity target) =>
        target.FriendlyName.Length != 0 ? target.FriendlyName : Key(target);

    private static unsafe DisplayTargetIdentity ReadTarget(DisplayTopology.PathInfo path)
    {
        DisplayTopology.TargetDeviceName target = new()
        {
            Header = new()
            {
                Type = 2,
                Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<DisplayTopology.TargetDeviceName>(),
                AdapterId = path.TargetInfo.AdapterId,
                Id = path.TargetInfo.Id,
            },
        };
        int status = DisplayTopology.DisplayConfigGetDeviceInfo(ref target);
        if (status != 0) { throw new Win32Exception(status, "Display target identity query failed."); }
        bool edidValid = (target.Flags & 0x2) != 0;
        return new(DisplayTopology.ReadNativeString(target.MonitorDevicePath, 128),
            edidValid ? target.EdidManufacturerId : null, edidValid ? target.EdidProductCodeId : null,
            DisplayTopology.ReadNativeString(target.MonitorFriendlyDeviceName, 64),
            path.TargetInfo.AdapterId.LowPart, path.TargetInfo.AdapterId.HighPart, path.TargetInfo.Id);
    }

    private static DisplayLayoutOutput? ReadOutput(
        DisplayTopology.PathInfo path, DisplayTopology.ModeInfo[] modes, DisplayTargetIdentity identity)
    {
        if (path.SourceInfo.ModeInfoIdx >= modes.Length) { return null; }
        DisplayTopology.ModeInfo mode = modes[path.SourceInfo.ModeInfoIdx];
        if (mode.InfoType != SourceModeInfo) { return null; }
        DisplayTopology.SourceMode source = mode.Mode.Source;
        return new(identity, source.X, source.Y, (int)source.Width, (int)source.Height,
            new(path.TargetInfo.RefreshRate.Numerator, path.TargetInfo.RefreshRate.Denominator),
            path.TargetInfo.Rotation,
            DisplayScaling.TryRead(identity, out int percent) ? percent : null,
            DisplayColor.TryReadHdr(identity, out bool enabled, out bool supported) && supported ? enabled : null);
    }

    private static (DisplayTopology.PathInfo[] Paths, DisplayTopology.ModeInfo[] Modes) Query(uint flags)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int status = DisplayTopology.GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount);
            if (status != 0) { throw new Win32Exception(status, "Display topology buffer sizing failed."); }
            if (pathCount > 256 || modeCount > 1024)
            {
                throw new InvalidOperationException("Display topology exceeds supported bounds.");
            }
            DisplayTopology.PathInfo[] paths = new DisplayTopology.PathInfo[pathCount];
            DisplayTopology.ModeInfo[] modes = new DisplayTopology.ModeInfo[modeCount];
            status = DisplayTopology.QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, 0);
            if (status == ErrorInsufficientBuffer) { continue; }
            if (status != 0) { throw new Win32Exception(status, "Display topology query failed."); }
            if (pathCount != paths.Length) { Array.Resize(ref paths, checked((int)pathCount)); }
            if (modeCount != modes.Length) { Array.Resize(ref modes, checked((int)modeCount)); }
            return (paths, modes);
        }
        throw new Win32Exception(ErrorInsufficientBuffer, "Display topology changed repeatedly during capture.");
    }

    private static string Bound(string value) => value.Length <= 512 ? value : value[..512];

    internal const uint PathActiveFlag = PathActive;
    internal const uint InvalidModeIndex = ModeInfoIdxInvalid;
    internal const uint SourceModeType = SourceModeInfo;
    internal const uint TargetModeType = TargetModeInfo;
    internal const uint Pixel32Bpp = PixelFormat32Bpp;
    internal const int SourceNameType = GetSourceName;
}
