using System;
using System.Runtime.InteropServices;

namespace WindowsDeviceControl;

public static partial class WindowsPower
{
    /// <summary>Registers a window for WM_POWERBROADCAST setting notifications.</summary>
    /// <param name="window">The caller-owned message window.</param>
    /// <param name="setting">Windows power-setting identity.</param>
    /// <returns>A registration to release with UnregisterSettingNotification, or zero on failure.</returns>
    /// <remarks>The caller owns window dispatch and registration lifetime. Marshal.GetLastPInvokeError preserves failures.</remarks>
    public static nint RegisterSettingNotification(nint window, Guid setting) =>
        RegisterPowerSettingNotification(window, in setting, 0);

    /// <summary>Unregisters a previously acquired setting notification.</summary>
    /// <param name="registration">The handle returned by RegisterSettingNotification.</param>
    /// <returns>Whether Windows released the registration; inspect Marshal.GetLastPInvokeError on failure.</returns>
    public static bool UnregisterSettingNotification(nint registration) => UnregisterPowerSettingNotification(registration);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint RegisterPowerSettingNotification(nint recipient, in Guid setting, uint flags);
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterPowerSettingNotification(nint registration);
}
