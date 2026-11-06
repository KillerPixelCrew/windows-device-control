using System;
using System.Collections.Generic;
using Xunit;

namespace WindowsDeviceControl.Tests;

public sealed class EffectivePowerModeTests
{
    [Fact]
    public void ANotificationReachesOnlyTheCallbackRegisteredUnderItsId()
    {
        var first = 0;
        var second = 0;
        var firstId = WindowsPower.AddEffectiveModeCallback(new WindowsPower.EffectiveModeCallback(() => first++, null));
        var secondId =
            WindowsPower.AddEffectiveModeCallback(new WindowsPower.EffectiveModeCallback(() => second++, null));
        try
        {
            WindowsPower.DispatchEffectiveModeChange(secondId);

            Assert.Equal(0, first);
            Assert.Equal(1, second);
        }
        finally
        {
            WindowsPower.RemoveEffectiveModeCallback(firstId);
            WindowsPower.RemoveEffectiveModeCallback(secondId);
        }
    }

    [Fact]
    public void ANotificationAfterUnregisteringCallsNothing()
    {
        var calls = 0;
        var registration = Register(() => calls++, null, _ => 0, out var id);

        Assert.Equal(0, registration.Unregister());
        WindowsPower.DispatchEffectiveModeChange(id);

        Assert.Equal(0, calls);
    }

    [Fact]
    public void UnregisteringTwiceCallsWindowsOnce()
    {
        List<nint> unregistered = [];
        var registration = Register(() => { }, null, handle =>
        {
            unregistered.Add(handle);
            return 0;
        }, out _);

        registration.Unregister();
        registration.Dispose();
        registration.Unregister();

        Assert.Equal([(nint)42], unregistered);
    }

    [Fact]
    public void ARefusedUnregistrationOnDisposeGoesToTheFailureHandler()
    {
        List<Exception> failures = [];
        var registration = Register(() => { }, failures.Add, _ => unchecked((int)0x80070005), out _);

        registration.Dispose();
        registration.Dispose();

        Assert.Single(failures);
        Assert.Equal(unchecked((int)0x80070005), failures[0].HResult);
    }

    [Fact]
    public void ACallbackExceptionGoesToTheFailureHandler()
    {
        List<Exception> failures = [];
        var registration = Register(() => throw new InvalidOperationException("boom"), failures.Add, _ => 0,
            out var id);
        try
        {
            WindowsPower.DispatchEffectiveModeChange(id);

            Assert.IsType<InvalidOperationException>(Assert.Single(failures));
        }
        finally
        {
            registration.Dispose();
        }
    }

    [Fact]
    public void AFailureHandlerThatThrowsIsContainedToo()
    {
        var registration = Register(() => throw new InvalidOperationException("boom"),
            _ => throw new InvalidOperationException("handler"), _ => unchecked((int)0x80004005), out var id);

        WindowsPower.DispatchEffectiveModeChange(id);
        registration.Dispose();
    }

    [Fact]
    public void ACallbackExceptionWithoutAHandlerIsDiscarded()
    {
        var registration = Register(() => throw new InvalidOperationException("boom"), null, _ => 0, out var id);
        try
        {
            WindowsPower.DispatchEffectiveModeChange(id);
        }
        finally
        {
            registration.Dispose();
        }
    }

    private static EffectivePowerModeRegistration Register(Action changed, Action<Exception>? failed,
        Func<nint, int> unregister, out nint id)
    {
        WindowsPower.EffectiveModeCallback callback = new(changed, failed);
        id = WindowsPower.AddEffectiveModeCallback(callback);
        return new EffectivePowerModeRegistration(id, 42, callback, unregister);
    }
}
