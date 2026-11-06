using System;
using System.Runtime.InteropServices;

namespace WindowsDeviceControl;

/// <summary>How Windows' own Power Options present one power setting (<c>POWER_ATTRIBUTE_*</c>).</summary>
[Flags]
public enum PowerSettingAttributes : uint
{
    /// <summary>Shown normally.</summary>
    None = 0,

    /// <summary><c>POWER_ATTRIBUTE_HIDE</c>: hidden from Power Options.</summary>
    Hidden = 1,

    /// <summary><c>POWER_ATTRIBUTE_SHOW_AOAC</c>: shown on Modern Standby (AOAC) machines too; clears the hidden bit.</summary>
    ShowAoac = 2
}

public static partial class WindowsPower
{
    /// <summary>Processor performance boost mode (PERFBOOSTMODE), under <see cref="SubgroupProcessor" />.</summary>
    public static readonly Guid SettingProcessorBoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");

    /// <summary>Writes how Power Options presents a setting. The setting's stored values are untouched.</summary>
    /// <param name="subgroup">Policy subgroup identity.</param>
    /// <param name="setting">Policy setting identity.</param>
    /// <param name="attributes">The presentation attributes to store.</param>
    /// <param name="status">The Win32 status Windows returned; zero on success.</param>
    /// <returns>Whether Windows accepted the write.</returns>
    /// <remarks>
    ///     Machine-wide; Windows may refuse an unelevated caller. The request is issued once and a refusal
    ///     changes nothing about the setting's values.
    /// </remarks>
    public static bool TryWriteSettingAttributes(Guid subgroup, Guid setting, PowerSettingAttributes attributes,
        out uint status)
    {
        status = PowerWriteSettingAttributes(in subgroup, in setting, (uint)attributes);
        return status == Win32Error.ErrorSuccess;
    }

    /// <summary>Posts the display-off system command (<c>SC_MONITORPOWER</c>) to every top-level window.</summary>
    /// <returns>Whether Windows accepted the message.</returns>
    /// <remarks>
    ///     On a Modern Standby (S0 low power idle) machine this enters standby: Kernel-Power logs event 506,
    ///     "entering modern standby", reason SC_MONITORPOWER. It is the only request a desktop process can
    ///     make that does; <see cref="SuspendAsync(bool, System.Threading.CancellationToken)" /> hibernates
    ///     there. Posted, not sent, so a hung window cannot stall the caller.
    /// </remarks>
    public static bool TurnOffDisplay()
    {
        const uint WmSysCommand = 0x0112;
        const nint ScMonitorPower = 0xF170;
        const nint MonitorOff = 2;
        return User32.PostMessageW(User32.HwndBroadcast, WmSysCommand, ScMonitorPower, MonitorOff);
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerWriteSettingAttributes(in Guid subgroup, in Guid setting, uint attributes);
}
