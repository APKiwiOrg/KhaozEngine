using System.Numerics;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The bound on the resume-position hints (#642). The cache is what lets a rejoining player's entity be built where
/// they left, and it lives for the life of the server process, so the thing worth pinning is that it cannot grow
/// with the number of accounts a long-running server has ever seen.
/// </summary>
public class ResumePositionCacheTests
{
    [Fact]
    public void It_holds_what_was_recorded()
    {
        var cache = new ResumePositionCache();
        cache.Record("hero", new Vector3(400f, 0.9f, 300f));

        Assert.True(cache.TryGet("hero", out Vector3 got));
        Assert.Equal(new Vector3(400f, 0.9f, 300f), got);
        Assert.False(cache.TryGet("nobody", out _));
        Assert.False(cache.TryGet("", out _));
    }

    [Fact]
    public void A_second_record_for_an_account_replaces_the_first()
    {
        var cache = new ResumePositionCache();
        cache.Record("hero", new Vector3(1f, 0f, 2f));
        cache.Record("hero", new Vector3(3f, 0f, 4f));

        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet("hero", out Vector3 got));
        Assert.Equal(new Vector3(3f, 0f, 4f), got);
    }

    [Fact]
    public void It_evicts_the_least_recently_recorded_account_at_capacity()
    {
        var cache = new ResumePositionCache(capacity: 3);
        cache.Record("a", Vector3.One);
        cache.Record("b", Vector3.One);
        cache.Record("c", Vector3.One);
        cache.Record("a", new Vector3(9f, 0f, 9f));   // refreshing "a" moves it off the eviction end
        cache.Record("d", Vector3.One);               // over capacity: "b" is now the oldest

        Assert.Equal(3, cache.Count);
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("a", out Vector3 a));
        Assert.Equal(new Vector3(9f, 0f, 9f), a);
        Assert.True(cache.TryGet("c", out _));
        Assert.True(cache.TryGet("d", out _));
    }

    [Fact]
    public void A_capacity_of_zero_holds_nothing()
    {
        // The documented opt-out: a game that wants every join on the configured spawn sets ResumeHintCapacity to 0.
        var cache = new ResumePositionCache(capacity: 0);
        cache.Record("hero", Vector3.One);

        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet("hero", out _));
    }

    [Fact]
    public void A_guest_key_is_refused()
    {
        // guest:{slot} is what both heads key a tokenless connection under, and slots are recycled to the next
        // connection, so the key names a seat rather than a player. A hint held under it would build a brand-new
        // guest on the last occupant's position, silently (no restore, so no teleport signal either).
        var cache = new ResumePositionCache();
        cache.Record(ResumePositionCache.GuestAccountPrefix + "0", new Vector3(400f, 0.9f, 300f));

        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet("guest:0", out _));

        // It is the PREFIX that is refused, not the word: an account genuinely called "guest" is an account.
        cache.Record("guest", Vector3.One);
        Assert.True(cache.TryGet("guest", out _));
    }

    [Fact]
    public void Forget_and_Clear_drop_hints()
    {
        var cache = new ResumePositionCache(capacity: 4);
        cache.Record("a", Vector3.One);
        cache.Record("b", Vector3.One);

        Assert.True(cache.Forget("a"));
        Assert.False(cache.Forget("a"));
        Assert.False(cache.TryGet("a", out _));
        Assert.Equal(1, cache.Count);

        cache.Record("c", Vector3.One);   // the forgotten entry left no hole in the recency list
        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet("b", out _));
    }
}
