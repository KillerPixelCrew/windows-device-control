using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WSGM.Interop;

/// <summary>Flat Win32 control of the internal panel's backlight through <c>\\.\LCD</c>.</summary>
/// <remarks>
/// The ACPI backlight driver's legacy device interface. Device verification on the reference Claw
/// showed that values written here read back identically through
/// <c>WmiMonitorBrightnessMethods</c>; the direct interface reaches the same driver with one small,
/// synchronous Win32 transaction and no WMI session.
/// <para>
/// The transfer is the documented <c>DISPLAY_BRIGHTNESS</c> triple: policy byte, then the AC and DC
/// levels as percent. Writes set both power sources to the same level, because a slider that only
/// moves the panel on one of them looks broken exactly half the time.
/// </para>
/// <para>
/// A machine without the interface (a desktop, an external-only setup) simply reports failure from
/// every call; callers translate that into an absent control, never an error state.
/// </para>
/// </remarks>
internal static partial class NativeBacklight
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;

    private const uint IoctlVideoQueryDisplayBrightness = 0x230498;
    private const uint IoctlVideoSetDisplayBrightness = 0x23049C;

    /// <summary>DISPLAYPOLICY_AC | DISPLAYPOLICY_DC: apply to both power sources.</summary>
    private const byte PolicyBoth = 0x03;

    /// <summary>Reads the panel's current backlight level.</summary>
    /// <param name="percent">The level, 0 to 100, when this returns true.</param>
    /// <returns>Whether the panel exposes a readable backlight.</returns>
    internal static unsafe bool TryReadBrightness(out int percent)
    {
        percent = 0;
        using SafeFileHandle device = OpenLcd();
        if (device.IsInvalid)
        {
            return false;
        }

        // DISPLAY_BRIGHTNESS: ucDisplayPolicy, ucACBrightness, ucDCBrightness.
        byte* buffer = stackalloc byte[3];
        if (!DeviceIoControl(
                device,
                IoctlVideoQueryDisplayBrightness,
                0,
                0,
                (nint)buffer,
                3,
                out uint returned,
                0)
            || returned < 3)
        {
            return false;
        }

        // The AC level; on the reference device both levels track the same slider.
        percent = buffer[1];
        return percent is >= 0 and <= 100;
    }

    /// <summary>Sets the panel's backlight level on both power sources.</summary>
    /// <param name="percent">The level, clamped to 0 to 100.</param>
    /// <returns>Whether the driver took it.</returns>
    internal static unsafe bool TrySetBrightness(int percent)
    {
        byte level = (byte)Math.Clamp(percent, 0, 100);
        using SafeFileHandle device = OpenLcd();
        if (device.IsInvalid)
        {
            return false;
        }

        byte* request = stackalloc byte[3];
        request[0] = PolicyBoth;
        request[1] = level;
        request[2] = level;
        return DeviceIoControl(
            device,
            IoctlVideoSetDisplayBrightness,
            (nint)request,
            3,
            0,
            0,
            out _,
            0);
    }

    private static SafeFileHandle OpenLcd() => CreateFileW(
        @"\\.\LCD",
        GenericRead | GenericWrite,
        ShareReadWrite,
        0,
        OpenExisting,
        0,
        0);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode, nint inBuffer, uint inBufferSize,
        nint outBuffer, uint outBufferSize, out uint bytesReturned, nint overlapped);
}
