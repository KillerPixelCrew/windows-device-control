using System;
using Xunit;

namespace WindowsDeviceControl.Tests;

public sealed class CoreAudioHostPinTests
{
    private static readonly Guid AudioEngine = new("35CAF6E4-F3B3-4168-BB4B-55E77A461C7E");
    private static readonly Guid Volume = new("3A5ACC00-C557-11D0-8A2B-00A0C9255AC1");

    [Fact]
    public void OffloadFilterSelectsThePinFeedingTheEngineHostInput()
    {
        // The SoundWire speaker filter recorded on build 26300: pin 0 feeds the host input and
        // pin 2 the offload input, which accepted formats a default-format write then refused.
        var nodes = new[] { AudioEngine, new Guid("45AAB42E-CAEB-4052-8AA9-B38CB5109619") };
        CoreAudio.KsTopologyConnection[] connections =
        [
            new(uint.MaxValue, 2, 0, 2),
            new(uint.MaxValue, 0, 0, 1),
            new(0, 3, uint.MaxValue, 3),
            new(0, 0, uint.MaxValue, 1)
        ];

        Assert.Equal(0u, CoreAudio.SelectHostPin(nodes, connections));
    }

    [Fact]
    public void FilterWithoutAnAudioEngineHasNoHostPin()
    {
        CoreAudio.KsTopologyConnection[] connections = [new(uint.MaxValue, 0, 0, 1), new(0, 0, uint.MaxValue, 1)];

        Assert.Null(CoreAudio.SelectHostPin([Volume], connections));
    }

    [Fact]
    public void EngineWithNothingOnItsHostInputHasNoHostPin()
    {
        CoreAudio.KsTopologyConnection[] connections = [new(uint.MaxValue, 2, 0, 2), new(0, 0, uint.MaxValue, 1)];

        Assert.Null(CoreAudio.SelectHostPin([AudioEngine], connections));
    }
}
