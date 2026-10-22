using Abl.Core.Rendering;
using Xunit;

namespace Abl.Core.Tests;

public class GpuLeaseTests
{
    [Fact]
    public async Task SingleSlotBlocksSecondAcquirer()
    {
        var lease = new GpuLease();
        var ct = CancellationToken.None;
        using var first = await lease.TryAcquireAsync(TimeSpan.Zero, ct);
        Assert.NotNull(first);

        var second = await lease.TryAcquireAsync(TimeSpan.FromMilliseconds(50), ct);
        Assert.Null(second);
    }

    [Fact]
    public async Task ReleaseHandsTheSlotToTheWaiter()
    {
        var lease = new GpuLease();
        var ct = CancellationToken.None;
        var first = await lease.TryAcquireAsync(TimeSpan.Zero, ct);
        var waiter = lease.TryAcquireAsync(TimeSpan.FromSeconds(5), ct);
        Assert.False(waiter.IsCompleted);

        first!.Dispose();
        using var second = await waiter;
        Assert.NotNull(second);
    }

    [Fact]
    public async Task DoubleDisposeReleasesOnce()
    {
        var lease = new GpuLease();
        var ct = CancellationToken.None;
        var first = await lease.TryAcquireAsync(TimeSpan.Zero, ct);
        first!.Dispose();
        first.Dispose();

        // If Dispose released twice the semaphore count would exceed 1 and this would allow two holders.
        using var a = await lease.TryAcquireAsync(TimeSpan.Zero, ct);
        var b = await lease.TryAcquireAsync(TimeSpan.FromMilliseconds(50), ct);
        Assert.NotNull(a);
        Assert.Null(b);
    }
}
