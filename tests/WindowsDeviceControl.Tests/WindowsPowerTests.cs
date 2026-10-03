using System;
using System.ComponentModel;
using System.Diagnostics;
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
        var bytes = Encoding.Unicode.GetBytes(name + "\0ignored padding");
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
        var bytes = Encoding.Unicode.GetBytes("abc\0");
        AssertInvalidData(() => WindowsPower.DecodeName(bytes, size, Custom));
    }

    [Fact]
    public void EmptyNameFallsBackToStableGuid()
    {
        Assert.Equal(Custom.ToString("D"), WindowsPower.DecodeName([0, 0], 2, Custom));
    }

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
    public async Task CancelledActionsNeverCallThePort()
    {
        CancellationToken cancelled = new(true);
        var api = new FakePowerActionApi();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsPower.SuspendAsync(false, api, cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WindowsPower.RequestActionAsync(WindowsPowerAction.Restart, api, cancelled));
        Assert.Equal(0, api.SuspendCalls);
        Assert.Equal(0, api.ToolCalls);
    }

    [Theory]
    [InlineData(WindowsPowerAction.Shutdown, "/s /t 0", false)]
    [InlineData(WindowsPowerAction.Restart, "/r /t 0", true)]
    [InlineData(WindowsPowerAction.SignOut, "/l", false)]
    public async Task ActionArgumentsAreForwardedUnchanged(WindowsPowerAction action, string arguments, bool hibernate)
    {
        using var cancellation = new CancellationTokenSource();
        var api = new FakePowerActionApi();
        await WindowsPower.SuspendAsync(hibernate, api, cancellation.Token);
        await WindowsPower.RequestActionAsync(action, api, cancellation.Token);

        Assert.Equal(1, api.SuspendCalls);
        Assert.Equal(hibernate, api.Hibernate);
        Assert.Equal(1, api.ToolCalls);
        Assert.NotNull(api.Start);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), api.Start.FileName);
        Assert.Equal(arguments, api.Start.Arguments);
        Assert.True(api.Start.CreateNoWindow);
        Assert.False(api.Start.UseShellExecute);
        Assert.Equal(cancellation.Token, api.Token);
    }

    [Fact]
    public async Task ToolRefusalPreservesTheExitCodeWithoutRetry()
    {
        var api = new FakePowerActionApi { ExitCode = 5 };
        var error = await Assert.ThrowsAsync<Win32Exception>(() =>
            WindowsPower.RequestActionAsync(WindowsPowerAction.Restart, api, CancellationToken.None));
        Assert.Equal(5, error.NativeErrorCode);
        Assert.Equal("Windows refused the power request.", error.Message);
        Assert.Equal(1, api.ToolCalls);
    }

    [Fact]
    public async Task SuspendFailurePropagatesOnce()
    {
        var refusal = new Win32Exception(50);
        var api = new FakePowerActionApi { SuspendFailure = refusal };
        var error = await Assert.ThrowsAsync<Win32Exception>(() =>
            WindowsPower.SuspendAsync(true, api, CancellationToken.None));
        Assert.Same(refusal, error);
        Assert.Equal(1, api.SuspendCalls);
        Assert.Equal(0, api.ToolCalls);
    }

    [Fact]
    public void UnknownActionIsRejectedBeforeDispatch()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsPower.ActionStartInfo((WindowsPowerAction)100));
    }

    private sealed class FakePowerActionApi : IPowerActionApi
    {
        internal int SuspendCalls, ToolCalls;
        internal bool Hibernate;
        internal int ExitCode;
        internal Win32Exception? SuspendFailure;
        internal ProcessStartInfo? Start;
        internal CancellationToken Token;

        public bool Suspend(bool hibernate)
        {
            SuspendCalls++;
            Hibernate = hibernate;
            if (SuspendFailure is not null)
            {
                throw SuspendFailure;
            }

            return true;
        }

        public Task<int> RunToolAsync(ProcessStartInfo start, CancellationToken cancellationToken)
        {
            ToolCalls++;
            Start = start;
            Token = cancellationToken;
            return Task.FromResult(ExitCode);
        }
    }
}
