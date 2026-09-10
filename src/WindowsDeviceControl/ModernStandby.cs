using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

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
    Fixed,
}

/// <summary>One device Windows reports as able to wake the machine from Modern Standby.</summary>
/// <param name="Name">The device description Windows uses as its identity in this API.</param>
/// <param name="Armed">Whether the device is currently armed to wake the machine.</param>
/// <param name="Control">Whether the arming can be changed.</param>
public sealed record WakeDevice(string Name, bool Armed, WakeDeviceControl Control);

/// <summary>The wake arming observed at one moment. Persist it before changing anything.</summary>
/// <param name="Known">Every wake-capable device observed, armed or not.</param>
/// <param name="Armed">Those of them that were armed.</param>
public sealed record WakeDeviceSnapshot(IReadOnlyList<string> Known, IReadOnlyList<string> Armed);

/// <summary>Interrupt-time marks around the machine's last standby, all from the same read.</summary>
/// <remarks>
///     Interrupt time counts from boot and does not advance while the machine is asleep, so these
///     are only comparable with each other and with a later read on the same boot.
/// </remarks>
/// <param name="Sleep">Interrupt time at the last transition into sleep.</param>
/// <param name="Wake">Interrupt time at the last wake.</param>
/// <param name="Now">Interrupt time when the three were read.</param>
public sealed record StandbyTiming(TimeSpan Sleep, TimeSpan Wake, TimeSpan Now)
{
    /// <summary>How long the machine was asleep, or zero when it has not slept this boot.</summary>
    public TimeSpan Slept => Wake > Sleep ? Wake - Sleep : TimeSpan.Zero;

    /// <summary>How long the machine has been awake since that wake.</summary>
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
///     a caller names one device at a time, persists <see cref="CaptureWakeDevices"/> before it
///     changes anything, and keeps that snapshot until <see cref="RestoreWakeDevices"/> succeeds.
///     <para>
///     The power button, sleep button and lid are reported by <see cref="Query"/> and are not part
///     of the device list this class writes to, so no call here can take away a recovery wake path.
///     Software wake sources are ordinary power settings; the GUIDs are exposed here and read and
///     written through <see cref="WindowsPower.ReadSetting"/> and
///     <see cref="WindowsPower.WriteSetting"/>.
///     </para>
///     Changing device wake state needs elevation. Native failures throw <see cref="Win32Exception"/>.
/// </remarks>
public static partial class ModernStandby
{
    /// <summary>Sleep settings subgroup (SUB_SLEEP).</summary>
    public static readonly Guid SubgroupSleep = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");

    /// <summary>The subgroup for settings that belong to no subgroup (SUB_NONE).</summary>
    public static readonly Guid SubgroupNone = new("fea3413e-7e05-4911-9a71-700331f1c294");

    /// <summary>Allow wake timers (RTCWAKE), under <see cref="SubgroupSleep"/>.</summary>
    public static readonly Guid SettingAllowWakeTimers = new("bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d");

    /// <summary>Allow away mode (AWAYMODE), under <see cref="SubgroupSleep"/>.</summary>
    public static readonly Guid SettingAllowAwayMode = new("25dfa149-5dd1-4736-b5ab-e8a37b5b8187");

    /// <summary>Unattended sleep timeout (UNATTENDSLEEP), under <see cref="SubgroupSleep"/>.</summary>
    public static readonly Guid SettingUnattendedSleepTimeout =
        new("7bc4a2f9-d8fc-4469-b07b-33eb785aaca0");

    /// <summary>Network connectivity in standby (CONNECTIVITYINSTANDBY), under <see cref="SubgroupNone"/>.</summary>
    public static readonly Guid SettingConnectivityInStandby =
        new("f15576e8-98b7-4186-b944-eafa664402d9");

    /// <summary>Disconnected standby mode (DISCONNECTEDSTANDBYMODE), under <see cref="SubgroupNone"/>.</summary>
    public static readonly Guid SettingDisconnectedStandby =
        new("68afb2d9-ee95-47a8-8f50-4115088073b1");

    private const uint FilterDevicesPresent = 0x20000000;
    private const uint FilterWakeEnabled = 0x08000000;
    private const uint FilterWakeProgrammable = 0x04000000;
    private const uint SetWakeEnabled = 0x00000001;
    private const uint ClearWakeEnabled = 0x00000002;

    /// <summary>Bounded buffer for one device description; Windows names are far below this.</summary>
    private const uint MaximumNameBytes = 4096;

    private const int CapabilitiesBytes = 76;
    private const int OffsetPowerButton = 0;
    private const int OffsetSleepButton = 1;
    private const int OffsetLid = 2;
    private const int OffsetWakeAlarm = 19;
    private const int OffsetAoAc = 20;
    private const int OffsetAoAcConnectivity = 23;

    /// <summary>Reads what the machine supports. Native failures throw Win32Exception.</summary>
    /// <returns>Modern Standby support and the mandatory wake paths this machine has.</returns>
    public static ModernStandbySupport Query()
    {
        byte[] buffer = new byte[CapabilitiesBytes];
        if (!GetPwrCapabilities(buffer))
        {
            throw Failure((uint)Marshal.GetLastWin32Error(), "GetPwrCapabilities");
        }
        return ReadCapabilities(buffer);
    }

    /// <summary>Whether Windows attributes the last resume to something other than the user.</summary>
    /// <remarks>
    ///     False means a person woke the machine — a power button, a key, a lid. True means it came
    ///     back on its own: a wake timer, a device, background work. This is the one call that
    ///     separates a wake worth staying awake for from one worth going back to sleep on, and it is
    ///     the whole basis of an automatic re-suspend policy. It describes the last resume, so read
    ///     it on the resume notification rather than caching it.
    /// </remarks>
    /// <returns>True when the last resume was unattended.</returns>
    public static bool WasLastResumeUnattended() => IsSystemResumeAutomatic();

    /// <summary>Reads the interrupt-time marks around the last standby.</summary>
    /// <remarks>
    ///     All three come from one call sequence so they can be compared without a clock skewing
    ///     between them. Answers "how long was it asleep" and "how long has it been awake", which is
    ///     what a re-suspend grace period and a standby diagnostic both need. It does not report
    ///     what woke the machine: Windows exposes no documented call for that.
    /// </remarks>
    /// <returns>The last sleep and wake marks and the current interrupt time.</returns>
    public static StandbyTiming ReadStandbyTiming() => new(
        ReadInterruptTime(LastSleepTime),
        ReadInterruptTime(LastWakeTime),
        QueryInterruptTimeNow());

    /// <summary>Enumerates the wake sources a caller can act on: those armed, and those Windows
    /// reports as programmable.</summary>
    /// <remarks>
    ///     Not every device that merely supports waking from S0 — that set is most of the HID and
    ///     Bluetooth endpoints on a handheld, none of which Windows offers for change, and listing
    ///     them would bury the few that matter. A device that is armed but not programmable comes
    ///     back as <see cref="WakeDeviceControl.Fixed"/>: visible, reportable, never written to.
    ///     Ordered by name so two reads are comparable.
    /// </remarks>
    /// <returns>The actionable wake sources present on the machine.</returns>
    public static IReadOnlyList<WakeDevice> EnumerateWakeDevices()
    {
        if (!DevicePowerOpen(0))
        {
            throw Failure((uint)Marshal.GetLastWin32Error(), "DevicePowerOpen");
        }
        try
        {
            HashSet<string> programmable = new(
                Enumerate(FilterDevicesPresent | FilterWakeProgrammable), StringComparer.Ordinal);
            HashSet<string> armed = new(
                Enumerate(FilterDevicesPresent | FilterWakeEnabled), StringComparer.Ordinal);

            List<string> names = [.. programmable];
            foreach (string name in armed)
            {
                if (!programmable.Contains(name)) { names.Add(name); }
            }
            names.Sort(StringComparer.Ordinal);

            List<WakeDevice> devices = [];
            foreach (string name in names)
            {
                devices.Add(new(
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
    /// <param name="name">The device description from <see cref="EnumerateWakeDevices"/>.</param>
    /// <param name="armed">True to let the device wake the machine.</param>
    /// <returns>False when Windows does not offer that device as programmable, or it is absent.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    public static bool TrySetWakeArmed(string name, bool armed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        foreach (WakeDevice device in EnumerateWakeDevices())
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
            Check(
                DevicePowerSetDeviceState(name, armed ? SetWakeEnabled : ClearWakeEnabled, 0),
                "DevicePowerSetDeviceState");
            return true;
        }
        return false;
    }

    /// <summary>Captures the current arming so it can be restored later.</summary>
    /// <remarks>Persist this before the first write and retain it until a restore succeeds.</remarks>
    /// <returns>Every wake-capable device observed, and which of them were armed.</returns>
    public static WakeDeviceSnapshot CaptureWakeDevices()
    {
        List<string> known = [];
        List<string> armed = [];
        foreach (WakeDevice device in EnumerateWakeDevices())
        {
            known.Add(device.Name);
            if (device.Armed) { armed.Add(device.Name); }
        }
        return new(known, armed);
    }

    /// <summary>Puts the arming back the way a snapshot recorded it.</summary>
    /// <remarks>
    ///     Only devices the snapshot actually observed are touched: a device that appeared since
    ///     then has no prior state to restore, and guessing one would be a change dressed as a
    ///     restore. Idempotent, and a device that is no longer programmable is skipped rather than
    ///     failing the rest.
    /// </remarks>
    /// <param name="snapshot">A snapshot from <see cref="CaptureWakeDevices"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    public static void RestoreWakeDevices(WakeDeviceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        foreach ((string name, bool armed) in RestorePlan(EnumerateWakeDevices(), snapshot))
        {
            TrySetWakeArmed(name, armed);
        }
    }

    internal static IReadOnlyList<(string Name, bool Armed)> RestorePlan(
        IReadOnlyList<WakeDevice> current, WakeDeviceSnapshot snapshot)
    {
        HashSet<string> known = new(snapshot.Known, StringComparer.Ordinal);
        HashSet<string> armed = new(snapshot.Armed, StringComparer.Ordinal);
        List<(string, bool)> writes = [];
        foreach (WakeDevice device in current)
        {
            if (device.Control != WakeDeviceControl.Programmable || !known.Contains(device.Name))
            {
                continue;
            }
            bool wanted = armed.Contains(device.Name);
            if (wanted != device.Armed) { writes.Add((device.Name, wanted)); }
        }
        return writes;
    }

    internal static ModernStandbySupport ReadCapabilities(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < CapabilitiesBytes)
        {
            throw Failure(ErrorInvalidData, "GetPwrCapabilities");
        }
        return new(
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
    internal static string DecodeDeviceName(byte[] buffer)
    {
        for (int end = 0; end + 1 < buffer.Length; end += 2)
        {
            if (buffer[end] != 0 || buffer[end + 1] != 0)
            {
                continue;
            }
            string name = Encoding.Unicode.GetString(buffer, 0, end);
            // A name is this API's device identity. An empty or unterminated one names nothing, and
            // guessing at it would hand the caller something to write to that it cannot address.
            return string.IsNullOrWhiteSpace(name)
                ? throw Failure(ErrorInvalidData, "DevicePowerEnumDevices")
                : name;
        }
        throw Failure(ErrorInvalidData, "DevicePowerEnumDevices");
    }

    /// <summary>One interrupt-time value, in 100 ns units, read through the power information call.</summary>
    internal static TimeSpan ReadInterruptTime(uint level)
    {
        ulong ticks = 0;
        uint status = CallNtPowerInformation(level, 0, 0, ref ticks, sizeof(ulong));
        if (status != 0)
        {
            // NTSTATUS, not a Win32 code: preserved as-is rather than mapped to an invented one.
            throw Failure(status, "CallNtPowerInformation");
        }
        return TimeSpan.FromTicks(checked((long)ticks));
    }

    private static TimeSpan QueryInterruptTimeNow()
    {
        QueryInterruptTime(out ulong ticks);
        return TimeSpan.FromTicks(checked((long)ticks));
    }

    private static List<string> Enumerate(uint interpretation)
    {
        List<string> names = [];
        for (uint index = 0; index < MaximumDevices; index++)
        {
            byte[] buffer = new byte[MaximumNameBytes];
            uint size = MaximumNameBytes;
            if (DevicePowerEnumDevices(index, interpretation, 0, buffer, ref size))
            {
                names.Add(DecodeDeviceName(buffer));
                continue;
            }

            // The end of the list and a real failure both return FALSE, and only the error code
            // separates them. Treating every FALSE as the end would report a partial device list as
            // a complete one, which is the answer a caller cannot detect.
            uint error = (uint)Marshal.GetLastWin32Error();
            if (error is ErrorNoMoreItems)
            {
                return names;
            }
            throw Failure(error, "DevicePowerEnumDevices");
        }
        return names;
    }

    private const uint MaximumDevices = 4096;
    private const uint ErrorInvalidData = 13;
    private const uint ErrorNoMoreItems = 259;

    /// <summary>POWER_INFORMATION_LEVEL.LastWakeTime; interrupt time at the last wake.</summary>
    private const uint LastWakeTime = 14;

    /// <summary>POWER_INFORMATION_LEVEL.LastSleepTime; interrupt time at the last sleep.</summary>
    private const uint LastSleepTime = 15;

    private static void Check(uint status, string operation)
    {
        if (status != 0)
        {
            throw Failure(status, operation);
        }
    }

    private static Win32Exception Failure(uint status, string operation)
        => new(unchecked((int)status), $"{operation} failed (status {status}).");

    [LibraryImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool GetPwrCapabilities([Out] byte[] capabilities);

    [LibraryImport("powrprof.dll")]
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
