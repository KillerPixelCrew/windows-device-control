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
        Assert.Equal(
            @"\\?\SWD#MMDEVAPI#" + endpoint + "#{2eef81be-33fa-4800-9670-1cd474972c3f}",
            CoreAudio.ToWinRtDeviceId(endpoint, CoreAudio.AudioDirection.Capture));
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

        Assert.Equal(0xFFFE, native.FormatTag);
        Assert.Equal(8, native.Channels);
        Assert.Equal(96000u, native.SamplesPerSecond);
        Assert.Equal(32, native.BitsPerSample);
        Assert.Equal(24, native.ValidBitsPerSample);
        Assert.Equal(32, native.BlockAlign);
        Assert.Equal(96000u * 32, native.AverageBytesPerSecond);
        Assert.Equal(22, native.ExtraSize);
        Assert.Equal(0x63Fu, native.ChannelMask);
        Assert.Equal(new System.Guid("00000001-0000-0010-8000-00AA00389B71"), native.SubFormat);
        Assert.Equal(40, System.Runtime.InteropServices.Marshal.SizeOf<CoreAudio.WaveFormatExtensible>());
        Assert.Equal(18, System.Runtime.InteropServices.Marshal.SizeOf<CoreAudio.WaveFormat>());
    }

    [Fact]
    public void MixFormatIsFloatAtTheSameLayout()
    {
        var native = CoreAudio.BuildFormat(CoreAudio.AudioDeviceFormat.Pcm(2, 48000, 24).AsMixFormat());

        Assert.Equal(32, native.BitsPerSample);
        Assert.Equal(32, native.ValidBitsPerSample);
        Assert.Equal(2, native.Channels);
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
            FormatTag = 1,
            Channels = 2,
            SamplesPerSecond = 44100,
            BitsPerSample = 16
        };
        Assert.True(CoreAudio.TryReadFormat(plain, out read));
        Assert.Equal(CoreAudio.AudioDeviceFormat.Pcm(2, 44100, 16), read);

        Assert.False(CoreAudio.TryReadFormat(new CoreAudio.WaveFormatExtensible { FormatTag = 0x55 }, out _));
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
    public void AQueuedWaveOutCueIsRecognisedBeforeWritingAgain()
    {
        Assert.True(WaveOutFeedback.IsQueued(0x10));
        Assert.False(WaveOutFeedback.IsQueued(0));
    }
}
