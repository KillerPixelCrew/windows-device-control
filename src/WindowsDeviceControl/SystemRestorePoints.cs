using System;
using System.Runtime.InteropServices;

namespace WindowsDeviceControl;

/// <summary>What a restore-point request did.</summary>
public enum SystemRestorePointResult
{
    /// <summary>Windows created the restore point.</summary>
    Created,

    /// <summary>System Restore is turned off for the system drive or by policy; nothing was created.</summary>
    Disabled,

    /// <summary>Windows refused or failed the request; <see cref="SystemRestorePointOutcome.Error" /> says why.</summary>
    Failed
}

/// <summary>The result of one restore-point request.</summary>
/// <param name="Result">Whether a point was created.</param>
/// <param name="Error">The Win32 status for <see cref="SystemRestorePointResult.Failed" />, otherwise zero.</param>
public sealed record SystemRestorePointOutcome(SystemRestorePointResult Result, int Error);

/// <summary>Creates Windows System Restore points through the installer restore-point API.</summary>
/// <remarks>
///     Uses <c>SRSetRestorePointW</c>, the API Windows provides for installers, rather than the WMI or
///     <c>Checkpoint-Computer</c> path, which Windows skips without an error when another point was made
///     within 24 hours. The call is synchronous and can take tens of seconds while the shadow copy is
///     made, so it belongs on a worker. It requires administrator permission. The caller owns consent;
///     a created point is never removed by this library.
/// </remarks>
public static partial class SystemRestorePoints
{
    private const int ApplicationInstall = 0;
    private const int BeginSystemChange = 100;
    private const int EndSystemChange = 101;
    private const int DescriptionLength = 256;
    private const int ErrorServiceDisabled = 1058;

    /// <summary>Creates one application-install restore point with the given description.</summary>
    /// <param name="description">What the point is for, as System Restore lists it; truncated to 255 characters.</param>
    /// <returns>Whether a point was created, System Restore is off, or the request failed.</returns>
    public static SystemRestorePointOutcome Create(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        RestorePointInfo begin = new() { EventType = BeginSystemChange, RestorePointType = ApplicationInstall };
        WriteDescription(ref begin, description);
        bool created;
        StateManagerStatus status;
        try
        {
            created = SetRestorePoint(ref begin, out status);
        }
        catch (EntryPointNotFoundException)
        {
            return new SystemRestorePointOutcome(SystemRestorePointResult.Disabled, 0);
        }
        catch (DllNotFoundException)
        {
            // Server SKUs and stripped images ship without System Restore.
            return new SystemRestorePointOutcome(SystemRestorePointResult.Disabled, 0);
        }

        if (!created)
        {
            return status.Status == ErrorServiceDisabled
                ? new SystemRestorePointOutcome(SystemRestorePointResult.Disabled, 0)
                : new SystemRestorePointOutcome(SystemRestorePointResult.Failed, (int)status.Status);
        }

        // The point exists from the begin call; ending the change closes the sequence so System Restore
        // does not hold it open until the next boot.
        RestorePointInfo end = new()
        {
            EventType = EndSystemChange, RestorePointType = ApplicationInstall,
            SequenceNumber = status.SequenceNumber
        };
        SetRestorePoint(ref end, out _);
        return new SystemRestorePointOutcome(SystemRestorePointResult.Created, 0);
    }

    private static unsafe void WriteDescription(ref RestorePointInfo info, string description)
    {
        var length = Math.Min(description.Length, DescriptionLength - 1);
        fixed (char* target = info.Description)
        {
            description.AsSpan(0, length).CopyTo(new Span<char>(target, DescriptionLength));
            target[length] = '\0';
        }
    }

    // srrestoreptapi.h declares both structures under #pragma pack(1).
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private unsafe struct RestorePointInfo
    {
        public int EventType;
        public int RestorePointType;
        public long SequenceNumber;
        public fixed char Description[DescriptionLength];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct StateManagerStatus
    {
        public uint Status;
        public long SequenceNumber;
    }

    [LibraryImport("srclient.dll", EntryPoint = "SRSetRestorePointW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetRestorePoint(ref RestorePointInfo info, out StateManagerStatus status);
}
