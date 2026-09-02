using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The resume hints are memory-only, so the FIRST rejoin of every account after a process restart falls back to
/// the configured spawn and takes the restore teleport that #642 shipped to remove. A deploy or a container
/// recycle is routine, so that cost lands on every returning player.
///
/// <para>The engine's own docs named the fix ("a game that wants the cross-restart case covered can pre-warm this
/// from its own store at boot") and shipped nothing, which left every consumer re-deriving four engine-owned
/// invariants: the configurable key prefix, the record format, the guest exclusion, and the capacity bound with
/// its ordering. The fifth is the one worth the tests below: a record the LOAD path would quarantine must not
/// become a hint, because the join seed builds the player ON the hint and nothing validates a hint. A naive
/// pre-warm seeds precisely the positions the engine takes care to reject, and does it silently (#671).</para>
/// </summary>
public class WorldPersistencePrewarmTests
{
    private static float Flat(float x, float z) => 0f;

    private static WorldServer NewServer()
    {
        (LoopbackTransport st, _) = LoopbackTransport.CreatePair();
        return new WorldServer(st, new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 8 }, Flat,
            MoveTuning.Default);
    }

    // A store whose clock the test drives, so UpdatedAt ordering is stated rather than raced.
    private sealed class Clock
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Now() => now;
        public void Advance() => now = now.AddMinutes(1);
    }

    private static async Task Seed(IWorldStore store, string key, Vector3 position, Clock? clock = null)
    {
        await store.SaveAsync(key, PlayerRecord.From(new PlayerMoveState { Position = position }).Encode());
        clock?.Advance();
    }

    [Fact]
    public async Task Seeds_every_stored_record_and_returns_the_count()
    {
        var store = new InMemoryWorldStore();
        await Seed(store, "player:a", new Vector3(10f, 1f, 20f));
        await Seed(store, "player:b", new Vector3(-30f, 1f, 40f));

        var persistence = new WorldPersistence(NewServer(), store);
        int seeded = await persistence.PrewarmResumeHintsAsync();

        Assert.Equal(2, seeded);
        Assert.True(persistence.ResumeHints.TryGet("a", out Vector3 a));
        Assert.Equal(new Vector3(10f, 1f, 20f), a);
        Assert.True(persistence.ResumeHints.TryGet("b", out Vector3 b));
        Assert.Equal(new Vector3(-30f, 1f, 40f), b);
    }

    [Fact]
    public async Task Keeps_the_newest_when_there_are_more_records_than_capacity()
    {
        var clock = new Clock();
        var store = new InMemoryWorldStore(clock.Now);
        await Seed(store, "player:oldest", new Vector3(1f, 0f, 1f), clock);
        await Seed(store, "player:middle", new Vector3(2f, 0f, 2f), clock);
        await Seed(store, "player:newest", new Vector3(3f, 0f, 3f), clock);

        var persistence = new WorldPersistence(NewServer(), store,
            new WorldPersistenceConfig { ResumeHintCapacity = 2 });
        int seeded = await persistence.PrewarmResumeHintsAsync();

        Assert.Equal(2, seeded);
        Assert.Equal(2, persistence.ResumeHints.Count);
        Assert.True(persistence.ResumeHints.TryGet("newest", out _));
        Assert.True(persistence.ResumeHints.TryGet("middle", out _));
        Assert.False(persistence.ResumeHints.TryGet("oldest", out _));
    }

    [Fact]
    public async Task The_oldest_seeded_account_is_the_first_the_cache_evicts()
    {
        // Recency order matters as much as the cut: the cache evicts least-recently-recorded, so a pre-warm that
        // records newest-first would make the newest account the first casualty of the next live save.
        var clock = new Clock();
        var store = new InMemoryWorldStore(clock.Now);
        await Seed(store, "player:older", new Vector3(1f, 0f, 1f), clock);
        await Seed(store, "player:newer", new Vector3(2f, 0f, 2f), clock);

        var persistence = new WorldPersistence(NewServer(), store,
            new WorldPersistenceConfig { ResumeHintCapacity = 2 });
        await persistence.PrewarmResumeHintsAsync();
        persistence.ResumeHints.Record("live", new Vector3(9f, 0f, 9f));   // one more than capacity

        Assert.False(persistence.ResumeHints.TryGet("older", out _));
        Assert.True(persistence.ResumeHints.TryGet("newer", out _));
        Assert.True(persistence.ResumeHints.TryGet("live", out _));
    }

    [Fact]
    public async Task The_max_argument_caps_it_below_the_capacity()
    {
        var clock = new Clock();
        var store = new InMemoryWorldStore(clock.Now);
        await Seed(store, "player:a", new Vector3(1f, 0f, 1f), clock);
        await Seed(store, "player:b", new Vector3(2f, 0f, 2f), clock);
        await Seed(store, "player:c", new Vector3(3f, 0f, 3f), clock);

        var persistence = new WorldPersistence(NewServer(), store);
        Assert.Equal(1, await persistence.PrewarmResumeHintsAsync(max: 1));
        Assert.Equal(1, persistence.ResumeHints.Count);
        Assert.True(persistence.ResumeHints.TryGet("c", out _));
    }

    [Fact]
    public async Task A_guest_record_is_never_seeded()
    {
        var store = new InMemoryWorldStore();
        await Seed(store, "player:" + ResumePositionCache.GuestAccountPrefix + "0", new Vector3(5f, 0f, 5f));
        await Seed(store, "player:real", new Vector3(6f, 0f, 6f));

        var persistence = new WorldPersistence(NewServer(), store);
        int seeded = await persistence.PrewarmResumeHintsAsync();

        Assert.Equal(1, seeded);
        Assert.True(persistence.ResumeHints.TryGet("real", out _));
        Assert.Equal(1, persistence.ResumeHints.Count);
    }

    [Fact]
    public async Task A_record_the_load_path_would_quarantine_is_not_seeded()
    {
        // The whole reason this belongs in the engine. Bounds vets the LOADED record and never the hint, and the
        // join builds the player ON the hint, so seeding an out-of-bounds position puts a player exactly where the
        // quarantine path exists to stop them standing.
        var store = new InMemoryWorldStore();
        await Seed(store, "player:inside", new Vector3(1f, 0f, 1f));
        await Seed(store, "player:outside", new Vector3(900f, 0f, 900f));

        var persistence = new WorldPersistence(NewServer(), store, new WorldPersistenceConfig
        {
            Bounds = new CircleBounds(Vector2.Zero, 100f),
        });
        int seeded = await persistence.PrewarmResumeHintsAsync();

        Assert.Equal(1, seeded);
        Assert.True(persistence.ResumeHints.TryGet("inside", out _));
        Assert.False(persistence.ResumeHints.TryGet("outside", out _));
    }

    [Fact]
    public async Task An_undecodable_record_is_skipped_rather_than_thrown()
    {
        var store = new InMemoryWorldStore();
        await store.SaveAsync("player:broken", new byte[] { 0xFF, 0x00, 0xFF });
        await Seed(store, "player:fine", new Vector3(2f, 0f, 2f));

        var persistence = new WorldPersistence(NewServer(), store);
        int seeded = await persistence.PrewarmResumeHintsAsync();

        Assert.Equal(1, seeded);
        Assert.True(persistence.ResumeHints.TryGet("fine", out _));
        Assert.False(persistence.ResumeHints.TryGet("broken", out _));
    }

    [Fact]
    public async Task A_quarantine_copy_is_not_mistaken_for_a_record()
    {
        // The quarantine key is {QuarantineKeyPrefix}{KeyPrefix}{accountId}, which the default prefix filter already
        // excludes. An empty KeyPrefix does not, and re-seeding a rejected position from its own quarantine copy is
        // the worst possible way to fail.
        var store = new InMemoryWorldStore();
        await Seed(store, "quarantine:bad", new Vector3(900f, 0f, 900f));
        await Seed(store, "good", new Vector3(3f, 0f, 3f));

        var persistence = new WorldPersistence(NewServer(), store,
            new WorldPersistenceConfig { KeyPrefix = string.Empty });
        int seeded = await persistence.PrewarmResumeHintsAsync();

        Assert.Equal(1, seeded);
        Assert.True(persistence.ResumeHints.TryGet("good", out _));
        Assert.False(persistence.ResumeHints.TryGet("quarantine:bad", out _));
    }

    [Fact]
    public async Task A_store_that_cannot_enumerate_seeds_nothing()
    {
        var store = new OpaqueStore();
        await Seed(store, "player:a", new Vector3(1f, 0f, 1f));

        var persistence = new WorldPersistence(NewServer(), store);

        Assert.Equal(0, await persistence.PrewarmResumeHintsAsync());
        Assert.Equal(0, persistence.ResumeHints.Count);
    }

    [Fact]
    public async Task A_capacity_of_zero_seeds_nothing()
    {
        var store = new InMemoryWorldStore();
        await Seed(store, "player:a", new Vector3(1f, 0f, 1f));

        var persistence = new WorldPersistence(NewServer(), store,
            new WorldPersistenceConfig { ResumeHintCapacity = 0 });

        Assert.Equal(0, await persistence.PrewarmResumeHintsAsync());
    }

    /// <summary>An <see cref="IWorldStore"/> that does NOT implement <see cref="IEnumerableWorldStore"/>, which is
    /// the shape the pre-warm has to no-op on rather than throw.</summary>
    private sealed class OpaqueStore : IWorldStore
    {
        private readonly Dictionary<string, byte[]> rows = new(StringComparer.Ordinal);

        public Task<byte[]?> LoadAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(rows.TryGetValue(key, out byte[]? v) ? v : null);

        public Task SaveAsync(string key, byte[] data, CancellationToken cancellationToken = default)
        {
            rows[key] = data;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(rows.Remove(key));

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(rows.ContainsKey(key));
    }
}
