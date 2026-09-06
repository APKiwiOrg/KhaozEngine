using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

/// <summary>
/// The per-connection message <see cref="RateLimiter"/>: a deterministic token bucket advanced one step at a time
/// (the server refills it from elapsed simulation time), with no wall-clock dependency so it is headless-testable. A message
/// consumes a token; an empty bucket rejects (the server drops the message and signals suspicious activity).
/// </summary>
public class RateLimiterTests
{
    [Fact]
    public void Starts_full_and_allows_up_to_capacity_consumes()
    {
        var rl = new RateLimiter(capacity: 3, refillPerStep: 0);
        Assert.True(rl.TryConsume());
        Assert.True(rl.TryConsume());
        Assert.True(rl.TryConsume());
        Assert.False(rl.TryConsume());   // bucket drained, no refill
    }

    [Fact]
    public void Refill_restores_budget()
    {
        var rl = new RateLimiter(capacity: 2, refillPerStep: 1);
        Assert.True(rl.TryConsume());
        Assert.True(rl.TryConsume());
        Assert.False(rl.TryConsume());   // drained
        rl.Refill();                     // +1
        Assert.True(rl.TryConsume());
        Assert.False(rl.TryConsume());
    }

    [Fact]
    public void Refill_never_exceeds_capacity()
    {
        var rl = new RateLimiter(capacity: 2, refillPerStep: 5);
        rl.Refill();                     // already full; must clamp at capacity
        rl.Refill();
        Assert.True(rl.TryConsume());
        Assert.True(rl.TryConsume());
        Assert.False(rl.TryConsume());   // never accumulated a burst beyond capacity
    }

    [Fact]
    public void Fractional_refill_accumulates_across_steps()
    {
        var rl = new RateLimiter(capacity: 1, refillPerStep: 0.5);
        Assert.True(rl.TryConsume());    // spend the initial token
        Assert.False(rl.TryConsume());   // empty
        rl.Refill();                     // 0.5 - still less than a whole token
        Assert.False(rl.TryConsume());
        rl.Refill();                     // 1.0
        Assert.True(rl.TryConsume());    // a whole token is now available
    }

    [Fact]
    public void Scaled_refill_tracks_elapsed_steps_and_ignores_invalid_time()
    {
        var rl = new RateLimiter(capacity: 2, refillPerStep: 0.5);
        Assert.True(rl.TryConsume());
        Assert.True(rl.TryConsume());

        rl.Refill(1.5);
        rl.Refill(0);
        rl.Refill(-1);
        rl.Refill(double.NaN);
        rl.Refill(double.PositiveInfinity);

        Assert.Equal(0.75, rl.Tokens);
        Assert.False(rl.TryConsume());
        rl.Refill(0.5);
        Assert.True(rl.TryConsume());
    }
}
