using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Xunit;

namespace WindowsDeviceControl.Tests;

/// <summary>Layout rules and planning, on synthetic paths. Nothing here touches a real display:
/// the planner is pure, which is what lets the awkward cases be covered at all.</summary>
public sealed class DisplayLayoutTests
{
    private const uint Active = 0x1;
    private const uint InvalidIndex = 0xffffffff;

    private static DisplayTargetIdentity Target(string path, string name = "Monitor", uint id = 1) =>
        new(path, null, null, name, 0, 0, id);

    private static DisplayLayoutOutput Output(DisplayTargetIdentity target,
        int x = 0, int y = 0, int width = 1920, int height = 1080, int hertz = 60) =>
        new(target, x, y, width, height, DisplayRefresh.FromHertz(hertz));

    /// <summary>One advertised path: a source that can drive a target, active or not.</summary>
    private static DisplayTopology.PathInfo Path(uint source, uint target, bool active = false) => new()
    {
        SourceInfo = new() { Id = source, ModeInfoIdx = InvalidIndex },
        TargetInfo = new() { Id = target, ModeInfoIdx = InvalidIndex, TargetAvailable = 1, Rotation = 1 },
        Flags = active ? Active : 0,
    };

    private static Func<DisplayTopology.PathInfo, DisplayTargetIdentity> Naming(
        params (uint Target, string Path)[] names) =>
        path => names.FirstOrDefault(entry => entry.Target == path.TargetInfo.Id) is { Path: not null } match
            ? Target(match.Path, $"Monitor {path.TargetInfo.Id}", path.TargetInfo.Id)
            : throw new System.ComponentModel.Win32Exception(31, "unreadable target");

    [Fact]
    public void ALayoutWithoutExactlyOnePrimaryIsRefused()
    {
        DisplayTargetIdentity first = Target(@"\\?\a"), second = Target(@"\\?\b", id: 2);

        Assert.Contains("at least one display", DisplayLayoutPlanner.Describe(new([])));
        Assert.Contains("0,0", DisplayLayoutPlanner.Describe(
            new([Output(first, x: 100), Output(second, x: 2020)]))!);
        Assert.Contains("0,0", DisplayLayoutPlanner.Describe(
            new([Output(first), Output(second)]))!);
    }

    [Fact]
    public void OverlappingOrDetachedDisplaysAreRefused()
    {
        DisplayTargetIdentity first = Target(@"\\?\a"), second = Target(@"\\?\b", id: 2);

        Assert.Contains("overlap", DisplayLayoutPlanner.Describe(
            new([Output(first), Output(second, x: 1000)]))!);
        // Windows snaps a detached desktop back against the primary, so the readback would never
        // match what was asked for.
        Assert.Contains("touch", DisplayLayoutPlanner.Describe(
            new([Output(first), Output(second, x: 4000)]))!);
        Assert.Null(DisplayLayoutPlanner.Describe(new([Output(first), Output(second, x: 1920)])));
    }

    [Fact]
    public void ADisplayListedTwiceOrScaledOutOfRangeIsRefused()
    {
        DisplayTargetIdentity target = Target(@"\\?\a");

        Assert.Contains("twice", DisplayLayoutPlanner.Describe(
            new([Output(target), Output(target, x: 1920)]))!);
        Assert.Contains("scaling", DisplayLayoutPlanner.Describe(
            new([Output(target) with { DpiPercent = 700 }]))!);
    }

    [Fact]
    public void AnInactiveTargetIsActivatedAndGivenTheDesktopRectangleItWasAsked()
    {
        DisplayTopology.PathInfo[] paths = [Path(0, 10), Path(1, 20)];
        var naming = Naming((10, @"\\?\tv"), (20, @"\\?\desk"));
        DisplayLayout layout = new([
            Output(Target(@"\\?\desk", id: 20), width: 2560, height: 1440),
            Output(Target(@"\\?\tv", id: 10), x: 2560, width: 3840, height: 2160, hertz: 120),
        ]);

        (DisplayTopology.PathInfo[] planned, DisplayTopology.ModeInfo[] modes) =
            DisplayLayoutPlanner.Plan(paths, [], layout, naming);

        Assert.Equal(2, modes.Length);
        Assert.All(planned.Take(2), path => Assert.NotEqual(0u, path.Flags & Active));
        Assert.Equal(2560u, modes[0].Mode.Source.Width);
        Assert.Equal(2560, modes[1].Mode.Source.X);
        Assert.Equal(3840u, modes[1].Mode.Source.Width);
        Assert.Equal(120u, planned[1].TargetInfo.RefreshRate.Numerator);
        // The target mode is left for Windows to choose for the requested resolution and rate.
        Assert.All(planned.Take(2), path => Assert.Equal(InvalidIndex, path.TargetInfo.ModeInfoIdx));
    }

    [Fact]
    public void ATargetLeftOutOfTheLayoutIsSuppliedInactive()
    {
        DisplayTopology.PathInfo[] paths = [Path(0, 10, active: true), Path(1, 20, active: true)];
        var naming = Naming((10, @"\\?\keep"), (20, @"\\?\drop"));

        (DisplayTopology.PathInfo[] planned, _) = DisplayLayoutPlanner.Plan(
            paths, [], new([Output(Target(@"\\?\keep", id: 10))]), naming);

        DisplayTopology.PathInfo dropped = planned.Single(path => path.TargetInfo.Id == 20);
        Assert.Equal(0u, dropped.Flags & Active);
        Assert.Equal(InvalidIndex, dropped.SourceInfo.ModeInfoIdx);
    }

    [Fact]
    public void TheSourceAlreadyDrivingADisplayIsKept()
    {
        // Two ways to reach the same monitor; the active one must win so a display that is only
        // moving is not re-routed.
        DisplayTopology.PathInfo[] paths = [Path(3, 10), Path(1, 10, active: true)];
        var naming = Naming((10, @"\\?\a"));

        (DisplayTopology.PathInfo[] planned, _) = DisplayLayoutPlanner.Plan(
            paths, [], new([Output(Target(@"\\?\a", id: 10))]), naming);

        Assert.Equal(1u, planned[0].SourceInfo.Id);
    }

    [Fact]
    public void TwoDisplaysNeverShareOneSource()
    {
        // Both monitors advertise source 0; the second has to take its other option.
        DisplayTopology.PathInfo[] paths = [Path(0, 10), Path(0, 20), Path(1, 20)];
        var naming = Naming((10, @"\\?\a"), (20, @"\\?\b"));
        DisplayLayout layout = new([
            Output(Target(@"\\?\a", id: 10)),
            Output(Target(@"\\?\b", id: 20), x: 1920),
        ]);

        (DisplayTopology.PathInfo[] planned, _) = DisplayLayoutPlanner.Plan(paths, [], layout, naming);

        Assert.Equal(0u, planned[0].SourceInfo.Id);
        Assert.Equal(1u, planned[1].SourceInfo.Id);
    }

    [Fact]
    public void ADisplayWithNoPathAtAllIsReportedByName()
    {
        DisplayTopology.PathInfo[] paths = [Path(0, 10)];

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            DisplayLayoutPlanner.Plan(paths, [], new([Output(Target(@"\\?\missing", "Living room TV", 99))]),
                Naming((10, @"\\?\a"))));

        Assert.Contains("Living room TV", failure.Message);
    }

    [Fact]
    public void AnUnreadableTargetDoesNotStopTheOthers()
    {
        // One path's name query fails, which happens while a monitor is dropping out.
        DisplayTopology.PathInfo[] paths = [Path(0, 99), Path(1, 10)];
        var naming = Naming((10, @"\\?\a"));

        (DisplayTopology.PathInfo[] planned, _) = DisplayLayoutPlanner.Plan(
            paths, [], new([Output(Target(@"\\?\a", id: 10))]), naming);

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
        => Assert.Equal(expected, DisplayLayouts.SameRefresh(
            new(observedNumerator, observedDenominator), new(requestedNumerator, requestedDenominator)));

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
        DisplayTargetIdentity target = Target(@"\\?\a");
        DisplayArrangement arrangement = new(
            [new(target, true, true, Output(target))], "x", DateTimeOffset.UnixEpoch);

        Assert.True(DisplayLayouts.Matches(arrangement, new([Output(target)])));
        Assert.False(DisplayLayouts.Matches(arrangement, new([Output(target, width: 2560)])));
        Assert.False(DisplayLayouts.Matches(arrangement,
            new([Output(target), Output(Target(@"\\?\b", id: 2), x: 1920)])));
    }

    [Fact]
    public void TheNativeModeRecordKeepsItsDocumentedLayout()
    {
        // The union sits at offset 16 and the whole record is 64 bytes; a wrong offset here would
        // silently write a resolution into the wrong field.
        Assert.Equal(64, Marshal.SizeOf<DisplayTopology.ModeInfo>());
        Assert.Equal(48, Marshal.SizeOf<DisplayTopology.ModeUnion>());
        Assert.Equal(20, Marshal.SizeOf<DisplayTopology.SourceMode>());
        Assert.Equal(48, Marshal.SizeOf<DisplayTopology.VideoSignalInfo>());
        Assert.Equal(16, Marshal.OffsetOf<DisplayTopology.ModeInfo>(nameof(DisplayTopology.ModeInfo.Mode)).ToInt32());
    }

    [Fact]
    public void TheLayoutRulesAreReachableWithoutTouchingADisplay()
    {
        // An editor has to refuse a layout as it is typed, and a stored layout has to be checkable
        // while the monitors it names are unplugged. Neither can call Validate, which asks Windows.
        DisplayTargetIdentity target = Target(@"\\?\a");

        Assert.Null(DisplayLayouts.Describe(new([Output(target)])));
        Assert.Contains("at least one display", DisplayLayouts.Describe(new([]))!);
        Assert.Throws<ArgumentNullException>(() => DisplayLayouts.Describe(null!));
    }

    [Fact]
    public void ACapturedProfileReadsBackAsTheLayoutItRecorded()
    {
        DisplayTargetIdentity first = Target(@"\\?\a", "Desk", 10);
        DisplayTargetIdentity second = Target(@"\\?\b", "TV", 20);
        DisplayProfile profile = Profile(
            [(first, 0, 0, 2560, 1440, 60), (second, 2560, 0, 3840, 2160, 120)]);

        DisplayLayout layout = DisplayLayouts.FromProfile(profile)!;

        Assert.Equal(2, layout.Outputs.Count);
        Assert.Equal((0, 2560, 1440), (layout.Outputs[0].X, layout.Outputs[0].Width, layout.Outputs[0].Height));
        Assert.Equal(2560, layout.Outputs[1].X);
        Assert.Equal(120, layout.Outputs[1].Refresh.Hertz);
        // The profile never recorded these, and reading them now would describe today's desktop.
        Assert.All(layout.Outputs, output => Assert.Null(output.DpiPercent));
        Assert.All(layout.Outputs, output => Assert.Null(output.Hdr));
    }

    [Theory]
    // A record of the wrong length, a target list that does not line up with the paths, and a
    // recorded arrangement that no longer describes a desktop: all are dropped, never guessed at.
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void AProfileThatCannotBeReadAsALayoutIsRefused(bool truncated, bool mismatched, bool overlapping)
    {
        DisplayTargetIdentity first = Target(@"\\?\a", "Desk", 10);
        DisplayTargetIdentity second = Target(@"\\?\b", "TV", 20);
        DisplayProfile profile = overlapping
            ? Profile([(first, 0, 0, 2560, 1440, 60), (second, 0, 0, 3840, 2160, 60)])
            : Profile([(first, 0, 0, 2560, 1440, 60)]);
        if (truncated) { profile = profile with { PathData = [new byte[3]] }; }
        if (mismatched) { profile = profile with { Targets = [first, second] }; }

        Assert.Null(DisplayLayouts.FromProfile(profile));
    }

    /// <summary>Builds the native records <see cref="DisplayTopology.CaptureProfile"/> would have
    /// written for one arrangement, so the decode can be tested without a second monitor.</summary>
    private static DisplayProfile Profile(
        (DisplayTargetIdentity Target, int X, int Y, int Width, int Height, int Hertz)[] outputs)
    {
        List<byte[]> paths = [], modes = [];
        foreach (var (target, x, y, width, height, hertz) in outputs)
        {
            DisplayTopology.ModeInfo mode = new()
            {
                InfoType = 1,
                Id = target.TargetId,
                Mode = new() { Source = new() { Width = (uint)width, Height = (uint)height, X = x, Y = y } },
            };
            DisplayTopology.PathInfo path = new()
            {
                SourceInfo = new() { Id = target.TargetId, ModeInfoIdx = (uint)modes.Count },
                TargetInfo = new()
                {
                    Id = target.TargetId,
                    ModeInfoIdx = InvalidIndex,
                    Rotation = 1,
                    RefreshRate = new() { Numerator = (uint)hertz, Denominator = 1 },
                },
                Flags = Active,
            };
            modes.Add(Bytes(mode));
            paths.Add(Bytes(path));
        }
        return new(1, [.. outputs.Select(output => output.Target)], paths, modes);
    }

    private static unsafe byte[] Bytes<T>(T value) where T : unmanaged
    {
        byte[] buffer = new byte[sizeof(T)];
        fixed (byte* destination = buffer) { *(T*)destination = value; }
        return buffer;
    }

    [Fact]
    public void ScalingSnapsToTheStepsWindowsOffers()
    {
        Assert.Equal(100, DisplayScaling.Snap(90));
        Assert.Equal(150, DisplayScaling.Snap(160));
        Assert.Equal(500, DisplayScaling.Snap(9000));
    }
}
