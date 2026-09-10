using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using Xunit;

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
    {
        var error = Assert.Throws<Win32Exception>(
            () => ModernStandby.ReadCapabilities(new byte[CapabilitiesBytes - 1]));
        Assert.Equal(13, error.NativeErrorCode);
    }

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
    {
        byte[] buffer = Encoding.Unicode.GetBytes("USB4");

        var error = Assert.Throws<Win32Exception>(() => ModernStandby.DecodeDeviceName(buffer));
        Assert.Equal(13, error.NativeErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyNameIsRefusedBecauseItNamesNoDevice(string name)
    {
        byte[] buffer = new byte[64];
        Encoding.Unicode.GetBytes(name).CopyTo(buffer, 0);

        var error = Assert.Throws<Win32Exception>(() => ModernStandby.DecodeDeviceName(buffer));
        Assert.Equal(13, error.NativeErrorCode);
    }

    [Fact]
    public void RestoreWritesOnlyTheDevicesWhoseArmingActuallyMoved()
    {
        List<WakeDevice> current =
        [
            new("Sensors", true, WakeDeviceControl.Programmable),
            new("Wi-Fi", true, WakeDeviceControl.Programmable),
        ];
        WakeDeviceSnapshot snapshot = new(["Sensors", "Wi-Fi"], ["Wi-Fi"]);

        Assert.Equal([("Sensors", false)], ModernStandby.RestorePlan(current, snapshot));
    }

    [Fact]
    public void RestoringAnUnchangedMachineWritesNothing()
    {
        List<WakeDevice> current =
        [
            new("Sensors", false, WakeDeviceControl.Programmable),
            new("Wi-Fi", true, WakeDeviceControl.Programmable),
        ];

        Assert.Empty(ModernStandby.RestorePlan(current, new(["Sensors", "Wi-Fi"], ["Wi-Fi"])));
    }

    [Fact]
    public void ADeviceThatAppearedAfterTheSnapshotIsLeftAlone()
    {
        // There is no prior state for it, and inventing one would be a change dressed as a restore.
        List<WakeDevice> current = [new("Dock", true, WakeDeviceControl.Programmable)];

        Assert.Empty(ModernStandby.RestorePlan(current, new(["Wi-Fi"], ["Wi-Fi"])));
    }

    [Fact]
    public void ADeviceThatIsNoLongerProgrammableIsSkippedRatherThanFailingTheRestore()
    {
        List<WakeDevice> current =
        [
            new("Firmware timer", true, WakeDeviceControl.Fixed),
            new("Wi-Fi", true, WakeDeviceControl.Programmable),
        ];
        WakeDeviceSnapshot snapshot = new(["Firmware timer", "Wi-Fi"], []);

        Assert.Equal([("Wi-Fi", false)], ModernStandby.RestorePlan(current, snapshot));
    }

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

    [Fact]
    public void StandbyTimingMeasuresTheSleepAndTheTimeSinceTheWake()
    {
        StandbyTiming timing = new(
            Sleep: TimeSpan.FromHours(2),
            Wake: TimeSpan.FromHours(9),
            Now: TimeSpan.FromHours(10));

        Assert.Equal(TimeSpan.FromHours(7), timing.Slept);
        Assert.Equal(TimeSpan.FromHours(1), timing.SinceWake);
    }

    [Fact]
    public void AMachineThatHasNotSleptThisBootMeasuresNoSleep()
    {
        // Both marks are zero on a machine that has never slept, and interrupt time does not run
        // while asleep, so a wake mark can also sit behind the sleep mark. Neither is negative time.
        StandbyTiming never = new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromMinutes(30));
        Assert.Equal(TimeSpan.Zero, never.Slept);
        Assert.Equal(TimeSpan.FromMinutes(30), never.SinceWake);

        StandbyTiming asleep = new(TimeSpan.FromHours(5), TimeSpan.FromHours(1), TimeSpan.FromHours(5));
        Assert.Equal(TimeSpan.Zero, asleep.Slept);
    }

    [Fact]
    public void AWakeMarkAheadOfTheReadDoesNotProduceNegativeTimeSinceWake()
    {
        StandbyTiming timing = new(TimeSpan.Zero, TimeSpan.FromHours(3), TimeSpan.FromHours(2));

        Assert.Equal(TimeSpan.Zero, timing.SinceWake);
    }
}
