using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WindowsDeviceControl;

/// <summary>One decoded system-wide power request.</summary>
/// <param name="HoldsDisplay">Whether the request pins the display on.</param>
/// <param name="HoldsSystem">Whether the request blocks standby.</param>
/// <param name="HoldsAwayMode">
///     Whether the request holds away mode (S3-era; treated
///     like a standby hold by consumers).
/// </param>
/// <param name="CallerType">REQUESTER_TYPE: 0 kernel, 1 process, 2 service.</param>
/// <param name="Name">
///     Process image path (NT device form) or driver device
///     description; empty when the entry carries none.
/// </param>
/// <param name="Pid">The requesting process id; null for kernel requesters.</param>
/// <param name="Reason">The diagnostic reason string, when one was supplied.</param>
public readonly record struct PowerRequestEntry(
    bool HoldsDisplay,
    bool HoldsSystem,
    bool HoldsAwayMode,
    uint CallerType,
    string Name,
    uint? Pid,
    string? Reason);

/// <summary>
///     Enumerates system-wide power requests via the undocumented
///     <c>NtPowerInformation(GetPowerRequestList)</c> class — what `powercfg /requests`
///     uses internally. Ported from the maintainer's WakeWatch project (MIT, same
///     author): the returned POWER_REQUEST layout is undocumented and varies by Windows
///     build, so every read goes through bounds-checked accessors and any structural
///     surprise yields "unknown" (null) — never a plausible-looking wrong answer, and
///     in particular never a false "all clear".
/// </summary>
public static partial class PowerRequestList
{
    private const int GetPowerRequestListClass = 45;
    private const int StatusBufferTooSmall = unchecked((int)0xC0000023);
    private const int StatusAccessDenied = unchecked((int)0xC0000022);
    private const int InitialBuffer = 4096;
    private const int MaxBuffer = 1024 * 1024;

    /// <summary>Sanity ceiling on the request count; a live system shows ~50.</summary>
    private const int MaxRequests = 100_000;

    /// <summary>Sanity ceiling on a single UTF-16 string, in code units.</summary>
    private const int MaxStringUnits = 4096;

    /// <summary>
    ///     Queries and decodes the current request list. Entries is null when
    ///     no trustworthy answer exists; Error then carries the human reason (most
    ///     commonly missing elevation — the same restriction powercfg has).
    /// </summary>
    public static (IReadOnlyList<PowerRequestEntry>? Entries, string? Error) Query()
    {
        // The call reports only that the buffer was too small, not the size it needs, so the
        // buffer doubles up to the bound and is decoded where Windows wrote it.
        for (var length = InitialBuffer; length <= MaxBuffer; length *= 2)
        {
            var buffer = new byte[length];
            var status = NtPowerInformation(
                GetPowerRequestListClass, 0, 0, buffer, (uint)length);
            if (status == 0)
            {
                var entries = DecodeWithBuild(buffer, NtBuild());
                return entries is null
                    ? (null, "Unrecognized power request layout")
                    : (entries, null);
            }

            if (status == StatusAccessDenied)
            {
                return (null, "Administrator rights required");
            }

            if (status != StatusBufferTooSmall)
            {
                return (null, $"Query failed (NTSTATUS 0x{(uint)status:X8})");
            }
        }

        return (null, "Request list too large to read");
    }

    private static uint NtBuild()
    {
        RtlGetNtVersionNumbers(out _, out _, out var build);
        // The high nibble is a flag area, not part of the build number.
        return build & 0x0FFFFFFF;
    }

    /// <summary>
    ///     Entries in the POWER_REQUEST counter array, per
    ///     POWER_REQUEST_SUPPORTED_TYPES_Vn. Keyed off the OS build on purpose:
    ///     SupportedRequestMask is NOT reliable (kernel requesters were observed
    ///     reporting 0x12 rather than a full 0x3F).
    /// </summary>
    internal static int ModeCount(uint build)
    {
        return build switch
        {
            >= 14393 => 6, // V4, Win10 RS1+
            >= 9600 => 5, // V3, Win8.1 / Win10 TH1-TH2
            >= 9200 => 9, // V2, Win8
            _ => 3 // V1, Win7
        };
    }

    /// <summary>
    ///     Offset of DIAGNOSTIC_BUFFER within POWER_REQUEST: the mask plus the
    ///     counter array, rounded up to SIZE_T alignment.
    /// </summary>
    internal static int DiagOffset(int modes)
    {
        return (4 + modes * 4 + 7) & ~7;
    }

    /// <summary>
    ///     Decodes a raw POWER_REQUEST_LIST buffer; null on any structural
    ///     surprise so the caller shows "unknown" instead of a wrong state.
    /// </summary>
    internal static List<PowerRequestEntry>? DecodeWithBuild(ReadOnlySpan<byte> buffer, uint build)
    {
        var modes = ModeCount(build);
        var diagOffset = DiagOffset(modes);

        if (!TryUInt64(buffer, 0, out var rawCount) || rawCount > MaxRequests)
        {
            return null;
        }

        var count = (int)rawCount;
        var entries = new List<PowerRequestEntry>(Math.Min(count, 1024));
        for (var i = 0; i < count; i++)
        {
            if (!TryUInt64(buffer, 8 + i * 8, out var requestOffset)
                || requestOffset > int.MaxValue
                || DecodeOne(buffer, (int)requestOffset, modes, diagOffset) is not { } entry)
            {
                return null;
            }

            entries.Add(entry);
        }

        return entries;
    }

    private static PowerRequestEntry? DecodeOne(
        ReadOnlySpan<byte> buffer, int request, int modes, int diagOffset)
    {
        // Validate the whole counter array is present, even for modes we ignore.
        if (!TryUInt32(buffer, request + 4 + (modes - 1) * 4, out _))
        {
            return null;
        }

        Span<uint> counts = stackalloc uint[6];
        for (var mode = 0; mode < Math.Min(modes, 6); mode++)
        {
            if (!TryUInt32(buffer, request + 4 + mode * 4, out counts[mode]))
            {
                return null;
            }
        }

        var db = request + diagOffset;

        // DIAGNOSTIC_BUFFER.Size — a cheap structural plausibility check.
        if (!TryUInt64(buffer, db, out var size) || size == 0 || size > (ulong)buffer.Length)
        {
            return null;
        }

        if (!TryUInt32(buffer, db + 8, out var callerType) || callerType > 2)
        {
            return null;
        }

        // Union at +16. Both arms start with a string offset relative to the
        // DIAGNOSTIC_BUFFER.
        if (!TryUInt64(buffer, db + 16, out var nameOffset) || nameOffset > int.MaxValue)
        {
            return null;
        }

        var name = "";
        if (nameOffset != 0 && !TryWString(buffer, db + (int)nameOffset, out name))
        {
            return null;
        }

        uint? pid = null;
        if (callerType != 0)
        {
            if (!TryUInt32(buffer, db + 24, out var pidValue))
            {
                return null;
            }

            pid = pidValue;
        }

        return new PowerRequestEntry(
            counts[0] > 0,
            counts[1] > 0,
            counts[2] > 0,
            callerType,
            name,
            pid,
            ReadReason(buffer, db));
    }

    /// <summary>
    ///     COUNTED_REASON_CONTEXT_RELATIVE at DIAGNOSTIC_BUFFER + ReasonOffset.
    ///     Only the simple-string form is read; a missing or unreadable reason is not
    ///     an error.
    /// </summary>
    private static string? ReadReason(ReadOnlySpan<byte> buffer, int db)
    {
        if (!TryUInt64(buffer, db + 32, out var reasonOffset)
            || reasonOffset == 0 || reasonOffset > int.MaxValue)
        {
            return null;
        }

        var context = db + (int)reasonOffset;
        if (!TryUInt32(buffer, context, out var flags)
            || (flags & 0x1u) == 0)
        {
            return null;
        }

        if (!TryUInt64(buffer, context + 8, out var stringOffset)
            || stringOffset == 0 || stringOffset > int.MaxValue)
        {
            return null;
        }

        return TryWString(buffer, context + (int)stringOffset, out var reason) && reason.Length > 0
            ? reason
            : null;
    }

    // Bounds-checked readers: every one reports false instead of reading past the
    // buffer, and negative offsets (from int overflow upstream) are rejected.

    private static bool TryUInt32(ReadOnlySpan<byte> buffer, int offset, out uint value)
    {
        value = 0;
        if (offset < 0 || offset > buffer.Length - 4)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset, 4));
        return true;
    }

    private static bool TryUInt64(ReadOnlySpan<byte> buffer, int offset, out ulong value)
    {
        value = 0;
        if (offset < 0 || offset > buffer.Length - 8)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(offset, 8));
        return true;
    }

    /// <summary>
    ///     Reads a NUL-terminated UTF-16 string; false if it runs off the end
    ///     of the buffer without a terminator or exceeds the length cap.
    /// </summary>
    private static bool TryWString(ReadOnlySpan<byte> buffer, int offset, out string value)
    {
        value = "";
        if (offset < 0 || offset >= buffer.Length)
        {
            return false;
        }

        // Windows buffers are little-endian UTF-16. A terminator may follow the last allowed unit.
        var units = MemoryMarshal.Cast<byte, char>(buffer[offset..]);
        var length = units[..Math.Min(units.Length, MaxStringUnits + 1)].IndexOf('\0');
        if (length < 0)
        {
            return false;
        }

        value = new string(units[..length]);
        return true;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtPowerInformation(
        int informationLevel, nint inputBuffer, uint inputLength, [Out] byte[] outputBuffer, uint outputLength);

    [LibraryImport("ntdll.dll")]
    private static partial void RtlGetNtVersionNumbers(out uint major, out uint minor, out uint build);
}
