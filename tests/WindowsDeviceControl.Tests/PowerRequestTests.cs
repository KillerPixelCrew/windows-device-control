using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Xunit;

namespace WindowsDeviceControl.Tests;

public sealed class PowerRequestTests
{
    [Fact]
    public void AcquireAndReleaseAreIdempotentAndDisposeClosesExactlyOnce()
    {
        FakeApi api = new();
        var request = new WindowsPowerRequest("test reason", WindowsPowerRequestKind.System, api);
        Assert.Equal(0, api.Creates);
        request.Acquire(); request.Acquire();
        Assert.True(request.IsHeld);
        Assert.Equal(1, api.Creates); Assert.Equal(1, api.Sets);
        Assert.Equal("test reason", Marshal.PtrToStringUni(api.Reason));
        request.Release(); request.Release();
        Assert.False(request.IsHeld); Assert.Equal(1, api.Clears);
        request.Dispose(); request.Dispose();
        Assert.Equal(1, api.Closes);
        Assert.Throws<ObjectDisposedException>(() => request.Acquire());
    }

    [Fact]
    public void FailedClearRemainsHeldUntilHandleIsClosedWithoutRepeatingTheWrite()
    {
        FakeApi api = new() { ClearSucceeds = false };
        var request = new WindowsPowerRequest("test", WindowsPowerRequestKind.Display, api);
        request.Acquire();
        Assert.Equal(5, Assert.Throws<Win32Exception>(() => request.Release()).NativeErrorCode);
        Assert.True(request.IsHeld);
        request.Dispose();
        Assert.False(request.IsHeld);
        Assert.Equal(1, api.Clears); Assert.Equal(1, api.Closes);
    }

    [Fact]
    public void FailedSetIsNotReportedHeldOrRetried()
    {
        FakeApi api = new() { SetSucceeds = false };
        using var request = new WindowsPowerRequest("test", WindowsPowerRequestKind.System, api);
        Assert.Throws<Win32Exception>(() => request.Acquire());
        Assert.False(request.IsHeld); Assert.Equal(1, api.Sets);
    }

    private sealed class FakeApi : IPowerRequestApi
    {
        internal int Creates, Sets, Clears, Closes;
        internal nint Reason;
        internal bool SetSucceeds = true, ClearSucceeds = true;
        public int LastError => 5;
        public nint Create(nint reason) { Creates++; Reason = reason; return 17; }
        public bool Set(nint request, int kind) { Sets++; return SetSucceeds; }
        public bool Clear(nint request, int kind) { Clears++; return ClearSucceeds; }
        public void Close(nint request) { Assert.Equal((nint)17, request); Closes++; }
    }
}
