using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using KhaozEngine.NetWorld;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class PlayerPersistenceAdmissionTests
{
    [Fact]
    public async Task RefusedPlacementRetainsTheRecordAndDefersGameStateUntilItCanPublish()
    {
        var host = new Host();
        var store = new InMemoryWorldStore();
        var saved = new PlayerMoveState { Position = new(4, 0.751f, 4) };
        byte[] original = PlayerRecord.From(saved, new byte[] { 7, 8 }).Encode();
        await store.SaveAsync("player:hero", original);
        int gameApplied = 0;
        var persistence = new WorldPersistence(host, store, new()
        {
            SaveIntervalSeconds = 0.01f,
            CaptureGameState = (in PlayerPersistenceContext _) => new byte[] { 1 },
            ApplyGameState = (in PlayerPersistenceContext _, ReadOnlySpan<byte> _) => gameApplied++
        });
        host.Join();
        await persistence.FlushAsync();
        Assert.Equal(0, gameApplied);
        Assert.Equal(Vector3.Zero, host.Live.Position);
        persistence.Update(0.1f);
        await persistence.FlushAsync();
        Assert.Equal(original, await store.LoadAsync("player:hero"));
        host.Ready = true;
        persistence.Update(0);
        Assert.Equal(saved.Position, host.Live.Position);
        Assert.Equal(1, gameApplied);
        persistence.Update(0);
        Assert.Equal(1, gameApplied);
    }

    [Fact]
    public async Task ADeferredRecordCannotApplyToARecycledSeatOrOverwriteTheDepartedSave()
    {
        var host = new Host();
        var store = new InMemoryWorldStore();
        byte[] original = PlayerRecord.From(new PlayerMoveState { Position = new(4, 1, 4) }).Encode();
        await store.SaveAsync("player:hero", original);
        var persistence = new WorldPersistence(host, store);
        host.Join();
        await persistence.FlushAsync();
        host.Leave();
        host.Account = "other";
        host.Ready = true;
        host.Join();
        await persistence.FlushAsync();
        Assert.Equal(Vector3.Zero, host.Live.Position);
        Assert.Equal(original, await store.LoadAsync("player:hero"));
    }

    [Fact]
    public async Task InvalidRecordKeepsItsGuardUntilTheConfiguredResetCanBeAdmitted()
    {
        var host = new Host();
        var store = new InMemoryWorldStore();
        byte[] original = [1, 2, 3];
        await store.SaveAsync("player:hero", original);
        var persistence = new WorldPersistence(host, store, new() { SaveIntervalSeconds = 0.01f });
        host.Join();
        await persistence.FlushAsync();
        persistence.Update(0.1f);
        await persistence.FlushAsync();
        Assert.Equal(original, await store.LoadAsync("player:hero"));
        Assert.Equal(0, host.Resets);
        host.Ready = true;
        persistence.Update(0);
        Assert.Equal(1, host.Resets);
        await persistence.FlushAsync();
        Assert.Equal(original, await store.LoadAsync("quarantine:player:hero"));
    }

    sealed class Host : IWorldPersistenceHost
    {
        public PlayerMoveState Live;
        public bool Ready;
        public string Account = "hero";
        public int Resets;
        bool joined;
        public event Action<int, string>? PlayerJoined;
        public event Action<int, string, PlayerMoveState>? PlayerLeaving;
        public IReadOnlyCollection<int> JoinedSlots => joined ? new[] { 0 } : Array.Empty<int>();
        public void Join() { joined = true; PlayerJoined?.Invoke(0, Account); }
        public void Leave() { PlayerLeaving?.Invoke(0, Account, Live); joined = false; }
        public bool TryGetAccountId(int slot, out string account) { account = Account; return joined && slot == 0; }
        public bool TryGetPlayerState(int slot, out PlayerMoveState state) { state = Live; return joined && slot == 0; }
        public void SetPlayerState(int slot, in PlayerMoveState state, bool teleport = false) => TrySetPlayerState(slot, state, teleport);
        public bool TrySetPlayerState(int slot, in PlayerMoveState state, bool teleport = false)
        {
            if (!Ready || !joined) return false;
            Live = state;
            return true;
        }
        public bool TryGetConfiguredSpawn(int slot, out PlayerMoveState spawn) { spawn = default; return Ready && joined; }
        public bool TryResetToConfiguredSpawn(int slot)
        {
            if (!Ready || !joined) return false;
            Live = default;
            Resets++;
            return true;
        }
    }
}
