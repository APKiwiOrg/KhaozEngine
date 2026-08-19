using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Diagnostics;
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
    /// Whether a TOKENLESS connection is persisted at all. Default FALSE, which is the answer #647 settled on: both
    /// heads key a connection with no verified subject <c>guest:{slot}</c>, and a slot is a seat the next connection
    /// inherits, so a record filed under it names a chair rather than a player and loaded onto whoever sat down
    /// next. Off, such a connection is not persisted in any direction: no load-on-join, no save-on-leave, no
    /// periodic pass, no in-flight guard, and the guest is built on the host's configured spawn every session.
    /// <para>Set it true only if a game runs tokenless BY DESIGN and still wants durable state. The key is then a
    /// durable <c>guest:{guid}</c> minted for that one session at join and NEVER the seat, so no guest can inherit
    /// another's record. Be clear about what that buys: the minted id is unreachable afterwards (nothing can present
    /// it again), so it is crash-safety within a session and an audit trail, never a guest's return. If returning
    /// players matter, give them a connect token instead - that is what a durable identity is for.</para>
    /// </summary>
    public bool PersistGuests { get; init; }

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
/// the intact stored record stays protected, and a later rejoin retries the read (outage semantics).</para>
///
/// <para>The guard is held by a SESSION, not by an account. Every join takes a monotonic token, the guard holds the
/// token of the account's current one, and a completed load whose token is not that one is DROPPED exactly as a
/// recycled seat's is (below), without clearing the guard. That matters because an account that leaves and rejoins
/// inside a single store read has TWO loads outstanding under one key. As a plain set, the first to land cleared the
/// guard for both, which reopened the save window while its sibling was still in the air, and then let that sibling
/// apply a record the live session had already moved on from - a yank backwards, and past
/// <see cref="WorldPersistenceConfig.QuietRestoreDistance"/> a teleport (#654). Neither the account nor the slot can
/// tell those two loads apart, since both are genuinely the same account on (usually) the same seat. This used to
/// supersede a SECOND concurrent session for one account the same way, which was a regression rather than a fix: one
/// account keying one record was never a shape two live players could share. The join gate settles that now
/// (<see cref="Netcode.DuplicateSessionPolicy"/>, default KickOlder), ending the older session before admitting the
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
/// <see cref="WorldPersistenceConfig.PersistGuests"/>, which files each guest under a durable key minted for that one
/// session and never under the seat - crash-safety within a session, not a guest's return, since nothing can present
/// the minted id again. Give players a connect token if returning to where they left matters.</para>
///
/// <para>The ACCOUNT, not the slot, is what a completed load is applied to. A slot number is only a seat: both heads
/// hand the lowest free one to the next connection, so an account that joins and drops while its load is still in
/// flight frees its slot immediately and the next player can be sitting in it by the time the record lands. The drain
/// therefore re-resolves the slot's current occupant (<see cref="IWorldPersistenceHost.TryGetAccountId"/>) and DROPS a
/// record whose account no longer holds it, rather than writing one player's stored position, teleport and durable
/// blob onto another (#646). A drop is announced through <see cref="OnLoadApplyDropped"/> and a log line, never
/// silently, and it deliberately leaves the account's <c>loadsInFlight</c> guard SET: the account is gone, its stored
/// record was never applied, and the guard is exactly what keeps that record safe from being overwritten by live state
/// until a later rejoin retries the load. Two SUCCESSIVE tokenless guests on one recycled slot used to be
/// indistinguishable to this comparison, since both connections carry the key <c>guest:{slot}</c>. They no longer
/// reach it: a tokenless connection is not persisted under its seat at all (see above, #647).</para>
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
    // Resolved once per type, ambient: it follows Log.Configure rather than pinning whatever manager happened to be
    // configured when this type was first touched (#616).
    private static readonly ILogger Log = Diagnostics.Log.Get("WorldPersistence");

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
    // in, so a token identifies exactly one session of this layer. Two WorldPersistence instances hand out the same
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
    /// left behind. The same drop is written to the log at <see cref="LogLevel.Info"/> under the
    /// <c>WorldPersistence</c> category, naming the account, the slot, which of the two reasons it was and, for the
    /// session case, which of those two states the account is actually in, so a server that subscribes to nothing
    /// still records it.</para></summary>
    public event Action<string, int>? OnLoadApplyDropped;

    // A loaded record marshalled to the server thread for validation + apply. Raw is the exact stored bytes (copied
    // verbatim on quarantine). State/Game are the decoded position and blob (default/null when DecodeFailure is set).
    // DecodeFailure is non-null only when the stored bytes would not parse, which routes the item straight to
    // quarantine without ever touching State/Game.
    private readonly struct PendingApply
    {
        public PendingApply(int slot, string accountId, long token, byte[] raw, PlayerMoveState state, byte[]? game, string? decodeFailure)
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

    // The store key for a resolved persistence key (see TryResolveKey), which for any connection carrying a verified
    // subject is that account id.
    private string Key(string persistenceKey) => config.KeyPrefix + persistenceKey;

    private void Track(Task task)
    {
        lock (pendingLock) pending.Add(task);
    }

    private void OnPlayerJoined(int slot, string accountId)
    {
        if (ResumePositionCache.IsGuestAccount(accountId))
        {
            // A tokenless connection is handed to us as guest:{slot}, which names the chair and not the person
            // (#647). It is never a store key. Off (the default), nothing about this connection is persisted at all,
            // which is also why it needs no guard: there is no stored record to protect. On, the seat key is swapped
            // for one minted here and unique to THIS session, and no load is issued for it either - a key created
            // this instant cannot have a stored record waiting under it.
            if (config.PersistGuests)
                guestKeys[slot] = ResumePositionCache.GuestAccountPrefix + Guid.NewGuid().ToString("N");
            return;
        }
        long token = Interlocked.Increment(ref nextJoinToken);
        loadsInFlight[accountId] = token;                   // guard the account until THIS session's record applies (or its load returns null)
        // Read the account's outstanding write, if any, on the SERVER THREAD, before the load task starts: the
        // save-on-leave that has to be waited for was published from this same drain, one event earlier.
        savesInFlight.TryGetValue(accountId, out Task? outstandingSave);
        Track(LoadOnJoinAsync(slot, accountId, token, outstandingSave));
    }

    private async Task LoadOnJoinAsync(int slot, string accountId, long token, Task? outstandingSave)
    {
        if (outstandingSave is not null)
        {
            // A write for this key is still in the air - on the kick path it is the session this one just displaced,
            // saving where the player actually was. Reading now would hand this session whatever the store held
            // BEFORE that write, restore the player onto it, and let the next periodic pass write the rollback back
            // down as the truth. A FAILED write must not strand the join: log it and read anyway, which yields the
            // older record and is the same outage shape a failed save already has.
            try { await outstandingSave.ConfigureAwait(false); }
            catch (Exception ex)
            {
                Log.Warn($"the save outstanding for account '{accountId}' failed, so its load-on-join on slot {slot} "
                       + "reads whatever the store still holds, which may predate that session's last state.", ex);
            }
        }
        byte[]? data = await store.LoadAsync(Key(accountId)).ConfigureAwait(false);
        if (data is null)                                  // no save -> keep wherever the join built them (spawn, or this account's hint)
        {
            ClearGuard(accountId, token);                  // brand-new player: nothing stored to clobber, drop the guard now
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
            applyQueue.Enqueue(new PendingApply(slot, accountId, token, data, default, null, $"undecodable record: {ex.Message}"));
            return;                                        // guard stays set until the drain quarantines and clears it
        }
        applyQueue.Enqueue(new PendingApply(slot, accountId, token, data, record.ToState(), record.Game, null));   // validated + guard cleared in DrainApplyQueue
    }

    // Drops the guard only when it is still the one THIS session put there. A load left over from a superseded
    // session must never reopen the window its successor's load is holding open (#654), and the pair overload is what
    // makes that mechanical rather than a rule every call site has to remember: a stale token matches nothing and
    // removes nothing.
    private void ClearGuard(string accountId, long token) =>
        loadsInFlight.TryRemove(new KeyValuePair<string, long>(accountId, token));

    // The key this layer files a connection's record under. For a connection with a verified subject that IS the
    // account id. A TOKENLESS one arrives as guest:{slot} and a slot is a seat the next connection inherits, so that
    // key names a chair and a record filed under it lands on whoever sits down next (#647): it is therefore never a
    // store key. With config.PersistGuests off this answers false and the connection is not persisted, and with it on
    // the answer is the durable key minted for this session at join.
    private bool TryResolveKey(int slot, string accountId, out string key)
    {
        if (!ResumePositionCache.IsGuestAccount(accountId)) { key = accountId; return true; }
        if (config.PersistGuests && guestKeys.TryGetValue(slot, out string? minted)) { key = minted; return true; }
        key = string.Empty;
        return false;
    }

    // Captures the game blob (on the server thread - the caller is on the server thread) and encodes the full record.
    // The context carries the RESOLVED key (TryResolveKey), never the id the head handed us, because
    // PlayerPersistenceContext.AccountId is documented as the durable id the record is keyed by and the game reads it
    // as one. The two are the same string for every connection with a verified subject. They diverge only for a
    // tokenless one on a server that set PersistGuests, where the head's id is the seat guest:{slot} and the key is
    // the durable id minted for this session, so passing the head's would hand the game back the seat identity #647
    // exists to keep out of the store.
    private byte[] BuildRecordBytes(int slot, string key, in PlayerMoveState state)
    {
        byte[]? game = config.CaptureGameState?.Invoke(new PlayerPersistenceContext(slot, key));
        return PlayerRecord.From(state, game).Encode();
    }

    private void OnPlayerLeaving(int slot, string accountId, PlayerMoveState finalState)
    {
        if (!TryResolveKey(slot, accountId, out string key)) return;   // a tokenless connection this layer does not persist (#647)
        if (loadsInFlight.ContainsKey(key)) return;         // load still outstanding: the stored record was never applied, so IT - not this pre-restore live state - is the truth worth keeping
        // Past that guard the live state IS the truth, so it is both what gets saved and what the next join is
        // seeded from. Deliberately not recorded above it: a hint taken from never-restored state would seed the
        // rejoin at the default spawn and then take the restore teleport, which is the bug this exists to fix. The
        // hint is filed under the id the HOST will ask with at the next join, which for a guest key the cache
        // refuses outright - correctly, since nothing can present a minted guest id again.
        resumeHints.Record(accountId, finalState.Position);
        Task save = SaveIfDirtyAsync(key, BuildRecordBytes(slot, key, finalState));
        if (ResumePositionCache.IsGuestAccount(key))
        {
            Track(RetireGuestSessionAsync(slot, key, save));   // a minted guest key is never loaded, so nothing waits on it
            return;
        }
        Track(save);
        Track(ReleaseWhenSavedAsync(key, PublishSave(key, save)));   // the rejoin's load waits behind this write
    }

    // Registers an outstanding write under one key and hands back the entry a join will await. Chains onto whatever
    // was already outstanding for that key rather than replacing it, so the entry always covers EVERY write issued
    // so far under it: a periodic batch and a save-on-leave can genuinely overlap for one account, and a join that
    // awaited only the newer of the two would still be reading while the older was in the air. Server thread only.
    private Task PublishSave(string key, Task save)
    {
        Task tail = savesInFlight.TryGetValue(key, out Task? prev) && !prev.IsCompleted
            ? Task.WhenAll(prev, save)
            : save;
        savesInFlight[key] = tail;
        return tail;
    }

    // Releases a published entry once its write has landed. Faults are swallowed HERE on purpose: the write itself is
    // tracked separately and surfaces through OnStoreError, so rethrowing would report one store outage twice. This
    // task's only job is to stop the key being guarded, which a failed write must do just as a successful one does.
    private async Task ReleaseWhenSavedAsync(string key, Task tail)
    {
        try { await tail.ConfigureAwait(false); }
        catch { /* already observed and surfaced by whoever tracked the underlying write */ }
        savesInFlight.TryRemove(new KeyValuePair<string, Task>(key, tail));
    }

    // A minted guest key belongs to one session and is unreachable once that session ends, so after its final save
    // there is nothing left to compare a future record against or to resolve the seat to. Drop both entries rather
    // than leak one of each per tokenless connection on a server that opted in. The pair overload is what makes this
    // safe against a successor that has already taken the seat: it removes only what THIS session put there.
    private async Task RetireGuestSessionAsync(int slot, string key, Task save)
    {
        try { await save.ConfigureAwait(false); }
        finally
        {
            lastSaved.TryRemove(key, out _);
            guestKeys.TryRemove(new KeyValuePair<int, string>(slot, key));
        }
    }

    private async Task SaveIfDirtyAsync(string key, byte[] data)
    {
        if (lastSaved.TryGetValue(key, out byte[]? prev) && prev.AsSpan().SequenceEqual(data))
            return;                                        // unchanged since last save
        await store.SaveAsync(Key(key), data).ConfigureAwait(false);
        lastSaved[key] = data;
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

    // Re-resolves identity, then validates, then applies (or quarantines) loaded records on the server thread. A
    // record whose slot has been recycled to another account is dropped outright (see the loop below and #646).
    // Past that, a record fails validation if it
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
            // SESSION first, because it is the narrower of the two identities: an account can be sitting in the seat
            // and still not be the party this record was read for. loadsInFlight holds the token of the account's
            // CURRENT join, so a load issued by a session that has since ended (a leave and rejoin inside one store
            // read) fails to match, and a load whose account has no guard at all is stale by construction - the only
            // thing that clears a guard is that same session's own load landing. Either way the record is dropped:
            // it carries what the store held before the live session started, so applying it writes over everything
            // that session has done since, and clearing the guard on the way out would reopen the save window its
            // sibling load is still holding (#654). The guard is deliberately left exactly as it is.
            bool guarded = loadsInFlight.TryGetValue(a.AccountId, out long current);
            if (!guarded || current != a.Token)
            {
                // Name the state actually found, the way the seat check below does. The two halves reach here for
                // different reasons and leave the account in different places: a live session's own load is still
                // outstanding and still guarding the record, or there is no guard left at all because that load has
                // already landed (or the account has since left). Reporting the first for both read as a guarantee
                // in the landing-last case, where the guard is long gone by the time the stale load arrives.
                Log.Info($"load-on-join for account '{a.AccountId}' dropped: it was read on slot {a.Slot} for an "
                       + "earlier session that has since been superseded, so the stored record was not applied over "
                       + "newer live state, and "
                       + (guarded
                           ? "the current session's own load is still outstanding and still guards the account."
                           : "the account carries no guard any more, its current session's load having already landed."));
                OnLoadApplyDropped?.Invoke(a.AccountId, a.Slot);
                continue;
            }

            // Then the SEAT. PendingApply carries the slot the account joined on, and
            // that slot is a seat rather than a name: the SlotAllocator hands the lowest free one to the next
            // connection, so a leave during the store read frees it and the load can land on a stranger. Re-resolve
            // the seat's current occupant and drop the record when it is not this account's any more (#646). It has
            // to run BEFORE validation because the quarantine path PLACES the slot at the configured spawn, which on
            // a recycled slot would teleport the new occupant for a record that was never theirs. A bad record
            // belonging to a departed account simply stays in the store, un-quarantined, and is re-read (and then
            // quarantined) on that account's next join, which is the same shape a store outage already has.
            bool held = server.TryGetAccountId(a.Slot, out string occupant);
            if (!held || !string.Equals(occupant, a.AccountId, StringComparison.Ordinal))
            {
                // Deliberately NOT clearing loadsInFlight: the record was never applied, so it is still the truth for
                // that account and the guard is what stops live state overwriting it. The account is not connected
                // under this slot any more, so the guard costs nothing until it rejoins and the retry clears it.
                Log.Info($"load-on-join for account '{a.AccountId}' dropped: slot {a.Slot} now holds "
                       + (held ? $"account '{occupant}'" : "no player")
                       + ", so the stored record was not applied and stays guarded for that account's next join.");
                OnLoadApplyDropped?.Invoke(a.AccountId, a.Slot);
                continue;
            }

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
                ClearGuard(a.AccountId, a.Token);              // quarantined, so the account is now free to dirty-save the fresh spawn over the bad primary
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
            ClearGuard(a.AccountId, a.Token);              // restore applied, the account is now safe to dirty-save
        }
    }

    /// <summary>Saves every joined player whose state changed since its last save.</summary>
    public void SaveDirtyPass()
    {
        List<(string key, byte[] data)>? dirty = null;
        foreach (int slot in new List<int>(server.JoinedSlots))
            if (server.TryGetAccountId(slot, out string accountId) &&
                TryResolveKey(slot, accountId, out string key) &&   // a tokenless connection this layer does not persist (#647)
                !loadsInFlight.ContainsKey(key) &&             // load outstanding: skip so this pass can't overwrite the stored record with pre-restore state
                server.TryGetPlayerState(slot, out PlayerMoveState state))
            {
                byte[] data = BuildRecordBytes(slot, key, state);
                if (lastSaved.TryGetValue(key, out byte[]? prev) && prev.AsSpan().SequenceEqual(data))
                    continue;                                    // unchanged since last save
                (dirty ??= new List<(string, byte[])>()).Add((key, data));
            }
        if (dirty is null) return;
        Task batch = SaveManyDirtyAsync(dirty);
        Track(batch);
        // The batch is an outstanding write for every account in it, so a rejoin landing while it is in the air waits
        // behind it exactly as it waits behind a save-on-leave.
        foreach ((string key, byte[] _) in dirty) Track(ReleaseWhenSavedAsync(key, PublishSave(key, batch)));
    }

    // Batches every dirty account's record into one store round trip (one SaveManyAsync call instead of N
    // SaveAsync calls). lastSaved is advanced per account only AFTER the whole batch lands, so a faulted/canceled
    // batch leaves every account in it dirty for the next pass - the same "never mark a record clean before it is
    // actually saved" guarantee the old per-account SaveIfDirtyAsync gave, just at batch grain: one failed round
    // trip means the whole pass retries next interval, not only the one record that actually caused the fault.
    private async Task SaveManyDirtyAsync(List<(string key, byte[] data)> dirty)
    {
        var items = new List<(string Key, byte[] Data)>(dirty.Count);
        foreach ((string key, byte[] data) in dirty) items.Add((Key(key), data));
        await store.SaveManyAsync(items).ConfigureAwait(false);
        foreach ((string key, byte[] data) in dirty) lastSaved[key] = data;
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
