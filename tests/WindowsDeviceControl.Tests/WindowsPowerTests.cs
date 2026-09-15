using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using static WindowsDeviceControl.Tests.TestFixtures;

namespace WindowsDeviceControl.Tests;

public sealed class WindowsPowerTests
{
    private static readonly Guid Custom = new("d0905d52-3ba4-4fe9-a463-e6d96ac80e93");

    [Theory]
    [InlineData("Höchstleistung")]
    [InlineData("省電力")]
    public void DecodesLocalizedUtf16NamesUsingTheReturnedByteCount(string name)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(name + "\0ignored padding");
        Assert.Equal(name, WindowsPower.DecodeName(bytes, (uint)(name.Length + 1) * 2, Custom));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(10)]
    public void RejectsMalformedNameLengthsOrMissingTerminators(uint size)
    {
        byte[] bytes = Encoding.Unicode.GetBytes("abc\0");
        AssertInvalidData(() => WindowsPower.DecodeName(bytes, size, Custom));
    }

    [Fact]
    public void EmptyNameFallsBackToStableGuid()
        => Assert.Equal(Custom.ToString("D"), WindowsPower.DecodeName([0, 0], 2, Custom));

    [Theory]
    [InlineData(WindowsPowerAction.Shutdown, "/s /t 0")]
    [InlineData(WindowsPowerAction.Restart, "/r /t 0")]
    [InlineData(WindowsPowerAction.SignOut, "/l")]
    public void ActionsUseTheSystemToolWithoutForceOrInteractiveWindows(WindowsPowerAction action, string arguments)
    {
        var start = WindowsPower.ActionStartInfo(action);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), start.FileName);
        Assert.Equal(arguments, start.Arguments);
        Assert.True(start.CreateNoWindow);
        Assert.False(start.UseShellExecute);
    }

    [Fact]
    public async Task CancelledActionsNeverDispatch()
    {
        CancellationToken cancelled = new(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsPower.SuspendAsync(false, cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsPower.RequestActionAsync(WindowsPowerAction.Restart, cancelled));
    }

    [Fact]
    public void UnknownActionIsRejectedBeforeDispatch() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsPower.ActionStartInfo((WindowsPowerAction)100));
}
