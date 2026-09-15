using System;
using System.Text;
using Xunit;
using static WindowsDeviceControl.Tests.TestFixtures;

namespace WindowsDeviceControl.Tests;

public sealed class ModernStandbyTests
{
    private const int CapabilitiesBytes = 76;

    [Fact]
    public void CapabilitiesAreReadFromTheirDocumentedOffsets()
    {
        byte[] buffer = new byte[CapabilitiesBytes];
        buffer[0] = 1;  // PowerButtonPresent
        buffer[2] = 1;  // LidPresent
        buffer[19] = 1; // WakeAlarmPresent
        buffer[20] = 1; // AoAc

        Assert.Equal(
            new ModernStandbySupport(
                LowPowerIdle: true,
                ConnectedStandby: false,
                WakeAlarm: true,
                PowerButton: true,
                SleepButton: false,
                Lid: true),
            ModernStandby.ReadCapabilities(buffer));
    }

    [Fact]
    public void ConnectedStandbyIsItsOwnFieldRatherThanImpliedByModernStandby()
    {
        byte[] buffer = new byte[CapabilitiesBytes];
        buffer[23] = 1; // AoAcConnectivitySupported without AoAc

        ModernStandbySupport support = ModernStandby.ReadCapabilities(buffer);
        Assert.False(support.LowPowerIdle);
        Assert.True(support.ConnectedStandby);
    }

    [Fact]
    public void ATruncatedCapabilityStructureIsRefused()
        => AssertInvalidData(() => ModernStandby.ReadCapabilities(new byte[CapabilitiesBytes - 1]));

    [Fact]
    public void ADeviceNameEndsAtItsTerminatorRatherThanAtTheBufferSize()
    {
        // Windows leaves the size argument at the buffer size it was handed, so the terminator is
        // the only length there is. A name read to the buffer's end would carry 4 KB of padding.
        byte[] buffer = new byte[4096];
        Encoding.Unicode.GetBytes("Intel(R) Wi-Fi 7 BE201 320MHz").CopyTo(buffer, 0);

        Assert.Equal("Intel(R) Wi-Fi 7 BE201 320MHz", ModernStandby.DecodeDeviceName(buffer));
    }

    [Fact]
    public void ANameWithNoTerminatorInTheBufferIsRefused()
        => AssertInvalidData(() => ModernStandby.DecodeDeviceName(Encoding.Unicode.GetBytes("USB4")));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyNameIsRefusedBecauseItNamesNoDevice(string name)
    {
        byte[] buffer = new byte[64];
        Encoding.Unicode.GetBytes(name).CopyTo(buffer, 0);

        AssertInvalidData(() => ModernStandby.DecodeDeviceName(buffer));
    }

    [Theory]
    [MemberData(nameof(RestoreCases))]
    public void RestoreWritesOnlyObservedProgrammableDevicesWhoseArmingMoved(
        WakeDevice[] current, WakeDeviceSnapshot snapshot, (string Name, bool Armed)[] expected)
        => Assert.Equal(expected, ModernStandby.RestorePlan(current, snapshot));

    public static TheoryData<WakeDevice[], WakeDeviceSnapshot, (string Name, bool Armed)[]> RestoreCases => new()
    {
        // Only the device whose arming actually moved is written.
        {
            [new("Sensors", true, WakeDeviceControl.Programmable), new("Wi-Fi", true, WakeDeviceControl.Programmable)],
            new(["Sensors", "Wi-Fi"], ["Wi-Fi"]),
            [("Sensors", false)]
        },
        // Restoring an unchanged machine writes nothing.
        {
            [new("Sensors", false, WakeDeviceControl.Programmable), new("Wi-Fi", true, WakeDeviceControl.Programmable)],
            new(["Sensors", "Wi-Fi"], ["Wi-Fi"]),
            []
        },
        // A device that appeared after the snapshot has no prior state, and inventing one would be a
        // change dressed as a restore.
        {
            [new("Dock", true, WakeDeviceControl.Programmable)],
            new(["Wi-Fi"], ["Wi-Fi"]),
            []
        },
        // A device that is no longer programmable is skipped rather than failing the restore.
        {
            [new("Firmware timer", true, WakeDeviceControl.Fixed), new("Wi-Fi", true, WakeDeviceControl.Programmable)],
            new(["Firmware timer", "Wi-Fi"], []),
            [("Wi-Fi", false)]
        },
    };

    [Fact]
    public void TheSubgroupAndSettingIdentitiesAreTheOnesWindowsPublishes()
    {
        // Verified against powercfg /qh on a Modern Standby machine: these aliases are SUB_SLEEP,
        // SUB_NONE, RTCWAKE, AWAYMODE, UNATTENDSLEEP, CONNECTIVITYINSTANDBY and
        // DISCONNECTEDSTANDBYMODE. A typo here writes a different setting than the name promises.
        Assert.Equal(new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20"), ModernStandby.SubgroupSleep);
        Assert.Equal(new Guid("fea3413e-7e05-4911-9a71-700331f1c294"), ModernStandby.SubgroupNone);
        Assert.Equal(
            new Guid("bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d"), ModernStandby.SettingAllowWakeTimers);
        Assert.Equal(
            new Guid("25dfa149-5dd1-4736-b5ab-e8a37b5b8187"), ModernStandby.SettingAllowAwayMode);
        Assert.Equal(
            new Guid("7bc4a2f9-d8fc-4469-b07b-33eb785aaca0"),
            ModernStandby.SettingUnattendedSleepTimeout);
        Assert.Equal(
            new Guid("f15576e8-98b7-4186-b944-eafa664402d9"),
            ModernStandby.SettingConnectivityInStandby);
        Assert.Equal(
            new Guid("68afb2d9-ee95-47a8-8f50-4115088073b1"),
            ModernStandby.SettingDisconnectedStandby);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void AnEmptyDeviceNameIsARejectedArgument(string name)
        => Assert.Throws<ArgumentException>(() => ModernStandby.TrySetWakeArmed(name, armed: true));

    [Theory]
    // Asleep from two hours to nine, read at ten.
    [InlineData(120, 540, 600, 420, 60)]
    // Both marks are zero on a machine that has not slept this boot.
    [InlineData(0, 0, 30, 0, 30)]
    // Interrupt time does not run while asleep, so a wake mark can sit behind the sleep mark.
    [InlineData(300, 60, 300, 0, 240)]
    // A wake mark ahead of the read is not negative time since the wake.
    [InlineData(0, 180, 120, 180, 0)]
    public void StandbyTimingNeverMeasuresNegativeTime(int sleep, int wake, int now, int slept, int sinceWake)
    {
        StandbyTiming timing = new(
            TimeSpan.FromMinutes(sleep), TimeSpan.FromMinutes(wake), TimeSpan.FromMinutes(now));

        Assert.Equal(TimeSpan.FromMinutes(slept), timing.Slept);
        Assert.Equal(TimeSpan.FromMinutes(sinceWake), timing.SinceWake);
    }
}
