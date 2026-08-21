using Jellyfin.Plugin.SessionProvisioning.Security;

namespace Jellyfin.Plugin.SessionProvisioning.Tests;

public static class MintRateLimiterTests
{
    [Fact]
    public static void TryAcquire_WithinLimit_Succeeds()
    {
        using var limiter = new MintRateLimiter(3, TimeSpan.FromMinutes(1));

        for (var i = 0; i < 3; i++)
        {
            Assert.True(limiter.TryAcquire(out var retryAfter), $"request {i + 1} should be permitted");
            Assert.Equal(TimeSpan.Zero, retryAfter);
        }
    }

    [Fact]
    public static void TryAcquire_OverLimit_IsRefusedWithRetryAfter()
    {
        using var limiter = new MintRateLimiter(2, TimeSpan.FromMinutes(1));

        Assert.True(limiter.TryAcquire(out _));
        Assert.True(limiter.TryAcquire(out _));

        Assert.False(limiter.TryAcquire(out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero, "a refused request must say how long to wait");
    }

    [Fact]
    public static void TryAcquire_AfterWindowElapses_IsPermittedAgain()
    {
        using var limiter = new MintRateLimiter(1, TimeSpan.FromMilliseconds(200));

        Assert.True(limiter.TryAcquire(out _));
        Assert.False(limiter.TryAcquire(out _));

        Thread.Sleep(TimeSpan.FromMilliseconds(400));

        Assert.True(limiter.TryAcquire(out _), "the window should have replenished");
    }

    [Fact]
    public static void DefaultLimit_LeavesHeadroomForBatchProvisioning()
    {
        // Enrolling a batch of managed installations must not trip the limiter.
        Assert.True(MintRateLimiter.DefaultPermitLimit >= 60);
    }

    [Fact]
    public static void TryAcquire_IsSafeFromMultipleThreads()
    {
        using var limiter = new MintRateLimiter(50, TimeSpan.FromMinutes(1));
        var permitted = 0;

        Parallel.For(0, 200, index =>
        {
            if (limiter.TryAcquire(out _))
            {
                Interlocked.Increment(ref permitted);
            }
        });

        Assert.Equal(50, permitted);
    }
}
