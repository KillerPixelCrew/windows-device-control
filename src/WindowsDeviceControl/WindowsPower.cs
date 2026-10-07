using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using static WindowsDeviceControl.Win32Error;

namespace WindowsDeviceControl;

/// <summary>Reads and changes Windows power schemes, policy, status and session transitions.</summary>
/// <remarks>
///     Native operations are synchronous unless named Async; use a worker thread for blocking calls.
///     Policy writes are single attempts without readback or automatic rollback. Windows permissions
///     and hardware support remain authoritative; Win32 failures retain their native error codes.
/// </remarks>
public static partial class WindowsPower
{
    private const uint AccessScheme = 16;

    /// <summary>Returns one installed scheme, or null at the end. Native failures throw Win32Exception.</summary>
    /// <param name="index">Zero-based enumeration index.</param>
    /// <returns>The scheme at <paramref name="index" />, or null past the last one.</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">Enumeration failed or returned an invalid identity.</exception>
    public static Guid? EnumerateScheme(uint index)
    {
        uint size = 16;
        var status = PowerEnumerate(0, 0, 0, AccessScheme, index, out var id, ref size);
        if (status == ErrorNoMoreItems)
        {
            return null;
        }

        Check(status, "PowerEnumerate");
        if (size != 16 || id == Guid.Empty)
        {
            throw Failure(ErrorInvalidData, "PowerEnumerate");
        }

        return id;
    }

    /// <summary>Every installed scheme, in enumeration order. Native failures throw Win32Exception.</summary>
    /// <returns>The installed scheme identities; empty only when Windows lists none.</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">Enumeration failed or returned an invalid identity.</exception>
    public static IReadOnlyList<Guid> EnumerateSchemes()
    {
        List<Guid> schemes = [];
        for (uint index = 0; EnumerateScheme(index) is { } scheme; index++)
        {
            schemes.Add(scheme);
        }

        return schemes;
    }

    /// <summary>Reads a localized scheme name, sizing the buffer from what Windows reports.</summary>
    /// <param name="id">Installed scheme identity.</param>
    /// <returns>The scheme's name, or its identity text when the name is blank.</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">The name could not be read.</exception>
    public static unsafe string ReadSchemeName(Guid id)
    {
        uint size = 0;
        var status = PowerReadFriendlyName(0, in id, 0, 0, 0, ref size);
        if (status != ErrorMoreData)
        {
            Check(status, "PowerReadFriendlyName");
        }

        // A rename can grow the buffer between reads, so the read is repeated a few times. The
        // allocation follows the size Windows reports.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (size < 2 || size % 2 != 0)
            {
                throw Failure(ErrorInvalidData, "PowerReadFriendlyName");
            }

            var buffer = new byte[checked((int)size)];
            fixed (byte* pointer = buffer)
            {
                status = PowerReadFriendlyName(0, in id, 0, 0, (nint)pointer, ref size);
            }

            if (status == ErrorMoreData)
            {
                continue;
            }

            Check(status, "PowerReadFriendlyName");
            return DecodeName(buffer, size, id);
        }

        throw Failure(ErrorMoreData, "PowerReadFriendlyName");
    }

    /// <summary>Decodes a complete UTF-16 scheme name returned by Windows.</summary>
    /// <param name="buffer">Native output bytes.</param>
    /// <param name="size">Reported byte count, including the terminator.</param>
    /// <param name="id">Scheme identity used when the decoded name is blank.</param>
    /// <returns>The decoded name or the scheme GUID in D format.</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">The byte count or terminator is invalid.</exception>
    internal static string DecodeName(byte[] buffer, uint size, Guid id)
    {
        if (size < 2 || size > buffer.Length || size % 2 != 0
            || buffer[(int)size - 2] != 0 || buffer[(int)size - 1] != 0)
        {
            throw Failure(ErrorInvalidData, "PowerReadFriendlyName");
        }

        var name = Encoding.Unicode.GetString(buffer, 0, (int)size - 2);
        return string.IsNullOrWhiteSpace(name) ? id.ToString("D") : name;
    }

    /// <summary>Reads the active power scheme identity.</summary>
    /// <returns>The active scheme identity.</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">The active scheme could not be read.</exception>
    public static Guid GetActiveScheme()
    {
        var status = PowerGetActiveScheme(0, out var pointer);
        try
        {
            Check(status, "PowerGetActiveScheme");
            if (pointer == 0)
            {
                throw Failure(ErrorInvalidData, "PowerGetActiveScheme");
            }

            var id = Marshal.PtrToStructure<Guid>(pointer);
            if (id == Guid.Empty)
            {
                throw Failure(ErrorInvalidData, "PowerGetActiveScheme");
            }

            return id;
        }
        finally
        {
            if (pointer != 0)
            {
                LocalFree(pointer);
            }
        }
    }

    /// <summary>Activates a scheme once. Does not retry; a failed request throws.</summary>
    /// <param name="id">Installed scheme identity.</param>
    /// <exception cref="System.ComponentModel.Win32Exception">Windows refused the request.</exception>
    public static void SetActiveScheme(Guid id)
    {
        Check(PowerSetActiveScheme(0, in id), "PowerSetActiveScheme");
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetActiveScheme(nint userRootPowerKey, out nint activePolicyGuid);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerSetActiveScheme(nint userRootPowerKey, in Guid schemeGuid);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerEnumerate(
        nint rootPowerKey, nint schemeGuid, nint subGroupGuid, uint accessFlags,
        uint index, out Guid buffer, ref uint bufferSize);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerReadFriendlyName(
        nint rootPowerKey, in Guid schemeGuid, nint subGroupGuid, nint powerSettingGuid,
        nint buffer, ref uint bufferSize);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerReadACValueIndex(
        nint rootPowerKey, in Guid schemeGuid, in Guid subGroupGuid, in Guid powerSettingGuid,
        out uint acValueIndex);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerReadDCValueIndex(
        nint rootPowerKey, in Guid schemeGuid, in Guid subGroupGuid, in Guid powerSettingGuid,
        out uint dcValueIndex);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerWriteACValueIndex(
        nint rootPowerKey, in Guid schemeGuid, in Guid subGroupGuid, in Guid powerSettingGuid,
        uint acValueIndex);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerWriteDCValueIndex(
        nint rootPowerKey, in Guid schemeGuid, in Guid subGroupGuid, in Guid powerSettingGuid,
        uint dcValueIndex);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
