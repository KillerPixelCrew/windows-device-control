using Xunit;

namespace WindowsDeviceControl.Tests;

public sealed class CoreAudioCurrentFormatTests
{
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
}
