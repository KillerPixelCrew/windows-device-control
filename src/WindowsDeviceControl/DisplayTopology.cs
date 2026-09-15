using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsDeviceControl;

/// <summary>Stable-enough monitor identity plus the current CCD route used to reach it.</summary>
/// <param name="DevicePath">Monitor device-interface path. This is the primary rematching key.</param>
/// <param name="EdidManufacturerId">Raw EDID manufacturer identifier when Windows reports it.</param>
/// <param name="EdidProductCodeId">Raw EDID product identifier when Windows reports it.</param>
/// <param name="FriendlyName">Display metadata for people; never the sole identity.</param>
/// <param name="AdapterLowPart">Current adapter LUID low part. This coordinate may change after hotplug.</param>
/// <param name="AdapterHighPart">Current adapter LUID high part. This coordinate may change after hotplug.</param>
/// <param name="TargetId">Current CCD target identifier. This coordinate may change after hotplug.</param>
public sealed record DisplayTargetIdentity(string DevicePath, ushort? EdidManufacturerId,
    ushort? EdidProductCodeId, string FriendlyName, uint AdapterLowPart, int AdapterHighPart, uint TargetId)
{
    /// <summary>Returns whether this saved identity describes the same physical monitor observation.</summary>
    /// <param name="other">Current observation to compare.</param>
    /// <remarks>The device path wins. EDID manufacturer/product is a fallback only when both sides lack a path.</remarks>
    public bool Matches(DisplayTargetIdentity other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (DevicePath.Length != 0 || other.DevicePath.Length != 0)
        { return DevicePath.Length != 0 && string.Equals(DevicePath, other.DevicePath, StringComparison.OrdinalIgnoreCase); }
        return EdidManufacturerId.HasValue && EdidProductCodeId.HasValue
            && EdidManufacturerId == other.EdidManufacturerId && EdidProductCodeId == other.EdidProductCodeId;
    }
}

/// <summary>One currently active Windows display path.</summary>
/// <param name="Target">Monitor identity and current target route.</param>
/// <param name="SourceName">Current GDI source name, such as <c>\\.\DISPLAY1</c>; display-only numbering is volatile.</param>
/// <param name="OutputTechnology">Raw DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY value.</param>
/// <param name="RefreshNumerator">Current path refresh numerator.</param>
/// <param name="RefreshDenominator">Current path refresh denominator.</param>
public sealed record ActiveDisplayPath(DisplayTargetIdentity Target, string SourceName, uint OutputTechnology,
    uint RefreshNumerator, uint RefreshDenominator);

/// <summary>A detached observation of the active CCD topology.</summary>
/// <param name="Paths">Active paths in Windows priority order.</param>
/// <param name="CapturedAt">Time after the native query and target-name reads completed.</param>
public sealed record DisplayTopologySnapshot(IReadOnlyList<ActiveDisplayPath> Paths, DateTimeOffset CapturedAt);

/// <summary>Serializable Windows display profile captured from supported CCD APIs.</summary>
/// <param name="FormatVersion">Profile schema version.</param>
/// <param name="Targets">Stable target identities in path order.</param>
/// <param name="PathData">Blittable DISPLAYCONFIG_PATH_INFO records without process pointers.</param>
/// <param name="ModeData">Blittable DISPLAYCONFIG_MODE_INFO records without process pointers.</param>
public sealed record DisplayProfile(int FormatVersion, IReadOnlyList<DisplayTargetIdentity> Targets,
    IReadOnlyList<byte[]> PathData, IReadOnlyList<byte[]> ModeData);

/// <summary>Result of validating or applying a display profile.</summary>
/// <param name="Applied">Whether the requested profile was applied and confirmed active.</param>
/// <param name="NativeStatus">SetDisplayConfig status for the failed stage, or zero.</param>
/// <param name="RollbackAttempted">Whether failure triggered restoration of the captured topology.</param>
/// <param name="RollbackSucceeded">Whether rollback returned success.</param>
/// <param name="Detail">Bounded diagnostic suitable for a log or UI.</param>
public sealed record DisplayProfileResult(bool Applied, int NativeStatus, bool RollbackAttempted,
    bool RollbackSucceeded, string Detail);

/// <summary>Result of waiting for a saved display identity.</summary>
public enum DisplayWaitOutcome
{
    /// <summary>A matching monitor satisfying the requested active/available wait was observed.</summary>
    Present,
    /// <summary>The deadline elapsed without a matching active monitor.</summary>
    TimedOut,
}

/// <summary>Supported Windows CCD display enumeration and appearance waits.</summary>
public static partial class DisplayTopology
{
    internal const uint OnlyActivePaths = 0x2;
    internal const uint AllPaths = 0x1;
    internal const uint SdcValidate = 0x40;
    internal const uint SdcApply = 0x80;
    internal const uint SaveToDatabase = 0x200;
    private const uint UseSupplied = 0x20;
    private const uint AllowChanges = 0x400;
    private const int GetSourceName = 1;
    private const int GetTargetName = 2;
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

    /// <summary>Captures the complete active topology in a serializable profile.</summary>
    /// <returns>A versioned profile containing monitor identities and native CCD records.</returns>
    /// <remarks>The records contain no process pointers. Validate immediately before application.</remarks>
    public static DisplayProfile CaptureProfile()
    {
        NativeSnapshot native = Query(OnlyActivePaths);
        var targets = native.Paths.Select(ReadTarget).ToArray();
        return new(1, targets, Encode(native.Paths), Encode(native.Modes));
    }

    /// <summary>Validates a stored profile against current monitor identities and Windows CCD.</summary>
    /// <param name="profile">Previously captured profile.</param>
    /// <returns>A result without changing display state.</returns>
    public static DisplayProfileResult ValidateProfile(DisplayProfile profile) =>
        TryPrepare(profile, out _) ?? new(true, 0, false, false, "Profile is valid for the current topology.");

    /// <summary>Validates, applies and confirms a stored profile, rolling back after an unconfirmed application.</summary>
    /// <param name="profile">Previously captured profile.</param>
    /// <returns>Detailed application and rollback evidence.</returns>
    /// <remarks>This can rearrange or blank displays. It captures rollback state before mutation and never retries automatically.</remarks>
    public static DisplayProfileResult ApplyProfile(DisplayProfile profile)
    {
        if (TryPrepare(profile, out NativeSnapshot requested) is { } refused) { return refused; }
        NativeSnapshot rollback;
        try { rollback = Query(OnlyActivePaths); }
        catch (Win32Exception ex) { return new(false, ex.NativeErrorCode, false, false, "Could not capture rollback topology; nothing was applied."); }
        int status = Supply(requested, SdcApply | SaveToDatabase);
        if (status == 0)
        {
            try
            {
                var observed = CaptureActive();
                if (profile.Targets.All(target => observed.Paths.Exists(path => target.Matches(path.Target))))
                { return new(true, 0, false, false, "Profile applied and target presence was confirmed."); }
            }
            catch (Win32Exception) { }
        }
        int rollbackStatus = Supply(rollback, SdcApply);
        return new(false, status, true, rollbackStatus == 0,
            rollbackStatus == 0 ? "Profile application was not confirmed; the captured topology was restored."
                : $"Profile application was not confirmed and rollback failed with status {rollbackStatus}.");
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

    /// <summary>Rematches a stored profile to the current topology and asks Windows to validate it
    /// without applying anything.</summary>
    /// <returns>Null when the profile is ready to apply; otherwise the result that refuses it.</returns>
    private static DisplayProfileResult? TryPrepare(DisplayProfile profile, out NativeSnapshot requested)
    {
        try { requested = Rematch(profile); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            requested = null!;
            return Failure(ex);
        }
        int status = Supply(requested, SdcValidate);
        return status == 0 ? null : new(false, status, false, false, "Windows rejected the profile during validation.");
    }

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

    private static NativeSnapshot Rematch(DisplayProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.FormatVersion != 1 || profile.Targets is null || profile.PathData is null || profile.ModeData is null
            || profile.Targets.Count is 0 or > 256 || profile.Targets.Count != profile.PathData.Count || profile.ModeData.Count > 1024)
        { throw new ArgumentException("Display profile has an unsupported shape.", nameof(profile)); }
        PathInfo[] paths = Decode<PathInfo>(profile.PathData);
        ModeInfo[] modes = Decode<ModeInfo>(profile.ModeData);
        NativeSnapshot current = Query(AllPaths);
        var currentTargets = current.Paths.Select(path => (Path: path, Target: ReadTarget(path))).ToArray();
        Dictionary<RouteKey, RouteKey> replacements = [];
        for (int index = 0; index < paths.Length; index++)
        {
            var match = currentTargets.FirstOrDefault(candidate => profile.Targets[index].Matches(candidate.Target));
            if (match.Target is null) { throw new InvalidOperationException("A saved display is not present in the current topology."); }
            PathInfo saved = paths[index];
            replacements[new(saved.TargetInfo.AdapterId, saved.TargetInfo.Id)] = new(match.Path.TargetInfo.AdapterId, match.Path.TargetInfo.Id);
            replacements[new(saved.SourceInfo.AdapterId, saved.SourceInfo.Id)] = new(match.Path.SourceInfo.AdapterId, match.Path.SourceInfo.Id);
            paths[index].TargetInfo.AdapterId = match.Path.TargetInfo.AdapterId;
            paths[index].TargetInfo.Id = match.Path.TargetInfo.Id;
            paths[index].SourceInfo.AdapterId = match.Path.SourceInfo.AdapterId;
            paths[index].SourceInfo.Id = match.Path.SourceInfo.Id;
        }
        for (int index = 0; index < modes.Length; index++)
        {
            var key = new RouteKey(modes[index].AdapterId, modes[index].Id);
            if (replacements.TryGetValue(key, out RouteKey replacement))
            {
                modes[index].AdapterId = replacement.Adapter;
                modes[index].Id = replacement.Id;
            }
        }
        return new(paths, modes);
    }

    private static unsafe IReadOnlyList<byte[]> Encode<T>(T[] values) where T : unmanaged
    {
        int size = sizeof(T);
        var encoded = new byte[values.Length][];
        for (int index = 0; index < values.Length; index++)
        {
            encoded[index] = new byte[size];
            fixed (T* source = &values[index])
            fixed (byte* destination = encoded[index]) { Buffer.MemoryCopy(source, destination, size, size); }
        }
        return encoded;
    }

    internal static unsafe T[] Decode<T>(IReadOnlyList<byte[]> values) where T : unmanaged
    {
        int size = sizeof(T);
        T[] decoded = new T[values.Count];
        for (int index = 0; index < values.Count; index++)
        {
            if (values[index] is not { Length: var length } data || length != size)
            { throw new ArgumentException("Display profile contains an invalid native record."); }
            fixed (byte* source = data) { decoded[index] = *(T*)source; }
        }
        return decoded;
    }

    private static DisplayProfileResult Failure(Exception exception) => new(false,
        exception is Win32Exception native ? native.NativeErrorCode : 0, false, false, Bound(exception.Message));

    internal static string Bound(string value) => value.Length <= 512 ? value : value[..512];

    private static bool Exists<T>(this IReadOnlyList<T> values, Predicate<T> predicate)
    {
        for (int index = 0; index < values.Count; index++) { if (predicate(values[index])) { return true; } }
        return false;
    }

    /// <summary>Builds the header every CCD device-info packet starts with.</summary>
    internal static DeviceInfoHeader Header<T>(int type, Luid adapter, uint id) where T : struct
        => new() { Type = type, Size = (uint)Marshal.SizeOf<T>(), AdapterId = adapter, Id = id };

    // The native CCD shapes are internal rather than private so the layout editor beside this class
    // can build a supplied configuration from the same declarations. One decoded layout, one set of
    // offsets: a second copy would be a second thing to get wrong.
    [StructLayout(LayoutKind.Sequential)] internal record struct Luid { public uint LowPart; public int HighPart; }
    internal readonly record struct RouteKey(Luid Adapter, uint Id);
    internal sealed record NativeSnapshot(PathInfo[] Paths, ModeInfo[] Modes);
    [StructLayout(LayoutKind.Sequential)] internal struct Rational { public uint Numerator; public uint Denominator; }
    [StructLayout(LayoutKind.Sequential)] internal struct DeviceInfoHeader { public int Type; public uint Size; public Luid AdapterId; public uint Id; }
    [StructLayout(LayoutKind.Sequential)] internal struct PathSourceInfo { public Luid AdapterId; public uint Id; public uint ModeInfoIdx; public uint StatusFlags; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct PathTargetInfo
    { public Luid AdapterId; public uint Id; public uint ModeInfoIdx; public uint OutputTechnology; public uint Rotation; public uint Scaling; public Rational RefreshRate; public uint ScanLineOrdering; public int TargetAvailable; public uint StatusFlags; }
    [StructLayout(LayoutKind.Sequential)] internal struct PathInfo { public PathSourceInfo SourceInfo; public PathTargetInfo TargetInfo; public uint Flags; }

    /// <summary>Source half of a mode record: the desktop rectangle this display shows.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SourceMode { public uint Width; public uint Height; public uint PixelFormat; public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Region2D { public uint Cx; public uint Cy; }
    /// <summary>Target half of a mode record: the signal the adapter drives.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct VideoSignalInfo
    {
        public ulong PixelRate; public Rational HSyncFreq; public Rational VSyncFreq;
        public Region2D ActiveSize; public Region2D TotalSize; public uint VideoStandard; public uint ScanLineOrdering;
    }
    [StructLayout(LayoutKind.Explicit, Size = 48)]
    internal struct ModeUnion
    {
        [FieldOffset(0)] public VideoSignalInfo Target;
        [FieldOffset(0)] public SourceMode Source;
    }
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    internal struct ModeInfo { public uint InfoType; public uint Id; public Luid AdapterId; public ModeUnion Mode; }
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct SourceDeviceName
    { public DeviceInfoHeader Header; public fixed char ViewGdiDeviceName[32]; }
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct TargetDeviceName
    {
        public DeviceInfoHeader Header; public uint Flags; public uint OutputTechnology; public ushort EdidManufacturerId;
        public ushort EdidProductCodeId; public uint ConnectorInstance;
        public fixed char MonitorFriendlyDeviceName[64];
        public fixed char MonitorDevicePath[128];
    }

    [LibraryImport("user32.dll")] internal static partial int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);
    [LibraryImport("user32.dll")]
    internal static partial int QueryDisplayConfig(uint flags, ref uint pathCount,
        [In, Out] PathInfo[] paths, ref uint modeCount, [In, Out] ModeInfo[] modes, nint topologyId);
    [LibraryImport("user32.dll")]
    internal static partial int SetDisplayConfig(uint pathCount, [In] PathInfo[] paths,
        uint modeCount, [In] ModeInfo[] modes, uint flags);
    [LibraryImport("user32.dll")] internal static partial int DisplayConfigGetDeviceInfo(ref SourceDeviceName packet);
    [LibraryImport("user32.dll")] internal static partial int DisplayConfigGetDeviceInfo(ref TargetDeviceName packet);
}
