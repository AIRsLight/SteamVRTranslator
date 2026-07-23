using SteamVRTranslator.App.Translation;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class ProviderConcurrencyLimiterTests
{
    [Fact]
    public async Task EachProviderUsesItsOwnConcurrencyLimit()
    {
        var limiter = new ProviderConcurrencyLimiter();
        using var a1 = await limiter.AcquireAsync("provider-a", 2, CancellationToken.None);
        using var a2 = await limiter.AcquireAsync("provider-a", 2, CancellationToken.None);
        var queuedA = limiter.AcquireAsync("provider-a", 2, CancellationToken.None);

        using var b1 = await limiter.AcquireAsync("provider-b", 1, CancellationToken.None);

        Assert.False(queuedA.IsCompleted);
        a1.Dispose();
        using var a3 = await queuedA.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task RaisingProviderLimitReleasesItsQueuedRequest()
    {
        var limiter = new ProviderConcurrencyLimiter();
        using var first = await limiter.AcquireAsync("provider-a", 1, CancellationToken.None);
        var queued = limiter.AcquireAsync("provider-a", 1, CancellationToken.None);

        limiter.UpdateLimits(
        [
            new Configuration.TranslationProviderConfiguration
            {
                Id = "provider-a",
                MaxConcurrency = 2
            }
        ]);

        using var second = await queued.WaitAsync(TimeSpan.FromSeconds(1));
    }
}
