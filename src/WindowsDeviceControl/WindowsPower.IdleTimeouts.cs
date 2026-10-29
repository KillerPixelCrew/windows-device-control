using System;
using static WindowsDeviceControl.Win32Error;

namespace WindowsDeviceControl;

/// <summary>An idle timeout a power scheme holds once for AC and once for battery, in seconds; zero means never.</summary>
public enum WindowsIdleTimeout
{
    /// <summary>Turn off the display after (VIDEOIDLE).</summary>
    DisplayOff,

    /// <summary>Go to standby after (STANDBYIDLE).</summary>
    Standby,

    /// <summary>Hibernate after (HIBERNATEIDLE). On Modern Standby it counts time spent in standby.</summary>
    Hibernate
}

public static partial class WindowsPower
{
    private const int OffsetSystemS4 = 6;
    private const int OffsetHiberFilePresent = 8;
    private const int OffsetHiberFileType = 22;
    private const int MinimumCapabilityBytes = OffsetHiberFileType + 1;

    /// <summary>HIBERFILE_TYPE_REDUCED: the file serves fast startup only and cannot hold a hibernation.</summary>
    private const byte HiberFileTypeReduced = 1;

    /// <summary>Display settings subgroup (SUB_VIDEO).</summary>
    public static readonly Guid SubgroupVideo = new("7516b95f-f776-4464-8c53-06167f40cc99");

    /// <summary>Turn off the display after (VIDEOIDLE), under <see cref="SubgroupVideo" />.</summary>
    public static readonly Guid SettingDisplayIdle = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");

    /// <summary>Go to standby after (STANDBYIDLE), under <see cref="ModernStandby.SubgroupSleep" />.</summary>
    public static readonly Guid SettingStandbyIdle = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");

    /// <summary>Hibernate after (HIBERNATEIDLE), under <see cref="ModernStandby.SubgroupSleep" />.</summary>
    public static readonly Guid SettingHibernateIdle = new("9d7815a6-7ee4-497e-8888-515a05f02364");

    /// <summary>The scheme setting that stores one idle timeout.</summary>
    /// <param name="timeout">The idle timeout.</param>
    /// <returns>Its subgroup and setting identities, for <see cref="ReadSetting" /> and <see cref="WriteSetting" />.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined timeout.</exception>
    public static (Guid Subgroup, Guid Setting) LocateIdleTimeout(WindowsIdleTimeout timeout)
    {
        return timeout switch
        {
            WindowsIdleTimeout.DisplayOff => (SubgroupVideo, SettingDisplayIdle),
            WindowsIdleTimeout.Standby => (ModernStandby.SubgroupSleep, SettingStandbyIdle),
            WindowsIdleTimeout.Hibernate => (ModernStandby.SubgroupSleep, SettingHibernateIdle),
            _ => throw new ArgumentOutOfRangeException(nameof(timeout), timeout, null)
        };
    }

    /// <summary>Whether this machine can hibernate now, so a hibernate timeout would take effect.</summary>
    /// <returns>False when hibernation is switched off or its file only serves fast startup.</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">Windows could not provide its power capabilities.</exception>
    public static bool IsHibernationAvailable()
    {
        return ReadHibernationAvailable(ModernStandby.ReadPowerCapabilities());
    }

    /// <summary>Decodes hibernation availability from SYSTEM_POWER_CAPABILITIES.</summary>
    /// <param name="buffer">The native capability structure.</param>
    /// <returns>True when S4 is supported and a full hibernation file is present.</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">The structure is truncated.</exception>
    internal static bool ReadHibernationAvailable(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < MinimumCapabilityBytes)
        {
            throw Failure(ErrorInvalidData, "GetPwrCapabilities");
        }

        return buffer[OffsetSystemS4] != 0
               && buffer[OffsetHiberFilePresent] != 0
               && buffer[OffsetHiberFileType] != HiberFileTypeReduced;
    }
}
