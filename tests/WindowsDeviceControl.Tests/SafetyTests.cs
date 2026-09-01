using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace WindowsDeviceControl.Tests;

public sealed class SafetyTests
{
    [Fact]
    public void AggregatePowerReportsDisabledBeforeOff()
    {
        var result = WindowsRadio.AggregatePower(
            [WindowsRadio.Power.Off, WindowsRadio.Power.Disabled]);

        Assert.Equal(WindowsRadio.Power.Disabled, result);
    }

    [Fact]
    public void InvalidRadioKindIsRejectedBeforeEnumeration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WindowsRadio.GetPower((WindowsRadio.RadioKind)int.MaxValue));
    }

    [Fact]
    public void UnknownAuthenticationIsUnsupported()
    {
        Assert.Equal(
            WindowsRadio.WifiSecurity.Unsupported,
            WindowsRadio.ClassifySecurity(secured: true, auth: int.MaxValue));
    }

    [Fact]
    public void ProfileMutationKeepsExactPreviousXml()
    {
        var target = new byte[] { 1, 2, 3 };
        const string xml = "<WLANProfile><SSIDConfig /></WLANProfile>";
        var profiles = new[]
        {
            new WindowsRadio.SavedProfile("network", target, xml),
        };

        var mutation = WindowsRadio.FindFreeProfileName(profiles, "network", target);

        Assert.True(mutation.Existed);
        Assert.Equal("network", mutation.Name);
        Assert.Equal(xml, mutation.PreviousXml);
    }

    [Fact]
    public void ProfileMutationDoesNotOverwriteUnreadableProfile()
    {
        var target = new byte[] { 1, 2, 3 };
        var profiles = new[]
        {
            new WindowsRadio.SavedProfile("network", null, null),
        };

        var mutation = WindowsRadio.FindFreeProfileName(profiles, "network", target);

        Assert.False(mutation.Existed);
        Assert.Equal("network 2", mutation.Name);
    }

    [Fact]
    public void ProfileMutationNeverOverwritesAfterSuffixExhaustion()
    {
        var profiles = Enumerable.Range(1, 64)
            .Select(index => new WindowsRadio.SavedProfile(
                index == 1 ? "network" : $"network {index}",
                new[] { checked((byte)index) },
                $"<profile>{index}</profile>"))
            .ToArray();

        Assert.Throws<InvalidOperationException>(
            () => WindowsRadio.FindFreeProfileName(profiles, "network", [0]));
    }

    [Fact]
    public void ConflictingAdapterFactsAreNotMergedIntoAConnectableCandidate()
    {
        var first = Facts(
            WindowsRadio.WifiSecurity.PersonalPsk,
            authentication: 7,
            profile: "network");
        var second = Facts(
            WindowsRadio.WifiSecurity.Open,
            authentication: 1,
            profile: "network 2");

        var merged = WindowsRadio.MergeNetworkFacts(first, second);

        Assert.True(merged.Ambiguous);
        Assert.False(merged.Connectable);
        Assert.Equal(WindowsRadio.WifiSecurity.Unsupported, merged.Security);
        Assert.Null(merged.ProfileName);
    }

    [Fact]
    public void ExistingSsidAmbiguityCannotBeResetByLaterObservation()
    {
        var ambiguous = Facts(
            WindowsRadio.WifiSecurity.PersonalPsk,
            authentication: 7,
            profile: "network") with
        {
            Ambiguous = true,
        };
        var compatible = Facts(
            WindowsRadio.WifiSecurity.PersonalPsk,
            authentication: 7,
            profile: "network");

        var merged = WindowsRadio.MergeNetworkFacts(ambiguous, compatible);

        Assert.True(merged.Ambiguous);
        Assert.False(merged.Connectable);
    }

    [Fact]
    public void BluetoothTransportEndpointsShareContainerIdentity()
    {
        const string property = "System.Devices.Aep.ContainerId";
        var container = Guid.NewGuid();
        var fromClassic = WindowsRadio.BluetoothIdentity(
            "classic-id",
            new Dictionary<string, object> { [property] = container });
        var fromLowEnergy = WindowsRadio.BluetoothIdentity(
            "le-id",
            new Dictionary<string, object> { [property] = container.ToString("B") });

        Assert.Equal(fromClassic, fromLowEnergy, ignoreCase: true);
    }

    [Fact]
    public void UnknownPairingResponseIsIdempotent()
    {
        WindowsRadio.RespondToPairing(uint.MaxValue, accept: false, pin: null);
        WindowsRadio.RespondToPairing(uint.MaxValue, accept: false, pin: null);
    }

    [Fact]
    public void DefaultEndpointFailureRollsBackEveryAppliedRole()
    {
        var applyFailure = unchecked((int)0x80004005);
        var rollbackFailure = unchecked((int)0x80070005);
        var previous = new Dictionary<CoreAudio.AudioRole, string>
        {
            [CoreAudio.AudioRole.Console] = "old-console",
            [CoreAudio.AudioRole.Multimedia] = "old-media",
            [CoreAudio.AudioRole.Communications] = "old-comms",
        };
        var calls = new List<(string Id, CoreAudio.AudioRole Role)>();

        var result = CoreAudio.ApplyDefaultEndpointTransaction(
            "target",
            previous,
            (id, role) =>
            {
                calls.Add((id, role));
                if (id == "target" && role == CoreAudio.AudioRole.Communications)
                {
                    return applyFailure;
                }
                return id == "old-media" ? rollbackFailure : 0;
            },
            out var roleResults);

        Assert.Equal(applyFailure, result);
        Assert.Equal(
            new[]
            {
                ("target", CoreAudio.AudioRole.Console),
                ("target", CoreAudio.AudioRole.Multimedia),
                ("target", CoreAudio.AudioRole.Communications),
                ("old-media", CoreAudio.AudioRole.Multimedia),
                ("old-console", CoreAudio.AudioRole.Console),
            },
            calls);
        Assert.Equal(0, roleResults.Single(item => item.Role == CoreAudio.AudioRole.Console)
            .RollbackHResult);
        Assert.Equal(
            rollbackFailure,
            roleResults.Single(item => item.Role == CoreAudio.AudioRole.Multimedia)
                .RollbackHResult);
    }

    [Fact]
    public void EndpointSortIsFullyDeterministic()
    {
        var endpoints = new List<CoreAudio.AudioEndpoint>
        {
            new("z", "Same", false),
            new("b", "beta", false),
            new("a", "Alpha", false),
            new("default", "Zulu", true),
        };

        endpoints.Sort(CoreAudio.CompareEndpoints);

        Assert.Equal(new[] { "default", "a", "b", "z" }, endpoints.Select(item => item.Id));
    }

    [Fact]
    public void QueuedWaveOutCueReturnsSuccessWithoutWritingAgain()
    {
        Assert.True(WaveOutFeedback.TryGetQueuedResult(0x10, out var result));
        Assert.Equal(0, result);
    }

    private static WindowsRadio.WifiNetworkFacts Facts(
        WindowsRadio.WifiSecurity security,
        int authentication,
        string profile)
        => new(
            "network",
            [1, 2, 3],
            50,
            security,
            authentication,
            true,
            true,
            false,
            profile,
            false);
}
