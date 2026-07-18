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

    /// <summary>
    /// Optional authoritative play area the loaded position is checked against on the server thread at load-on-join. A
    /// record whose (X, Z) falls outside the bounds is quarantined WHOLE (see <see cref="WorldPersistence"/>) rather
    /// than applied. Null (the default) accepts any position.
    /// </summary>
    public WorldBounds? Bounds { get; init; }

    /// <summary>
    /// Optional game hook that vets the loaded durable blob on the server thread at load-on-join, before it is applied.
    /// A rejecting <see cref="PlayerGameStateVerdict"/> quarantines the WHOLE record (position and blob). Only fired for
    /// a record that actually carries a blob. Null (the default) accepts any blob.
    /// </summary>
    public PlayerGameStateValidate? ValidateGameState { get; init; }

    /// <summary>
    /// Key prefix under which a quarantined record's raw bytes are copied verbatim. The quarantine key is
    /// <c>{QuarantineKeyPrefix}{KeyPrefix}{accountId}</c> (default <c>quarantine:player:{accountId}</c>), so the intact
    /// original survives for offline inspection while the primary record is free to be overwritten by the fresh spawn.
    /// </summary>
    public string QuarantineKeyPrefix { get; init; } = "quarantine:";
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
/// record has been applied (or quarantined) on the server thread, or immediately if the load found no saved record.
/// Skipping the leave-save is intentional: the state was never applied, so the stored record - not the pre-restore
/// live state - is the truth worth keeping. A faulted store READ is different: the guard is deliberately LEFT set so
/// the intact stored record stays protected, and a later rejoin retries the read (outage semantics). One edge is not
/// covered: on an async store, store operations for the same account are not ordered across a rapid leave/rejoin that
/// overlaps an in-flight load-on-join, so a rejoin can briefly apply pre-leave state (the next periodic save
/// reconciles it). Use a stable account id. If a session needs strict ordering, serialize your own per-account store
/// operations on top.</para>
///
/// <para>Loaded records are validated on the server thread before they are applied, via
/// <see cref="WorldPersistenceConfig.Bounds"/> (position must be in-bounds), the game's
/// <see cref="WorldPersistenceConfig.ValidateGameState"/> verdict on its durable blob, and a decode guard (a record
/// whose JSON no longer parses). A record that fails ANY check is quarantined WHOLE: its raw bytes are copied verbatim
/// to <c>{QuarantineKeyPrefix}{KeyPrefix}{accountId}</c> (default <c>quarantine:player:{accountId}</c>), the player is
/// NOT placed from it (so it keeps its default spawn, a fresh start), <see cref="OnRecordQuarantined"/> fires on the
/// server thread, and the clean baseline is deliberately NOT advanced to the bad record. Because the baseline moves to
/// the loaded bytes only on a SUCCESSFUL apply, a quarantined record is never marked clean, so the next dirty pass
/// overwrites the bad PRIMARY record with the fresh-spawn state while the quarantine copy survives for offline repair.
/// This also fixes an undecodable RECORD, which previously faulted the load and left the guard set forever (progress
/// silently stopped persisting): it is now routed through quarantine, which clears the guard so persistence resumes.
/// A store READ fault is NOT quarantine - the record was never read, so the outage retry above still applies.</para>
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

    // Loaded records waiting to be validated + applied on the server thread (the drain below handles these): validate
    // (bounds, blob verdict, or a carried decode failure), then either quarantine the whole record or apply it -
    // position via SetPlayerState, then the opaque game blob via ApplyGameState. The raw bytes ride along so a
    // quarantine copies them verbatim. AccountId rides along for the apply/quarantine context.
    private readonly ConcurrentQueue<PendingApply> applyQueue = new();
    // accountId -> last persisted bytes, for dirty comparison (covers position AND the game blob, since both are
    // in the same encoded record - a change to either marks the record dirty and re-saves). The load-on-join baseline
    // is set only on a SUCCESSFUL apply (in DrainApplyQueue), NOT at load time: a quarantined record must never be
    // marked clean, so the fresh-spawn state stays dirty and overwrites the bad primary on the next pass.
    private readonly ConcurrentDictionary<string, byte[]> lastSaved = new();
    // accountId -> outstanding load-on-join guard. Set in OnPlayerJoined before the async load starts, cleared only
    // AFTER the loaded record is applied OR quarantined on the server thread (DrainApplyQueue), or immediately when the
    // load returns null (a brand-new player: no stored record to protect). SaveDirtyPass and OnPlayerLeaving skip an
    // account still in this set, so a periodic save or a quick leave can't overwrite the stored record (position AND
    // the durable game blob) with pre-restore state - the default spawn and a null blob - and permanently erase
    // progression. A faulted store READ deliberately leaves the guard set so the intact stored record stays protected
    // (a later rejoin retries the load), mirroring CellPersistence's loadsInFlight. An undecodable record is NOT a read
    // fault - it read fine, it just won't parse - so it clears the guard via quarantine, not via the outage path.
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

    /// <summary>Raised on the server thread (from <see cref="Update"/> / <see cref="FlushAsync"/> via the apply drain)
    /// when a loaded record failed validation and was quarantined WHOLE: (accountId, reason). The raw record has been
    /// copied to the quarantine key by the time this fires, the player keeps its default spawn, and the primary record
    /// will be overwritten by the fresh state on the next dirty pass. The reason is the bounds/blob/decode message, for
    /// logging or alerting.</summary>
    public event Action<string, string>? OnRecordQuarantined;

    // A loaded record marshalled to the server thread for validation + apply. Raw is the exact stored bytes (copied
    // verbatim on quarantine). State/Game are the decoded position and blob (default/null when DecodeFailure is set).
    // DecodeFailure is non-null only when the stored bytes would not parse, which routes the item straight to
    // quarantine without ever touching State/Game.
    private readonly struct PendingApply
    {
        public PendingApply(int slot, string accountId, byte[] raw, PlayerMoveState state, byte[]? game, string? decodeFailure)
        {
            Slot = slot;
            AccountId = accountId;
            Raw = raw;
            State = state;
            Game = game;
            DecodeFailure = decodeFailure;
        }

        public int Slot { get; }
        public string AccountId { get; }
        public byte[] Raw { get; }
        public PlayerMoveState State { get; }
        public byte[]? Game { get; }
        public string? DecodeFailure { get; }
    }

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
        // The baseline is NOT set here any more: it moves to apply time (DrainApplyQueue), so a quarantined record is
        // never marked clean. A read fault propagates out of this task and the guard deliberately stays set (outage
        // retry). An undecodable record read fine but won't parse, so route it through quarantine instead of faulting
        // the task and stranding the guard forever.
        PlayerRecord record;
        try
        {
            record = PlayerRecord.Decode(data);
        }
        catch (Exception ex)
        {
            applyQueue.Enqueue(new PendingApply(slot, accountId, data, default, null, $"undecodable record: {ex.Message}"));
            return;                                        // guard stays set until the drain quarantines and clears it
        }
        applyQueue.Enqueue(new PendingApply(slot, accountId, data, record.ToState(), record.Game, null));   // validated + guard cleared in DrainApplyQueue
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

    // Validates then applies (or quarantines) loaded records on the server thread. A record fails validation if it
    // carries a decode failure, its position is out of bounds, or the game's blob verdict rejects it - checked in that
    // order, first hit wins. On failure the WHOLE record is copied verbatim to the quarantine key, the guard clears,
    // the player is left at its default spawn (no SetPlayerState) and the baseline is untouched (so the fresh state
    // overwrites the bad primary next pass), and OnRecordQuarantined fires. On success: position first, then the
    // opaque game blob (only when present and a hook is set), then advance the baseline to the loaded bytes, then clear
    // the guard. Shared by Update and FlushAsync so both paths validate identically.
    private void DrainApplyQueue()
    {
        while (applyQueue.TryDequeue(out PendingApply a))
        {
            string? failure = a.DecodeFailure;
            if (failure is null && config.Bounds is { } b && !b.Contains(a.State.Position.X, a.State.Position.Z))
                failure = $"position ({a.State.Position.X}, {a.State.Position.Z}) outside world bounds";
            if (failure is null && a.Game is { Length: > 0 } && config.ValidateGameState is { } validate)
            {
                PlayerGameStateVerdict verdict = validate(new PlayerPersistenceContext(a.Slot, a.AccountId), a.Game);
                if (!verdict.IsValid) failure = verdict.Reason ?? "invalid";
            }

            if (failure is not null)
            {
                Track(store.SaveAsync(config.QuarantineKeyPrefix + Key(a.AccountId), a.Raw));   // copy the bad record verbatim, awaited by FlushAsync
                loadsInFlight.TryRemove(a.AccountId, out _);   // quarantined, so the account is now free to dirty-save the fresh spawn over the bad primary
                OnRecordQuarantined?.Invoke(a.AccountId, failure);
                continue;                                      // NOT applied, baseline NOT advanced
            }

            server.SetPlayerState(a.Slot, a.State, teleport: true);   // placing a loaded player is a teleport (cut, no glide)
            if (a.Game is { Length: > 0 } && config.ApplyGameState is { } apply)
                apply(new PlayerPersistenceContext(a.Slot, a.AccountId), a.Game);
            lastSaved[a.AccountId] = a.Raw;                 // loaded == clean baseline, set at apply time (never for a quarantined record)
            loadsInFlight.TryRemove(a.AccountId, out _);   // restore applied, the account is now safe to dirty-save
        }
    }

    /// <summary>Saves every joined player whose state changed since its last save.</summary>
    public void SaveDirtyPass()
    {
        List<(string accountId, byte[] data)>? dirty = null;
        foreach (int slot in new List<int>(server.JoinedSlots))
            if (server.TryGetAccountId(slot, out string accountId) &&
                !loadsInFlight.ContainsKey(accountId) &&        // load outstanding: skip so this pass can't overwrite the stored record with pre-restore state
                server.TryGetPlayerState(slot, out PlayerMoveState state))
            {
                byte[] data = BuildRecordBytes(slot, accountId, state);
                if (lastSaved.TryGetValue(accountId, out byte[]? prev) && prev.AsSpan().SequenceEqual(data))
                    continue;                                    // unchanged since last save
                (dirty ??= new List<(string, byte[])>()).Add((accountId, data));
            }
        if (dirty is not null) Track(SaveManyDirtyAsync(dirty));
    }

    // Batches every dirty account's record into one store round trip (one SaveManyAsync call instead of N
    // SaveAsync calls). lastSaved is advanced per account only AFTER the whole batch lands, so a faulted/canceled
    // batch leaves every account in it dirty for the next pass - the same "never mark a record clean before it is
    // actually saved" guarantee the old per-account SaveIfDirtyAsync gave, just at batch grain: one failed round
    // trip means the whole pass retries next interval, not only the one record that actually caused the fault.
    private async Task SaveManyDirtyAsync(List<(string accountId, byte[] data)> dirty)
    {
        var items = new List<(string Key, byte[] Data)>(dirty.Count);
        foreach ((string accountId, byte[] data) in dirty) items.Add((Key(accountId), data));
        await store.SaveManyAsync(items).ConfigureAwait(false);
        foreach ((string accountId, byte[] data) in dirty) lastSaved[accountId] = data;
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
