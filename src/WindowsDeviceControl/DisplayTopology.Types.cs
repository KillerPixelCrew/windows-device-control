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
public sealed record DisplayTargetIdentity(
    string DevicePath,
    ushort? EdidManufacturerId,
    ushort? EdidProductCodeId,
    string FriendlyName,
    uint AdapterLowPart,
    int AdapterHighPart,
    uint TargetId)
{
    /// <summary>Returns whether this saved identity describes the same physical monitor observation.</summary>
    /// <param name="other">Current observation to compare.</param>
    /// <remarks>
    ///     The device path wins. EDID manufacturer/product is a fallback only when both sides lack a path. An
    ///     identity with neither a path nor EDID ids matches nothing, itself included, so it cannot be found again
    ///     and should not be persisted.
    /// </remarks>
    public bool Matches(DisplayTargetIdentity other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (DevicePath.Length != 0 || other.DevicePath.Length != 0)
        {
            return DevicePath.Length != 0 &&
                   string.Equals(DevicePath, other.DevicePath, StringComparison.OrdinalIgnoreCase);
        }

        return EdidManufacturerId.HasValue && EdidProductCodeId.HasValue
                                           && EdidManufacturerId == other.EdidManufacturerId &&
                                           EdidProductCodeId == other.EdidProductCodeId;
    }
}

/// <summary>One currently active Windows display path.</summary>
/// <param name="Target">Monitor identity and current target route.</param>
/// <param name="SourceName">Current GDI source name, such as <c>\\.\DISPLAY1</c>; display-only numbering is volatile.</param>
/// <param name="OutputTechnology">Raw DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY value.</param>
/// <param name="RefreshNumerator">Current path refresh numerator.</param>
/// <param name="RefreshDenominator">Current path refresh denominator.</param>
public sealed record ActiveDisplayPath(
    DisplayTargetIdentity Target,
    string SourceName,
    uint OutputTechnology,
    uint RefreshNumerator,
    uint RefreshDenominator);

/// <summary>A detached observation of the active CCD topology.</summary>
/// <param name="Paths">Active paths in Windows priority order.</param>
/// <param name="CapturedAt">Time after the native query and target-name reads completed.</param>
public sealed record DisplayTopologySnapshot(IReadOnlyList<ActiveDisplayPath> Paths, DateTimeOffset CapturedAt);

/// <summary>How a <see cref="DisplayModes.Apply" /> call ended.</summary>
public enum DisplayModeOutcome
{
    /// <summary>Windows accepted the mode change (status zero). Nothing is read back to confirm it.</summary>
    Applied,

    /// <summary>
    ///     Windows refused the mode change. The captured original was written back once when the route was
    ///     unchanged; see <see cref="DisplayModeResult.RollbackAttempted" />. Never retried.
    /// </summary>
    Refused,

    /// <summary>The display changed since the observation, or the selected mode was not in it. Nothing was written.</summary>
    Stale,

    /// <summary>The display's route changed between validation and the write. Nothing was written.</summary>
    RouteChanged,

    /// <summary>The driver no longer advertises the selected mode. Nothing was written.</summary>
    NotAdvertised,

    /// <summary>The driver's test of the selected mode failed. Nothing was written.</summary>
    ValidationRefused,

    /// <summary>The display's current mode could not be read, so nothing was written.</summary>
    Unreadable
}

/// <summary>Result of applying a display mode.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="NativeStatus">
///     The <c>ChangeDisplaySettingsEx</c> status (a <c>DISP_CHANGE_*</c> value) of the refused test or write,
///     or zero when no native call refused.
/// </param>
/// <param name="RollbackAttempted">Whether the captured original mode was written back after a refusal.</param>
/// <param name="RollbackSucceeded">Whether that write-back returned success. Its own status, not a readback.</param>
public sealed record DisplayModeResult(
    DisplayModeOutcome Outcome,
    int NativeStatus,
    bool RollbackAttempted,
    bool RollbackSucceeded)
{
    /// <summary>Whether Windows accepted the requested mode.</summary>
    public bool Applied => Outcome is DisplayModeOutcome.Applied;
}

/// <summary>How a per-display scaling or advanced colour write ended.</summary>
public enum DisplaySetOutcome
{
    /// <summary>The display already had the requested value; nothing was written.</summary>
    AlreadySet,

    /// <summary>Windows accepted the write (status zero). Nothing is read back to confirm it.</summary>
    Written,

    /// <summary>Windows refused the write; the native status says why. Never retried.</summary>
    Refused,

    /// <summary>The display is not part of the desktop, so nothing was written.</summary>
    NotActive,

    /// <summary>The display does not offer the requested value, so nothing was written.</summary>
    Unsupported,

    /// <summary>The display, or the current value the write is computed from, could not be read. Nothing was written.</summary>
    Unreadable
}

/// <summary>Result of one per-display write.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="NativeStatus"><c>DisplayConfigSetDeviceInfo</c> status of a refused write, or zero.</param>
public readonly record struct DisplaySetResult(DisplaySetOutcome Outcome, int NativeStatus)
{
    /// <summary>Whether the display has, or was sent, the requested value.</summary>
    public bool Succeeded => Outcome is DisplaySetOutcome.AlreadySet or DisplaySetOutcome.Written;
}

/// <summary>Result of one scaling write.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="NativeStatus"><c>DisplayConfigSetDeviceInfo</c> status of a refused write, or zero.</param>
/// <param name="Percent">The scaling step the request was snapped to.</param>
public readonly record struct DisplayScaleResult(DisplaySetOutcome Outcome, int NativeStatus, int Percent)
{
    /// <summary>Whether the display has, or was sent, the requested step.</summary>
    public bool Succeeded => Outcome is DisplaySetOutcome.AlreadySet or DisplaySetOutcome.Written;
}
