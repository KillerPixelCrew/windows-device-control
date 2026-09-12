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
    private const uint OnlyActivePaths = 0x2;
    private const uint AllPaths = 0x1;
    private const int GetSourceName = 1;
    private const int GetTargetName = 2;
    private const int ErrorInsufficientBuffer = 122;
    private const uint UseSupplied = 0x20;
    private const uint Validate = 0x40;
    private const uint Apply = 0x80;
    private const uint SaveToDatabase = 0x200;
    private const uint AllowChanges = 0x400;

    /// <summary>Captures active display paths and monitor identities without changing display state.</summary>
    /// <returns>A detached snapshot in Windows path-priority order.</returns>
    /// <exception cref="Win32Exception">A CCD query or required identity query failed.</exception>
    public static unsafe DisplayTopologySnapshot CaptureActive()
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int status = GetDisplayConfigBufferSizes(OnlyActivePaths, out uint pathCount, out uint modeCount);
            if (status != 0) { throw new Win32Exception(status, "Display topology buffer sizing failed."); }
            if (pathCount > 256 || modeCount > 1024) { throw new InvalidOperationException("Display topology exceeds supported bounds."); }
            PathInfo[] paths = new PathInfo[pathCount];
            ModeInfo[] modes = new ModeInfo[modeCount];
            status = QueryDisplayConfig(OnlyActivePaths, ref pathCount, paths, ref modeCount, modes, 0);
            if (status == ErrorInsufficientBuffer) { continue; }
            if (status != 0) { throw new Win32Exception(status, "Active display topology query failed."); }
            List<ActiveDisplayPath> result = new(checked((int)pathCount));
            for (int index = 0; index < pathCount; index++)
            {
                PathInfo path = paths[index];
                TargetDeviceName target = new()
                {
                    Header = new() { Type = GetTargetName, Size = (uint)Marshal.SizeOf<TargetDeviceName>(), AdapterId = path.TargetInfo.AdapterId, Id = path.TargetInfo.Id }
                };
                status = DisplayConfigGetDeviceInfo(ref target);
                if (status != 0) { throw new Win32Exception(status, "Display target identity query failed."); }
                SourceDeviceName source = new()
                {
                    Header = new() { Type = GetSourceName, Size = (uint)Marshal.SizeOf<SourceDeviceName>(), AdapterId = path.SourceInfo.AdapterId, Id = path.SourceInfo.Id }
                };
                status = DisplayConfigGetDeviceInfo(ref source);
                if (status != 0) { throw new Win32Exception(status, "Display source identity query failed."); }
                bool edidValid = (target.Flags & 0x2) != 0;
                DisplayTargetIdentity identity = new(Read(target.MonitorDevicePath, 128),
                    edidValid ? target.EdidManufacturerId : null, edidValid ? target.EdidProductCodeId : null,
                    Read(target.MonitorFriendlyDeviceName, 64), path.TargetInfo.AdapterId.LowPart,
                    path.TargetInfo.AdapterId.HighPart, path.TargetInfo.Id);
                result.Add(new(identity, Read(source.ViewGdiDeviceName, 32), path.TargetInfo.OutputTechnology,
                    path.TargetInfo.RefreshRate.Numerator, path.TargetInfo.RefreshRate.Denominator));
            }
            return new(result.AsReadOnly(), DateTimeOffset.UtcNow);
        }
        throw new Win32Exception(ErrorInsufficientBuffer, "Display topology changed repeatedly during capture.");
    }

    /// <summary>Captures the complete active topology in a serializable profile.</summary>
    /// <returns>A versioned profile containing monitor identities and native CCD records.</returns>
    /// <remarks>The records contain no process pointers. Validate immediately before application.</remarks>
    public static unsafe DisplayProfile CaptureProfile()
    {
        NativeSnapshot native = Query(OnlyActivePaths);
        var targets = native.Paths.Select(ReadTarget).ToArray();
        return new(1, targets, Encode(native.Paths), Encode(native.Modes));
    }

    /// <summary>Validates a stored profile against current monitor identities and Windows CCD.</summary>
    /// <param name="profile">Previously captured profile.</param>
    /// <returns>A result without changing display state.</returns>
    public static DisplayProfileResult ValidateProfile(DisplayProfile profile)
    {
        try
        {
            NativeSnapshot requested = Rematch(profile);
            int status = SetDisplayConfig((uint)requested.Paths.Length, requested.Paths, (uint)requested.Modes.Length,
                requested.Modes, UseSupplied | Validate | AllowChanges);
            return status == 0
                ? new(true, 0, false, false, "Profile is valid for the current topology.")
                : new(false, status, false, false, "Windows rejected the profile during validation.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        { return Failure(ex); }
    }

    /// <summary>Validates, applies and confirms a stored profile, rolling back after an unconfirmed application.</summary>
    /// <param name="profile">Previously captured profile.</param>
    /// <returns>Detailed application and rollback evidence.</returns>
    /// <remarks>This can rearrange or blank displays. It captures rollback state before mutation and never retries automatically.</remarks>
    public static DisplayProfileResult ApplyProfile(DisplayProfile profile)
    {
        NativeSnapshot requested;
        try { requested = Rematch(profile); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { return Failure(ex); }
        int status = SetDisplayConfig((uint)requested.Paths.Length, requested.Paths, (uint)requested.Modes.Length,
            requested.Modes, UseSupplied | Validate | AllowChanges);
        if (status != 0) { return new(false, status, false, false, "Windows rejected the profile during validation."); }
        NativeSnapshot rollback;
        try { rollback = Query(OnlyActivePaths); }
        catch (Win32Exception ex) { return new(false, ex.NativeErrorCode, false, false, "Could not capture rollback topology; nothing was applied."); }
        status = SetDisplayConfig((uint)requested.Paths.Length, requested.Paths, (uint)requested.Modes.Length,
            requested.Modes, UseSupplied | Apply | SaveToDatabase | AllowChanges);
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
        int rollbackStatus = SetDisplayConfig((uint)rollback.Paths.Length, rollback.Paths, (uint)rollback.Modes.Length,
            rollback.Modes, UseSupplied | Apply | AllowChanges);
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
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10)) { throw new ArgumentOutOfRangeException(nameof(timeout)); }
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        DisplayTopologySnapshot snapshot;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshot = CaptureActive();
            if (snapshot.Paths.Exists(path => identity.Matches(path.Target))) { return (DisplayWaitOutcome.Present, snapshot); }
            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) { return (DisplayWaitOutcome.TimedOut, snapshot); }
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250), cancellationToken)
                .ConfigureAwait(false);
        } while (true);
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
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10)) { throw new ArgumentOutOfRangeException(nameof(timeout)); }
        long started = Environment.TickCount64;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var available = Query(AllPaths).Paths.Where(path => path.TargetInfo.TargetAvailable != 0).Select(ReadTarget);
            if (available.Any(identity.Matches)) { return DisplayWaitOutcome.Present; }
            var remaining = timeout - TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
            if (remaining <= TimeSpan.Zero) { return DisplayWaitOutcome.TimedOut; }
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250), cancellationToken)
                .ConfigureAwait(false);
        } while (true);
    }

    private static unsafe NativeSnapshot Query(uint flags)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int status = GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount);
            if (status != 0) { throw new Win32Exception(status, "Display topology buffer sizing failed."); }
            if (pathCount > 256 || modeCount > 1024) { throw new InvalidOperationException("Display topology exceeds supported bounds."); }
            PathInfo[] paths = new PathInfo[pathCount];
            ModeInfo[] modes = new ModeInfo[modeCount];
            status = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, 0);
            if (status == ErrorInsufficientBuffer) { continue; }
            if (status != 0) { throw new Win32Exception(status, "Display topology query failed."); }
            if (pathCount != paths.Length) { Array.Resize(ref paths, checked((int)pathCount)); }
            if (modeCount != modes.Length) { Array.Resize(ref modes, checked((int)modeCount)); }
            return new(paths, modes);
        }
        throw new Win32Exception(ErrorInsufficientBuffer, "Display topology changed repeatedly during capture.");
    }

    private static unsafe DisplayTargetIdentity ReadTarget(PathInfo path)
    {
        TargetDeviceName target = new()
        {
            Header = new() { Type = GetTargetName, Size = (uint)Marshal.SizeOf<TargetDeviceName>(), AdapterId = path.TargetInfo.AdapterId, Id = path.TargetInfo.Id }
        };
        int status = DisplayConfigGetDeviceInfo(ref target);
        if (status != 0) { throw new Win32Exception(status, "Display target identity query failed."); }
        bool edidValid = (target.Flags & 0x2) != 0;
        return new(Read(target.MonitorDevicePath, 128), edidValid ? target.EdidManufacturerId : null,
            edidValid ? target.EdidProductCodeId : null, Read(target.MonitorFriendlyDeviceName, 64),
            path.TargetInfo.AdapterId.LowPart, path.TargetInfo.AdapterId.HighPart, path.TargetInfo.Id);
    }

    private static unsafe NativeSnapshot Rematch(DisplayProfile profile)
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

    private static unsafe T[] Decode<T>(IReadOnlyList<byte[]> values) where T : unmanaged
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

    private static string Bound(string value) => value.Length <= 512 ? value : value[..512];

    private static bool Exists<T>(this IReadOnlyList<T> values, Predicate<T> predicate)
    {
        for (int index = 0; index < values.Count; index++) { if (predicate(values[index])) { return true; } }
        return false;
    }

    private static unsafe string Read(char* value, int capacity)
    {
        int length = 0;
        while (length < capacity && value[length] != '\0') { length++; }
        return new string(value, 0, length);
    }

    // The native CCD shapes are internal rather than private so the layout editor beside this class
    // can build a supplied configuration from the same declarations. One decoded layout, one set of
    // offsets: a second copy would be a second thing to get wrong.
    [StructLayout(LayoutKind.Sequential)] internal struct Luid { public uint LowPart; public int HighPart; }
    private readonly record struct RouteKey(Luid Adapter, uint Id);
    private sealed record NativeSnapshot(PathInfo[] Paths, ModeInfo[] Modes);
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

    /// <summary>Reads a fixed-width native string, which is not always terminated.</summary>
    /// <param name="value">Pointer to the first character.</param>
    /// <param name="capacity">Maximum characters to read.</param>
    /// <returns>The string up to its terminator or capacity.</returns>
    internal static unsafe string ReadNativeString(char* value, int capacity) => Read(value, capacity);

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
