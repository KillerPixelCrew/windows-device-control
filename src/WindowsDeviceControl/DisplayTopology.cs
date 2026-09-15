using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsDeviceControl;

/// <summary>Supported Windows CCD display enumeration and appearance waits.</summary>
public static partial class DisplayTopology
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Captures active display paths and monitor identities without changing display state.</summary>
    /// <returns>A detached snapshot in Windows path-priority order.</returns>
    /// <exception cref="Win32Exception">A CCD query or required identity query failed.</exception>
    public static DisplayTopologySnapshot CaptureActive()
    {
        NativeSnapshot native = Query(OnlyActivePaths, "Active display topology query failed.");
        List<ActiveDisplayPath> result = new(native.Paths.Length);
        foreach (PathInfo path in native.Paths)
        {
            DisplayTargetIdentity identity = ReadTarget(path);
            result.Add(new(identity, ReadSourceName(path), path.TargetInfo.OutputTechnology,
                path.TargetInfo.RefreshRate.Numerator, path.TargetInfo.RefreshRate.Denominator));
        }
        return new(result.AsReadOnly(), DateTimeOffset.UtcNow);
    }

    /// <summary>Waits until a matching monitor is active, using fresh CCD snapshots.</summary>
    /// <param name="identity">Previously captured monitor identity.</param>
    /// <param name="timeout">Maximum elapsed time. Must be positive and no more than ten minutes.</param>
    /// <param name="cancellationToken">Cancels waiting without changing display state.</param>
    /// <returns>Presence or timeout, plus the last complete snapshot.</returns>
    public static async Task<(DisplayWaitOutcome Outcome, DisplayTopologySnapshot Snapshot)> WaitForPresentAsync(
        DisplayTargetIdentity identity, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        DisplayTopologySnapshot snapshot = null!;
        DisplayWaitOutcome outcome = await PollAsync(timeout, () =>
        {
            snapshot = CaptureActive();
            return snapshot.Paths.Exists(path => identity.Matches(path.Target));
        }, cancellationToken).ConfigureAwait(false);
        return (outcome, snapshot);
    }

    /// <summary>Waits for a connected display, including a target disabled in the current desktop profile.</summary>
    /// <param name="identity">Previously captured monitor identity.</param>
    /// <param name="timeout">Positive deadline no greater than ten minutes.</param>
    /// <param name="cancellationToken">Cancels observation without changing display state.</param>
    /// <returns>Present when a matching available CCD target is found, otherwise TimedOut.</returns>
    /// <remarks>Uses all CCD paths and checks target availability. It does not enable a monitor.</remarks>
    public static async Task<DisplayWaitOutcome> WaitForAvailableAsync(DisplayTargetIdentity identity,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return await PollAsync(timeout, () =>
        {
            Dictionary<RouteKey, DisplayTargetIdentity> read = [];
            return Query(AllPaths).Paths.Where(path => path.TargetInfo.TargetAvailable != 0)
                .Select(path => ReadTarget(path, read)).Any(identity.Matches);
        }, cancellationToken).ConfigureAwait(false);
    }

    // QDC_ALL_PATHS contains possible source/target combinations, not just connected monitors.
    // The desktop reported 284 paths for three active routes on 2026-09-13. Keep allocations
    // bounded below one MiB while allowing adapters with many routing combinations.
    internal static void ValidateBufferCounts(uint paths, uint modes)
    {
        if (paths > 4096 || modes > 8192)
        { throw new InvalidOperationException($"Display topology exceeds supported bounds ({paths} paths, {modes} modes)."); }
    }

    /// <summary>Finds the active path currently driving one monitor. The route changes on hotplug, so
    /// it is resolved per call rather than stored.</summary>
    /// <returns>False when the monitor is not active or the active topology could not be read.</returns>
    internal static bool TryFindActive(DisplayTargetIdentity target, out PathInfo path)
    {
        path = default;
        try
        {
            bool found = false;
            foreach (PathInfo candidate in Query(OnlyActivePaths, "Active display topology query failed.").Paths)
            {
                // Every active target is read, so an unreadable display still fails the lookup.
                DisplayTargetIdentity identity = ReadTarget(candidate);
                if (!found && target.Matches(identity)) { path = candidate; found = true; }
            }
            return found;
        }
        catch (Win32Exception) { return false; }
    }

    internal static NativeSnapshot Query(uint flags, string failure = "Display topology query failed.")
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int status = GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount);
            if (status != 0) { throw new Win32Exception(status, "Display topology buffer sizing failed."); }
            ValidateBufferCounts(pathCount, modeCount);
            PathInfo[] paths = new PathInfo[pathCount];
            ModeInfo[] modes = new ModeInfo[modeCount];
            status = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, 0);
            if (status == Win32Error.ErrorInsufficientBuffer) { continue; }
            if (status != 0) { throw new Win32Exception(status, failure); }
            if (pathCount != paths.Length) { Array.Resize(ref paths, checked((int)pathCount)); }
            if (modeCount != modes.Length) { Array.Resize(ref modes, checked((int)modeCount)); }
            return new(paths, modes);
        }
        throw new Win32Exception((int)Win32Error.ErrorInsufficientBuffer, "Display topology changed repeatedly during capture.");
    }

    internal static unsafe DisplayTargetIdentity ReadTarget(PathInfo path)
    {
        TargetDeviceName target = new() { Header = Header<TargetDeviceName>(GetTargetName, path.TargetInfo.AdapterId, path.TargetInfo.Id) };
        int status = DisplayConfigGetDeviceInfo(ref target);
        if (status != 0) { throw new Win32Exception(status, "Display target identity query failed."); }
        bool edidValid = (target.Flags & 0x2) != 0;
        return new(NativeText.ReadFixed(target.MonitorDevicePath, 128), edidValid ? target.EdidManufacturerId : null,
            edidValid ? target.EdidProductCodeId : null, NativeText.ReadFixed(target.MonitorFriendlyDeviceName, 64),
            path.TargetInfo.AdapterId.LowPart, path.TargetInfo.AdapterId.HighPart, path.TargetInfo.Id);
    }

    /// <summary>Reads a path's monitor identity once per target route within one observation. All
    /// paths query many possible routes to the same target, and its identity is the same on each.
    /// A failed read is not remembered.</summary>
    internal static DisplayTargetIdentity ReadTarget(PathInfo path, Dictionary<RouteKey, DisplayTargetIdentity> read)
    {
        RouteKey key = new(path.TargetInfo.AdapterId, path.TargetInfo.Id);
        if (!read.TryGetValue(key, out DisplayTargetIdentity? identity))
        {
            identity = ReadTarget(path);
            read[key] = identity;
        }
        return identity;
    }

    private static unsafe string ReadSourceName(PathInfo path)
    {
        SourceDeviceName source = new() { Header = Header<SourceDeviceName>(GetSourceName, path.SourceInfo.AdapterId, path.SourceInfo.Id) };
        int status = DisplayConfigGetDeviceInfo(ref source);
        if (status != 0) { throw new Win32Exception(status, "Display source identity query failed."); }
        return NativeText.ReadFixed(source.ViewGdiDeviceName, 32);
    }

    /// <summary>Supplies a complete configuration and lets Windows adjust modes to fit it.</summary>
    internal static int Supply(PathInfo[] paths, ModeInfo[] modes, uint flags) =>
        SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, UseSupplied | AllowChanges | flags);

    private static int Supply(NativeSnapshot snapshot, uint flags) => Supply(snapshot.Paths, snapshot.Modes, flags);

    /// <summary>Repeats a fresh observation until it matches or the deadline passes. Timeout and
    /// cancellation never change display state.</summary>
    private static async Task<DisplayWaitOutcome> PollAsync(TimeSpan timeout, Func<bool> observe,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10)) { throw new ArgumentOutOfRangeException(nameof(timeout)); }
        long started = Environment.TickCount64;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (observe()) { return DisplayWaitOutcome.Present; }
            var remaining = timeout - TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
            if (remaining <= TimeSpan.Zero) { return DisplayWaitOutcome.TimedOut; }
            await Task.Delay(remaining < PollInterval ? remaining : PollInterval, cancellationToken).ConfigureAwait(false);
        } while (true);
    }

    internal static string Bound(string value) => value.Length <= 512 ? value : value[..512];

    private static bool Exists<T>(this IReadOnlyList<T> values, Predicate<T> predicate)
    {
        for (int index = 0; index < values.Count; index++) { if (predicate(values[index])) { return true; } }
        return false;
    }

    /// <summary>Builds the header every CCD device-info packet starts with.</summary>
    internal static DeviceInfoHeader Header<T>(int type, Luid adapter, uint id) where T : struct
        => new() { Type = type, Size = (uint)Marshal.SizeOf<T>(), AdapterId = adapter, Id = id };
}
