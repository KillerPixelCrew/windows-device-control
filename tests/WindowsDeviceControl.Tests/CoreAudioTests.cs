using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace WindowsDeviceControl.Tests;

public sealed class CoreAudioTests
{
    private static readonly Guid AudioEngine = new("35CAF6E4-F3B3-4168-BB4B-55E77A461C7E");
    private static readonly Guid Volume = new("3A5ACC00-C557-11D0-8A2B-00A0C9255AC1");

    [Fact]
    public void DefaultEndpointFailureRollsBackEveryAppliedRole()
    {
        var applyFailure = unchecked((int)0x80004005);
        var rollbackFailure = unchecked((int)0x80070005);
        var previous = new Dictionary<CoreAudio.AudioRole, string>
        {
            [CoreAudio.AudioRole.Console] = "old-console",
            [CoreAudio.AudioRole.Multimedia] = "old-media",
            [CoreAudio.AudioRole.Communications] = "old-comms"
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
                ("old-console", CoreAudio.AudioRole.Console)
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
            new("default", "Zulu", true)
        };

        endpoints.Sort(CoreAudio.CompareEndpoints);

        Assert.Equal(new[] { "default", "a", "b", "z" }, endpoints.Select(item => item.Id));
    }

    [Fact]
    public void WinRtDeviceIdWrapsTheEndpointIdInTheRenderInterfaceClass()
    {
        const string endpoint = "{0.0.0.00000000}.{ac47f12e-df80-4b93-b101-44b890dd83c4}";

        Assert.Equal(
            @"\\?\SWD#MMDEVAPI#" + endpoint + "#{e6327cad-dcec-4949-ae8a-991e976a79d2}",
            CoreAudio.ToWinRtDeviceId(endpoint));
        Assert.Equal(@"\\?\already", CoreAudio.ToWinRtDeviceId(@"\\?\already"));
    }

    [Fact]
    public void PcmFormatCarriesTwentyFourBitsInAThirtyTwoBitContainer()
    {
        var format = CoreAudio.AudioDeviceFormat.Pcm(6, 48000, 24);

        Assert.Equal(24, format.BitsPerSample);
        Assert.Equal(32, format.ContainerBitsPerSample);
        Assert.Equal(0x3Fu, format.ChannelMask);
        Assert.False(format.IsFloat);
        Assert.Equal(16, CoreAudio.AudioDeviceFormat.Pcm(2, 44100, 16).ContainerBitsPerSample);
        Assert.Equal(0x63Fu, CoreAudio.AudioDeviceFormat.Pcm(8, 48000, 16).ChannelMask);
        Assert.Equal(0x37u, CoreAudio.AudioDeviceFormat.Pcm(6, 48000, 16, 0x37).ChannelMask);
    }

    [Fact]
    public void ExtensibleFormatIsBuiltWithConsistentSizes()
    {
        var native = CoreAudio.BuildFormat(CoreAudio.AudioDeviceFormat.Pcm(8, 96000, 24));

        Assert.Equal(0xFFFE, native.Format.FormatTag);
        Assert.Equal(8, native.Format.Channels);
        Assert.Equal(96000u, native.Format.SamplesPerSecond);
        Assert.Equal(32, native.Format.BitsPerSample);
        Assert.Equal(24, native.ValidBitsPerSample);
        Assert.Equal(32, native.Format.BlockAlign);
        Assert.Equal(96000u * 32, native.Format.AverageBytesPerSecond);
        Assert.Equal(22, native.Format.ExtraSize);
        Assert.Equal(0x63Fu, native.ChannelMask);
        Assert.Equal(new System.Guid("00000001-0000-0010-8000-00AA00389B71"), native.SubFormat);
        Assert.Equal(40, System.Runtime.InteropServices.Marshal.SizeOf<CoreAudio.WaveFormatExtensible>());
        Assert.Equal(18, System.Runtime.InteropServices.Marshal.SizeOf<CoreAudio.WaveFormat>());
    }

    [Fact]
    public void MixFormatIsFloatAtTheSameLayout()
    {
        var native = CoreAudio.BuildFormat(CoreAudio.AudioDeviceFormat.Pcm(2, 48000, 24).AsMixFormat());

        Assert.Equal(32, native.Format.BitsPerSample);
        Assert.Equal(32, native.ValidBitsPerSample);
        Assert.Equal(2, native.Format.Channels);
        Assert.Equal(new System.Guid("00000003-0000-0010-8000-00AA00389B71"), native.SubFormat);
    }

    [Fact]
    public void ReadingAFormatRoundTripsAndFallsBackForPlainWaveFormat()
    {
        var original = CoreAudio.AudioDeviceFormat.Pcm(6, 48000, 24);

        Assert.True(CoreAudio.TryReadFormat(CoreAudio.BuildFormat(original), out var read));
        Assert.Equal(original, read);

        var plain = new CoreAudio.WaveFormatExtensible
        {
            Format = new CoreAudio.WaveFormat
            {
                FormatTag = 1,
                Channels = 2,
                SamplesPerSecond = 44100,
                BitsPerSample = 16
            }
        };
        Assert.True(CoreAudio.TryReadFormat(plain, out read));
        Assert.Equal(CoreAudio.AudioDeviceFormat.Pcm(2, 44100, 16), read);

        Assert.False(CoreAudio.TryReadFormat(new CoreAudio.WaveFormatExtensible { Format = new CoreAudio.WaveFormat { FormatTag = 0x55 } }, out _));
    }

    [Fact]
    public void ImplausibleFormatsAreRefusedBeforeWindowsSeesThem()
    {
        Assert.True(CoreAudio.AudioDeviceFormat.Pcm(2, 48000, 24).IsPlausible);
        Assert.False(new CoreAudio.AudioDeviceFormat(0, 48000, 16, 16, 0x3, false).IsPlausible);
        Assert.False(new CoreAudio.AudioDeviceFormat(2, 0, 16, 16, 0x3, false).IsPlausible);
        Assert.False(new CoreAudio.AudioDeviceFormat(2, 48000, 24, 16, 0x3, false).IsPlausible);
        Assert.False(new CoreAudio.AudioDeviceFormat(2, 48000, 20, 20, 0x3, false).IsPlausible);
    }

    [Fact]
    public void SharedDefaultRemainsAvailableWhenExclusiveModeRejectsEveryCandidate()
    {
        var current = new CoreAudio.AudioDeviceFormat(2, 48000, 24, 24, 3, false);
        var choices = CoreAudio.IncludeCurrentDeviceFormat([], current);
        Assert.Equal(current, Assert.Single(choices));
    }

    [Fact]
    public void CurrentFormatIsNotDuplicatedAndProbedFormatsAreRetained()
    {
        var current = CoreAudio.AudioDeviceFormat.Pcm(2, 48000, 16);
        var surround = CoreAudio.AudioDeviceFormat.Pcm(6, 96000, 24);
        var choices = CoreAudio.IncludeCurrentDeviceFormat([surround, current], current);
        Assert.Equal([current, surround], choices);
    }

    [Fact]
    public void FailedCurrentFormatReadDoesNotInventChoices()
    {
        Assert.Empty(CoreAudio.IncludeCurrentDeviceFormat([], null));
        Assert.Empty(CoreAudio.IncludeCurrentDeviceFormat([], default(CoreAudio.AudioDeviceFormat)));
    }

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
