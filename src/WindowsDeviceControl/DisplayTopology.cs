using System;
using System.Collections.Generic;
using System.ComponentModel;
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

/// <summary>Result of waiting for a saved display identity.</summary>
public enum DisplayWaitOutcome
{
    /// <summary>A matching active monitor was observed.</summary>
    Present,
    /// <summary>The deadline elapsed without a matching active monitor.</summary>
    TimedOut,
}

/// <summary>Supported Windows CCD display enumeration and appearance waits.</summary>
public static partial class DisplayTopology
{
    private const uint OnlyActivePaths = 0x2;
    private const int GetSourceName = 1;
    private const int GetTargetName = 2;
    private const int ErrorInsufficientBuffer = 122;

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

    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] private struct Rational { public uint Numerator; public uint Denominator; }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfoHeader { public int Type; public uint Size; public Luid AdapterId; public uint Id; }
    [StructLayout(LayoutKind.Sequential)] private struct PathSourceInfo { public Luid AdapterId; public uint Id; public uint ModeInfoIdx; public uint StatusFlags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PathTargetInfo
    { public Luid AdapterId; public uint Id; public uint ModeInfoIdx; public uint OutputTechnology; public uint Rotation; public uint Scaling; public Rational RefreshRate; public uint ScanLineOrdering; public int TargetAvailable; public uint StatusFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct PathInfo { public PathSourceInfo SourceInfo; public PathTargetInfo TargetInfo; public uint Flags; }
    [StructLayout(LayoutKind.Sequential, Size = 64)] private struct ModeInfo { public uint InfoType; public uint Id; public Luid AdapterId; }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SourceDeviceName
    { public DeviceInfoHeader Header; public fixed char ViewGdiDeviceName[32]; }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct TargetDeviceName
    {
        public DeviceInfoHeader Header; public uint Flags; public uint OutputTechnology; public ushort EdidManufacturerId;
        public ushort EdidProductCodeId; public uint ConnectorInstance;
        public fixed char MonitorFriendlyDeviceName[64];
        public fixed char MonitorDevicePath[128];
    }

    [LibraryImport("user32.dll")] private static partial int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);
    [LibraryImport("user32.dll")]
    private static partial int QueryDisplayConfig(uint flags, ref uint pathCount,
        [In, Out] PathInfo[] paths, ref uint modeCount, [In, Out] ModeInfo[] modes, nint topologyId);
    [LibraryImport("user32.dll")] private static partial int DisplayConfigGetDeviceInfo(ref SourceDeviceName packet);
    [LibraryImport("user32.dll")] private static partial int DisplayConfigGetDeviceInfo(ref TargetDeviceName packet);
}
