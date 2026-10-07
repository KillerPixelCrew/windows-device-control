using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using static WindowsDeviceControl.Win32Error;

namespace WindowsDeviceControl;

/// <summary>What Windows will let a caller do with one wake-capable device.</summary>
public enum WakeDeviceControl
{
    /// <summary>Windows offers the device as programmable: its wake arming can be changed.</summary>
    Programmable,

    /// <summary>
    ///     Wake-capable, but Windows does not offer it as programmable. Firmware or the OS owns it;
    ///     report it and leave it alone.
    /// </summary>
    Fixed
}

/// <summary>One device Windows reports as able to wake the machine from Modern Standby.</summary>
/// <param name="Name">The device description Windows uses as its identity in this API.</param>
/// <param name="Armed">Whether the device is currently armed to wake the machine.</param>
/// <param name="Control">Whether the arming can be changed.</param>
public sealed record WakeDevice(string Name, bool Armed, WakeDeviceControl Control);

/// <summary>The wake arming observed at one moment. Persist it before changing anything.</summary>
/// <param name="Known">Every programmable or armed device observed; not a list of all wake-capable hardware.</param>
/// <param name="Armed">Those of them that were armed.</param>
public sealed record WakeDeviceSnapshot(IReadOnlyList<string> Known, IReadOnlyList<string> Armed);

/// <summary>One device whose wake arming a restore could not write.</summary>
/// <param name="Name">The device description.</param>
/// <param name="NativeErrorCode">The Win32 error the write returned.</param>
public sealed record WakeDeviceRestoreFailure(string Name, int NativeErrorCode);

/// <summary>Sequential interrupt-time observations around the machine's last standby.</summary>
/// <remarks>
///     These boot-relative values are comparable only within the same boot. The current mark uses
///     QueryInterruptTime, whose time includes sleep; the three observations are not an atomic snapshot.
/// </remarks>
/// <param name="Sleep">Interrupt time at the last transition into sleep.</param>
/// <param name="Wake">Interrupt time at the last wake.</param>
/// <param name="Now">Current interrupt time, read after the sleep and wake marks.</param>
public sealed record StandbyTiming(TimeSpan Sleep, TimeSpan Wake, TimeSpan Now)
{
    /// <summary>The last wake mark minus the sleep mark, or zero when the wake mark is not later.</summary>
    public TimeSpan Slept => Wake > Sleep ? Wake - Sleep : TimeSpan.Zero;

    /// <summary>The current mark minus the wake mark, clamped to zero.</summary>
    public TimeSpan SinceWake => Now > Wake ? Now - Wake : TimeSpan.Zero;
}

/// <summary>What this machine reports about Modern Standby and its mandatory wake paths.</summary>
/// <param name="LowPowerIdle">Whether the machine supports S0 low-power idle (Modern Standby).</param>
/// <param name="ConnectedStandby">Whether it supports the network-connected form of it.</param>
/// <param name="WakeAlarm">Whether a wake alarm device is present.</param>
/// <param name="PowerButton">Whether a power button is present.</param>
/// <param name="SleepButton">Whether a sleep button is present.</param>
/// <param name="Lid">Whether a lid is present.</param>
public sealed record ModernStandbySupport(
    bool LowPowerIdle,
    bool ConnectedStandby,
    bool WakeAlarm,
    bool PowerButton,
    bool SleepButton,
    bool Lid);

/// <summary>
///     Modern Standby wake-source primitives: what the machine supports, which devices can wake it,
///     and per-device arming with snapshot and restore.
/// </summary>
/// <remarks>
///     Policy-neutral on purpose. There is no "disable every wake source" call and no stored state:
///     a caller names one device at a time, persists <see cref="CaptureWakeDevices" /> before it
///     changes anything, and keeps that snapshot until <see cref="RestoreWakeDevices" /> reports no failure.
///     <para>
///         The power button, sleep button and lid are reported by <see cref="Query" /> and are not part
///         of the device list this class writes to, so no call here can take away a recovery wake path.
///         Software wake sources are ordinary power settings; the GUIDs are exposed here and read and
///         written through <see cref="WindowsPower.ReadSetting" /> and
///         <see cref="WindowsPower.WriteSetting" />.
///     </para>
///     Changing device wake state needs elevation. Native failures throw <see cref="Win32Exception" />.
/// </remarks>
public static partial class ModernStandby
{
    private const uint FilterDevicesPresent = 0x20000000;
    private const uint FilterWakeEnabled = 0x08000000;
    private const uint FilterWakeProgrammable = 0x04000000;
    private const uint SetWakeEnabled = 0x00000001;
    private const uint ClearWakeEnabled = 0x00000002;

    /// <summary>The first buffer size for one device description; it grows when Windows asks for more.</summary>
    private const int InitialNameBytes = 4096;

    private const int CapabilitiesBytes = 76;
    private const int OffsetPowerButton = 0;
    private const int OffsetSleepButton = 1;
    private const int OffsetLid = 2;
    private const int OffsetWakeAlarm = 19;
    private const int OffsetAoAc = 20;
    private const int OffsetAoAcConnectivity = 23;

    /// <summary>POWER_INFORMATION_LEVEL.LastWakeTime; interrupt time at the last wake.</summary>
    private const uint LastWakeTime = 14;

    /// <summary>POWER_INFORMATION_LEVEL.LastSleepTime; interrupt time at the last sleep.</summary>
    private const uint LastSleepTime = 15;

    /// <summary>Sleep settings subgroup (SUB_SLEEP).</summary>
    public static readonly Guid SubgroupSleep = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");

    /// <summary>The subgroup for settings that belong to no subgroup (SUB_NONE).</summary>
    public static readonly Guid SubgroupNone = new("fea3413e-7e05-4911-9a71-700331f1c294");

    /// <summary>Allow wake timers (RTCWAKE), under <see cref="SubgroupSleep" />.</summary>
    public static readonly Guid SettingAllowWakeTimers = new("bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d");

    /// <summary>Allow away mode (AWAYMODE), under <see cref="SubgroupSleep" />.</summary>
    public static readonly Guid SettingAllowAwayMode = new("25dfa149-5dd1-4736-b5ab-e8a37b5b8187");

    /// <summary>Unattended sleep timeout (UNATTENDSLEEP), under <see cref="SubgroupSleep" />.</summary>
    public static readonly Guid SettingUnattendedSleepTimeout =
        new("7bc4a2f9-d8fc-4469-b07b-33eb785aaca0");

    /// <summary>Network connectivity in standby (CONNECTIVITYINSTANDBY), under <see cref="SubgroupNone" />.</summary>
    public static readonly Guid SettingConnectivityInStandby =
        new("f15576e8-98b7-4186-b944-eafa664402d9");

    /// <summary>Disconnected standby mode (DISCONNECTEDSTANDBYMODE), under <see cref="SubgroupNone" />.</summary>
    public static readonly Guid SettingDisconnectedStandby =
        new("68afb2d9-ee95-47a8-8f50-4115088073b1");

    // DevicePowerOpen owns a process-global list; serialize its lifetime and each read/write sequence.
    private static readonly object WakeDeviceGate = new();

    /// <summary>Reads what the machine supports. Native failures throw Win32Exception.</summary>
    /// <returns>Modern Standby support and the mandatory wake paths this machine has.</returns>
    /// <exception cref="Win32Exception">Windows could not provide the capability structure.</exception>
    public static ModernStandbySupport Query()
    {
        var buffer = new byte[CapabilitiesBytes];
        if (!GetPwrCapabilities(buffer))
        {
            throw Failure((uint)Marshal.GetLastWin32Error(), "GetPwrCapabilities");
        }

        return ReadCapabilities(buffer);
    }

    /// <summary>Whether Windows attributes the last resume to something other than the user.</summary>
    /// <remarks>
    ///     Reports Windows' IsSystemResumeAutomatic classification, without identifying the wake source.
    ///     Read after a resume notification; subsequent resumes replace this observation.
    /// </remarks>
    /// <returns>True when the last resume was unattended.</returns>
    public static bool WasLastResumeUnattended()
    {
        return IsSystemResumeAutomatic();
    }

    /// <summary>Reads the interrupt-time marks around the last standby.</summary>
    /// <remarks>
    ///     The sleep, wake and current marks are read sequentially on the same interrupt-time clock,
    ///     not as one atomic snapshot. A transition between reads can produce mismatched marks;
    ///     the result does not identify the wake source.
    /// </remarks>
    /// <returns>The last sleep and wake marks and the current interrupt time.</returns>
    /// <exception cref="Win32Exception">
    ///     The power information read failed. Its <see cref="Win32Exception.NativeErrorCode" /> is the NTSTATUS
    ///     <c>CallNtPowerInformation</c> returned, not a Win32 error code.
    /// </exception>
    public static StandbyTiming ReadStandbyTiming()
    {
        return new StandbyTiming(
            ReadInterruptTime(LastSleepTime),
            ReadInterruptTime(LastWakeTime),
            QueryInterruptTimeNow());
    }

    /// <summary>
    ///     Enumerates the wake sources a caller can act on: those armed, and those Windows
    ///     reports as programmable.
    /// </summary>
    /// <remarks>
    ///     Returns the union of present programmable and armed devices, ordered by ordinal name.
    ///     Armed devices that are not programmable have <see cref="WakeDeviceControl.Fixed" /> control.
    ///     Other wake-capable devices are outside this enumeration.
    /// </remarks>
    /// <returns>The actionable wake sources present on the machine.</returns>
    /// <exception cref="Win32Exception">The device list could not be opened or read completely.</exception>
    public static IReadOnlyList<WakeDevice> EnumerateWakeDevices()
    {
        lock (WakeDeviceGate)
        {
            return EnumerateWakeDevicesCore();
        }
    }

    private static IReadOnlyList<WakeDevice> EnumerateWakeDevicesCore()
    {
        if (!DevicePowerOpen(0))
        {
            throw Failure((uint)Marshal.GetLastWin32Error(), "DevicePowerOpen");
        }

        try
        {
            var buffer = new byte[InitialNameBytes];
            HashSet<string> programmable = new(
                Enumerate(FilterDevicesPresent | FilterWakeProgrammable, ref buffer), StringComparer.Ordinal);
            HashSet<string> armed = new(
                Enumerate(FilterDevicesPresent | FilterWakeEnabled, ref buffer), StringComparer.Ordinal);

            List<string> names = [.. programmable];
            foreach (var name in armed)
            {
                if (!programmable.Contains(name))
                {
                    names.Add(name);
                }
            }

            names.Sort(StringComparer.Ordinal);

            List<WakeDevice> devices = [];
            foreach (var name in names)
            {
                devices.Add(new WakeDevice(
                    name,
                    armed.Contains(name),
                    programmable.Contains(name)
                        ? WakeDeviceControl.Programmable
                        : WakeDeviceControl.Fixed));
            }

            return devices;
        }
        finally
        {
            DevicePowerClose();
        }
    }

    /// <summary>Arms or disarms one device, when Windows still offers it as programmable.</summary>
    /// <remarks>
    ///     Programmability is re-read here rather than taken from the caller's record, so a device
    ///     that became firmware-managed since it was enumerated is refused rather than written to.
    ///     Idempotent: a device already in the requested state is left alone and reported as done.
    /// </remarks>
    /// <param name="name">The device description from <see cref="EnumerateWakeDevices" />.</param>
    /// <param name="armed">True to let the device wake the machine.</param>
    /// <returns>
    ///     True when the device was already in the requested state or Windows accepted the write;
    ///     false when absent or not programmable. An accepted write is not read back.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="name" /> is null, empty or whitespace.</exception>
    /// <exception cref="Win32Exception">
    ///     Device enumeration or the write failed. Writes require elevation; the native error is preserved.
    /// </exception>
    public static bool TrySetWakeArmed(string name, bool armed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        // Excludes competing library writes; external device changes can still race this check.
        lock (WakeDeviceGate)
        {
            foreach (var device in EnumerateWakeDevicesCore())
            {
                if (!string.Equals(device.Name, name, StringComparison.Ordinal))
                {
                    continue;
                }

                if (device.Control != WakeDeviceControl.Programmable)
                {
                    return false;
                }

                if (device.Armed == armed)
                {
                    return true;
                }

                SetArmedCore(name, armed);
                return true;
            }

            return false;
        }
    }

    /// <summary>Captures the current arming so it can be restored later.</summary>
    /// <remarks>Persist this before the first write and retain it until a restore succeeds.</remarks>
    /// <returns>The programmable or armed devices observed, and which of them were armed.</returns>
    /// <exception cref="Win32Exception">The device list could not be read completely; no snapshot is returned.</exception>
    public static WakeDeviceSnapshot CaptureWakeDevices()
    {
        List<string> known = [];
        List<string> armed = [];
        foreach (var device in EnumerateWakeDevices())
        {
            known.Add(device.Name);
            if (device.Armed)
            {
                armed.Add(device.Name);
            }
        }

        return new WakeDeviceSnapshot(known, armed);
    }

    /// <summary>Puts the arming back the way a snapshot recorded it.</summary>
    /// <remarks>
    ///     Enumerates once, then writes only changed, still-programmable devices named by the snapshot.
    ///     New, absent and fixed devices are skipped. Each planned write is attempted once even after
    ///     a native refusal; accepted writes are not read back.
    /// </remarks>
    /// <param name="snapshot">A snapshot from <see cref="CaptureWakeDevices" />.</param>
    /// <returns>The devices whose write failed; empty when everything restorable was restored.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot" /> is null.</exception>
    /// <exception cref="Win32Exception">The devices could not be enumerated; nothing was written.</exception>
    public static IReadOnlyList<WakeDeviceRestoreFailure> RestoreWakeDevices(WakeDeviceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (WakeDeviceGate)
        {
            return ExecuteRestore(RestorePlan(EnumerateWakeDevicesCore(), snapshot), SetArmedCore);
        }
    }

    /// <summary>Writes every planned arming once, collecting failures instead of stopping at the first.</summary>
    /// <param name="plan">Ordered device names and desired arming states.</param>
    /// <param name="write">One synchronous write; Win32Exception is collected and other exceptions propagate.</param>
    /// <returns>Native failures in plan order; every step after a native refusal is still attempted.</returns>
    internal static List<WakeDeviceRestoreFailure> ExecuteRestore(
        IReadOnlyList<(string Name, bool Armed)> plan, Action<string, bool> write)
    {
        List<WakeDeviceRestoreFailure> failures = [];
        foreach (var (name, armed) in plan)
        {
            try
            {
                write(name, armed);
            }
            catch (Win32Exception ex)
            {
                failures.Add(new WakeDeviceRestoreFailure(name, ex.NativeErrorCode));
            }
        }

        return failures;
    }

    /// <summary>Plans writes only for captured devices still present and programmable.</summary>
    /// <param name="current">Current programmable/armed device observations.</param>
    /// <param name="snapshot">Previously captured known devices and armed subset.</param>
    /// <returns>Changed devices in current enumeration order; new, absent, fixed and already-matching devices are omitted.</returns>
    internal static IReadOnlyList<(string Name, bool Armed)> RestorePlan(
        IReadOnlyList<WakeDevice> current, WakeDeviceSnapshot snapshot)
    {
        HashSet<string> known = new(snapshot.Known, StringComparer.Ordinal);
        HashSet<string> armed = new(snapshot.Armed, StringComparer.Ordinal);
        List<(string, bool)> writes = [];
        foreach (var device in current)
        {
            if (device.Control != WakeDeviceControl.Programmable || !known.Contains(device.Name))
            {
                continue;
            }

            var wanted = armed.Contains(device.Name);
            if (wanted != device.Armed)
            {
                writes.Add((device.Name, wanted));
            }
        }

        return writes;
    }

    /// <summary>Decodes Modern Standby and physical wake controls from SYSTEM_POWER_CAPABILITIES.</summary>
    /// <param name="buffer">At least 76 bytes of the native capability structure.</param>
    /// <returns>The six supported flags without making native calls.</returns>
    /// <exception cref="Win32Exception">The structure is truncated.</exception>
    internal static ModernStandbySupport ReadCapabilities(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < CapabilitiesBytes)
        {
            throw Failure(ErrorInvalidData, "GetPwrCapabilities");
        }

        return new ModernStandbySupport(
            buffer[OffsetAoAc] != 0,
            buffer[OffsetAoAcConnectivity] != 0,
            buffer[OffsetWakeAlarm] != 0,
            buffer[OffsetPowerButton] != 0,
            buffer[OffsetSleepButton] != 0,
            buffer[OffsetLid] != 0);
    }

    /// <summary>
    ///     Reads the null-terminated name the enumeration wrote. The size argument is not an output:
    ///     Windows leaves it at the buffer size it was given, so the terminator is the only length.
    /// </summary>
    /// <param name="buffer">UTF-16 bytes returned by DevicePowerEnumDevices, including the terminator.</param>
    /// <returns>The nonblank name before the first UTF-16 terminator.</returns>
    /// <exception cref="Win32Exception">The name is blank or has no terminator within the buffer.</exception>
    internal static string DecodeDeviceName(byte[] buffer)
    {
        for (var end = 0; end + 1 < buffer.Length; end += 2)
        {
            if (buffer[end] != 0 || buffer[end + 1] != 0)
            {
                continue;
            }

            var name = Encoding.Unicode.GetString(buffer, 0, end);
            // Names are write identities; never accept an empty or unterminated descriptor.
            return string.IsNullOrWhiteSpace(name)
                ? throw Failure(ErrorInvalidData, "DevicePowerEnumDevices")
                : name;
        }

        throw Failure(ErrorInvalidData, "DevicePowerEnumDevices");
    }

    /// <summary>One interrupt-time value, in 100 ns units, read through the power information call.</summary>
    /// <param name="level">LastSleepTime or LastWakeTime power-information class.</param>
    /// <returns>The boot-relative mark converted from 100-nanosecond units.</returns>
    /// <exception cref="Win32Exception">The query failed; NativeErrorCode preserves its NTSTATUS.</exception>
    internal static TimeSpan ReadInterruptTime(uint level)
    {
        ulong ticks = 0;
        var status = CallNtPowerInformation(level, 0, 0, ref ticks, sizeof(ulong));
        if (status != 0)
        {
            // NTSTATUS, not a Win32 code: preserved as-is rather than mapped to an invented one.
            throw new Win32Exception(unchecked((int)status),
                $"CallNtPowerInformation failed (NTSTATUS 0x{status:X8}).");
        }

        return TimeSpan.FromTicks(checked((long)ticks));
    }

    private static TimeSpan QueryInterruptTimeNow()
    {
        QueryInterruptTime(out var ticks);
        return TimeSpan.FromTicks(checked((long)ticks));
    }

    private static void SetArmedCore(string name, bool armed)
    {
        Check(
            DevicePowerSetDeviceState(name, armed ? SetWakeEnabled : ClearWakeEnabled, 0),
            "DevicePowerSetDeviceState");
    }

    private static List<string> Enumerate(uint interpretation, ref byte[] buffer)
    {
        List<string> names = [];
        uint index = 0;
        while (true)
        {
            // Cleared rather than reallocated, so a shorter name still ends at the zero padding a
            // fresh buffer would have given it.
            Array.Clear(buffer);
            var size = (uint)buffer.Length;
            if (DevicePowerEnumDevices(index, interpretation, 0, buffer, ref size))
            {
                names.Add(DecodeDeviceName(buffer));
                index++;
                continue;
            }

            // FALSE also signals failures: only ERROR_NO_MORE_ITEMS is a complete enumeration.
            var error = (uint)Marshal.GetLastWin32Error();
            if (error is ErrorNoMoreItems)
            {
                return names;
            }

            if (error is not (ErrorInsufficientBuffer or ErrorMoreData))
            {
                throw Failure(error, "DevicePowerEnumDevices");
            }

            // A name longer than the buffer: grow to the size Windows reports, or double when it
            // reports none, and read the same index again.
            buffer = new byte[size > buffer.Length ? checked((int)size) : checked(buffer.Length * 2)];
        }
    }

    [LibraryImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool GetPwrCapabilities([Out] byte[] capabilities);

    [LibraryImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool DevicePowerOpen(uint debugMask);

    [LibraryImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool DevicePowerClose();

    [LibraryImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool DevicePowerEnumDevices(
        uint queryIndex, uint queryInterpretationFlags, uint queryFlags,
        [Out] byte[] returnBuffer, ref uint bufferSize);

    [LibraryImport("powrprof.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint DevicePowerSetDeviceState(
        string deviceDescription, uint setFlags, nint setData);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsSystemResumeAutomatic();

    [LibraryImport("powrprof.dll")]
    private static partial uint CallNtPowerInformation(
        uint informationLevel, nint inputBuffer, uint inputBufferLength,
        ref ulong outputBuffer, uint outputBufferLength);

    // Declared against kernel32 in the SDK headers but not exported from it. The API set is the
    // documented forward-compatible name; KernelBase also carries it, and kernel32 does not.
    [LibraryImport("api-ms-win-core-realtime-l1-1-0.dll")]
    private static partial void QueryInterruptTime(out ulong interruptTime);
}
