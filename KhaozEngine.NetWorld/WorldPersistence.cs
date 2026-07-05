using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.WorldStore;

namespace KhaozEngine.NetWorld;

/// <summary>Tunables and game-state hooks for <see cref="WorldPersistence"/>.</summary>
public sealed class WorldPersistenceConfig
{
    /// <summary>How often the periodic snapshot saves dirty players, seconds. A crash loses at most this much.</summary>
    public float SaveIntervalSeconds { get; init; } = 30f;

    /// <summary>Key namespace for player records. Stored key is <c>{KeyPrefix}{accountId}</c>.</summary>
    public string KeyPrefix { get; init; } = "player:";

    /// <summary>
    /// Optional hook the game supplies to attach its opaque durable per-player blob (XP, skills, inventory, quest
    /// log, …) to each save. Invoked on the server thread at every save point (save-on-leave and the periodic dirty
    /// pass), so it may read the live per-player object by <see cref="PlayerPersistenceContext.Slot"/>. The returned
    /// bytes ride in the same <see cref="PlayerRecord"/> as position and share its dirty-tracking / interval-save /
    /// flush machinery. Null (the default) persists position only.
    /// </summary>
    public PlayerGameStateCapture? CaptureGameState { get; init; }

    /// <summary>
    /// Optional hook the game supplies to re-attach a previously-captured game blob at load-on-join, invoked on the
    /// server thread as the loaded position is applied. Never fired for a player with no saved blob. Null (the
    /// default) discards any stored blob on load. Pair it with <see cref="CaptureGameState"/>.
    /// </summary>
    public PlayerGameStateApply? ApplyGameState { get; init; }
}

/// <summary>
/// Wires an <see cref="IWorldStore"/> into the <see cref="WorldServer"/> lifecycle so the world survives a
/// restart. Backend-agnostic (only <see cref="IWorldStore"/> + <see cref="PlayerRecord"/>):
/// load-on-join (place the player at the saved position, or leave the default spawn if absent),
/// save-on-leave (persist the final state), and a periodic snapshot of players whose state changed since their
/// last save. Async loads are applied to the server on the server thread inside <see cref="Update"/> (never from
/// a background continuation), so a genuinely-async backend can't race the tick loop.
///
/// <para>A game attaches durable per-player state (XP, inventory, quests) through
/// <see cref="WorldPersistenceConfig.CaptureGameState"/> / <see cref="WorldPersistenceConfig.ApplyGameState"/>: an
/// opaque blob that rides the SAME record, dirty comparison, interval save, flush-on-drain and load-on-join
/// thread-marshalling as position. The engine never interprets the blob; the game owns its format and migration.
/// Because the record is account-keyed (<c>player:{accountId}</c>), the blob is unaffected by cell handoff.</para>
/// </summary>
public sealed class WorldPersistence
{
    private readonly IWorldPersistenceHost server;
    private readonly IWorldStore store;
    private readonly WorldPersistenceConfig config;

    // Loaded records waiting to be applied on the server thread (the drain below applies these): position via
    // SetPlayerState, then the opaque game blob via ApplyGameState. AccountId rides along for the apply context.
    private readonly ConcurrentQueue<(int slot, string accountId, PlayerMoveState state, byte[]? game)> applyQueue = new();
    // accountId -> last persisted bytes, for dirty comparison (covers position AND the game blob, since both are
    // in the same encoded record - a change to either marks the record dirty and re-saves).
    private readonly ConcurrentDictionary<string, byte[]> lastSaved = new();
    // In-flight loads/saves, so FlushAsync can await them (tests + shutdown).
    private readonly object pendingLock = new();
    private readonly List<Task> pending = new();
    private float sinceSave;

    public WorldPersistence(IWorldPersistenceHost server, IWorldStore store, WorldPersistenceConfig? config = null)
    {
        this.server = server ?? throw new ArgumentNullException(nameof(server));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.config = config ?? new WorldPersistenceConfig();
        server.PlayerJoined += OnPlayerJoined;
        server.PlayerLeaving += OnPlayerLeaving;
    }

    private string Key(string accountId) => config.KeyPrefix + accountId;

    private void Track(Task task)
    {
        lock (pendingLock) pending.Add(task);
    }

    private void OnPlayerJoined(int slot, string accountId) => Track(LoadOnJoinAsync(slot, accountId));

    private async Task LoadOnJoinAsync(int slot, string accountId)
    {
        byte[]? data = await store.LoadAsync(Key(accountId)).ConfigureAwait(false);
        if (data is null) return;                          // no save -> keep the default spawn
        lastSaved[accountId] = data;                       // loaded == clean baseline
        PlayerRecord record = PlayerRecord.Decode(data);
        applyQueue.Enqueue((slot, accountId, record.ToState(), record.Game));
    }

    // Captures the game blob (on the server thread - the caller is on the server thread) and encodes the full record.
    private byte[] BuildRecordBytes(int slot, string accountId, in PlayerMoveState state)
    {
        byte[]? game = config.CaptureGameState?.Invoke(new PlayerPersistenceContext(slot, accountId));
        return PlayerRecord.From(state, game).Encode();
    }

    private void OnPlayerLeaving(int slot, string accountId, PlayerMoveState finalState)
        => Track(SaveIfDirtyAsync(accountId, BuildRecordBytes(slot, accountId, finalState)));

    private async Task SaveIfDirtyAsync(string accountId, byte[] data)
    {
        if (lastSaved.TryGetValue(accountId, out byte[]? prev) && prev.AsSpan().SequenceEqual(data))
            return;                                        // unchanged since last save
        await store.SaveAsync(Key(accountId), data).ConfigureAwait(false);
        lastSaved[accountId] = data;
    }

    /// <summary>Call once per server frame. Applies any completed load-on-join state (on this thread) and runs
    /// the periodic dirty snapshot when <see cref="WorldPersistenceConfig.SaveIntervalSeconds"/> has elapsed.</summary>
    public void Update(float dt)
    {
        DrainApplyQueue();

        lock (pendingLock) pending.RemoveAll(t => t.Status == TaskStatus.RanToCompletion);

        sinceSave += dt;
        if (sinceSave >= config.SaveIntervalSeconds)
        {
            sinceSave = 0f;
            SaveDirtyPass();
        }
    }

    // Applies loaded records on the server thread: position first, then the opaque game blob (only when present and a
    // hook is set). Shared by Update and FlushAsync so the game blob is re-attached on both paths.
    private void DrainApplyQueue()
    {
        while (applyQueue.TryDequeue(out (int slot, string accountId, PlayerMoveState state, byte[]? game) a))
        {
            server.SetPlayerState(a.slot, a.state);
            if (a.game is { Length: > 0 } && config.ApplyGameState is { } apply)
                apply(new PlayerPersistenceContext(a.slot, a.accountId), a.game);
        }
    }

    /// <summary>Saves every joined player whose state changed since its last save.</summary>
    public void SaveDirtyPass()
    {
        foreach (int slot in new List<int>(server.JoinedSlots))
            if (server.TryGetAccountId(slot, out string accountId) &&
                server.TryGetPlayerState(slot, out PlayerMoveState state))
                Track(SaveIfDirtyAsync(accountId, BuildRecordBytes(slot, accountId, state)));
    }

    /// <summary>Awaits all in-flight loads/saves, then applies any pending loaded state. Call on shutdown (or in
    /// tests) to reach a quiescent, fully-persisted point. Invoke from the server thread / when the loop is idle.</summary>
    public async Task FlushAsync()
    {
        Task[] tasks;
        lock (pendingLock) { tasks = pending.ToArray(); pending.Clear(); }
        await Task.WhenAll(tasks).ConfigureAwait(false);
        DrainApplyQueue();
    }
}
