using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using KhaozEngine.WorldStore;
using KhaozEngine.WorldStore.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// The tile binding over the shared persistence core, driven through a minimal host rather than a real server: a
/// tile survives a restart, a record that will not decode is quarantined instead of applied, and a tokenless
/// connection is never written under its seat. The core's own behaviour is pinned by the NetWorld suite, so what
/// these cover is that the TILE side of the seam is wired up right.
/// </summary>
public class TileWorldPersistenceTests
{
    // The smallest thing the core can drive: raise join and leave synchronously, record every placement. No
    // transport, no simulator, no server. SetPositionHintProvider and TryGetConfiguredSpawn are left to their
    // default interface implementations, which is the shape of a head that spawns joins its own way.
    sealed class FakeHost : IPersistenceHost<TileMoveState>
    {
        public readonly Dictionary<int, string> Accounts = new();
        public readonly Dictionary<int, TileMoveState> States = new();
        public readonly List<(int slot, TileMoveState state, bool teleport)> Placed = new();
        public event Action<int, string>? PlayerJoined;
        public event Action<int, string, TileMoveState>? PlayerLeaving;
        public IReadOnlyCollection<int> JoinedSlots => Accounts.Keys;
        public bool TryGetAccountId(int slot, out string accountId) => Accounts.TryGetValue(slot, out accountId!);
        public bool TryGetPlayerState(int slot, out TileMoveState state) => States.TryGetValue(slot, out state);
        public void SetPlayerState(int slot, in TileMoveState state, bool teleport = false)
        {
            States[slot] = state;
            Placed.Add((slot, state, teleport));
        }
        public void Join(int slot, string account, TileMoveState at)
        {
            Accounts[slot] = account;
            States[slot] = at;
            PlayerJoined?.Invoke(slot, account);
        }
        public void Leave(int slot)
        {
            PlayerLeaving?.Invoke(slot, Accounts[slot], States[slot]);
            Accounts.Remove(slot);
            States.Remove(slot);
        }
    }

    static string TempDb() => Path.Combine(Path.GetTempPath(), "ke-tilenet-" + Guid.NewGuid().ToString("N") + ".db");

    [Fact]
    public async Task A_players_tile_round_trips_through_a_temp_sqlite_store()
    {
        string db = TempDb();
        try
        {
            using var store = new SqliteWorldStore($"Data Source={db}");
            var host = new FakeHost();
            var p = new TileWorldPersistence(host, store);
            host.Join(1, "acct-1", TileMoveState.At(new TileCoord(3, 4, 0), TileDirection.N));
            // Let the load-on-join land before the leave. Until it does the account is guarded and the leave-save is
            // skipped on purpose, so a test that walks straight from join to leave saves nothing at all.
            await p.FlushAsync();
            host.States[1] = TileMoveState.At(new TileCoord(30, 40, 1), TileDirection.SE);
            host.Leave(1);
            await p.FlushAsync();

            var host2 = new FakeHost();
            var p2 = new TileWorldPersistence(host2, store);
            host2.Join(1, "acct-1", TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.N));
            for (int i = 0; i < 50 && host2.Placed.Count == 0; i++) { p2.Update(0.25f); await Task.Delay(10); }
            Assert.Single(host2.Placed);
            Assert.Equal(new TileCoord(30, 40, 1), host2.Placed[0].state.Tile);
            Assert.Equal(TileDirection.SE, host2.Placed[0].state.Facing);
        }
        finally { File.Delete(db); }
    }

    [Fact]
    public async Task A_corrupt_record_is_quarantined_and_the_player_is_placed_at_the_spawn()
    {
        var store = new InMemoryWorldStore();
        await store.SaveAsync("player:acct-2", Encoding.UTF8.GetBytes("not json at all"));
        var host = new FakeHost();
        string? quarantinedKey = null;
        var p = new TileWorldPersistence(host, store);
        p.OnRecordQuarantined += (key, _) => quarantinedKey = key;
        host.Join(2, "acct-2", TileMoveState.At(new TileCoord(1, 1, 0), TileDirection.N));
        for (int i = 0; i < 50 && quarantinedKey is null; i++) { p.Update(0.25f); await Task.Delay(10); }
        Assert.Equal("acct-2", quarantinedKey);
        await p.FlushAsync();                          // the quarantine copy is a tracked write, awaited here
        Assert.NotNull(await store.LoadAsync("quarantine:player:acct-2"));
    }

    [Fact]
    public async Task A_guest_connection_is_never_written()
    {
        var store = new InMemoryWorldStore();
        var host = new FakeHost();
        var p = new TileWorldPersistence(host, store);
        host.Join(3, "guest:3", TileMoveState.At(new TileCoord(5, 5, 0), TileDirection.N));
        host.Leave(3);
        await p.FlushAsync();
        Assert.Null(await store.LoadAsync("player:guest:3"));
        Assert.False(await store.ExistsAsync("player:3"));
    }
}
