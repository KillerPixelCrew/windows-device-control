using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WindowsDeviceControl;

/// <summary>
///     One WM_POWERBROADCAST registration for a caller-owned window. Disposing it unregisters it with
///     the matching Windows call.
/// </summary>
/// <remarks>
///     Created only by <see cref="WindowsPower.RegisterSettingNotification" /> and
///     <see cref="WindowsPower.RegisterSuspendResumeNotification(nint)" />. Release has no failure channel: an
///     unregistration Windows refuses is not surfaced to the caller. Disposal is idempotent.
/// </remarks>
public sealed class PowerNotificationRegistration : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly bool _suspendResume;

    private PowerNotificationRegistration(nint handle, bool suspendResume)
        : base(true)
    {
        _suspendResume = suspendResume;
        SetHandle(handle);
    }

    internal static PowerNotificationRegistration Create(nint handle, bool suspendResume, string operation)
    {
        if (handle == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(error, $"{operation} failed (Win32 {error}).");
        }

        return new PowerNotificationRegistration(handle, suspendResume);
    }

    /// <inheritdoc />
    protected override bool ReleaseHandle()
    {
        return _suspendResume
            ? WindowsPower.UnregisterSuspendResume(handle)
            : WindowsPower.UnregisterPowerSetting(handle);
    }
}

public static partial class WindowsPower
{
    /// <summary>Registers a window for WM_POWERBROADCAST setting notifications.</summary>
    /// <param name="window">The caller-owned message window.</param>
    /// <param name="setting">Windows power-setting identity.</param>
    /// <returns>The registration; dispose it to unregister.</returns>
    /// <remarks>
    ///     Messages arrive through the window's own message loop. The caller owns the window and must
    ///     retain this registration while listening, then dispose it before destroying the window.
    /// </remarks>
    /// <exception cref="Win32Exception">Windows refused the registration; the native error is preserved.</exception>
    public static PowerNotificationRegistration RegisterSettingNotification(nint window, Guid setting)
    {
        return PowerNotificationRegistration.Create(
            RegisterPowerSettingNotification(window, in setting, 0), false, "RegisterPowerSettingNotification");
    }

    /// <summary>Registers a window for WM_POWERBROADCAST suspend and resume notifications.</summary>
    /// <param name="window">The caller-owned message window.</param>
    /// <returns>The registration; dispose it to unregister.</returns>
    /// <remarks>
    ///     Windows broadcasts PBT_APMSUSPEND, PBT_APMRESUMEAUTOMATIC and PBT_APMRESUMESUSPEND to top-level windows
    ///     only; a message-only window never receives them unless it is registered here. Modern Standby entry and
    ///     exit are delivered the same way. The caller owns window dispatch and the registration's lifetime.
    /// </remarks>
    /// <exception cref="Win32Exception">Windows refused the registration; the native error is preserved.</exception>
    public static PowerNotificationRegistration RegisterSuspendResumeNotification(nint window)
    {
        return PowerNotificationRegistration.Create(
            RegisterSuspendResumeNotification(window, 0), true, "RegisterSuspendResumeNotification");
    }

    internal static bool UnregisterPowerSetting(nint registration)
    {
        return UnregisterPowerSettingNotification(registration);
    }

    internal static bool UnregisterSuspendResume(nint registration)
    {
        return UnregisterSuspendResumeNotificationNative(registration);
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint RegisterPowerSettingNotification(nint recipient, in Guid setting, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterPowerSettingNotification(nint registration);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint RegisterSuspendResumeNotification(nint recipient, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "UnregisterSuspendResumeNotification", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterSuspendResumeNotificationNative(nint registration);
}
