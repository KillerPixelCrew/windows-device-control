using System;
using Xunit;

namespace WindowsDeviceControl.Tests;

public sealed class WakeSecurityTests
{
    [Fact]
    public void PolicyTakesPrecedenceAndMissingBatteryPolicyUsesAc()
    {
        Assert.True(WindowsWakeSecurity.IsSignInDisabled(new WakeSecuritySnapshot(true, 0, -1, -1,
            [new WakeSecurityScheme(Guid.NewGuid(), 1, 1)])));
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
}
