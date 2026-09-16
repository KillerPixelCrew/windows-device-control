using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using static WindowsDeviceControl.Win32Error;

namespace WindowsDeviceControl;

/// <summary>Owns powrprof buffers and preserves native failures as Win32Exception codes.</summary>
public static partial class WindowsPower
{
    private const uint AccessScheme = 16;
    private const uint MaximumNameBytes = 65536;

    /// <summary>Returns one installed scheme, or null at the end. Native failures throw Win32Exception.</summary>
    /// <param name="index">Zero-based enumeration index.</param>
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

    /// <summary>Every installed scheme, in enumeration order.</summary>
    internal static List<Guid> EnumerateSchemes()
    {
        List<Guid> schemes = [];
        for (uint index = 0; EnumerateScheme(index) is { } scheme; index++)
        {
            schemes.Add(scheme);
        }

        return schemes;
    }

    /// <summary>Reads a localized scheme name with bounded buffer retries.</summary>
    /// <param name="id">Installed scheme identity.</param>
    public static unsafe string ReadSchemeName(Guid id)
    {
        uint size = 0;
        var status = PowerReadFriendlyName(0, in id, 0, 0, 0, ref size);
        if (status != ErrorMoreData)
        {
            Check(status, "PowerReadFriendlyName");
        }

        // A rename can grow the buffer between reads. Retry reads only, with a bounded allocation.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (size < 2 || size > MaximumNameBytes || size % 2 != 0)
            {
                throw Failure(ErrorInvalidData, "PowerReadFriendlyName");
            }

            var buffer = new byte[size];
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

    /// <summary>Reads the active scheme and releases the native allocation on every outcome.</summary>
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

    /// <summary>Requests a scheme once; failures throw Win32Exception. Read back to confirm.</summary>
    /// <param name="id">Installed scheme identity.</param>
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
