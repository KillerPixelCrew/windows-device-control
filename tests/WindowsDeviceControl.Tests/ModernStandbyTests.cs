using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using Xunit;
using static WindowsDeviceControl.Tests.TestFixtures;

namespace WindowsDeviceControl.Tests;

public sealed class ModernStandbyTests
{
    private const int CapabilitiesBytes = 76;

    public static TheoryData<WakeDevice[], WakeDeviceSnapshot, (string Name, bool Armed)[]> RestoreCases => new()
    {
        // Only the device whose arming actually moved is written.
        {
            [
                new WakeDevice("Sensors", true, WakeDeviceControl.Programmable),
                new WakeDevice("Wi-Fi", true, WakeDeviceControl.Programmable)
            ],
            new WakeDeviceSnapshot(["Sensors", "Wi-Fi"], ["Wi-Fi"]),
            [("Sensors", false)]
        },
        // Restoring an unchanged machine writes nothing.
        {
            [
                new WakeDevice("Sensors", false, WakeDeviceControl.Programmable),
                new WakeDevice("Wi-Fi", true, WakeDeviceControl.Programmable)
            ],
            new WakeDeviceSnapshot(["Sensors", "Wi-Fi"], ["Wi-Fi"]),
            []
        },
        // A device that appeared after the snapshot has no prior state, and inventing one would be a
        // change dressed as a restore.
        {
            [new WakeDevice("Dock", true, WakeDeviceControl.Programmable)],
            new WakeDeviceSnapshot(["Wi-Fi"], ["Wi-Fi"]),
            []
        },
        // A device that is no longer programmable is skipped rather than failing the restore.
        {
            [
                new WakeDevice("Firmware timer", true, WakeDeviceControl.Fixed),
                new WakeDevice("Wi-Fi", true, WakeDeviceControl.Programmable)
            ],
            new WakeDeviceSnapshot(["Firmware timer", "Wi-Fi"], []),
            [("Wi-Fi", false)]
        }
    };

    [Fact]
    public void CapabilitiesAreReadFromTheirDocumentedOffsets()
    {
        var buffer = new byte[CapabilitiesBytes];
        buffer[0] = 1; // PowerButtonPresent
        buffer[2] = 1; // LidPresent
        buffer[19] = 1; // WakeAlarmPresent
        buffer[20] = 1; // AoAc

        Assert.Equal(
            new ModernStandbySupport(
                true,
                false,
                true,
                true,
                false,
                true),
            ModernStandby.ReadCapabilities(buffer));
    }

    [Fact]
    public void ConnectedStandbyIsItsOwnFieldRatherThanImpliedByModernStandby()
    {
        var buffer = new byte[CapabilitiesBytes];
        buffer[23] = 1; // AoAcConnectivitySupported without AoAc

        var support = ModernStandby.ReadCapabilities(buffer);
        Assert.False(support.LowPowerIdle);
        Assert.True(support.ConnectedStandby);
    }

    [Fact]
    public void ATruncatedCapabilityStructureIsRefused()
    {
        AssertInvalidData(() => ModernStandby.ReadCapabilities(new byte[CapabilitiesBytes - 1]));
    }

    [Fact]
    public void ADeviceNameEndsAtItsTerminatorRatherThanAtTheBufferSize()
    {
        // Windows leaves the size argument at the buffer size it was handed, so the terminator is
        // the only length there is. A name read to the buffer's end would carry 4 KB of padding.
        var buffer = new byte[4096];
        Encoding.Unicode.GetBytes("Intel(R) Wi-Fi 7 BE201 320MHz").CopyTo(buffer, 0);

        Assert.Equal("Intel(R) Wi-Fi 7 BE201 320MHz", ModernStandby.DecodeDeviceName(buffer));
    }

    [Fact]
    public void ANameWithNoTerminatorInTheBufferIsRefused()
    {
        AssertInvalidData(() => ModernStandby.DecodeDeviceName(Encoding.Unicode.GetBytes("USB4")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyNameIsRefusedBecauseItNamesNoDevice(string name)
    {
        var buffer = new byte[64];
        Encoding.Unicode.GetBytes(name).CopyTo(buffer, 0);

        AssertInvalidData(() => ModernStandby.DecodeDeviceName(buffer));
    }

    [Theory]
    [MemberData(nameof(RestoreCases))]
    public void RestoreWritesOnlyObservedProgrammableDevicesWhoseArmingMoved(
        WakeDevice[] current, WakeDeviceSnapshot snapshot, (string Name, bool Armed)[] expected)
    {
        Assert.Equal(expected, ModernStandby.RestorePlan(current, snapshot));
    }

    [Fact]
    public void RestoreAttemptsEveryPlannedDeviceAndCollectsEachFailure()
    {
        (string Name, bool Armed)[] plan = [("A", true), ("B", false), ("C", true)];
        List<string> written = [];

        var failures = ModernStandby.ExecuteRestore(plan, (name, _) =>
        {
            written.Add(name);
            if (name == "B")
            {
                throw new Win32Exception(4214);
            }
        });

        Assert.Equal(["A", "B", "C"], written);
        Assert.Equal(new WakeDeviceRestoreFailure("B", 4214), Assert.Single(failures));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void AnEmptyDeviceNameIsARejectedArgument(string name)
    {
        Assert.Throws<ArgumentException>(() => ModernStandby.TrySetWakeArmed(name, true));
    }

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
