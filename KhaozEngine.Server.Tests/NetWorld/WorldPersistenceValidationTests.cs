using System;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Locks the server-side validate-and-quarantine seam on <see cref="WorldPersistence"/>: a loaded player record that
/// fails validation (out-of-bounds position, a rejecting game-blob verdict, or undecodable JSON) is quarantined WHOLE
/// under <c>quarantine:player:{accountId}</c>, the player spawns fresh, <see cref="WorldPersistence.OnRecordQuarantined"/>
/// fires on the server thread, and the <c>lastSaved</c> baseline never moves to the bad record so the fresh spawn
/// overwrites the primary on the next dirty pass while the quarantine copy survives. A faulted store READ keeps the
/// old outage semantics (guard stays set, no quarantine). Drives the real <c>WorldServer</c> through the same
/// <see cref="Harness"/> pattern as <c>WorldPersistenceTests</c>.
/// </summary>
public class WorldPersistenceValidationTests
{
    private static float FlatGround(float x, float z) => 0f;

    private sealed class Harness : IDisposable
    {
        public readonly WorldServer Server;
        public readonly WorldPersistence Persistence;
        private readonly LoopbackTransport serverTransport;
        private readonly LoopbackTransport clientTransport;
        private readonly NetClient client;
        private readonly WorldServerConfig config;

        public Harness(IWorldStore store, byte[] token, WorldPersistenceConfig? pcfg = null)
        {
            (serverTransport, clientTransport) = LoopbackTransport.CreatePair();
            config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
            Server = new WorldServer(serverTransport, config, FlatGround, MoveTuning.Default);
            Persistence = new WorldPersistence(Server, store, pcfg);
            client = new NetClient(clientTransport, TestHandshake.Wire(token));
        }

        // Pump until the predicate is true (or the frame budget runs out), running persistence.Update each frame.
        public void PumpUntil(Func<bool> done, int frames = 300)
        {
            for (int i = 0; i < frames && !done(); i++)
            {
                client.Poll();
                Server.Poll();
                Server.Tick(config.TickSeconds);
                Persistence.Update(config.TickSeconds);
            }
        }

        public void Dispose()
        {
            serverTransport.Dispose();
            clientTransport.Dispose();
        }
    }

    // A store whose LoadAsync always faults (a store-read outage). Saves pass through to the inner store so a test
    // can observe whether a save (primary or quarantine) actually landed.
    private sealed class LoadFaultingWorldStore : IWorldStore
    {
        private readonly IWorldStore inner;
        public LoadFaultingWorldStore(IWorldStore inner) => this.inner = inner;
        public Task<byte[]?> LoadAsync(string key, CancellationToken ct = default) =>
            Task.FromException<byte[]?>(new System.IO.IOException("store read offline"));
        public Task SaveAsync(string key, byte[] data, CancellationToken ct = default) => inner.SaveAsync(key, data, ct);
        public Task<bool> DeleteAsync(string key, CancellationToken ct = default) => inner.DeleteAsync(key, ct);
        public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => inner.ExistsAsync(key, ct);
    }

    [Fact]
    public async Task InvalidBlob_QuarantinesWholeRecord()
    {
        IWorldStore store = new InMemoryWorldStore();
        byte[] original = PlayerRecord.From(
            new PlayerMoveState { Position = new Vector3(5f, 0f, 5f) }, Encoding.UTF8.GetBytes("loot")).Encode();
        await store.SaveAsync("player:hero", original);

        var pcfg = new WorldPersistenceConfig
        {
            ValidateGameState = (in PlayerPersistenceContext ctx, ReadOnlySpan<byte> blob) => PlayerGameStateVerdict.Invalid("bad blob"),
        };
        using var h = new Harness(store, Encoding.UTF8.GetBytes("hero"), pcfg);

        int slot = -1;
        string? quarantinedAccount = null, reason = null;
        h.Server.PlayerJoined += (s, _) => slot = s;
        h.Persistence.OnRecordQuarantined += (acct, r) => { quarantinedAccount = acct; reason = r; };

        h.PumpUntil(() => quarantinedAccount is not null);

        Assert.Equal("hero", quarantinedAccount);
        Assert.Equal("bad blob", reason);

        Assert.True(h.Server.TryGetPlayerState(slot, out PlayerMoveState got));
        Assert.NotEqual(new Vector3(5f, 0f, 5f), got.Position);   // did NOT apply the bad record
        Assert.Equal(slot * 2f, got.Position.X);                  // default spawn (WorldServerConfig has no SpawnPosition)
        Assert.Equal(0f, got.Position.Z);

        Assert.Equal(original, await store.LoadAsync("quarantine:player:hero"));   // whole record quarantined

        h.Persistence.SaveDirtyPass();
        await h.Persistence.FlushAsync();

        PlayerRecord primary = PlayerRecord.Decode((await store.LoadAsync("player:hero"))!);
        Assert.Equal(got.Position, primary.ToState().Position);                    // primary overwritten by the fresh spawn
        Assert.Equal(original, await store.LoadAsync("quarantine:player:hero"));    // quarantine copy still survives
    }

    [Fact]
    public async Task OutOfBoundsPosition_Quarantined()
    {
        IWorldStore store = new InMemoryWorldStore();
        await store.SaveAsync("player:hero",
            PlayerRecord.From(new PlayerMoveState { Position = new Vector3(500f, 0f, 0f) }).Encode());

        var pcfg = new WorldPersistenceConfig { Bounds = new RectBounds(-10f, -10f, 10f, 10f) };
        using var h = new Harness(store, Encoding.UTF8.GetBytes("hero"), pcfg);

        string? reason = null;
        h.Persistence.OnRecordQuarantined += (_, r) => reason = r;
        h.PumpUntil(() => reason is not null);

        Assert.NotNull(reason);
        Assert.Contains("bounds", reason!);
        Assert.NotNull(await store.LoadAsync("quarantine:player:hero"));
    }

    [Fact]
    public async Task UndecodableRecord_Quarantined_GuardCleared_PersistenceResumes()
    {
        IWorldStore store = new InMemoryWorldStore();
        byte[] garbage = Encoding.UTF8.GetBytes("{ not json");
        await store.SaveAsync("player:hero", garbage);

        using var h = new Harness(store, Encoding.UTF8.GetBytes("hero"));
        int slot = -1;
        bool quarantined = false;
        h.Server.PlayerJoined += (s, _) => slot = s;
        h.Persistence.OnRecordQuarantined += (_, _) => quarantined = true;

        h.PumpUntil(() => quarantined);
        Assert.True(quarantined);
        Assert.Equal(garbage, await store.LoadAsync("quarantine:player:hero"));

        // The old behavior faulted the load and left the guard set FOREVER, so this save is the regression assertion:
        // with the guard cleared at quarantine, a dirty pass now writes a valid fresh record over the garbage primary.
        Assert.True(h.Server.TryGetPlayerState(slot, out PlayerMoveState fresh));
        h.Persistence.SaveDirtyPass();
        await h.Persistence.FlushAsync();

        byte[]? data = await store.LoadAsync("player:hero");
        Assert.NotNull(data);
        Assert.Equal(fresh.Position, PlayerRecord.Decode(data!).ToState().Position);   // decodes cleanly -> fresh, not garbage
    }

    [Fact]
    public async Task StoreReadFault_StillLeavesGuardSet()
    {
        var inner = new InMemoryWorldStore();
        var store = new LoadFaultingWorldStore(inner);

        var pcfg = new WorldPersistenceConfig { Bounds = new RectBounds(-10f, -10f, 10f, 10f) };
        using var h = new Harness(store, Encoding.UTF8.GetBytes("hero"), pcfg);

        int slot = -1;
        bool quarantined = false;
        h.Server.PlayerJoined += (s, _) => slot = s;
        h.Persistence.OnRecordQuarantined += (_, _) => quarantined = true;

        h.PumpUntil(() => slot >= 0);
        h.Persistence.Update(0f);          // prune + observe the faulted read; the guard is never cleared
        h.Persistence.SaveDirtyPass();     // guard still set -> hero is skipped, no primary overwrite
        h.Persistence.Update(0f);

        Assert.False(quarantined);                                        // a store READ fault does NOT quarantine
        Assert.Null(await inner.LoadAsync("player:hero"));                // no primary write while the guard holds
        Assert.Null(await inner.LoadAsync("quarantine:player:hero"));     // and no quarantine write
    }

    [Fact]
    public async Task ValidRecord_HooksConfigured_AppliesNormally()
    {
        IWorldStore store = new InMemoryWorldStore();
        byte[] blob = Encoding.UTF8.GetBytes("xp=1");
        await store.SaveAsync("player:hero",
            PlayerRecord.From(new PlayerMoveState { Position = new Vector3(5f, 0f, 5f) }, blob).Encode());

        byte[]? applied = null;
        var pcfg = new WorldPersistenceConfig
        {
            Bounds = new RectBounds(-100f, -100f, 100f, 100f),
            ValidateGameState = (in PlayerPersistenceContext ctx, ReadOnlySpan<byte> b) => PlayerGameStateVerdict.Valid(),
            ApplyGameState = (in PlayerPersistenceContext ctx, ReadOnlySpan<byte> b) => applied = b.ToArray(),
        };
        using var h = new Harness(store, Encoding.UTF8.GetBytes("hero"), pcfg);

        int slot = -1;
        bool quarantined = false;
        h.Server.PlayerJoined += (s, _) => slot = s;
        h.Persistence.OnRecordQuarantined += (_, _) => quarantined = true;

        h.PumpUntil(() => slot >= 0);
        await h.Persistence.FlushAsync();   // settle the async load
        h.Persistence.Update(0f);           // apply the loaded state on the server thread

        Assert.False(quarantined);
        Assert.True(h.Server.TryGetPlayerState(slot, out PlayerMoveState got));
        Assert.Equal(new Vector3(5f, 0f, 5f), got.Position);              // position restored
        Assert.Equal(blob, applied);                                     // blob applied through the accepting validator
        Assert.Null(await store.LoadAsync("quarantine:player:hero"));    // nothing quarantined
    }
}
