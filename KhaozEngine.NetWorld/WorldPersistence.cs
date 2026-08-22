using System;
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
/// Wires an <see cref="IWorldStore"/> into the <see cref="WorldServer"/> lifecycle so the world survives a restart:
/// load-on-join, save-on-leave, and a periodic snapshot of players whose state changed since their last save.
///
/// <para>This is the FLOAT binding of <see cref="StatePersistence{TState}"/>, and everything subtle lives there:
/// the save interval, the dirty comparison, the per-session load guard, the per-key write ordering a rejoin waits
/// behind, quarantine, the guest policy and the rejoin hints are all the shared core, documented once on that type.
/// Read it for the WHY behind any of those behaviours. What this type supplies is the handful of things that are
/// <see cref="PlayerMoveState"/>-shaped: a <see cref="PlayerRecord"/> is how a state becomes bytes, a position is a
/// <see cref="System.Numerics.Vector3"/> of world metres, and <see cref="WorldPersistenceConfig.Bounds"/> is what
/// makes a loaded position unacceptable.</para>
///
/// <para>The split is a refactor and nothing else: the keys, the record JSON, the save cadence, the quarantine
/// decisions, the events and their order, and the log lines are all exactly what they were before the core became
/// generic. The core has no logger of its own (<c>KhaozEngine.WorldStore</c> is dependency-free), so this type wires
/// its diagnostic sink to the <c>WorldPersistence</c> logging category the lines have always carried.</para>
///
/// <para>A game attaches durable per-player state (XP, inventory, quests) through
/// <see cref="WorldPersistenceConfig.CaptureGameState"/> / <see cref="WorldPersistenceConfig.ApplyGameState"/>: an
/// opaque blob that rides the SAME record, dirty comparison, interval save, flush-on-drain and load-on-join
/// thread-marshalling as position. The engine never interprets the blob. Because the record is account-keyed
/// (<c>player:{accountId}</c>), the blob is unaffected by cell handoff.</para>
/// </summary>
public sealed class WorldPersistence
{
    // Resolved once per type, ambient: it follows Log.Configure rather than pinning whatever manager happened to be
    // configured when this type was first touched (#616).
    private static readonly ILogger Log = Diagnostics.Log.Get("WorldPersistence");

    private readonly StatePersistence<PlayerMoveState> core;
    private readonly ResumePositionCache hints;

    /// <summary>Subscribes to the host's join/leave events and installs the rejoin seed, so the layer is live the
    /// moment it is constructed. A game installing its own resume provider must do it AFTER this.</summary>
    public WorldPersistence(IWorldPersistenceHost server, IWorldStore store, WorldPersistenceConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        WorldPersistenceConfig c = config ?? new WorldPersistenceConfig();

        // The three game hooks keep their PlayerPersistenceContext shape out here, where the games are already
        // written against it, and reach the core as plain (slot, key, blob) delegates. Bridged through a pattern
        // local rather than a null-conditional so the captured delegate is provably non-null inside the lambda.
        Func<int, string, byte[]?>? capture = null;
        if (c.CaptureGameState is { } captureHook)
            capture = (slot, key) => captureHook(new PlayerPersistenceContext(slot, key));
        Action<int, string, byte[]?>? apply = null;
        if (c.ApplyGameState is { } applyHook)
            apply = (slot, accountId, blob) => applyHook(new PlayerPersistenceContext(slot, accountId), blob);
        Func<int, string, byte[]?, string?>? validate = null;
        if (c.ValidateGameState is { } validateHook)
            validate = (slot, accountId, blob) =>
            {
                PlayerGameStateVerdict verdict = validateHook(new PlayerPersistenceContext(slot, accountId), blob);
                return verdict.IsValid ? null : verdict.Reason ?? "invalid";
            };

        core = new StatePersistence<PlayerMoveState>(server, store, Binding(c), new PersistenceCoreConfig
        {
            SaveIntervalSeconds = c.SaveIntervalSeconds,
            KeyPrefix = c.KeyPrefix,
            QuarantineKeyPrefix = c.QuarantineKeyPrefix,
            PersistGuests = c.PersistGuests,
            ResumeHintCapacity = c.ResumeHintCapacity,
            QuietRestoreDistance = c.QuietRestoreDistance,
            CaptureGameState = capture,
            ApplyGameState = apply,
            ValidateGameState = validate,
            // The core carries no logger, so the lines it used to write itself come back out here and go to the same
            // category, at the same level, with the same text. A null exception is the Info case.
            Diagnostic = (message, ex) => { if (ex is null) Log.Info(message); else Log.Warn(message, ex); },
        });
        hints = new ResumePositionCache(core.Hints);
    }

    // What the core cannot know about a float player: where a state is, how it encodes, and what puts a loaded
    // position out of the play area. The bounds message is the one the quarantine event has always carried.
    private static PersistenceBinding<PlayerMoveState> Binding(WorldPersistenceConfig c) => new(
        PositionOf: s => s.Position,
        Encode: (s, game) => PlayerRecord.From(s, game).Encode(),
        Decode: (byte[] data, out PlayerMoveState state, out byte[]? game) =>
        {
            // Throws on bytes that will not parse, which the core catches and routes to quarantine exactly as it did
            // when this call was inline. There is no second "false" answer to give.
            PlayerRecord record = PlayerRecord.Decode(data);
            state = record.ToState();
            game = record.Game;
            return true;
        },
        Validate: (s, _) => c.Bounds is { } b && !b.Contains(s.Position.X, s.Position.Z)
            ? $"position ({s.Position.X}, {s.Position.Z}) outside world bounds"
            : null,
        WithPosition: (p, s) => s with { Position = p });

    /// <summary>Where each account was last seen, in ABSOLUTE world metres: the hint the host's join reads to build
    /// a rejoining player's entity where they left rather than at the configured spawn (see the class doc, and
    /// <see cref="WorldPersistenceConfig.ResumeHintCapacity"/> for its bound). Recorded on save-on-leave and on a
    /// successful load-on-join apply. Exposed so a game can pre-warm it from its own store at boot, which is what
    /// extends the quiet rejoin across a process restart. Read and write it on the server thread.</summary>
    public ResumePositionCache ResumeHints => hints;

    /// <inheritdoc cref="StatePersistence{TState}.OnStoreError"/>
    public event Action<Exception>? OnStoreError
    {
        add => core.OnStoreError += value;
        remove => core.OnStoreError -= value;
    }

    /// <inheritdoc cref="StatePersistence{TState}.OnRecordQuarantined"/>
    public event Action<string, string>? OnRecordQuarantined
    {
        add => core.OnRecordQuarantined += value;
        remove => core.OnRecordQuarantined -= value;
    }

    /// <inheritdoc cref="StatePersistence{TState}.OnLoadApplyDropped"/>
    public event Action<string, int>? OnLoadApplyDropped
    {
        add => core.OnLoadApplyDropped += value;
        remove => core.OnLoadApplyDropped -= value;
    }

    /// <summary>Call once per server frame. Applies any completed load-on-join state (on this thread) and runs
    /// the periodic dirty snapshot when <see cref="WorldPersistenceConfig.SaveIntervalSeconds"/> has elapsed.</summary>
    public void Update(float dt) => core.Update(dt);

    /// <summary>Saves every joined player whose state changed since its last save.</summary>
    public void SaveDirtyPass() => core.SaveDirtyPass();

    /// <summary>Awaits all in-flight loads/saves, then applies any pending loaded state. Call on shutdown (or in
    /// tests) to reach a quiescent, fully-persisted point. Invoke from the server thread / when the loop is idle.</summary>
    public Task FlushAsync() => core.FlushAsync();
}
