using System;
using Microsoft.Win32.SafeHandles;

namespace WindowsDeviceControl;

/// <summary>Flat Win32 control of the internal panel's backlight through <c>\\.\LCD</c>.</summary>
/// <remarks>
///     Calls are synchronous and open/close their own device handle. No WMI or DDC/CI fallback is used.
///     False reports an unavailable interface, native refusal or invalid read, not a known absence of
///     panel hardware. Writes use one DISPLAY_BRIGHTNESS packet for both AC and DC, without readback.
/// </remarks>
public static class Backlight
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareReadWrite = 0x00000003;

    private const uint IoctlVideoQueryDisplayBrightness = 0x230498;
    private const uint IoctlVideoSetDisplayBrightness = 0x23049C;

    /// <summary>DISPLAYPOLICY_DC: the driver reports the battery level only.</summary>
    private const byte PolicyDc = 0x02;

    /// <summary>DISPLAYPOLICY_AC | DISPLAYPOLICY_DC: apply to both power sources.</summary>
    private const byte PolicyBoth = 0x03;

    /// <summary>Reads the panel's current backlight level.</summary>
    /// <param name="percent">
    ///     The level, 0 to 100, when this returns true: the level of the power source the driver's
    ///     policy byte names, which is the AC level unless the driver reports DC only. Ignore on false.
    /// </param>
    /// <returns>True for a complete reply containing a valid percentage; false otherwise.</returns>
    public static unsafe bool TryReadBrightness(out int percent)
    {
        percent = 0;
        using var device = OpenLcd();
        if (device.IsInvalid)
        {
            return false;
        }

        // DISPLAY_BRIGHTNESS: ucDisplayPolicy, ucACBrightness, ucDCBrightness.
        var buffer = stackalloc byte[3];
        if (!Kernel32.DeviceIoControl(
                device,
                IoctlVideoQueryDisplayBrightness,
                0,
                0,
                (nint)buffer,
                3,
                out var returned,
                0)
            || returned < 3)
        {
            return false;
        }

        percent = LevelFor(new ReadOnlySpan<byte>(buffer, 3));
        return percent is >= 0 and <= 100;
    }

    /// <summary>
    ///     The level a <c>DISPLAY_BRIGHTNESS</c> reply reports for its own policy: the DC byte for a
    ///     DC-only policy, the AC byte for AC and for both.
    /// </summary>
    /// <param name="brightness">A validated DISPLAY_BRIGHTNESS packet containing at least three bytes.</param>
    /// <returns>The selected AC or DC brightness byte; this does not query the panel.</returns>
    internal static byte LevelFor(ReadOnlySpan<byte> brightness)
    {
        return brightness[0] == PolicyDc ? brightness[2] : brightness[1];
    }

    /// <summary>Sets the panel's backlight level on both power sources.</summary>
    /// <param name="percent">The level, clamped to 0 to 100.</param>
    /// <returns>Whether the driver took it.</returns>
    public static unsafe bool TrySetBrightness(int percent)
    {
        var level = (byte)Math.Clamp(percent, 0, 100);
        using var device = OpenLcd();
        if (device.IsInvalid)
        {
            return false;
        }

        var request = stackalloc byte[3];
        request[0] = PolicyBoth;
        request[1] = level;
        request[2] = level;
        return Kernel32.DeviceIoControl(
            device,
            IoctlVideoSetDisplayBrightness,
            (nint)request,
            3,
            0,
            0,
            out _,
            0);
    }

    private static SafeFileHandle OpenLcd()
    {
        return Kernel32.CreateFile(
            @"\\.\LCD",
            GenericRead | GenericWrite,
            ShareReadWrite,
            0,
            Kernel32.OpenExisting,
            0,
            0);
    }
}
