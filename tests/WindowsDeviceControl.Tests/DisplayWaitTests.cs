using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace WindowsDeviceControl.Tests;

public sealed class DisplayWaitTests
{
    [Fact]
    public async Task AvailableWaitRejectsInvalidDeadlineAndCancelsBeforeNativeQuery()
    {
        DisplayTargetIdentity target = new("display", null, null, "TV", 0, 0, 0);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            DisplayTopology.WaitForAvailableAsync(target, TimeSpan.Zero));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DisplayTopology.WaitForAvailableAsync(target, TimeSpan.FromSeconds(5), new CancellationToken(true)));
    }
}
