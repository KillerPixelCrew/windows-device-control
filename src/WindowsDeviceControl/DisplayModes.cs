using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;

namespace WindowsDeviceControl;

/// <summary>A driver-advertised display mode, using the integer GDI refresh rate.</summary>
/// <param name="Width">Horizontal pixel count.</param>
/// <param name="Height">Vertical pixel count.</param>
/// <param name="RefreshHz">Refresh rate in hertz.</param>
public sealed record DisplayMode(int Width, int Height, int RefreshHz);

/// <summary>A fresh mode observation for one active, unambiguous display source.</summary>
/// <param name="Path">Target identity and source route.</param>
/// <param name="Current">Current mode.</param>
/// <param name="Supported">Driver modes passing validation at observation time.</param>
public sealed record DisplayModeSnapshot(
    ActiveDisplayPath Path,
    DisplayMode Current,
    IReadOnlyList<DisplayMode> Supported);

/// <summary>A mode of the primary display, including its colour depth.</summary>
/// <param name="Width">Horizontal pixel count.</param>
/// <param name="Height">Vertical pixel count.</param>
/// <param name="RefreshHz">Refresh rate in hertz.</param>
/// <param name="BitsPerPixel">Colour depth.</param>
public readonly record struct PrimaryDisplayMode(int Width, int Height, int RefreshHz, int BitsPerPixel);

/// <summary>Enumerates and applies validated modes to an explicitly identified active display.</summary>
/// <remarks>
///     Calls block on display drivers; use a worker thread. No registry settings are persisted.
///     Read/Apply reject disconnected, rerouted and cloned sources. Apply can restore a refused write
///     once while its original route remains present. Primary-display helpers provide no equivalent
///     route check or rollback. Driver acceptance does not prove physical visibility; writes are not
///     read back. Writes share a process-wide gate; reads do not.
/// </remarks>
public static partial class DisplayModes
{
    private const uint ModeFields = 0x00040000 | 0x00080000 | 0x00100000 | 0x00400000;

    // Width, height and frequency only: a transient primary-display change keeps the colour depth.
    private const uint PrimaryModeFields = 0x00080000 | 0x00100000 | 0x00400000;
    private const uint ChangeTest = 2;

    // CDS_UPDATEREGISTRY: the mode becomes the display's saved one, as Windows Settings saves it.
    private const uint ChangeUpdateRegistry = 1;

    // CDS_NORESET: with CDS_UPDATEREGISTRY, saves the mode without applying it.
    private const uint ChangeNoReset = 0x10000000;

    // ENUM_CURRENT_SETTINGS and ENUM_REGISTRY_SETTINGS.
    private const uint CurrentSettings = uint.MaxValue;
    private const uint RegistrySettings = uint.MaxValue - 1;

    // DISP_CHANGE_FAILED, returned when the primary display's current mode cannot be read.
    private const int ChangeFailed = -1;

    /// <summary>Reads current and supported modes from a fresh topology observation.</summary>
    /// <param name="target">Active target to query.</param>
    /// <returns>Null for an absent, cloned, ambiguous, unreadable or changed route; otherwise a detached snapshot.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target" /> is null.</exception>
    /// <remarks>
    ///     Each distinct mode is tested with the driver, which can take a while. That holds no lock, and the
    ///     route is checked again afterwards so an interleaved change returns null instead of a stale list.
    /// </remarks>
    public static DisplayModeSnapshot? Read(DisplayTargetIdentity target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var path = Find(target);
        if (path is null || !ReadNative(path.SourceName, uint.MaxValue, out var current))
        {
            return null;
        }

        HashSet<DisplayMode> supported = [];
        for (uint index = 0; ReadNative(path.SourceName, index, out var mode); index++)
        {
            if (mode.Width == 0 || mode.Height == 0 || mode.Frequency < 2 || mode.Bits != current.Bits)
            {
                continue;
            }

            // The driver lists a width, height and rate once per variant; one passing test offers it.
            var projected = Project(mode);
            if (supported.Contains(projected))
            {
                continue;
            }

            mode.Fields = ModeFields;
            if (Change(path.SourceName, ref mode, ChangeTest) == 0)
            {
                supported.Add(projected);
            }
        }

        if (!SameRoute(path, Find(target)))
        {
            return null;
        }

        return new DisplayModeSnapshot(path, Project(current), supported.OrderBy(mode => mode.Width)
            .ThenBy(mode => mode.Height).ThenBy(mode => mode.RefreshHz).ToArray());
    }

    /// <summary>Revalidates a selected mode and applies it once.</summary>
    /// <param name="observation">Observation from which the user selected the mode.</param>
    /// <param name="requested">Mode explicitly selected by the user.</param>
    /// <param name="persist">
    ///     Whether the mode also becomes the display's saved one, as a choice in Windows Settings does. A
    ///     refused write's write-back is saved the same way.
    /// </param>
    /// <returns>
    ///     <see cref="DisplayModeOutcome.Applied" /> when Windows accepted the write, which is not read back;
    ///     otherwise why nothing was written, or the refusal and its one write-back. No automatic retry is
    ///     performed.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="observation" /> or <paramref name="requested" /> is null.</exception>
    public static DisplayModeResult Apply(DisplayModeSnapshot observation, DisplayMode requested,
        bool persist = false)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(requested);
        lock (DisplayTopology.WriteGate)
        {
            var path = Find(observation.Path.Target);
            if (!SameRoute(observation.Path, path) || !observation.Supported.Contains(requested))
            {
                return new DisplayModeResult(DisplayModeOutcome.Stale, 0, false, false);
            }

            if (!ReadNative(path!.SourceName, uint.MaxValue, out var original))
            {
                return new DisplayModeResult(DisplayModeOutcome.Unreadable, 0, false, false);
            }

            for (uint index = 0; ReadNative(path.SourceName, index, out var mode); index++)
            {
                if (Project(mode) != requested || mode.Bits != original.Bits)
                {
                    continue;
                }

                mode.Fields = ModeFields;
                var test = Change(path.SourceName, ref mode, ChangeTest);
                if (test != 0)
                {
                    return new DisplayModeResult(DisplayModeOutcome.ValidationRefused, test, false, false);
                }

                if (!SameRoute(path, Find(path.Target)))
                {
                    return new DisplayModeResult(DisplayModeOutcome.RouteChanged, 0, false, false);
                }

                var flags = persist ? ChangeUpdateRegistry : 0;
                var status = Change(path.SourceName, ref mode, flags);
                if (status == 0)
                {
                    return new DisplayModeResult(DisplayModeOutcome.Applied, 0, false, false);
                }

                // A refusal can follow a partial driver change, so the captured mode goes back once, and
                // only while the source name still names this display: otherwise it could name another one.
                if (!SameRoute(path, Find(path.Target)))
                {
                    return new DisplayModeResult(DisplayModeOutcome.Refused, status, false, false);
                }

                original.Fields = ModeFields;
                var rollback = Change(path.SourceName, ref original, flags);
                return new DisplayModeResult(DisplayModeOutcome.Refused, status, true, rollback == 0);
            }

            return new DisplayModeResult(DisplayModeOutcome.NotAdvertised, 0, false, false);
        }
    }

    /// <summary>Reads the primary display's current mode.</summary>
    /// <returns>The mode, or null when the display cannot be read.</returns>
    public static PrimaryDisplayMode? ReadPrimaryMode()
    {
        return ReadNative(null, uint.MaxValue, out var current) ? ProjectPrimary(current) : null;
    }

    /// <summary>Lists every mode the driver enumerates for the primary display.</summary>
    /// <returns>
    ///     Modes in driver order, duplicates included. Enumeration stops on the first native false
    ///     result, including a read failure; an empty list does not establish no supported modes.
    ///     Entries are not tested; use <see cref="TestPrimaryMode" /> before requesting one.
    /// </returns>
    public static IReadOnlyList<PrimaryDisplayMode> EnumeratePrimaryModes()
    {
        List<PrimaryDisplayMode> modes = [];
        for (uint index = 0; ReadNative(null, index, out var mode); index++)
        {
            modes.Add(ProjectPrimary(mode));
        }

        return modes;
    }

    /// <summary>Asks the driver whether the primary display would accept a mode. Changes nothing.</summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="refreshHz">Refresh rate in hertz.</param>
    /// <returns>Whether the driver's test accepted the mode.</returns>
    /// <exception cref="OverflowException">A negative component cannot be represented by the native unsigned field.</exception>
    public static bool TestPrimaryMode(int width, int height, int refreshHz)
    {
        return ChangePrimary(width, height, refreshHz, ChangeTest) == 0;
    }

    /// <summary>Applies a resolution to the primary display and saves it as the display's resolution.</summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <returns>
    ///     The <c>ChangeDisplaySettingsEx</c> status of the visible change: zero on success, and
    ///     <c>DISP_CHANGE_FAILED</c> (-1) without a write when the current mode cannot be read. A failed
    ///     visible change saves nothing.
    /// </returns>
    /// <exception cref="OverflowException">A negative component cannot be represented by the native unsigned field.</exception>
    /// <remarks>
    ///     Two writes, the first skipped when the display already shows the resolution. The visible one keeps the current refresh rate and colour depth, so a rate another
    ///     owner holds transiently (a frame-limit pairing) is not undone. The saved one keeps the saved
    ///     refresh rate and colour depth, so that held rate never becomes the display's default; it is written
    ///     with <c>CDS_NORESET</c> and changes nothing visible. Its status is not part of the result, because
    ///     the visible change has already happened. Serialized with every other display write in the process.
    ///     This lower-level call performs no driver test, target-route verification or automatic rollback.
    /// </remarks>
    public static int ApplyPrimaryResolution(int width, int height)
    {
        lock (DisplayTopology.WriteGate)
        {
            if (!ReadNative(null, CurrentSettings, out var current))
            {
                return ChangeFailed;
            }

            // Already showing it, perhaps from an earlier dynamic change: only the saved mode is behind.
            var status = current.Width == width && current.Height == height
                ? 0
                : ChangePrimary(current, width, height, 0);
            if (status != 0)
            {
                return status;
            }

            var saved = ReadNative(null, RegistrySettings, out var registry) ? registry : current;
            saved.Fields = ModeFields;
            saved.Width = checked((uint)width);
            saved.Height = checked((uint)height);
            _ = Change(null, ref saved, ChangeUpdateRegistry | ChangeNoReset);
            return status;
        }
    }

    private static int ChangePrimary(int width, int height, int refreshHz, uint flags)
    {
        if (!ReadNative(null, CurrentSettings, out var mode))
        {
            return ChangeFailed;
        }

        mode.Frequency = checked((uint)refreshHz);
        return ChangePrimary(mode, width, height, flags);
    }

    private static int ChangePrimary(NativeMode mode, int width, int height, uint flags)
    {
        mode.Fields = PrimaryModeFields;
        mode.Width = checked((uint)width);
        mode.Height = checked((uint)height);
        return Change(null, ref mode, flags);
    }

    private static PrimaryDisplayMode ProjectPrimary(NativeMode mode)
    {
        return new PrimaryDisplayMode((int)mode.Width, (int)mode.Height, (int)mode.Frequency, (int)mode.Bits);
    }

    /// <summary>
    ///     The one active path driving this monitor, or null when it is absent, ambiguous, unreadable or
    ///     shares its source with another target (a clone, where one mode change would change them all).
    ///     Win32 query failures return null; unreadable unrelated paths are skipped.
    /// </summary>
    private static ActiveDisplayPath? Find(DisplayTargetIdentity target)
    {
        DisplayTopology.PathInfo[] paths;
        try
        {
            paths = DisplayTopology.Query(DisplayTopology.OnlyActivePaths, "Active display topology query failed.")
                .Paths;
        }
        catch (Win32Exception)
        {
            return null;
        }

        DisplayTopology.PathInfo? match = null;
        DisplayTargetIdentity? identity = null;
        foreach (var path in paths)
        {
            DisplayTargetIdentity candidate;
            try
            {
                candidate = DisplayTopology.ReadTarget(path);
            }
            catch (Win32Exception)
            {
                continue;
            }

            if (!target.Matches(candidate))
            {
                continue;
            }

            if (match is not null)
            {
                return null;
            }

            match = path;
            identity = candidate;
        }

        if (match is not { } found || identity is null || paths.Count(path =>
                path.SourceInfo.AdapterId.LowPart == found.SourceInfo.AdapterId.LowPart
                && path.SourceInfo.AdapterId.HighPart == found.SourceInfo.AdapterId.HighPart
                && path.SourceInfo.Id == found.SourceInfo.Id) != 1)
        {
            return null;
        }

        try
        {
            return DisplayTopology.ToActive(found, identity);
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private static bool SameRoute(ActiveDisplayPath expected, ActiveDisplayPath? current)
    {
        return current is not null && expected.Target == current.Target && expected.SourceName == current.SourceName;
    }

    private static DisplayMode Project(NativeMode mode)
    {
        return new DisplayMode((int)mode.Width, (int)mode.Height, (int)mode.Frequency);
    }

    private static bool ReadNative(string? source, uint index, out NativeMode mode)
    {
        mode = new NativeMode { Size = 220 };
        return EnumDisplaySettingsEx(source, index, ref mode, 0);
    }

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplaySettingsExW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplaySettingsEx(string? source, uint index, ref NativeMode mode, uint flags);

    private static int Change(string? source, ref NativeMode mode, uint flags)
    {
        return ChangeDisplaySettingsEx(source, ref mode, 0, flags, 0);
    }

    [LibraryImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ChangeDisplaySettingsEx(string? source, ref NativeMode mode, nint window, uint flags,
        nint parameter);

    [StructLayout(LayoutKind.Explicit, Size = 220)]
    private struct NativeMode
    {
        [FieldOffset(68)] internal ushort Size;
        [FieldOffset(72)] internal uint Fields;
        [FieldOffset(168)] internal uint Bits;
        [FieldOffset(172)] internal uint Width;
        [FieldOffset(176)] internal uint Height;
        [FieldOffset(184)] internal uint Frequency;
    }
}
