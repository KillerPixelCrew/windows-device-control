using System.Runtime.InteropServices;

namespace WindowsDeviceControl;

/// <summary>Windows power-source and battery observations, preserving native unknown sentinels.</summary>
/// <param name="ACLineStatus">0 means battery, 1 means AC, 255 means unknown.</param>
/// <param name="BatteryFlag">Native battery flags; 128 means absent and 255 means unknown.</param>
/// <param name="BatteryLifePercent">Charge percentage, or 255 when unknown.</param>
/// <param name="SystemStatusFlag">Whether Windows battery saver is enabled.</param>
/// <param name="BatteryLifeTime">Estimated seconds remaining, or uint.MaxValue when unknown.</param>
/// <param name="BatteryFullLifeTime">Estimated full-charge seconds, or uint.MaxValue when unknown.</param>
public readonly record struct WindowsPowerStatus(byte ACLineStatus, byte BatteryFlag, byte BatteryLifePercent,
    byte SystemStatusFlag, uint BatteryLifeTime, uint BatteryFullLifeTime);

public static partial class WindowsPower
{
    /// <summary>Reads system power status without changing Windows state.</summary>
    /// <param name="status">Observed native values. Ignore this output when the call returns false.</param>
    /// <returns>False when Windows cannot provide a status snapshot.</returns>
    public static bool TryGetStatus(out WindowsPowerStatus status)
    {
        bool success = GetSystemPowerStatus(out NativePowerStatus native);
        status = new(native.ACLineStatus, native.BatteryFlag, native.BatteryLifePercent,
            native.SystemStatusFlag, native.BatteryLifeTime, native.BatteryFullLifeTime);
        return success;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePowerStatus
    {
        internal byte ACLineStatus;
        internal byte BatteryFlag;
        internal byte BatteryLifePercent;
        internal byte SystemStatusFlag;
        internal uint BatteryLifeTime;
        internal uint BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out NativePowerStatus status);
}
