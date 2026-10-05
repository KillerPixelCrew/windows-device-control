using System;
using System.ComponentModel;
using System.Linq;
using Xunit;
using static WindowsDeviceControl.Tests.TestFixtures;

namespace WindowsDeviceControl.Tests;

/// <summary>
///     Layout rules and planning, on synthetic paths. Nothing here touches a real display:
///     the planner is pure, which is what lets the awkward cases be covered at all.
/// </summary>
public sealed class DisplayLayoutTests
{
    private const uint Active = 0x1;
    private const uint InvalidIndex = 0xffffffff;

    private static DisplayLayoutOutput Output(DisplayTargetIdentity target,
        int x = 0, int y = 0, int width = 1920, int height = 1080, int hertz = 60)
    {
        return new DisplayLayoutOutput(target, x, y, width, height, DisplayRefresh.FromHertz(hertz));
    }

    /// <summary>One advertised path: a source that can drive a target, active or not.</summary>
    private static DisplayTopology.PathInfo Path(uint source, uint target, bool active = false)
    {
        return new DisplayTopology.PathInfo
        {
            SourceInfo = new DisplayTopology.PathSourceInfo { Id = source, ModeInfoIdx = InvalidIndex },
            TargetInfo = new DisplayTopology.PathTargetInfo
                { Id = target, ModeInfoIdx = InvalidIndex, TargetAvailable = 1, Rotation = 1 },
            Flags = active ? Active : 0
        };
    }

    private static Func<DisplayTopology.PathInfo, DisplayTargetIdentity> Naming(
        params (uint Target, string Path)[] names)
    {
        return path => names.FirstOrDefault(entry => entry.Target == path.TargetInfo.Id) is { Path: not null } match
            ? Target(match.Path, $"Monitor {path.TargetInfo.Id}", path.TargetInfo.Id)
            : throw new Win32Exception(31, "unreadable target");
    }

    [Fact]
    public void ALayoutWithoutExactlyOnePrimaryIsRefused()
    {
        DisplayTargetIdentity first = Target(@"\\?\a"), second = Target(@"\\?\b", id: 2);

        Assert.Equal(DisplayLayoutProblem.NoDisplays, DisplayLayoutPlanner.Describe(new DisplayLayout([])));
        Assert.Equal(DisplayLayoutProblem.PrimaryNotAtOrigin, DisplayLayoutPlanner.Describe(
            new DisplayLayout([Output(first, 100), Output(second, 2020)])));
        Assert.Equal(DisplayLayoutProblem.PrimaryNotAtOrigin, DisplayLayoutPlanner.Describe(
            new DisplayLayout([Output(first), Output(second)])));
    }

    [Fact]
    public void OverlappingOrDetachedDisplaysAreRefused()
    {
        DisplayTargetIdentity first = Target(@"\\?\a"), second = Target(@"\\?\b", id: 2);

        Assert.Equal(DisplayLayoutProblem.Overlap, DisplayLayoutPlanner.Describe(
            new DisplayLayout([Output(first), Output(second, 1000)])));
        // Windows snaps a detached desktop back against the primary, so the readback would never
        // match what was asked for.
        Assert.Equal(DisplayLayoutProblem.Detached, DisplayLayoutPlanner.Describe(
            new DisplayLayout([Output(first), Output(second, 4000)])));
        Assert.Null(DisplayLayoutPlanner.Describe(new DisplayLayout([Output(first), Output(second, 1920)])));
    }

    [Fact]
    public void ADisplayListedTwiceOrScaledOutOfRangeIsRefused()
    {
        var target = Target(@"\\?\a");

        Assert.Equal(DisplayLayoutProblem.DuplicateDisplay, DisplayLayoutPlanner.Describe(
            new DisplayLayout([Output(target), Output(target, 1920)])));
        Assert.Equal(DisplayLayoutProblem.ScalingOutOfRange, DisplayLayoutPlanner.Describe(
            new DisplayLayout([Output(target) with { DpiPercent = 700 }])));
    }

    [Fact]
    public void AnInactiveTargetIsActivatedAndGivenTheDesktopRectangleItWasAsked()
    {
        DisplayTopology.PathInfo[] paths = [Path(0, 10), Path(1, 20)];
        var naming = Naming((10, @"\\?\tv"), (20, @"\\?\desk"));
        DisplayLayout layout = new([
            Output(Target(@"\\?\desk", id: 20), width: 2560, height: 1440),
            Output(Target(@"\\?\tv", id: 10), 2560, width: 3840, height: 2160, hertz: 120)
        ]);

        var (planned, modes, _, _) =
            DisplayLayoutPlanner.Plan(paths, layout, naming);

        Assert.Equal(2, modes.Length);
        Assert.All(planned.Take(2), path => Assert.NotEqual(0u, path.Flags & Active));
        Assert.Equal(2560u, modes[0].Mode.Source.Width);
        Assert.Equal(2560, modes[1].Mode.Source.X);
        Assert.Equal(3840u, modes[1].Mode.Source.Width);
        Assert.Equal(120u, planned[1].TargetInfo.RefreshRate.Numerator);
        Assert.All(planned.Take(2), path => Assert.Equal(1u, path.TargetInfo.ScanLineOrdering));
        // DISPLAYCONFIG_PIXELFORMAT_32BPP is 4; 5 means NONGDI.
        Assert.All(modes, mode => Assert.Equal(4u, mode.Mode.Source.PixelFormat));
        // The target mode is left for Windows to choose for the requested resolution and rate.
        Assert.All(planned.Take(2), path => Assert.Equal(InvalidIndex, path.TargetInfo.ModeInfoIdx));
    }

    [Fact]
    public void ATargetLeftOutOfTheLayoutIsSuppliedInactive()
    {
        DisplayTopology.PathInfo[] paths = [Path(0, 10, true), Path(1, 20, true)];
        var naming = Naming((10, @"\\?\keep"), (20, @"\\?\drop"));

        var (planned, _, _, _) = DisplayLayoutPlanner.Plan(
            paths, new DisplayLayout([Output(Target(@"\\?\keep", id: 10))]), naming);

        var dropped = planned.Single(path => path.TargetInfo.Id == 20);
        Assert.Equal(0u, dropped.Flags & Active);
        Assert.Equal(InvalidIndex, dropped.SourceInfo.ModeInfoIdx);
    }

    [Fact]
    public void TheSourceAlreadyDrivingADisplayIsKept()
    {
        // Two ways to reach the same monitor; the active one must win so a display that is only
        // moving is not re-routed.
        DisplayTopology.PathInfo[] paths = [Path(3, 10), Path(1, 10, true)];
        var naming = Naming((10, @"\\?\a"));

        var (planned, _, _, _) = DisplayLayoutPlanner.Plan(
            paths, new DisplayLayout([Output(Target(@"\\?\a", id: 10))]), naming);

        Assert.Equal(1u, planned[0].SourceInfo.Id);
    }

    [Fact]
    public void RotationZeroKeepsTheActiveRotationAndAnInactiveDisplayGetsLandscape()
    {
        var portrait = Path(1, 10, true);
        portrait.TargetInfo.Rotation = 2;
        DisplayTopology.PathInfo[] paths = [portrait, Path(2, 20)];
        var naming = Naming((10, @"\\?\a"), (20, @"\\?\b"));
        DisplayLayout layout = new([
            Output(Target(@"\\?\a", id: 10)) with { Rotation = 0 },
            Output(Target(@"\\?\b", id: 20), 1920) with { Rotation = 0 }
        ]);

        var (planned, _, _, _) = DisplayLayoutPlanner.Plan(paths, layout, naming);

        Assert.Equal(2u, planned[0].TargetInfo.Rotation);
        Assert.Equal(1u, planned[1].TargetInfo.Rotation);
    }

    [Fact]
    public void AnExplicitRotationIsWrittenAndComparedButZeroIsNot()
    {
        var target = Target(@"\\?\a");
        DisplayArrangement arrangement = new(
            [new DisplayTargetObservation(target, true, true, Output(target) with { Rotation = 2 })], "x",
            DateTimeOffset.UnixEpoch);
        var (planned, _, _, _) = DisplayLayoutPlanner.Plan([Path(1, 10, true)],
            new DisplayLayout([Output(Target(@"\\?\a", id: 10)) with { Rotation = 4 }]), Naming((10, @"\\?\a")));

        Assert.True(DisplayLayouts.Matches(arrangement, new DisplayLayout([Output(target) with { Rotation = 0 }])));
        Assert.True(DisplayLayouts.Matches(arrangement, new DisplayLayout([Output(target) with { Rotation = 2 }])));
        Assert.False(DisplayLayouts.Matches(arrangement, new DisplayLayout([Output(target)])));
        Assert.Equal(4u, planned[0].TargetInfo.Rotation);
    }

    [Fact]
    public void TwoDisplaysNeverShareOneSource()
    {
        // Both monitors advertise source 0; the second has to take its other option.
        DisplayTopology.PathInfo[] paths = [Path(0, 10), Path(0, 20), Path(1, 20)];
        var naming = Naming((10, @"\\?\a"), (20, @"\\?\b"));
        DisplayLayout layout = new([
            Output(Target(@"\\?\a", id: 10)),
            Output(Target(@"\\?\b", id: 20), 1920)
        ]);

        var (planned, _, _, _) = DisplayLayoutPlanner.Plan(paths, layout, naming);

        Assert.Equal(0u, planned[0].SourceInfo.Id);
        Assert.Equal(1u, planned[1].SourceInfo.Id);
    }

    [Fact]
    public void ADisplayWithNoPathAtAllIsReportedWithItsIdentity()
    {
        DisplayTopology.PathInfo[] paths = [Path(0, 10)];
        var missing = Target(@"\\?\missing", "Living room TV", 99);

        var (planned, _, problem, problemTarget) =
            DisplayLayoutPlanner.Plan(paths, new DisplayLayout([Output(missing)]), Naming((10, @"\\?\a")));

        Assert.Empty(planned);
        Assert.Equal(DisplayLayoutProblem.NoDisplayPath, problem);
        Assert.Same(missing, problemTarget);
    }

    [Fact]
    public void AnUnreadableTargetDoesNotStopTheOthers()
    {
        // One path's name query fails, which happens while a monitor is dropping out.
        DisplayTopology.PathInfo[] paths = [Path(0, 99), Path(1, 10)];
        var naming = Naming((10, @"\\?\a"));

        var (planned, _, _, _) = DisplayLayoutPlanner.Plan(
            paths, new DisplayLayout([Output(Target(@"\\?\a", id: 10))]), naming);

        Assert.Single(planned);
        Assert.Equal(10u, planned[0].TargetInfo.Id);
    }

    [Theory]
    // A captured rational rate must survive a round trip; a requested default matches anything.
    [InlineData(60000u, 1001u, 0u, 0u, true)]
    [InlineData(60000u, 1001u, 60u, 1u, true)]
    [InlineData(120u, 1u, 60u, 1u, false)]
    public void RefreshComparisonToleratesTheAdaptersOwnRational(
        uint observedNumerator, uint observedDenominator, uint requestedNumerator, uint requestedDenominator,
        bool expected)
    {
        Assert.Equal(expected, DisplayLayouts.SameRefresh(
            new DisplayRefresh(observedNumerator, observedDenominator),
            new DisplayRefresh(requestedNumerator, requestedDenominator)));
    }

    [Fact]
    public void TheFingerprintIgnoresObservationOrderAndChangesWithTheTopology()
    {
        DisplayTargetObservation first = new(Target(@"\\?\a"), true, true,
            Output(Target(@"\\?\a")));
        DisplayTargetObservation second = new(Target(@"\\?\b", id: 2), true, false, null);

        Assert.Equal(DisplayLayouts.Fingerprint([first, second]), DisplayLayouts.Fingerprint([second, first]));
        Assert.NotEqual(DisplayLayouts.Fingerprint([first, second]),
            DisplayLayouts.Fingerprint([first, second with { Available = false }]));
    }

    [Fact]
    public void AnArrangementMatchesOnlyWhenEveryDisplayAgrees()
    {
        var target = Target(@"\\?\a");
        DisplayArrangement arrangement = new(
            [new DisplayTargetObservation(target, true, true, Output(target))], "x", DateTimeOffset.UnixEpoch);

        Assert.True(DisplayLayouts.Matches(arrangement, new DisplayLayout([Output(target)])));
        Assert.False(DisplayLayouts.Matches(arrangement, new DisplayLayout([Output(target, width: 2560)])));
        Assert.False(DisplayLayouts.Matches(arrangement,
            new DisplayLayout([Output(target), Output(Target(@"\\?\b", id: 2), 1920)])));
    }

    [Fact]
    public void TheLayoutRulesAreReachableWithoutTouchingADisplay()
    {
        // An editor has to refuse a layout as it is typed, and a stored layout has to be checkable
        // while the monitors it names are unplugged. Neither can call Validate, which asks Windows.
        var target = Target(@"\\?\a");

        Assert.Null(DisplayLayouts.Describe(new DisplayLayout([Output(target)])));
        Assert.Equal(DisplayLayoutProblem.NoDisplays, DisplayLayouts.Describe(new DisplayLayout([])));
        Assert.Throws<ArgumentNullException>(() => DisplayLayouts.Describe(null!));
    }

    [Fact]
    public void ScalingSnapsToTheStepsWindowsOffers()
    {
        Assert.Equal(100, DisplayScaling.Snap(90));
        Assert.Equal(150, DisplayScaling.Snap(160));
        Assert.Equal(500, DisplayScaling.Snap(9000));
    }
}
