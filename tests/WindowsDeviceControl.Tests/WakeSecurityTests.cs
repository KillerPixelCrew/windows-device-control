using System;
using System.IO;
using System.Linq;
using Xunit;

namespace WindowsDeviceControl.Tests;

public sealed class WakeSecurityTests
{
    [Fact]
    public void PolicyTakesPrecedenceForEachPowerLineOnItsOwn()
    {
        Assert.True(WindowsWakeSecurity.IsSignInDisabled(new WakeSecuritySnapshot(true, 0, -1, -1,
            [new WakeSecurityScheme(Guid.NewGuid(), 1, 0)])));
        // An AC-only policy leaves the battery line to the schemes.
        Assert.False(WindowsWakeSecurity.IsSignInDisabled(new WakeSecuritySnapshot(true, 0, -1, -1,
            [new WakeSecurityScheme(Guid.NewGuid(), 1, 1)])));
        Assert.True(WindowsWakeSecurity.IsSignInDisabled(new WakeSecuritySnapshot(true, -1, 0, -1,
            [new WakeSecurityScheme(Guid.NewGuid(), 0, 1)])));
        Assert.False(WindowsWakeSecurity.IsSignInDisabled(new WakeSecuritySnapshot(true, 1, 0, -1,
            [new WakeSecurityScheme(Guid.NewGuid(), 0, 0)])));
    }

    [Fact]
    public void MissingSchemeValuesAndEmptySnapshotsNeverClaimDisabledSignIn()
    {
        Assert.False(WindowsWakeSecurity.IsSignInDisabled(new WakeSecuritySnapshot(false, -1, -1, -1, [])));
        Assert.False(WindowsWakeSecurity.IsSignInDisabled(new WakeSecuritySnapshot(false, -1, -1, -1,
            [new WakeSecurityScheme(Guid.NewGuid(), 0, -1)])));
        Assert.True(WindowsWakeSecurity.IsSignInDisabled(new WakeSecuritySnapshot(false, -1, -1, -1,
            [new WakeSecurityScheme(Guid.NewGuid(), 0, 0)])));
    }

    [Fact]
    public void RestorePlanTouchesOnlyCapturedEntriesAndSkipsVanishedSchemes()
    {
        var kept = Guid.NewGuid();
        var vanished = Guid.NewGuid();
        var snapshot = new WakeSecuritySnapshot(false, -1, -1, 0,
            [new WakeSecurityScheme(kept, 1, 0), new WakeSecurityScheme(vanished, 1, 1)]);

        var plan = WindowsWakeSecurity.RestorePlan(snapshot, [kept, Guid.NewGuid()]);

        WakeSecuritySetting[] order =
        [
            WakeSecuritySetting.ConsoleLockPolicy, WakeSecuritySetting.SchemeConsoleLock,
            WakeSecuritySetting.ActiveSchemeRefresh, WakeSecuritySetting.NoLockScreen
        ];
        Assert.Equal(order, plan.Select(item => item.Setting));
        Assert.False(plan[0].PolicyExisted);
        Assert.Equal(kept, plan[1].Scheme);
        Assert.Equal((1, 0), (plan[1].Ac, plan[1].Dc));
        Assert.Equal(0, plan[3].Ac);
    }

    [Fact]
    public void RestoreAttemptsEveryStepAndCollectsEachFailure()
    {
        var scheme = Guid.NewGuid();
        var plan = WindowsWakeSecurity.RestorePlan(
            new WakeSecuritySnapshot(true, 0, 0, 1, [new WakeSecurityScheme(scheme, 0, 0)]), [scheme]);
        var applied = 0;

        var failures = WindowsWakeSecurity.ExecuteRestore(plan, item =>
        {
            applied++;
            if (item.Setting is WakeSecuritySetting.ConsoleLockPolicy or WakeSecuritySetting.SchemeConsoleLock)
            {
                throw new UnauthorizedAccessException();
            }
        });

        Assert.Equal(plan.Count, applied);
        WakeSecuritySetting[] failed = [WakeSecuritySetting.ConsoleLockPolicy, WakeSecuritySetting.SchemeConsoleLock];
        Assert.Equal(failed, failures.Select(failure => failure.Setting));
        Assert.False(failures[0].Scheme.HasValue);
        Assert.Equal(scheme, failures[1].Scheme);
    }

    [Fact]
    public void ACapturedValueThatIsNotADwordIsRefused()
    {
        Assert.Equal(-1, WindowsWakeSecurity.ToDword(null, "key", "value"));
        Assert.Equal(1, WindowsWakeSecurity.ToDword(1, "key", "value"));
        Assert.Throws<InvalidDataException>(() => WindowsWakeSecurity.ToDword("1", "key", "value"));
    }
}
