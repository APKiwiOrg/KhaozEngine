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
/// <para>While a load-on-join is still in flight, the account is guarded: the periodic dirty pass and save-on-leave
/// both skip it, so a save firing mid-load can't overwrite the stored record (position AND the durable game blob)
/// with the pre-restore default-spawn state and permanently erase progression. The guard clears once the loaded
/// record has been applied on the server thread, or immediately if the load found no saved record. Skipping the
/// leave-save is intentional: the state was never applied, so the stored record - not the pre-restore live state -
/// is the truth worth keeping. One edge is not covered: on an async store, store operations for the same account
/// are not ordered across a rapid leave/rejoin that overlaps an in-flight load-on-join, so a rejoin can briefly apply
/// pre-leave state (the next periodic save reconciles it). Use a stable account id; if a session needs strict
/// ordering, serialize your own per-account store operations on top.</para>
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
    // accountId -> outstanding load-on-join guard. Set in OnPlayerJoined before the async load starts; cleared only
    // AFTER the loaded record is applied on the server thread (DrainApplyQueue), or immediately when the load returns
    // null (a brand-new player: no stored record to protect). SaveDirtyPass and OnPlayerLeaving skip an account still
    // in this set, so a periodic save or a quick leave can't overwrite the stored record (position AND the durable
    // game blob) with pre-restore state - the default spawn and a null blob - and permanently erase progression. A
    // faulted load deliberately leaves the guard set so the intact stored record stays protected (a later rejoin
    // retries the load); mirrors CellPersistence's loadsInFlight.
    private readonly ConcurrentDictionary<string, byte> loadsInFlight = new();
    // In-flight loads/saves, so FlushAsync can await them (tests + shutdown).
    private readonly object pendingLock = new();
    private readonly List<Task> pending = new();
    private float sinceSave;

    /// <summary>Raised on the server thread from <see cref="Update"/> when a tracked load/save task faulted or was
    /// canceled (typically a store outage). The engine drops the finished task so the pending list can't grow
    /// unbounded, and this hook lets the game log or alert. The failed save's state stays dirty and is retried on the
    /// next pass.</summary>
    public event Action<Exception>? OnStoreError;

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

    private void OnPlayerJoined(int slot, string accountId)
    {
        loadsInFlight[accountId] = 0;                       // guard the account until the loaded record applies (or the load returns null)
        Track(LoadOnJoinAsync(slot, accountId));
    }

    private async Task LoadOnJoinAsync(int slot, string accountId)
    {
        byte[]? data = await store.LoadAsync(Key(accountId)).ConfigureAwait(false);
        if (data is null)                                  // no save -> keep the default spawn
        {
            loadsInFlight.TryRemove(accountId, out _);     // brand-new player: nothing stored to clobber, drop the guard now
            return;
        }
        lastSaved[accountId] = data;                       // loaded == clean baseline
        PlayerRecord record = PlayerRecord.Decode(data);
        applyQueue.Enqueue((slot, accountId, record.ToState(), record.Game));   // guard cleared in DrainApplyQueue, AFTER this applies
    }

    // Captures the game blob (on the server thread - the caller is on the server thread) and encodes the full record.
    private byte[] BuildRecordBytes(int slot, string accountId, in PlayerMoveState state)
    {
        byte[]? game = config.CaptureGameState?.Invoke(new PlayerPersistenceContext(slot, accountId));
        return PlayerRecord.From(state, game).Encode();
    }

    private void OnPlayerLeaving(int slot, string accountId, PlayerMoveState finalState)
    {
        if (loadsInFlight.ContainsKey(accountId)) return;   // load still outstanding: the stored record was never applied, so IT - not this pre-restore live state - is the truth worth keeping
        Track(SaveIfDirtyAsync(accountId, BuildRecordBytes(slot, accountId, finalState)));
    }

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

        PrunePending();

        sinceSave += dt;
        if (sinceSave >= config.SaveIntervalSeconds)
        {
            sinceSave = 0f;
            SaveDirtyPass();
        }
    }

    // Drops every finished task from the pending list. Previously only RanToCompletion was pruned, so on a store
    // outage the faulted/canceled tasks accumulated until FlushAsync surfaced them - the list grew unbounded. Faults
    // are observed (reading Task.Exception) and surfaced via OnStoreError; the failed save's state stays dirty and is
    // retried on the next pass. Collect the exceptions inside the lock but raise the event outside it (never run a
    // game callback while holding pendingLock).
    private void PrunePending()
    {
        List<Exception>? failures = null;
        lock (pendingLock)
            pending.RemoveAll(t =>
            {
                if (!t.IsCompleted) return false;
                if (t.IsFaulted || t.IsCanceled)
                    (failures ??= new List<Exception>()).Add(t.Exception?.GetBaseException() ?? new TaskCanceledException());
                return true;
            });
        if (failures is not null)
            foreach (Exception ex in failures) OnStoreError?.Invoke(ex);
    }

    // Applies loaded records on the server thread: position first, then the opaque game blob (only when present and a
    // hook is set). Shared by Update and FlushAsync so the game blob is re-attached on both paths.
    private void DrainApplyQueue()
    {
        while (applyQueue.TryDequeue(out (int slot, string accountId, PlayerMoveState state, byte[]? game) a))
        {
            server.SetPlayerState(a.slot, a.state, teleport: true);   // placing a loaded player is a teleport (cut, no glide)
            if (a.game is { Length: > 0 } && config.ApplyGameState is { } apply)
                apply(new PlayerPersistenceContext(a.slot, a.accountId), a.game);
            loadsInFlight.TryRemove(a.accountId, out _);   // restore applied; the account is now safe to dirty-save
        }
    }

    /// <summary>Saves every joined player whose state changed since its last save.</summary>
    public void SaveDirtyPass()
    {
        foreach (int slot in new List<int>(server.JoinedSlots))
            if (server.TryGetAccountId(slot, out string accountId) &&
                !loadsInFlight.ContainsKey(accountId) &&        // load outstanding: skip so this pass can't overwrite the stored record with pre-restore state
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
