using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowsDeviceControl;

/// <summary>Supported Windows CCD display enumeration.</summary>
public static partial class DisplayTopology
{
    /// <summary>
    ///     Serializes every display write in this process: layout apply, mode apply, the transient primary mode,
    ///     scaling and advanced colour. CCD state is machine-wide and every host in the process shares this
    ///     assembly, so the gate is static. Reads never take it, so a long mode probe cannot hold up a write.
    /// </summary>
    internal static readonly object WriteGate = new();

    /// <summary>Captures active display paths and monitor identities without changing display state.</summary>
    /// <returns>
    ///     A detached snapshot in Windows path-priority order. A path whose target or source name cannot be read
    ///     (a display dropping out, or an indirect or virtual display) is left out instead of failing the rest.
    /// </returns>
    /// <exception cref="Win32Exception">The CCD query itself failed.</exception>
    public static DisplayTopologySnapshot CaptureActive()
    {
        var native = Query(OnlyActivePaths, "Active display topology query failed.");
        List<ActiveDisplayPath> result = new(native.Paths.Length);
        foreach (var path in native.Paths)
        {
            try
            {
                result.Add(ToActive(path, ReadTarget(path)));
            }
            catch (Win32Exception)
            {
                // Unrelated to the displays that can be read; the caller still gets those.
            }
        }

        return new DisplayTopologySnapshot(result.AsReadOnly(), DateTimeOffset.UtcNow);
    }

    /// <summary>Describes one active path whose target identity is already read.</summary>
    /// <exception cref="Win32Exception">The source name could not be read.</exception>
    internal static ActiveDisplayPath ToActive(PathInfo path, DisplayTargetIdentity identity)
    {
        return new ActiveDisplayPath(identity, ReadSourceName(path), path.TargetInfo.OutputTechnology,
            path.TargetInfo.RefreshRate.Numerator, path.TargetInfo.RefreshRate.Denominator);
    }

    /// <summary>
    ///     Finds the active path currently driving one monitor. The route changes on hotplug, so
    ///     it is resolved per call rather than stored. An unreadable path is skipped; when nothing readable
    ///     matches and an unreadable path sits on the identity's last known route, or the query itself fails,
    ///     the display is reported unreadable rather than inactive.
    /// </summary>
    internal static ActiveLookup FindActive(DisplayTargetIdentity target, out PathInfo path)
    {
        path = default;
        PathInfo[] paths;
        try
        {
            paths = Query(OnlyActivePaths, "Active display topology query failed.").Paths;
        }
        catch (Win32Exception)
        {
            return ActiveLookup.Unreadable;
        }

        var unreadable = false;
        foreach (var candidate in paths)
        {
            DisplayTargetIdentity identity;
            try
            {
                identity = ReadTarget(candidate);
            }
            catch (Win32Exception)
            {
                unreadable |= candidate.TargetInfo.AdapterId.LowPart == target.AdapterLowPart
                              && candidate.TargetInfo.AdapterId.HighPart == target.AdapterHighPart
                              && candidate.TargetInfo.Id == target.TargetId;
                continue;
            }

            if (target.Matches(identity))
            {
                path = candidate;
                return ActiveLookup.Found;
            }
        }

        return unreadable ? ActiveLookup.Unreadable : ActiveLookup.NotActive;
    }

    internal static NativeSnapshot Query(uint flags, string failure = "Display topology query failed.")
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var status = GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount);
            if (status != 0)
            {
                throw new Win32Exception(status, "Display topology buffer sizing failed.");
            }

            var paths = new PathInfo[pathCount];
            var modes = new ModeInfo[modeCount];
            status = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, 0);
            if (status == Win32Error.ErrorInsufficientBuffer)
            {
                continue;
            }

            if (status != 0)
            {
                throw new Win32Exception(status, failure);
            }

            if (pathCount != paths.Length)
            {
                Array.Resize(ref paths, checked((int)pathCount));
            }

            if (modeCount != modes.Length)
            {
                Array.Resize(ref modes, checked((int)modeCount));
            }

            return new NativeSnapshot(paths, modes);
        }

        throw new Win32Exception((int)Win32Error.ErrorInsufficientBuffer,
            "Display topology changed repeatedly during capture.");
    }

    internal static unsafe DisplayTargetIdentity ReadTarget(PathInfo path)
    {
        TargetDeviceName target = new()
            { Header = Header<TargetDeviceName>(GetTargetName, path.TargetInfo.AdapterId, path.TargetInfo.Id) };
        var status = DisplayConfigGetDeviceInfo(ref target);
        if (status != 0)
        {
            throw new Win32Exception(status, "Display target identity query failed.");
        }

        var (manufacturerId, productCodeId) =
            DecodeEdidIds(target.Flags, target.EdidManufacturerId, target.EdidProductCodeId);
        return new DisplayTargetIdentity(NativeText.ReadFixed(target.MonitorDevicePath, 128),
            manufacturerId, productCodeId, NativeText.ReadFixed(target.MonitorFriendlyDeviceName, 64),
            path.TargetInfo.AdapterId.LowPart, path.TargetInfo.AdapterId.HighPart, path.TargetInfo.Id);
    }

    /// <summary>Decodes IDs only when DISPLAYCONFIG_TARGET_DEVICE_NAME_FLAG_EDID_IDS_VALID (0x4) is set.</summary>
    internal static (ushort? ManufacturerId, ushort? ProductCodeId) DecodeEdidIds(uint flags, ushort manufacturerId,
        ushort productCodeId)
    {
        const uint edidIdsValid = 1u << 2;
        if ((flags & edidIdsValid) != 0)
        {
            return (manufacturerId, productCodeId);
        }

        return (null, null);
    }

    /// <summary>
    ///     Reads a path's monitor identity once per target route within one observation. All
    ///     paths query many possible routes to the same target, and its identity is the same on each.
    ///     A failed read is not remembered.
    /// </summary>
    internal static DisplayTargetIdentity ReadTarget(PathInfo path, Dictionary<RouteKey, DisplayTargetIdentity> read)
    {
        RouteKey key = new(path.TargetInfo.AdapterId, path.TargetInfo.Id);
        if (!read.TryGetValue(key, out var identity))
        {
            identity = ReadTarget(path);
            read[key] = identity;
        }

        return identity;
    }

    private static unsafe string ReadSourceName(PathInfo path)
    {
        SourceDeviceName source = new()
            { Header = Header<SourceDeviceName>(GetSourceName, path.SourceInfo.AdapterId, path.SourceInfo.Id) };
        var status = DisplayConfigGetDeviceInfo(ref source);
        if (status != 0)
        {
            throw new Win32Exception(status, "Display source identity query failed.");
        }

        return NativeText.ReadFixed(source.ViewGdiDeviceName, 32);
    }

    /// <summary>Supplies a complete configuration and lets Windows adjust modes to fit it.</summary>
    internal static int Supply(PathInfo[] paths, ModeInfo[] modes, uint flags)
    {
        return SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes,
            UseSupplied | AllowChanges | flags);
    }

    /// <summary>Builds the header every CCD device-info packet starts with.</summary>
    internal static DeviceInfoHeader Header<T>(int type, Luid adapter, uint id) where T : struct
    {
        return new DeviceInfoHeader { Type = type, Size = (uint)Marshal.SizeOf<T>(), AdapterId = adapter, Id = id };
    }
}

/// <summary>Where <see cref="DisplayTopology.FindActive" /> found a monitor.</summary>
internal enum ActiveLookup
{
    Found,
    NotActive,
    Unreadable
}
