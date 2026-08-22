using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace KhaozEngine.WorldStore;

/// <summary>
/// Wires an <see cref="IWorldStore"/> into a server head's lifecycle so the world survives a restart. Backend-
/// agnostic AND record-agnostic (an <see cref="IWorldStore"/> plus a <see cref="PersistenceBinding{TState}"/>, which
/// is the only thing here that knows what a player's state looks like):
/// load-on-join (place the player at the saved position, or leave the join's own spawn if absent),
/// save-on-leave (persist the final state), and a periodic snapshot of players whose state changed since their
/// last save. Async loads are applied to the server on the server thread inside <see cref="Update"/> (never from
/// a background continuation), so a genuinely-async backend can't race the tick loop.
///
/// <para>A REJOIN is seeded rather than only restored. Every position this layer persists is also kept as an
/// in-process hint (<see cref="Hints"/>, installed on the host at construction through
/// <see cref="IPersistenceHost{TState}.SetPositionHintProvider"/>), and the host builds a known account's entity
/// there instead of at its configured spawn. That matters because the client decides whether a rejoin moved the
/// player by measuring the RESUME SNAPSHOT: serving the spawn first and restoring afterwards reported one teleport
/// for the spawn and a second for the restore, which is what made a reconnect rebuild a consumer's whole streaming
/// ring while the player stood still (#642). The seed is a hint and never the authority - the asynchronous load
/// still runs, and still applies the stored record over it - but when the two agree the restore now lands within
/// <see cref="PersistenceCoreConfig.QuietRestoreDistance"/> of where the player already is and is applied WITHOUT
/// advancing the teleport epoch, so the whole rejoin is quiet. The hints are memory-only: after a process restart
/// the first rejoin of each account falls back to the configured spawn and takes the restore teleport, unless the
/// game pre-warms <see cref="Hints"/> from its own store at boot.</para>
///
/// <para>While a load-on-join is still in flight, the account is guarded: the periodic dirty pass and save-on-leave
/// both skip it, so a save firing mid-load can't overwrite the stored record (position AND the durable game blob)
/// with the pre-restore default-spawn state and permanently erase progression. The guard clears once the loaded
/// record has been applied (or quarantined) on the server thread, or immediately if the load found no saved record.
/// Skipping the leave-save is intentional: the state was never applied, so the stored record - not the pre-restore
/// live state - is the truth worth keeping. A faulted store READ is different: the guard is deliberately LEFT set so
/// the intact stored record stays protected, and a later rejoin retries the read (outage semantics).</para>
///
/// <para>The guard is held by a SESSION, not by an account. Every join takes a monotonic token, the guard holds the
/// token of the account's current one, and a completed load whose token is not that one is DROPPED exactly as a
/// recycled seat's is (below), without clearing the guard. That matters because an account that leaves and rejoins
/// inside a single store read has TWO loads outstanding under one key. As a plain set, the first to land cleared the
/// guard for both, which reopened the save window while its sibling was still in the air, and then let that sibling
/// apply a record the live session had already moved on from - a yank backwards, and past
/// <see cref="PersistenceCoreConfig.QuietRestoreDistance"/> a teleport (#654). Neither the account nor the slot can
/// tell those two loads apart, since both are genuinely the same account on (usually) the same seat. This used to
/// supersede a SECOND concurrent session for one account the same way, which was a regression rather than a fix: one
/// account keying one record was never a shape two live players could share. The join gate settles that now
/// (<c>DuplicateSessionPolicy</c>, default KickOlder), ending the older session before admitting the
/// newer one, so what reaches this layer is an ordinary leave-then-join and never two live sessions (#662). A
/// rejoin deliberately issues a fresh read rather than adopting the one already in flight: the outstanding read may
/// already have completed, it was issued for the seat the previous session held, and its bytes are by then the older
/// of the two answers. The saving would have been one store read on a path that only opens during an outstanding
/// read.</para>
///
/// <para>A load-on-join also WAITS for the account's own outstanding writes, which is the other half of what the
/// join gate made routine. Store operations for one key are not ordered against each other, so the save-on-leave and
/// the rejoin's load issued from a single event drain race, and on any store whose write costs more than its read
/// the read wins: the newcomer was restored onto the record from BEFORE the leave, and the next periodic save then
/// wrote that rollback down as the truth. A kick is exactly that shape every time (#662), so every write this layer
/// issues is published under its key, and a join whose key carries one awaits it before it reads. A write that FAILS
/// still releases the join, which then reads whatever the store still holds - the same outage semantics a failed
/// save already had, and never a join stranded behind a dead store. What that buys is per-key ordering between this
/// layer's own writes and its own reads. It is not a distributed lock: a store also written by something else, or by
/// a second process running this layer, still needs that store's own ordering. Use a stable account id.</para>
///
/// <para>A TOKENLESS connection is not persisted at all by default. Both heads key one <c>guest:{slot}</c>, and a
/// slot is a seat the next connection inherits, so a record filed under that key named a chair and loaded onto
/// whoever sat down next, moving them to a stranger's last position (#647). So there is no load-on-join, no
/// save-on-leave, no periodic pass and no guard for one, and a guest is built on the host's configured spawn every
/// session. A game that runs tokenless by design opts back in with
/// <see cref="PersistenceCoreConfig.PersistGuests"/>, which files each guest under a durable key minted for that one
/// session and never under the seat - crash-safety within a session, not a guest's return, since nothing can present
/// the minted id again. Give players a connect token if returning to where they left matters.</para>
///
/// <para>The ACCOUNT, not the slot, is what a completed load is applied to. A slot number is only a seat: both heads
/// hand the lowest free one to the next connection, so an account that joins and drops while its load is still in
/// flight frees its slot immediately and the next player can be sitting in it by the time the record lands. The drain
/// therefore re-resolves the slot's current occupant (<see cref="IPersistenceHost{TState}.TryGetAccountId"/>) and DROPS a
/// record whose account no longer holds it, rather than writing one player's stored position, teleport and durable
/// blob onto another (#646). A drop is announced through <see cref="OnLoadApplyDropped"/> and a log line, never
/// silently, and it deliberately leaves the account's <c>loadsInFlight</c> guard SET: the account is gone, its stored
/// record was never applied, and the guard is exactly what keeps that record safe from being overwritten by live state
/// until a later rejoin retries the load. Two SUCCESSIVE tokenless guests on one recycled slot used to be
/// indistinguishable to this comparison, since both connections carry the key <c>guest:{slot}</c>. They no longer
/// reach it: a tokenless connection is not persisted under its seat at all (see above, #647).</para>
///
/// <para>Loaded records are validated on the server thread before they are applied, via
/// <see cref="PersistenceBinding{TState}.Validate"/> (the state's own shape: a play area, a facing in range), the
/// game's <see cref="PersistenceCoreConfig.ValidateGameState"/> verdict on its durable blob, and a decode guard (a
/// record the binding will not parse). A record that fails ANY check is quarantined WHOLE: its raw bytes are copied verbatim
/// to <c>{QuarantineKeyPrefix}{KeyPrefix}{accountId}</c> (default <c>quarantine:player:{accountId}</c>), the player is
/// NOT placed from it and is instead RESET to the host's configured spawn, as a fresh start and as a genuine teleport,
/// with its resume hint forgotten so a further rejoin cannot re-seed the rejected position. That reset is not
/// ceremony: the join seed means a rejoiner is already standing on the hint, which nothing here validated
/// (<see cref="PersistenceBinding{TState}.Validate"/> vets the loaded record, never the hint), so simply declining
/// to place
/// them would leave a rejected record's player on an unvalidated position. Then <see cref="OnRecordQuarantined"/>
/// fires on the server thread, and the clean baseline is deliberately NOT advanced to the bad record. Because the
/// baseline moves to the loaded bytes only on a SUCCESSFUL apply, a quarantined record is never marked clean, so the
/// next dirty pass overwrites the bad PRIMARY record with that fresh spawn state while the quarantine copy survives
/// for offline repair. A host that implements no <see cref="IPersistenceHost{TState}.TryGetConfiguredSpawn"/> keeps the
/// old shape (no placement), which is the right answer for it: with no seed installed there is nothing to undo.
/// This also fixes an undecodable RECORD, which previously faulted the load and left the guard set forever (progress
/// silently stopped persisting): it is now routed through quarantine, which clears the guard so persistence resumes.
/// A store READ fault is NOT quarantine - the record was never read, so the outage retry above still applies.</para>
///
/// <para>A game attaches durable per-player state (XP, inventory, quests) through
/// <see cref="PersistenceCoreConfig.CaptureGameState"/> / <see cref="PersistenceCoreConfig.ApplyGameState"/>: an
/// opaque blob that rides the SAME record, dirty comparison, interval save, flush-on-drain and load-on-join
/// thread-marshalling as position. The engine never interprets the blob; the game owns its format and migration.
/// Because the record is account-keyed (<c>player:{accountId}</c>), the blob is unaffected by cell handoff.</para>
/// </summary>
/// <typeparam name="TState">The head's authoritative per-player movement state, opaque to everything here except
/// through <see cref="PersistenceBinding{TState}"/>.</typeparam>
public sealed partial class StatePersistence<TState>
{
    private readonly IPersistenceHost<TState> server;
    private readonly IWorldStore store;
    // How this head's state becomes a record and back. The one thing about a player that this type does not know.
    private readonly PersistenceBinding<TState> binding;
    private readonly PersistenceCoreConfig config;

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
    // accountId -> the join token of the session whose load-on-join is outstanding. Written in OnPlayerJoined before
    // the async load starts, cleared only AFTER that session's loaded record is applied OR quarantined on the server
    // thread (DrainApplyQueue), or immediately when its load returns null (a brand-new player: no stored record to
    // protect). SaveDirtyPass and OnPlayerLeaving skip an account with an entry here, so a periodic save or a quick
    // leave can't overwrite the stored record (position AND the durable game blob) with pre-restore state - the
    // default spawn and a null blob - and permanently erase progression. A faulted store READ deliberately leaves the
    // guard set so the intact stored record stays protected (a later rejoin retries the load), mirroring
    // CellPersistence's loadsInFlight. An undecodable record is NOT a read fault - it read fine, it just won't parse -
    // so it clears the guard via quarantine, not via the outage path.
    //
    // It holds a TOKEN rather than a presence flag because a load belongs to one JOIN, not to an account (#654). An
    // account that leaves and rejoins while its store read is still outstanding has two loads in flight under one
    // key: as a set, the first to land cleared the guard for both, which reopened the save window while its sibling
    // was still in the air and then let that sibling write a superseded record over newer live state. Holding the
    // CURRENT session's token means a load can be told apart from its own predecessor, which nothing about the
    // account or the slot can do.
    private readonly ConcurrentDictionary<string, long> loadsInFlight = new();
    // key -> a task that completes once every store WRITE issued so far under that key has landed. The mirror image
    // of loadsInFlight, and it exists for the handover the join gate made routine: a kick runs the old session's
    // save-on-leave and the newcomer's load-on-join out of ONE event drain, and store operations for one key are not
    // ordered against each other, so on any store whose write costs more than its read the newcomer read the record
    // from BEFORE the leave and was restored onto it - a rollback the next periodic save then made permanent (#662).
    // A join whose key has an entry here awaits it before reading. Entries are written on the server thread (both
    // save points) and removed from the write's own continuation, so the removal takes the pair overload and can
    // only ever drop the entry it published.
    private readonly ConcurrentDictionary<string, Task> savesInFlight = new();
    // Hands out one token per join, monotonic within THIS instance, which is the same scope loadsInFlight is keyed
    // in, so a token identifies exactly one session of this layer. Two StatePersistence instances hand out the same
    // numbers and never compare them.
    private long nextJoinToken;
    // slot -> the durable key minted for the tokenless connection currently in that seat. Only ever populated when
    // config.PersistGuests is set. Without it a tokenless connection is not persisted at all and needs no key (#647).
    private readonly ConcurrentDictionary<int, string> guestKeys = new();
    // In-flight loads/saves, so FlushAsync can await them (tests + shutdown).
    private readonly object pendingLock = new();
    private readonly List<Task> pending = new();
    // Where each account was last seen, so a rejoin is BUILT there instead of at the configured spawn (see the
    // class doc). Server-thread only: written from OnPlayerLeaving and the apply drain, read from the host's join.
    private readonly PositionHintCache hints;
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

    /// <summary>Raised on the server thread from the apply drain when a completed load-on-join was DROPPED rather
    /// than applied: (accountId, slot). Two things drop a record, and both come down to the load having been read
    /// for a party that is no longer the one sitting there.
    /// <para>The SEAT moved on: both heads recycle a freed slot to the next connection, so an account that drops
    /// while its load is in flight can have a stranger sitting in its seat by the time the record arrives, and
    /// applying it there would move THAT player to this account's stored position and hand them its durable blob
    /// (#646).</para>
    /// <para>Or the SESSION did: an account that leaves and rejoins inside one store read has two loads outstanding
    /// under one key, and the one belonging to the session that ended carries what the store held before the live
    /// session even started, so applying it writes over everything that session has done since (#654).</para>
    /// <para>Nothing is written for a dropped record either way, and the drop never clears the guard itself. In the
    /// SEAT case the account is not connected under that slot any more, so its record stays guarded until it rejoins
    /// and that read clears it. In the SESSION case the live session's own load is either still in the air, and still
    /// guarding, or has already landed and applied, which is what cleared the guard: that account was restored, not
    /// left behind. The same drop is written to the log at <c>Info</c> under the
    /// <c>StatePersistence</c> category, naming the account, the slot, which of the two reasons it was and, for the
    /// session case, which of those two states the account is actually in, so a server that subscribes to nothing
    /// still records it.</para></summary>
    public event Action<string, int>? OnLoadApplyDropped;

    // A loaded record marshalled to the server thread for validation + apply. Raw is the exact stored bytes (copied
    // verbatim on quarantine). State/Game are the decoded position and blob (default/null when DecodeFailure is set).
    // DecodeFailure is non-null only when the stored bytes would not parse, which routes the item straight to
    // quarantine without ever touching State/Game.
    private readonly struct PendingApply
    {
        public PendingApply(int slot, string accountId, long token, byte[] raw, TState state, byte[]? game, string? decodeFailure)
        {
            Slot = slot;
            AccountId = accountId;
            Token = token;
            Raw = raw;
            State = state;
            Game = game;
            DecodeFailure = decodeFailure;
        }

        public int Slot { get; }
        public string AccountId { get; }
        /// <summary>The join this load was issued for. Compared against the account's CURRENT session at drain
        /// time, which is what tells a load apart from one its own account's earlier session left behind (#654).</summary>
        public long Token { get; }
        public byte[] Raw { get; }
        public TState State { get; }
        public byte[]? Game { get; }
        public string? DecodeFailure { get; }
    }

    /// <summary>Subscribes to the host's join/leave events and installs the rejoin seed, so the layer is live the
    /// moment it is constructed. Build it before the head can admit anyone: a join raised in between is a record
    /// this layer never loads.</summary>
    /// <param name="server">The head whose players are persisted.</param>
    /// <param name="store">Where records are written.</param>
    /// <param name="binding">How this head's state becomes a record, and what makes a loaded one unacceptable.</param>
    /// <param name="config">The machinery's tunables, or null for the defaults.</param>
    public StatePersistence(IPersistenceHost<TState> server, IWorldStore store, PersistenceBinding<TState> binding,
        PersistenceCoreConfig? config = null)
    {
        this.server = server ?? throw new ArgumentNullException(nameof(server));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.binding = binding ?? throw new ArgumentNullException(nameof(binding));
        this.config = config ?? new PersistenceCoreConfig();
        hints = new PositionHintCache(this.config.ResumeHintCapacity);
        server.PlayerJoined += OnPlayerJoined;
        server.PlayerLeaving += OnPlayerLeaving;
        // Install the join seed BEFORE any join can arrive. A game with its own account store installs its own
        // provider after constructing this layer, which replaces (rather than chains onto) the one wired here.
        server.SetPositionHintProvider(hints.TryGet);
    }

    /// <summary>Where each account was last seen, in the binding's own position space: the hint the host's join reads to build
    /// a rejoining player's entity where they left rather than at the configured spawn (see the class doc, and
    /// <see cref="PersistenceCoreConfig.ResumeHintCapacity"/> for its bound). Recorded on save-on-leave and on a
    /// successful load-on-join apply. Exposed so a game can pre-warm it from its own store at boot, which is what
    /// extends the quiet rejoin across a process restart. Read and write it on the server thread.</summary>
    public PositionHintCache Hints => hints;

    // The store key for a resolved persistence key (see TryResolveKey), which for any connection carrying a verified
    // subject is that account id.
    private string Key(string persistenceKey) => config.KeyPrefix + persistenceKey;

    private void Track(Task task)
    {
        lock (pendingLock) pending.Add(task);
    }

    /// <summary>Call once per server frame. Applies any completed load-on-join state (on this thread) and runs
    /// the periodic dirty snapshot when <see cref="PersistenceCoreConfig.SaveIntervalSeconds"/> has elapsed.</summary>
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
}
