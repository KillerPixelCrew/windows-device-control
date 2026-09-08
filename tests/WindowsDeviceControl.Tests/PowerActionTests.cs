using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace WindowsDeviceControl.Tests;

public sealed class PowerActionTests
{
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
