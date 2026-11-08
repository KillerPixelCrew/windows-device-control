using System;
using System.Runtime.InteropServices;

namespace WindowsDeviceControl;

/// <summary>Requests addressed to another process's top-level window, and what a window says about itself.</summary>
/// <remarks>Nothing here terminates a process; the window's owner decides what a request does.</remarks>
public static partial class DesktopWindows
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const ushort VariantLpwstr = 31;
    private static readonly Guid PropertyStoreId = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    private static readonly NativePropertyKey AppUserModelIdKey =
        new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    /// <summary>The application user model id a window belongs to.</summary>
    /// <param name="window">The window.</param>
    /// <param name="processId">The process that owns it.</param>
    /// <returns>
    ///     The window's own id, which is the hosted application's for a UWP frame owned by ApplicationFrameHost,
    ///     then the id of the owning packaged process; null when it has neither.
    /// </returns>
    public static string? ApplicationUserModelId(nint window, uint processId)
    {
        return window == 0 ? null : WindowApplicationId(window) ?? ProcessApplicationId(processId);
    }

    private static string? WindowApplicationId(nint window)
    {
        var interfaceId = PropertyStoreId;
        if (SHGetPropertyStoreForWindow(window, ref interfaceId, out var pointer) != 0 || pointer == 0)
        {
            return null;
        }

        object? store = null;
        try
        {
            store = Marshal.GetObjectForIUnknown(pointer);
            var key = AppUserModelIdKey;
            if (((INativePropertyStore)store).GetValue(ref key, out var value) != 0)
            {
                return null;
            }

            try
            {
                return value.Type == VariantLpwstr ? Marshal.PtrToStringUni(value.Pointer) : null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        catch (InvalidCastException)
        {
            return null;
        }
        finally
        {
            Marshal.Release(pointer);
            if (store is not null)
            {
                Marshal.FinalReleaseComObject(store);
            }
        }
    }

    private static unsafe string? ProcessApplicationId(uint processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, 0, processId);
        if (process == 0)
        {
            return null;
        }

        try
        {
            var length = 256u;
            var buffer = stackalloc char[(int)length];
            return GetApplicationUserModelId(process, ref length, buffer) == 0
                ? new string(buffer, 0, (int)Math.Max(length, 1) - 1)
                : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetPropertyStoreForWindow(nint window, ref Guid interfaceId, out nint store);

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(ref NativePropVariant value);

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenProcess(uint access, int inherit, uint processId);

    [LibraryImport("kernel32.dll")]
    private static partial int CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetApplicationUserModelId(nint process, ref uint length, char* buffer);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePropertyKey(Guid FormatId, uint PropertyId);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePropVariant
    {
        public ushort Type;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public nint Pointer;
        public nint Second;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INativePropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out NativePropertyKey key);

        [PreserveSig]
        int GetValue(ref NativePropertyKey key, out NativePropVariant value);
    }

    /// <summary>
    ///     Posts <c>WM_CLOSE</c> to a window, the same request as its close button, when it still belongs
    ///     to the process it was listed for.
    /// </summary>
    /// <param name="window">The window that was listed.</param>
    /// <param name="processId">The process that owned it when it was listed; a reused handle is left alone.</param>
    /// <returns>Whether the request was posted to that window.</returns>
    /// <remarks>
    ///     Posted, not sent, so a hung window cannot stall the caller. The application may ask the user to
    ///     confirm first or ignore the request.
    /// </remarks>
    public static bool RequestClose(nint window, uint processId)
    {
        if (window == 0 || User32.GetWindowThreadProcessId(window, out var owner) == 0 || owner != processId)
        {
            return false;
        }

        return User32.PostMessageW(window, User32.WmClose, 0, 0);
    }
}
