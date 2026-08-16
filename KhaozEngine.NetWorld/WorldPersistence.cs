using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
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
    /// How many accounts the resume-hint cache holds (<see cref="WorldPersistence.ResumeHints"/>), which is what
    /// lets a REJOINING player's entity be built where they left instead of at the configured spawn. Default 1024.
    /// Zero or less holds nothing, which turns the join seed off and returns the head to the pre-17.37.0 behaviour
    /// (spawn first, restore afterwards, two teleports for anyone away from the spawn).
    /// </summary>
    public int ResumeHintCapacity { get; init; } = 1024;

    /// <summary>
    /// How far (world metres) a loaded position may be from where the player already stands and still be applied
    /// QUIETLY: written without advancing the teleport epoch, so the client glides the remainder off instead of
    /// cutting. Default 1. A rejoiner seeded from the resume hint is already on the loaded position, so the restore
    /// moves nothing and must not report a teleport (#642); the metre of slack covers the ground clamp and the
    /// handful of ticks the player may have been simulated for while the load was in flight. Beyond it the restore
    /// really did move the player and is reported as the teleport it is. Zero or less makes every restore a
    /// teleport (the pre-17.37.0 behaviour).
    /// <para>The window is measured at DRAIN time, against where the player stands when the load actually lands, so
    /// on a high-latency store a rejoiner who is already moving can travel past it before the restore arrives and
    /// take the hard cut anyway. That is not a regression (every restore was a teleport before this), but the
    /// benefit degrades as store latency rises, and widening this is the knob for a slow store. The loopback rig the
    /// tests drive cannot show it: its store answers synchronously.</para>
    /// </summary>
    public float QuietRestoreDistance { get; init; } = 1f;

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
/// load-on-join (place the player at the saved position, or leave the join's own spawn if absent),
/// save-on-leave (persist the final state), and a periodic snapshot of players whose state changed since their
/// last save. Async loads are applied to the server on the server thread inside <see cref="Update"/> (never from
/// a background continuation), so a genuinely-async backend can't race the tick loop.
///
/// <para>A REJOIN is seeded rather than only restored. Every position this layer persists is also kept as an
/// in-process hint (<see cref="ResumeHints"/>, installed on the host at construction through
/// <see cref="IWorldPersistenceHost.SetResumePositionProvider"/>), and the host builds a known account's entity
/// there instead of at its configured spawn. That matters because the client decides whether a rejoin moved the
/// player by measuring the RESUME SNAPSHOT: serving the spawn first and restoring afterwards reported one teleport
/// for the spawn and a second for the restore, which is what made a reconnect rebuild a consumer's whole streaming
/// ring while the player stood still (#642). The seed is a hint and never the authority - the asynchronous load
/// still runs, and still applies the stored record over it - but when the two agree the restore now lands within
/// <see cref="WorldPersistenceConfig.QuietRestoreDistance"/> of where the player already is and is applied WITHOUT
/// advancing the teleport epoch, so the whole rejoin is quiet. The hints are memory-only: after a process restart
/// the first rejoin of each account falls back to the configured spawn and takes the restore teleport, unless the
/// game pre-warms <see cref="ResumeHints"/> from its own store at boot.</para>
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
/// NOT placed from it and is instead RESET to the host's configured spawn, as a fresh start and as a genuine teleport,
/// with its resume hint forgotten so a further rejoin cannot re-seed the rejected position. That reset is not
/// ceremony: the join seed means a rejoiner is already standing on the hint, which nothing here validated
/// (<see cref="WorldPersistenceConfig.Bounds"/> vets the loaded record, never the hint), so simply declining to place
/// them would leave a rejected record's player on an unvalidated position. Then <see cref="OnRecordQuarantined"/>
/// fires on the server thread, and the clean baseline is deliberately NOT advanced to the bad record. Because the
/// baseline moves to the loaded bytes only on a SUCCESSFUL apply, a quarantined record is never marked clean, so the
/// next dirty pass overwrites the bad PRIMARY record with that fresh spawn state while the quarantine copy survives
/// for offline repair. A host that implements no <see cref="IWorldPersistenceHost.TryGetConfiguredSpawn"/> keeps the
/// old shape (no placement), which is the right answer for it: with no seed installed there is nothing to undo.
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
    // Where each account was last seen, so a rejoin is BUILT there instead of at the configured spawn (see the
    // class doc). Server-thread only: written from OnPlayerLeaving and the apply drain, read from the host's join.
    private readonly ResumePositionCache resumeHints;
    private float sinceSave;

    /// <summary>Raised on the server thread from <see cref="Update"/> (via <see cref="PrunePending"/>) or from
    /// <see cref="FlushAsync"/> (via <see cref="AwaitAndObserve"/>) when a tracked load/save task faulted or was
    /// canceled (typically a store outage). The engine drops the finished task so the pending list can't grow
    /// unbounded, and this hook lets the game log or alert. The failed save's state stays dirty and is retried on the
    /// next pass. <see cref="FlushAsync"/> surfaces every fault this way instead of rethrowing, so it always reaches
    /// quiescence even through a store outage, mirroring <c>CellPersistence.AwaitPendingAsync</c>.</summary>
    public event Action<Exception>? OnStoreError;

    /// <summary>Raised on the server thread (from <see cref="Update"/> / <see cref="FlushAsync"/> via the apply drain)
    /// when a loaded record failed validation and was quarantined WHOLE: (accountId, reason). The raw record's copy
    /// to the quarantine key has only been queued (a Tracked <c>SaveAsync</c>) by the time this fires, not
    /// necessarily completed: an async store may still be writing it, though <see cref="FlushAsync"/> awaits that
    /// write before it returns. By the time this fires the player has already been reset to the host's configured
    /// spawn (as a teleport) and its resume hint forgotten, and the primary record will be overwritten by that fresh
    /// state on the next dirty pass. The reason is the bounds/blob/decode message, for logging or alerting. On
    /// the <see cref="FlushAsync"/> path the invoking continuation may run on a thread-pool thread rather than the
    /// true server thread, under FlushAsync's own documented precondition that it only be invoked when the server
    /// loop is idle.</summary>
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
        resumeHints = new ResumePositionCache(this.config.ResumeHintCapacity);
        server.PlayerJoined += OnPlayerJoined;
        server.PlayerLeaving += OnPlayerLeaving;
        // Install the join seed BEFORE any join can arrive. A game with its own account store installs its own
        // provider after constructing this layer, which replaces (rather than chains onto) the one wired here.
        server.SetResumePositionProvider(resumeHints.TryGet);
    }

    /// <summary>Where each account was last seen, in ABSOLUTE world metres: the hint the host's join reads to build
    /// a rejoining player's entity where they left rather than at the configured spawn (see the class doc, and
    /// <see cref="WorldPersistenceConfig.ResumeHintCapacity"/> for its bound). Recorded on save-on-leave and on a
    /// successful load-on-join apply. Exposed so a game can pre-warm it from its own store at boot, which is what
    /// extends the quiet rejoin across a process restart. Read and write it on the server thread.</summary>
    public ResumePositionCache ResumeHints => resumeHints;

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
        if (data is null)                                  // no save -> keep wherever the join built them (spawn, or this account's hint)
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
        // Past that guard the live state IS the truth, so it is both what gets saved and what the next join is
        // seeded from. Deliberately not recorded above it: a hint taken from never-restored state would seed the
        // rejoin at the default spawn and then take the restore teleport, which is the bug this exists to fix.
        resumeHints.Record(accountId, finalState.Position);
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
    // order, first hit wins. On failure the WHOLE record is copied verbatim to the quarantine key, the resume hint is
    // forgotten, the player is RESET to the host's configured spawn as a teleport, the guard clears and the baseline
    // is untouched (so that fresh state overwrites the bad primary next pass), and OnRecordQuarantined fires. On
    // success: position first (as a teleport only when it actually moves the player - see below), then the opaque
    // game blob (only when present and a hook is set), then advance the baseline to the loaded bytes, record the
    // resume hint, then clear the guard. Shared by Update and FlushAsync so both paths validate identically.
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
                // Reset to the configured spawn RATHER than simply declining to place the player. Before the join
                // seed, "not applied" meant the player kept the spawn the join built them at and quarantine needed
                // no placement at all. It does not mean that any more: a rejoin is built at the resume hint, which
                // no check here ever saw (config.Bounds is applied to the LOADED record, not to the hint), so
                // declining would leave a rejected record's player standing on an unvalidated position - and the
                // decode-failure and ValidateGameState triggers reach that state with no bounds divergence at all.
                // Forget the hint first, so a further rejoin cannot re-seed the position this record was rejected
                // for, and place as a genuine teleport: policy moved the player, and the client should cut.
                resumeHints.Forget(a.AccountId);
                if (server.TryGetConfiguredSpawn(a.Slot, out PlayerMoveState spawn))
                    server.SetPlayerState(a.Slot, spawn, teleport: true);
                loadsInFlight.TryRemove(a.AccountId, out _);   // quarantined, so the account is now free to dirty-save the fresh spawn over the bad primary
                OnRecordQuarantined?.Invoke(a.AccountId, failure);
                continue;                                      // NOT applied, baseline NOT advanced
            }

            // Placing a loaded player is a teleport only when it MOVES them. A rejoiner whose join was seeded from
            // the resume hint is already standing on the loaded position, and advancing the epoch there would
            // report a second teleport for a move of nothing - the half of the double-teleport the seed cannot fix
            // on its own (#642). Both sides are absolute world metres (TryGetPlayerState hands out absolute, and a
            // stored record is written absolute), so the comparison needs no frame conversion. A host that cannot
            // read the state back is treated as a move, which is the pre-17.37.0 behaviour.
            bool moved = !server.TryGetPlayerState(a.Slot, out PlayerMoveState live)
                || Vector3.Distance(live.Position, a.State.Position) > config.QuietRestoreDistance;
            server.SetPlayerState(a.Slot, a.State, teleport: moved);
            if (a.Game is { Length: > 0 } && config.ApplyGameState is { } apply)
                apply(new PlayerPersistenceContext(a.Slot, a.AccountId), a.Game);
            lastSaved[a.AccountId] = a.Raw;                 // loaded == clean baseline, set at apply time (never for a quarantined record)
            resumeHints.Record(a.AccountId, a.State.Position);   // the restored position is now the account's last known one
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
        // Loop rather than a single drain-then-await: DrainApplyQueue can itself Track a quarantine SaveAsync (a
        // loaded record that fails validation), and awaiting pending can complete a LoadOnJoinAsync that enqueues a
        // fresh applyQueue item. Either one leaves work this call has not yet awaited/applied, so keep going until a
        // drain finds nothing new to track - only then is the call actually quiescent, mirroring CellPersistence's
        // FlushAsync shape.
        while (true)
        {
            DrainApplyQueue();

            Task[] tasks;
            lock (pendingLock) { tasks = pending.ToArray(); pending.Clear(); }
            if (tasks.Length == 0) break;

            await AwaitAndObserve(tasks).ConfigureAwait(false);
        }
    }

    // Awaits every given task to completion, then observes it. Unlike a bare Task.WhenAll (which rethrows the
    // first fault and, having already cleared those tasks out of pending, would unwind FlushAsync's loop before
    // it reaches quiescence - the caller gets an exception instead of ever finding out the flush actually
    // landed) this surfaces EVERY faulted/canceled task through OnStoreError and never throws, so a store outage
    // during shutdown still lets the loop keep draining. A faulted save's account stays dirty and is retried on
    // the next pass. Mirrors CellPersistence.AwaitPendingAsync.
    private async Task AwaitAndObserve(Task[] tasks)
    {
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch { /* individual faults are observed + surfaced per-task below, not rethrown */ }
        List<Exception>? failures = null;
        foreach (Task t in tasks)
            if (t.IsFaulted || t.IsCanceled)
                (failures ??= new List<Exception>()).Add(t.Exception?.GetBaseException() ?? new TaskCanceledException());
        if (failures is not null)
            foreach (Exception ex in failures) OnStoreError?.Invoke(ex);
    }
}
