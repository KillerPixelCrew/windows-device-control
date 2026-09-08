using System;
using System.Runtime.InteropServices;

namespace WindowsDeviceControl;

public static partial class WindowsPower
{
    /// <summary>Reads an AC or battery policy value. Native failures throw Win32Exception.</summary>
    /// <param name="scheme">Power scheme identity.</param>
    /// <param name="subgroup">Policy subgroup identity.</param>
    /// <param name="setting">Policy setting identity.</param>
    /// <param name="onBattery">True selects the DC value; false selects AC.</param>
    /// <returns>The raw Windows policy value in the setting's units.</returns>
    public static uint ReadSetting(Guid scheme, Guid subgroup, Guid setting, bool onBattery)
    {
        uint status = onBattery
            ? PowerReadDCValueIndex(0, in scheme, in subgroup, in setting, out uint value)
            : PowerReadACValueIndex(0, in scheme, in subgroup, in setting, out value);
        Check(status, "Read power setting");
        return value;
    }

    /// <summary>Writes one policy value once. Does not activate a scheme or retry a failed write.</summary>
    /// <param name="scheme">Power scheme identity.</param>
    /// <param name="subgroup">Policy subgroup identity.</param>
    /// <param name="setting">Policy setting identity.</param>
    /// <param name="onBattery">True selects DC; false selects AC.</param>
    /// <param name="value">Raw value in the setting's units.</param>
    public static void WriteSetting(Guid scheme, Guid subgroup, Guid setting, bool onBattery, uint value)
        => Check(onBattery ? PowerWriteDCValueIndex(0, in scheme, in subgroup, in setting, value)
            : PowerWriteACValueIndex(0, in scheme, in subgroup, in setting, value), "Write power setting");

    /// <summary>Reads the effective Windows power-mode overlay. Guid.Empty denotes Balanced.</summary>
    /// <returns>The effective overlay identity; native failures throw Win32Exception.</returns>
    public static Guid GetEffectiveMode()
    {
        Check(PowerGetEffectiveOverlayScheme(out Guid mode), "Read effective power mode");
        return mode;
    }

    /// <summary>Requests a power-mode overlay once. Read back to confirm application.</summary>
    /// <param name="mode">Overlay identity, or Guid.Empty for Balanced.</param>
    public static void SetActiveMode(Guid mode) => Check(PowerSetActiveOverlayScheme(mode), "Set power mode");

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetEffectiveOverlayScheme(out Guid mode);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerSetActiveOverlayScheme(Guid mode);
}
