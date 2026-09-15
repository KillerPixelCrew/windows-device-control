using System;
using System.Collections.Generic;

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
