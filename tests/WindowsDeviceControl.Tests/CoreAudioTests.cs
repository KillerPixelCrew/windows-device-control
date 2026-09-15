using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace WindowsDeviceControl.Tests;

public sealed class CoreAudioTests
{
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
    public void AQueuedWaveOutCueIsRecognisedBeforeWritingAgain()
    {
        Assert.True(WaveOutFeedback.IsQueued(0x10));
        Assert.False(WaveOutFeedback.IsQueued(0));
    }
}
