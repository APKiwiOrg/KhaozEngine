using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.WorldStore;

namespace KhaozEngine.Tests.NetWorld;

internal sealed class WorldPersistenceFallbackRig
{
    public readonly HostStub Host = new();
    public readonly StoreStub Store = new();
    public readonly Dictionary<int, byte[]> Game = new();
    public readonly List<string> Applied = new();
    public readonly List<Exception> Errors = new();
    public readonly List<string> Quarantined = new();
    public readonly List<string> Dropped = new();
    public readonly WorldPersistence Persistence;

    public WorldPersistenceFallbackRig(PlayerRecordFallbackLoad fallback, bool persistGuests = false,
        WorldBounds? bounds = null, bool rejectGame = false)
    {
        Persistence = new WorldPersistence(Host, Store, new WorldPersistenceConfig
        {
            KeyPrefix = "position:",
            SaveIntervalSeconds = 1f,
            LoadFallback = fallback,
            PersistGuests = persistGuests,
            Bounds = bounds,
            CaptureGameState = (in PlayerPersistenceContext ctx) => Game.GetValueOrDefault(ctx.Slot),
            ApplyGameState = (in PlayerPersistenceContext ctx, ReadOnlySpan<byte> blob) =>
            {
                Game[ctx.Slot] = blob.ToArray();
                Applied.Add(ctx.AccountId);
            },
            ValidateGameState = (in PlayerPersistenceContext ctx, ReadOnlySpan<byte> blob) =>
                rejectGame ? PlayerGameStateVerdict.Invalid("legacy blob rejected") : PlayerGameStateVerdict.Valid(),
        });
        Persistence.OnStoreError += Errors.Add;
        Persistence.OnRecordQuarantined += (key, _) => Quarantined.Add(key);
        Persistence.OnLoadApplyDropped += (key, _) => Dropped.Add(key);
    }

    public Task Flush() => Persistence.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));

    internal sealed class HostStub : IWorldPersistenceHost
    {
        readonly Dictionary<int, (string Account, string Key, PlayerMoveState State)> players = new();
        public event Action<int, string>? PlayerJoined;
        public event Action<int, string, PlayerMoveState>? PlayerLeaving;
        public IReadOnlyCollection<int> JoinedSlots => players.Keys;

        public void Join(int slot = 0, string account = "account", string key = "durable")
        {
            players[slot] = (account, key, new PlayerMoveState { Position = Vector3.Zero });
            PlayerJoined?.Invoke(slot, account);
        }

        public void Leave(int slot = 0)
        {
            var player = players[slot];
            PlayerLeaving?.Invoke(slot, player.Account, player.State);
            players.Remove(slot);
        }

        public Vector3 Position(int slot = 0) => players[slot].State.Position;
        public void SetPlayerState(int slot, in PlayerMoveState state, bool teleport = false)
        {
            var player = players[slot];
            players[slot] = (player.Account, player.Key, state);
        }

        public bool TryGetAccountId(int slot, out string account)
        {
            bool found = players.TryGetValue(slot, out var player);
            account = found ? player.Account : string.Empty;
            return found;
        }

        public bool TryGetPersistenceKey(int slot, out string key)
        {
            bool found = players.TryGetValue(slot, out var player);
            key = found ? player.Key : string.Empty;
            return found;
        }

        public bool TryGetPlayerState(int slot, out PlayerMoveState state)
        {
            bool found = players.TryGetValue(slot, out var player);
            state = player.State;
            return found;
        }

        public bool TryGetConfiguredSpawn(int slot, out PlayerMoveState spawn)
        {
            spawn = new PlayerMoveState { Position = Vector3.Zero };
            return players.ContainsKey(slot);
        }
    }

    internal sealed class StoreStub : IWorldStore
    {
        public readonly InMemoryWorldStore Inner = new();
        public readonly List<string> Saved = new();
        public bool FailLoads;
        public bool FailSaves;
        public Task? SaveGate;

        public Task<byte[]?> LoadAsync(string key, CancellationToken ct = default) => FailLoads
            ? Task.FromException<byte[]?>(new IOException("primary read failed")) : Inner.LoadAsync(key, ct);

        public async Task SaveAsync(string key, byte[] data, CancellationToken ct = default)
        {
            if (FailSaves) throw new IOException("save failed");
            if (SaveGate is { } gate) await gate.ConfigureAwait(false);
            await Inner.SaveAsync(key, data, ct).ConfigureAwait(false);
            Saved.Add(key);
        }

        public Task<bool> DeleteAsync(string key, CancellationToken ct = default) => Inner.DeleteAsync(key, ct);
        public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Inner.ExistsAsync(key, ct);
    }
}
