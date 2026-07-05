using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.NetWorld;
using KhaozEngine.Persistence;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Covers the game-owned per-player durable blob seam on <see cref="WorldPersistence"/>: the engine captures the
/// game's opaque bytes on the server thread at each save, rides the SAME dirty-tracking / interval / flush / load
/// machinery as position, and hands the bytes back on the server thread at load-on-join. The engine never
/// interprets the payload; migration is game-side (demonstrated here with <see cref="MigrationChain"/>).
/// </summary>
public class WorldPersistenceGameStateTests
{
    // A minimal in-process IWorldPersistenceHost: the seam WorldServer/ShardedWorldServer expose. Lets a test raise
    // join/leave and observe load-on-join placement without the transport, mirroring the real hosts exactly (join
    // fires post-spawn; leave carries the final state; SetPlayerState overrides the authoritative state).
    private sealed class FakeHost : IWorldPersistenceHost
    {
        private readonly Dictionary<int, (string acct, PlayerMoveState state)> players = new();
        public event Action<int, string>? PlayerJoined;
        public event Action<int, string, PlayerMoveState>? PlayerLeaving;

        public void Join(int slot, string accountId, PlayerMoveState spawn)
        {
            players[slot] = (accountId, spawn);
            PlayerJoined?.Invoke(slot, accountId);
        }

        public void Leave(int slot)
        {
            if (!players.TryGetValue(slot, out (string acct, PlayerMoveState state) p)) return;
            PlayerLeaving?.Invoke(slot, p.acct, p.state);
            players.Remove(slot);
        }

        public void Move(int slot, Vector3 position) => SetPlayerState(slot, new PlayerMoveState { Position = position });

        public void SetPlayerState(int slot, in PlayerMoveState state)
        {
            if (players.TryGetValue(slot, out (string acct, PlayerMoveState state) p)) players[slot] = (p.acct, state);
        }

        public IReadOnlyCollection<int> JoinedSlots => players.Keys;

        public bool TryGetAccountId(int slot, out string accountId)
        {
            if (players.TryGetValue(slot, out (string acct, PlayerMoveState state) p)) { accountId = p.acct; return true; }
            accountId = string.Empty;
            return false;
        }

        public bool TryGetPlayerState(int slot, out PlayerMoveState state)
        {
            if (players.TryGetValue(slot, out (string acct, PlayerMoveState state) p)) { state = p.state; return true; }
            state = default;
            return false;
        }
    }

    // Counts SaveAsync calls so a test can assert a save happened (or didn't) for a given change.
    private sealed class CountingWorldStore : IWorldStore
    {
        private readonly IWorldStore inner;
        public int Saves;
        public CountingWorldStore(IWorldStore inner) => this.inner = inner;
        public Task<byte[]?> LoadAsync(string key, CancellationToken ct = default) => inner.LoadAsync(key, ct);
        public Task SaveAsync(string key, byte[] data, CancellationToken ct = default) { Saves++; return inner.SaveAsync(key, data, ct); }
        public Task<bool> DeleteAsync(string key, CancellationToken ct = default) => inner.DeleteAsync(key, ct);
        public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => inner.ExistsAsync(key, ct);
    }

    // A game's per-player durable-state model: what the game holds live per online slot (capture reads it) and what
    // it re-applies per account on load-on-join.
    private sealed class GameStateModel
    {
        public readonly Dictionary<int, byte[]> LiveBySlot = new();
        public readonly Dictionary<string, byte[]> AppliedByAccount = new();

        public WorldPersistenceConfig Config(float saveInterval = 30f) => new()
        {
            SaveIntervalSeconds = saveInterval,
            CaptureGameState = (in PlayerPersistenceContext ctx) =>
                LiveBySlot.TryGetValue(ctx.Slot, out byte[]? b) ? b : null,
            ApplyGameState = (in PlayerPersistenceContext ctx, ReadOnlySpan<byte> blob) =>
                AppliedByAccount[ctx.AccountId] = blob.ToArray(),
        };
    }

    [Fact]
    public async Task GameBlob_RoundTrips_JoinDirtySaveLeaveRejoin()
    {
        IWorldStore store = new InMemoryWorldStore();
        var game = new GameStateModel();
        var host = new FakeHost();
        var persistence = new WorldPersistence(host, store, game.Config());

        // Join, attach live game state, then leave -> save-on-leave captures the blob alongside position.
        host.Join(0, "hero", new PlayerMoveState { Position = new Vector3(7f, 0f, 8f) });
        byte[] blob = Encoding.UTF8.GetBytes("xp=100;quest=3");
        game.LiveBySlot[0] = blob;
        host.Leave(0);
        await persistence.FlushAsync();

        // Rejoin the same account on a fresh slot: load-on-join hands the blob back on the server thread.
        host.Join(1, "hero", new PlayerMoveState { Position = new Vector3(0f, 0f, 0f) });
        await persistence.FlushAsync();

        Assert.True(game.AppliedByAccount.TryGetValue("hero", out byte[]? applied));
        Assert.Equal(blob, applied);
        Assert.True(host.TryGetPlayerState(1, out PlayerMoveState got));
        Assert.Equal(new Vector3(7f, 0f, 8f), got.Position);   // position restored too, from the same record
    }

    [Fact]
    public async Task GameBlob_SurvivesRestart_ViaEnumerableInMemoryStore()
    {
        IWorldStore store = new InMemoryWorldStore();   // the store survives; the server restarts around it

        // First server session: join, hold game state, leave -> save.
        {
            var game = new GameStateModel();
            var host = new FakeHost();
            var persistence = new WorldPersistence(host, store, game.Config());
            host.Join(0, "hero", new PlayerMoveState { Position = new Vector3(3f, 0f, 4f) });
            game.LiveBySlot[0] = Encoding.UTF8.GetBytes("gold=500");
            host.Leave(0);
            await persistence.FlushAsync();
        }

        // Brand-new host + persistence on the SAME store (a "restart"): the blob is restored on join.
        var game2 = new GameStateModel();
        var host2 = new FakeHost();
        var persistence2 = new WorldPersistence(host2, store, game2.Config());
        host2.Join(0, "hero", new PlayerMoveState { Position = new Vector3(0f, 0f, 0f) });
        await persistence2.FlushAsync();

        Assert.True(game2.AppliedByAccount.TryGetValue("hero", out byte[]? applied));
        Assert.Equal(Encoding.UTF8.GetBytes("gold=500"), applied);
    }

    private sealed class GameSave
    {
        public int Version { get; set; } = 2;
        public int Xp { get; set; }
        public int Level { get; set; }
    }

    [Fact]
    public async Task SchemaBump_MigratesOldBlob_GameSide()
    {
        // The game evolves its player schema from v1 (Xp only) to v2 (adds a derived Level) via a MigrationChain.
        MigrationChain<GameSave> chain = MigrationChain
            .For<GameSave>(g => g.Version, (g, v) => g.Version = v)
            .Step(1, g => { g.Level = 1 + g.Xp / 100; return g; })
            .Build(2);

        // Pre-seed the store with an OLD (v1) game blob wrapped in a normal player record.
        IWorldStore store = new InMemoryWorldStore();
        byte[] oldBlob = JsonSerializer.SerializeToUtf8Bytes(new GameSave { Version = 1, Xp = 250, Level = 0 });
        await store.SaveAsync("player:hero",
            PlayerRecord.From(new PlayerMoveState { Position = new Vector3(1f, 0f, 2f) }, oldBlob).Encode());

        var migrated = new Dictionary<string, GameSave>();
        var config = new WorldPersistenceConfig
        {
            ApplyGameState = (in PlayerPersistenceContext ctx, ReadOnlySpan<byte> blob) =>
            {
                GameSave dto = JsonSerializer.Deserialize<GameSave>(blob) ?? new GameSave();
                migrated[ctx.AccountId] = chain.Migrate(dto);   // game-side migration inside the apply hook
            },
        };

        var host = new FakeHost();
        var persistence = new WorldPersistence(host, store, config);
        host.Join(0, "hero", new PlayerMoveState { Position = new Vector3(0f, 0f, 0f) });
        await persistence.FlushAsync();

        Assert.True(migrated.TryGetValue("hero", out GameSave? save));
        Assert.Equal(2, save!.Version);
        Assert.Equal(250, save.Xp);
        Assert.Equal(3, save.Level);       // 1 + 250/100 = 3, filled by the v1->v2 step
    }

    [Fact]
    public async Task PositionAndGameBlob_SaveIndependently()
    {
        var counting = new CountingWorldStore(new InMemoryWorldStore());
        var game = new GameStateModel();
        var host = new FakeHost();
        var persistence = new WorldPersistence(host, counting, game.Config());
        host.Join(0, "hero", new PlayerMoveState { Position = new Vector3(1f, 0f, 1f) });

        // First dirty pass: no prior baseline -> one save (position, no blob yet).
        persistence.SaveDirtyPass();
        await persistence.FlushAsync();
        Assert.Equal(1, counting.Saves);

        // Nothing changed -> no save.
        persistence.SaveDirtyPass();
        await persistence.FlushAsync();
        Assert.Equal(1, counting.Saves);

        // Change ONLY the game blob (position untouched) -> the record is dirty and re-saved.
        game.LiveBySlot[0] = Encoding.UTF8.GetBytes("xp=1");
        persistence.SaveDirtyPass();
        await persistence.FlushAsync();
        Assert.Equal(2, counting.Saves);
        PlayerRecord afterBlob = PlayerRecord.Decode((await counting.LoadAsync("player:hero"))!);
        Assert.Equal(new Vector3(1f, 0f, 1f), afterBlob.ToState().Position);   // position preserved
        Assert.Equal(Encoding.UTF8.GetBytes("xp=1"), afterBlob.Game);

        // Change ONLY the position (blob untouched) -> dirty and re-saved, blob preserved.
        host.Move(0, new Vector3(9f, 0f, 9f));
        persistence.SaveDirtyPass();
        await persistence.FlushAsync();
        Assert.Equal(3, counting.Saves);
        PlayerRecord afterMove = PlayerRecord.Decode((await counting.LoadAsync("player:hero"))!);
        Assert.Equal(new Vector3(9f, 0f, 9f), afterMove.ToState().Position);
        Assert.Equal(Encoding.UTF8.GetBytes("xp=1"), afterMove.Game);          // blob preserved

        // Nothing changed again -> no save.
        persistence.SaveDirtyPass();
        await persistence.FlushAsync();
        Assert.Equal(3, counting.Saves);
    }
}
