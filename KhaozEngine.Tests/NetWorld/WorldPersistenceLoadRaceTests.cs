using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.NetWorld;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Locks the load-on-join data-loss race on <see cref="WorldPersistence"/>: an async load-on-join must guard the
/// account so a periodic dirty pass or a quick leave landing mid-load can never overwrite the stored record (position
/// AND the durable game blob) with pre-restore default-spawn state. Also covers the null-after-bytes erase contract
/// and store-fault surfacing. Drives the exact in-flight window with <see cref="GatedWorldStore"/>, which a
/// synchronous <see cref="InMemoryWorldStore"/> can't expose (which is why the original four game-state tests missed
/// it). The companion real-<c>WorldServer</c> test lives in <c>WorldPersistenceTests</c>.
/// </summary>
public class WorldPersistenceLoadRaceTests
{
    // Minimal in-process IWorldPersistenceHost: raise join/leave synchronously and observe placement, no transport.
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

        public void SetPlayerState(int slot, in PlayerMoveState state, bool teleport = false)
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

    // A game's live/applied per-player blob model (capture reads Live by slot; apply records Applied by account).
    private sealed class Game
    {
        public readonly Dictionary<int, byte[]> Live = new();
        public readonly Dictionary<string, byte[]> Applied = new();

        public WorldPersistenceConfig Config(float saveInterval = 30f) => new()
        {
            SaveIntervalSeconds = saveInterval,
            CaptureGameState = (in PlayerPersistenceContext ctx) => Live.TryGetValue(ctx.Slot, out byte[]? b) ? b : null,
            ApplyGameState = (in PlayerPersistenceContext ctx, ReadOnlySpan<byte> blob) => Applied[ctx.AccountId] = blob.ToArray(),
        };
    }

    // A store whose saves always fault (a store outage), loads return "new player". Exercises the pending-prune path.
    private sealed class FaultingWorldStore : IWorldStore
    {
        public Task<byte[]?> LoadAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<byte[]?>(null);
        public Task SaveAsync(string key, byte[] data, CancellationToken cancellationToken = default) =>
            Task.FromException(new System.IO.IOException("store offline"));
        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    // A store that faults every SaveAsync while FailSaves is set, and otherwise passes through to a real inner
    // store. Mirrors CellPersistenceTests.ToggleFaultStore; unlike FaultingWorldStore (which always faults) this
    // one can "recover" mid-test, letting a test prove a previously-failed batch gets retried and lands. Does NOT
    // override SaveManyAsync, so it also exercises IWorldStore's default loop-of-SaveAsync implementation.
    private sealed class ToggleFaultWorldStore : IWorldStore
    {
        private readonly IWorldStore inner;
        public bool FailSaves;
        public ToggleFaultWorldStore(IWorldStore inner) => this.inner = inner;
        public Task<byte[]?> LoadAsync(string key, CancellationToken ct = default) => inner.LoadAsync(key, ct);
        public Task SaveAsync(string key, byte[] data, CancellationToken ct = default) =>
            FailSaves ? Task.FromException(new System.IO.IOException("store offline")) : inner.SaveAsync(key, data, ct);
        public Task<bool> DeleteAsync(string key, CancellationToken ct = default) => inner.DeleteAsync(key, ct);
        public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => inner.ExistsAsync(key, ct);
    }

    [Fact]
    public async Task PeriodicSave_DuringInFlightLoad_DoesNotWrite()
    {
        var inner = new InMemoryWorldStore();
        await inner.SaveAsync("player:hero",
            PlayerRecord.From(new PlayerMoveState { Position = new Vector3(33f, 0f, 44f) }, Encoding.UTF8.GetBytes("loot")).Encode());
        var store = new GatedWorldStore(inner);
        var host = new FakeHost();
        var persistence = new WorldPersistence(host, store, new Game().Config());

        host.Join(0, "hero", new PlayerMoveState { Position = Vector3.Zero });   // load-on-join parks at the gate
        Assert.Equal(1, store.PendingLoads);

        persistence.SaveDirtyPass();                                             // periodic pass fires mid-load

        Assert.DoesNotContain("player:hero", store.SavedKeys);                   // guard skipped it: stored record untouched

        store.ReleaseLoads();                                                    // let the load settle so nothing dangles
        await persistence.FlushAsync();
    }

    [Fact]
    public async Task JoinThenInstantLeave_DuringInFlightLoad_KeepsStoredBlob()
    {
        var inner = new InMemoryWorldStore();
        byte[] stored = PlayerRecord.From(new PlayerMoveState { Position = new Vector3(7f, 0f, 8f) }, Encoding.UTF8.GetBytes("xp=999")).Encode();
        await inner.SaveAsync("player:hero", stored);
        var store = new GatedWorldStore(inner);
        var game = new Game();
        var host = new FakeHost();
        var persistence = new WorldPersistence(host, store, game.Config());

        host.Join(0, "hero", new PlayerMoveState { Position = Vector3.Zero });   // load parks
        host.Leave(0);                                                           // leaves before the load applies

        Assert.DoesNotContain("player:hero", store.SavedKeys);                   // no clobbering save-on-leave
        Assert.Equal(stored, await inner.LoadAsync("player:hero"));              // stored record byte-identical to the pre-seed

        // and the blob survives to the next rejoin
        store.ReleaseLoads();
        await persistence.FlushAsync();                                         // post-leave load settles, guard clears
        host.Join(1, "hero", new PlayerMoveState { Position = Vector3.Zero });   // rejoin on a fresh slot
        store.ReleaseLoads();
        await persistence.FlushAsync();                                         // rejoin load applies

        Assert.True(game.Applied.TryGetValue("hero", out byte[]? blob));
        Assert.Equal(Encoding.UTF8.GetBytes("xp=999"), blob);
        Assert.True(host.TryGetPlayerState(1, out PlayerMoveState got));
        Assert.Equal(new Vector3(7f, 0f, 8f), got.Position);                     // position restored from the same record
    }

    [Fact]
    public async Task LoadReturningNull_ClearsGuard_SoLaterSavesProceed()
    {
        var inner = new InMemoryWorldStore();                                    // empty: a brand-new player
        var store = new GatedWorldStore(inner);
        var host = new FakeHost();
        var persistence = new WorldPersistence(host, store, new Game().Config());

        host.Join(0, "hero", new PlayerMoveState { Position = new Vector3(5f, 0f, 6f) });
        Assert.Equal(1, store.PendingLoads);

        store.ReleaseLoads();
        await persistence.FlushAsync();                                         // load returns null -> the guard must drop

        persistence.SaveDirtyPass();                                           // a save must now be allowed through
        await persistence.FlushAsync();

        Assert.Contains("player:hero", store.SavedKeys);
        Assert.Equal(new Vector3(5f, 0f, 6f),
            PlayerRecord.Decode((await inner.LoadAsync("player:hero"))!).ToState().Position);
    }

    [Fact]
    public async Task CaptureReturningNullAfterBytes_ErasesStoredBlob()
    {
        var store = new InMemoryWorldStore();                                    // synchronous: guard clears on the null load
        var game = new Game();
        var host = new FakeHost();
        var persistence = new WorldPersistence(host, store, game.Config());

        host.Join(0, "hero", new PlayerMoveState { Position = new Vector3(1f, 0f, 1f) });
        game.Live[0] = Encoding.UTF8.GetBytes("xp=5");
        persistence.SaveDirtyPass();
        await persistence.FlushAsync();
        Assert.Equal(Encoding.UTF8.GetBytes("xp=5"), PlayerRecord.Decode((await store.LoadAsync("player:hero"))!).Game);

        // capture now reports "no game state" (null) after previously storing bytes -> the record is dirty and the blob is ERASED
        game.Live.Remove(0);
        persistence.SaveDirtyPass();
        await persistence.FlushAsync();

        PlayerRecord after = PlayerRecord.Decode((await store.LoadAsync("player:hero"))!);
        Assert.Null(after.Game);                                                // stored blob erased, not preserved
        Assert.Equal(new Vector3(1f, 0f, 1f), after.ToState().Position);        // position still there
    }

    [Fact]
    public void StoreSaveFault_IsSurfacedAndPruned()
    {
        var store = new FaultingWorldStore();
        var host = new FakeHost();
        var persistence = new WorldPersistence(host, store, new WorldPersistenceConfig());
        var errors = new List<Exception>();
        persistence.OnStoreError += errors.Add;

        host.Join(0, "hero", new PlayerMoveState { Position = Vector3.Zero });   // load returns null -> guard clears
        persistence.SaveDirtyPass();                                            // creates a save task that faults immediately
        persistence.Update(0f);                                                 // prunes the faulted task + surfaces it

        Assert.Single(errors);
        Assert.IsType<System.IO.IOException>(errors[0]);
    }

    [Fact]
    public async Task SaveDirtyPass_BatchFault_KeepsEveryRecordDirty_SurfacesOncePerPass_AndRetriesAfterRecovery()
    {
        // Two dirty accounts in one pass: this exercises the batching itself (SaveDirtyPass -> one SaveManyAsync
        // call for the whole pass, not one SaveAsync per account) rather than the single-account case the other
        // fault tests cover. A faulted batch must (a) surface via OnStoreError exactly ONCE for the whole pass, not
        // once per account, and (b) leave BOTH accounts dirty (lastSaved is only advanced after the batch lands),
        // so a later successful pass retries and persists both - never silently drops one.
        var inner = new InMemoryWorldStore();
        var store = new ToggleFaultWorldStore(inner) { FailSaves = true };
        var host = new FakeHost();
        var persistence = new WorldPersistence(host, store, new WorldPersistenceConfig());
        var errors = new List<Exception>();
        persistence.OnStoreError += errors.Add;

        host.Join(0, "hero", new PlayerMoveState { Position = new Vector3(1f, 0f, 1f) });
        host.Join(1, "villain", new PlayerMoveState { Position = new Vector3(2f, 0f, 2f) });   // both loads return null -> guard clears immediately

        persistence.SaveDirtyPass();   // both dirty accounts batched into ONE SaveManyAsync call, which faults
        persistence.Update(0f);        // prunes the faulted batch task, surfacing exactly one error for the pass

        Assert.Single(errors);
        Assert.Null(await inner.LoadAsync("player:hero"));
        Assert.Null(await inner.LoadAsync("player:villain"));   // neither record landed - not even the one whose SaveAsync ran first

        store.FailSaves = false;       // store recovers
        persistence.SaveDirtyPass();   // both accounts are STILL dirty (the failed batch never advanced lastSaved) -> retried together
        persistence.Update(0f);

        Assert.NotNull(await inner.LoadAsync("player:hero"));
        Assert.NotNull(await inner.LoadAsync("player:villain"));
    }
}
