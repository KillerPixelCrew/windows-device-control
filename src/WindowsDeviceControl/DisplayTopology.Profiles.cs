using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace WindowsDeviceControl;

public static partial class DisplayTopology
{
    /// <summary>Captures the complete active topology in a serializable profile.</summary>
    /// <returns>A versioned profile containing monitor identities and native CCD records.</returns>
    /// <remarks>The records contain no process pointers. Validate immediately before application.</remarks>
    public static DisplayProfile CaptureProfile()
    {
        NativeSnapshot native = Query(OnlyActivePaths);
        var targets = native.Paths.Select(ReadTarget).ToArray();
        return new(1, targets, Encode(native.Paths), Encode(native.Modes));
    }

    /// <summary>Validates a stored profile against current monitor identities and Windows CCD.</summary>
    /// <param name="profile">Previously captured profile.</param>
    /// <returns>A result without changing display state.</returns>
    public static DisplayProfileResult ValidateProfile(DisplayProfile profile) =>
        TryPrepare(profile, out _) ?? new(true, 0, false, false, "Profile is valid for the current topology.");

    /// <summary>Validates, applies and confirms a stored profile, rolling back after an unconfirmed application.</summary>
    /// <param name="profile">Previously captured profile.</param>
    /// <returns>Detailed application and rollback evidence.</returns>
    /// <remarks>This can rearrange or blank displays. It captures rollback state before mutation and never retries automatically.</remarks>
    public static DisplayProfileResult ApplyProfile(DisplayProfile profile)
    {
        if (TryPrepare(profile, out NativeSnapshot requested) is { } refused) { return refused; }
        NativeSnapshot rollback;
        try { rollback = Query(OnlyActivePaths); }
        catch (Win32Exception ex) { return new(false, ex.NativeErrorCode, false, false, "Could not capture rollback topology; nothing was applied."); }
        int status = Supply(requested, SdcApply | SaveToDatabase);
        if (status == 0)
        {
            try
            {
                var observed = CaptureActive();
                if (profile.Targets.All(target => observed.Paths.Exists(path => target.Matches(path.Target))))
                { return new(true, 0, false, false, "Profile applied and target presence was confirmed."); }
            }
            catch (Win32Exception) { }
        }
        // Persist the rollback like the apply above did, so an unconfirmed profile does not leave
        // the broken topology saved in the CCD database for Windows to replay at the next sign-in
        // or hotplug. DisplayLayouts' own rollback already does this.
        int rollbackStatus = Supply(rollback, SdcApply | SaveToDatabase);
        return new(false, status, true, rollbackStatus == 0,
            rollbackStatus == 0 ? "Profile application was not confirmed; the captured topology was restored."
                : $"Profile application was not confirmed and rollback failed with status {rollbackStatus}.");
    }

    /// <summary>Rematches a stored profile to the current topology and asks Windows to validate it
    /// without applying anything.</summary>
    /// <returns>Null when the profile is ready to apply; otherwise the result that refuses it.</returns>
    private static DisplayProfileResult? TryPrepare(DisplayProfile profile, out NativeSnapshot requested)
    {
        try { requested = Rematch(profile); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            requested = null!;
            return Failure(ex);
        }
        int status = Supply(requested, SdcValidate);
        return status == 0 ? null : new(false, status, false, false, "Windows rejected the profile during validation.");
    }

    private static NativeSnapshot Rematch(DisplayProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.FormatVersion != 1 || profile.Targets is null || profile.PathData is null || profile.ModeData is null
            || profile.Targets.Count is 0 or > 256 || profile.Targets.Count != profile.PathData.Count || profile.ModeData.Count > 1024)
        { throw new ArgumentException("Display profile has an unsupported shape.", nameof(profile)); }
        PathInfo[] paths = Decode<PathInfo>(profile.PathData);
        ModeInfo[] modes = Decode<ModeInfo>(profile.ModeData);
        NativeSnapshot current = Query(AllPaths);
        var currentTargets = current.Paths.Select(path => (Path: path, Target: ReadTarget(path))).ToArray();
        Dictionary<RouteKey, RouteKey> replacements = [];
        for (int index = 0; index < paths.Length; index++)
        {
            var match = currentTargets.FirstOrDefault(candidate => profile.Targets[index].Matches(candidate.Target));
            if (match.Target is null) { throw new InvalidOperationException("A saved display is not present in the current topology."); }
            PathInfo saved = paths[index];
            replacements[new(saved.TargetInfo.AdapterId, saved.TargetInfo.Id)] = new(match.Path.TargetInfo.AdapterId, match.Path.TargetInfo.Id);
            replacements[new(saved.SourceInfo.AdapterId, saved.SourceInfo.Id)] = new(match.Path.SourceInfo.AdapterId, match.Path.SourceInfo.Id);
            paths[index].TargetInfo.AdapterId = match.Path.TargetInfo.AdapterId;
            paths[index].TargetInfo.Id = match.Path.TargetInfo.Id;
            paths[index].SourceInfo.AdapterId = match.Path.SourceInfo.AdapterId;
            paths[index].SourceInfo.Id = match.Path.SourceInfo.Id;
        }
        for (int index = 0; index < modes.Length; index++)
        {
            var key = new RouteKey(modes[index].AdapterId, modes[index].Id);
            if (replacements.TryGetValue(key, out RouteKey replacement))
            {
                modes[index].AdapterId = replacement.Adapter;
                modes[index].Id = replacement.Id;
            }
        }
        return new(paths, modes);
    }

    internal static unsafe IReadOnlyList<byte[]> Encode<T>(T[] values) where T : unmanaged
    {
        int size = sizeof(T);
        var encoded = new byte[values.Length][];
        for (int index = 0; index < values.Length; index++)
        {
            encoded[index] = new byte[size];
            fixed (T* source = &values[index])
            fixed (byte* destination = encoded[index]) { Buffer.MemoryCopy(source, destination, size, size); }
        }
        return encoded;
    }

    internal static unsafe T[] Decode<T>(IReadOnlyList<byte[]> values) where T : unmanaged
    {
        int size = sizeof(T);
        T[] decoded = new T[values.Count];
        for (int index = 0; index < values.Count; index++)
        {
            if (values[index] is not { Length: var length } data || length != size)
            { throw new ArgumentException("Display profile contains an invalid native record."); }
            fixed (byte* source = data) { decoded[index] = *(T*)source; }
        }
        return decoded;
    }

    private static DisplayProfileResult Failure(Exception exception) => new(false,
        exception is Win32Exception native ? native.NativeErrorCode : 0, false, false, Bound(exception.Message));
}
