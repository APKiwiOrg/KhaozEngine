using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using KhaozEngine.Simulation;

namespace KhaozEngine.NetWorld;

/// <summary>Tunables for <see cref="ShardedWorldServer"/>.</summary>
public sealed class ShardedWorldServerConfig
{
    /// <summary>Fixed server tick, seconds.</summary>
    public float TickSeconds { get; init; } = 1f / 30f;
    /// <summary>World-grid cell edge length (world units). Align to the terrain/streaming chunk grid.</summary>
    public float CellSize { get; init; } = 60f;
    /// <summary>Border-overlap distance for ghosting. Must be &gt;= <see cref="InterestRadius"/>.</summary>
    public float OverlapMargin { get; init; } = 24f;
    /// <summary>Per-client area-of-interest radius (world units).</summary>
    public float InterestRadius { get; init; } = 24f;
    /// <summary>Maximum concurrent players.</summary>
    public int MaxPlayers { get; init; } = 64;
    /// <summary>Per-slot spawn position (XZ used; Y is ground-clamped). Default spreads players along +X near origin.</summary>
    public Func<int, Vector3>? SpawnPosition { get; init; }

    /// <summary>When true (the default), each <see cref="ShardedWorldServer.Tick"/> calls
    /// <see cref="World.AdvanceTick"/> once on every cell's world, clearing the per-tick change-tracking sets and
    /// event buffer so they don't accumulate on a long-running server (one entry per <c>Set</c>/<c>Despawn</c> in
    /// the owning cell, never reclaimed otherwise). The authoritative movement path doesn't consume them. Set false
    /// ONLY if your own systems read a cell's change-tracking/events and you advance each cell's tick yourself.</summary>
    public bool AdvanceWorldTick { get; init; } = true;

    /// <summary>Opt-in server-side anti-cheat / input-hardening knobs (rate limiting, movement-correction anomaly).
    /// All off by default, so behaviour is unchanged until a consumer tightens it.</summary>
    public AntiCheatConfig AntiCheat { get; init; } = new();

    /// <summary>Server-owned destination for a client <see cref="WorldClient.RequestSelfRescue"/> ("unstuck" /
    /// return-to-spawn): given the requesting player, returns the safe world position to teleport it to (across cells,
    /// reusing the admin path - position set, vertical velocity zeroed). The client never supplies a position - the
    /// server alone decides - so a hostile client can't teleport anywhere. A fixed point is just <c>_ =&gt; point</c>.
    /// <b>Null (the default) =&gt; the feature is OFF</b>: a self-rescue request is ignored.</summary>
    public Func<PlayerRef, Vector3>? SelfRescueDestination { get; init; }

    /// <summary>Per-player minimum interval (seconds) between honored self-rescues; further requests inside the window
    /// are dropped. Default 5s. Ignored when <see cref="SelfRescueDestination"/> is null.</summary>
    public float SelfRescueCooldownSeconds { get; init; } = 5f;

    /// <summary>Per-player input-backlog catch-up cap, in commands (ticks); mirrors
    /// <see cref="WorldServerConfig.MaxInputBacklog"/>. When a client's queued move backlog grows deeper than this the
    /// server skips stale moves and applies only the most recent, so a reconnect flush / lag burst can't freeze a
    /// player under minutes-old input on rejoin. Default 8 (~0.27s at 30Hz); 0 disables (pre-8.8.0 one-per-tick).</summary>
    public int MaxInputBacklog { get; init; } = 8;

    /// <summary>Serve each client per-tick home-cell area-of-interest DELTAS (only what changed since that client's
    /// acknowledged baseline) instead of a full snapshot every tick. Default true. Mirrors
    /// <see cref="WorldServerConfig.DeltaReplication"/>: a client opts in with the
    /// <see cref="MoveProtocol.ClientControlKind.DeltaCapable"/> hello; older clients keep getting full snapshots, so
    /// client and server upgrade independently. The delta baseline is keyed by <see cref="NetId"/>, so a boundary
    /// crossing (home-cell change) stays a component delta, never a despawn+respawn. Set false to force full snapshots.</summary>
    public bool DeltaReplication { get; init; } = true;

    /// <summary>Maximum payload size (bytes) accepted on a client-to-server game message
    /// (<see cref="WorldClient.SendGameMessage"/>); mirrors <see cref="WorldServerConfig.MaxGameMessageBytes"/>. A
    /// larger payload is DROPPED (never dispatched to <see cref="ShardedWorldServer.OnGameMessage"/>) and flagged
    /// <see cref="SuspiciousReason.OversizedMessage"/>. The payload is opaque bytes to the engine. Default 1024. The
    /// rate limiter runs in front of this.</summary>
    public int MaxGameMessageBytes { get; init; } = 1024;
}

/// <summary>
/// Multi-cell authoritative movement server: the single-<see cref="World"/> <see cref="WorldServer"/> stack run
/// across a <see cref="ShardHost"/> grid of cells, so the world scales to many players / a large area without one
/// giant world, while the <see cref="WorldClient"/> and <see cref="MoveProtocol"/> stay unchanged. Each tick it
/// routes every client's <see cref="MoveCommand"/> to the cell that <b>owns</b> its player, steps every cell's
/// <see cref="PlayerMovementSystem"/> via <see cref="ShardHost.Tick"/> (ground-clamped, scheduler-fanned),
/// transfers authority for boundary crossers exactly-once (<see cref="ShardHost.ProcessHandoffs"/>), refreshes
/// border ghosts (<see cref="ShardHost.SyncGhosts"/>), then serves each client its single <b>home-cell</b>
/// area-of-interest snapshot (owned + ghosts) framed with the existing <c>[localNetId][ack]</c> header. A player's
/// <see cref="NetId"/> is stable across handoff, so the client's replication view + prediction continue without a
/// respawn. Headless, transport-injected. Persistence is the shipped <see cref="WorldPersistence"/> via
/// <see cref="IWorldPersistenceHost"/>, player-keyed across cells.
/// </summary>
public sealed class ShardedWorldServer : IWorldPersistenceHost, IAdminControllable, ICellPersistenceHost
{
    private readonly ShardedWorldServerConfig config;
    private readonly ReplicationRegistry registry;
    private readonly ShardHost host;
    private readonly NetServer net;
    private readonly RemoteCommandQueue<MoveCommand> commands;
    private readonly PlayerMovementSystem movement;
    private readonly PlayerMoveSimulator spawnClamp;

    // Per-client AoI delta encoder (null when DeltaReplication is off). NetId-keyed so a home-cell change on a
    // boundary crossing reads as a component delta. A slot is served deltas only once it advertised DeltaCapable.
    private readonly AoiDeltaReplicator? deltaReplicator;
    private readonly HashSet<int> deltaCapableSlots = new();

    // Monotonic per-tick serve epoch: bumped once before each tick's client-serving pass and passed to every
    // HomeInterest / SnapshotForClient call, so each home cell rebuilds its interest grid at most once per tick
    // (shared across every client homed there) instead of once per client. The world is fully settled by the time
    // the serve pass runs (movement, handoffs, ghost sync, admin drains are all done), so one rebuild per tick is
    // both correct and complete.
    private long interestServeEpoch;

    private readonly Dictionary<int, long> netIdBySlot = new();
    private readonly Dictionary<int, int> lastAckBySlot = new();
    private readonly Dictionary<int, string> accountIdBySlot = new();
    private readonly Dictionary<int, RateLimiter> rateBySlot = new();
    private readonly Dictionary<int, int> correctionStreakBySlot = new();
    // Self-rescue cooldown: a monotonic server-time accumulator (advanced one dt per Tick) plus, per slot, the
    // earliest clock time the next self-rescue is honored. No wall-clock; double for long-uptime resolution. Entries
    // are seeded only when a rescue is honored and cleared on leave.
    private double selfRescueClock;
    private readonly Dictionary<int, double> selfRescueReadyAt = new();
    private readonly HashSet<CellCoord> wiredCells = new();
    // Per-tick scratch: the pre-step state of each owned player whose command we routed this frame, so we can
    // measure the authoritative correction after the cells step. Reused across ticks (single-threaded orchestration).
    private readonly List<(int slot, PlayerMoveState prev, MoveCommand cmd)> correctionScratch = new();
    private readonly DrainController drain = new();
    private readonly AdminCommandBuffer admin = new();
    private readonly IBanStore? banStore;
    private readonly MoveTuning tuning;
    // The single NetId allocator (node 0 for this single-process server) player joins, SpawnEntity, and restored cells
    // all draw from / bound, so ids never collide. Replaces the pre-10.0.0 raw ++int; see NetIdAllocator for the scheme.
    private readonly NetIdAllocator allocator = new();

    public ShardedWorldServer(INetTransport transport, ShardedWorldServerConfig config,
        Func<float, float, float> groundHeight, MoveTuning tuning, Func<float, float, Vector3>? groundNormal = null,
        WorldBounds? bounds = null, IPhysicsWorld? physics = null,
        IConnectionAuthenticator? authenticator = null, IBanStore? banStore = null,
        ReplicationRegistry? registry = null, Func<float, float, float, MovementMedium>? medium = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        if (groundHeight is null) throw new ArgumentNullException(nameof(groundHeight));
        if (config.InterestRadius > config.OverlapMargin)
            throw new ArgumentException(
                $"InterestRadius {config.InterestRadius} must be <= OverlapMargin {config.OverlapMargin} so the home cell can hold the full AoI as ghosts.",
                nameof(config));

        // Consumer-injectable registry (shared with the client) so NPCs/enemies carry game components across every
        // cell's replication + handoff + ghosting; default = movement-only. Assigned before the host so cells use it.
        this.registry = registry ?? MoveProtocol.CreateRegistry();
        deltaReplicator = this.config.DeltaReplication ? new AoiDeltaReplicator(this.registry) : null;
        this.tuning = tuning;
        // Size the queue's distinct-slot cap to MaxPlayers (the SlotAllocator hands out slots in [0, MaxPlayers)).
        // Without this it used the RemoteCommandQueue default of 64, so on a server with MaxPlayers > 64 every move
        // for the 65th+ slot was silently dropped by the distinct-slot bound, freezing that avatar. Floor at 64 so a
        // smaller MaxPlayers never shrinks the queue below the library default (pure widening, no behaviour change).
        commands = new RemoteCommandQueue<MoveCommand>(neutralCommand: default,
            maxSlots: Math.Max(64, this.config.MaxPlayers),
            catchUpThreshold: Math.Max(0, this.config.MaxInputBacklog));
        movement = new PlayerMovementSystem(groundHeight, tuning, groundNormal, bounds, physics, medium);
        spawnClamp = new PlayerMoveSimulator(groundHeight, tuning, groundNormal, bounds, physics, medium);
        host = new ShardHost(
            cellSize: config.CellSize,
            tickSeconds: config.TickSeconds,
            registry: this.registry,
            interestCellSize: config.CellSize,
            overlapMargin: config.OverlapMargin,
            positionAccessor: PositionAccessor);
        host.CellCreated += cell => CellCreated?.Invoke(cell.Coord);
        // Always enforce the engine wire generation at connect (see WorldServer): a wire-skewed / version-less client
        // is rejected cleanly rather than admitted and left to misparse the wire.
        net = new NetServer(transport, config.MaxPlayers, WireGenerationAuthenticator.Install(authenticator));
        this.banStore = banStore;
    }

    /// <summary>The shard topology (cells, ownership, ghosts).</summary>
    public ShardHost Host => host;
    /// <summary>The replicated-component registry (the injected one, or the movement-only default), shared by every
    /// cell. Build the <see cref="WorldClient"/> with the SAME registry so both ends agree on the component ids.</summary>
    public ReplicationRegistry Registry => registry;

    // --- ICellPersistenceHost: per-cell world-state persistence (non-player entities). ---

    /// <inheritdoc />
    public event Action<CellCoord>? CellCreated;

    /// <inheritdoc />
    public IReadOnlyCollection<CellCoord> LiveCellCoords
    {
        get
        {
            var coords = new List<CellCoord>(host.CellCount);
            foreach (CellSim cell in host.Cells) coords.Add(cell.Coord);
            return coords;
        }
    }

    /// <inheritdoc />
    public byte[]? SnapshotCell(CellCoord coord) =>
        host.TryGetCell(coord, out CellSim cell) ? cell.SnapshotOwned(new HashSet<long>(netIdBySlot.Values)) : null;

    /// <inheritdoc />
    public IReadOnlyList<long> RestoreCell(CellCoord coord, byte[] snapshot) =>
        host.TryGetCell(coord, out CellSim cell) ? cell.RestoreOwned(snapshot) : Array.Empty<long>();

    /// <inheritdoc />
    public CellRestoreResult TryRestoreCell(CellCoord coord, byte[] snapshot) =>
        host.TryGetCell(coord, out CellSim cell)
            ? cell.TryRestoreOwned(snapshot)
            : new CellRestoreResult(true, Array.Empty<long>(), 0, null);

    /// <inheritdoc />
    public void EnsureCell(CellCoord coord) => host.EnsureCell(coord);

    /// <inheritdoc />
    public long NextNetId => allocator.NextValue;

    /// <inheritdoc />
    public void EnsureNextNetIdAtLeast(long atLeast) => allocator.EnsureNextAtLeast(atLeast);

    /// <summary>Number of joined players.</summary>
    public int PlayerCount => netIdBySlot.Count;
    /// <summary>The net id of the player entity for a joined slot.</summary>
    public bool TryGetPlayerNetId(int slot, out long netId) => netIdBySlot.TryGetValue(slot, out netId);

    /// <summary>The worker pool the per-cell movement tick fans across (defaults to single-threaded).</summary>
    public IJobScheduler Scheduler { get => host.Scheduler; set => host.Scheduler = value; }

    /// <inheritdoc/>
    public event Action<int, string>? PlayerJoined;
    /// <inheritdoc/>
    public event Action<int, string, PlayerMoveState>? PlayerLeaving;

    /// <summary>Raised when the server flags a connection as suspicious: a malformed/NaN move packet, a per-
    /// connection message-rate trip, or a sustained streak of large authoritative movement corrections. The engine
    /// signals; the game decides the policy (log / kick via <see cref="Disconnect"/> / ban). Allocation-free. The
    /// same hook contract as <see cref="WorldServer.OnSuspiciousActivity"/>.</summary>
    public event Action<SuspiciousActivity>? OnSuspiciousActivity;

    /// <summary>Raised on the host thread during <see cref="Poll"/> when a joined client sends a game message
    /// (<see cref="WorldClient.SendGameMessage"/>): the sender's slot, the game-defined kind, and the opaque payload.
    /// The engine never interprets the payload. Demuxed ahead of the move (it can never be an 18-byte move - see the
    /// aliasing contract in <see cref="MoveProtocol"/>). The rate limiter and the
    /// <see cref="ShardedWorldServerConfig.MaxGameMessageBytes"/> cap run in front of it; an oversize payload is dropped
    /// and flagged, never dispatched. The span is only valid for the call - copy it to keep the bytes. The same hook
    /// contract as <see cref="WorldServer.OnGameMessage"/>.</summary>
    public event ServerGameMessageHandler? OnGameMessage;

    private void Raise(int slot, SuspiciousReason reason, float magnitude = 0f) =>
        OnSuspiciousActivity?.Invoke(new SuspiciousActivity(slot, reason, magnitude));

    /// <summary>Disconnects a player's connection (a kick) - the policy seam a game's <see cref="OnSuspiciousActivity"/>
    /// handler calls. Immediately removes the slot from authoritative state via <see cref="OnLeave"/> and closes the
    /// transport connection. No-op for an unknown slot.</summary>
    public void Disconnect(int slot) { net.Disconnect(slot); OnLeave(slot); }

    /// <summary>Broadcasts a <see cref="ServerNotice"/> to every connected client (reliable-ordered). Same contract
    /// as <see cref="WorldServer.BroadcastNotice"/>.</summary>
    public void BroadcastNotice(in ServerNotice notice)
    {
        byte[] envelope = MoveProtocol.EncodeServerFrame(MoveProtocol.ServerFrameKind.Notice, MoveProtocol.EncodeNotice(notice));
        net.Broadcast(envelope, NetChannelReliability.ReliableOrdered);
    }

    /// <summary>Sends a game-defined message to one connected client's slot, surfaced on
    /// <see cref="WorldClient.GameMessageReceived"/>. Same opaque-payload + reliability contract as
    /// <see cref="WorldServer.SendGameMessageTo"/>. No-op for an unknown slot.</summary>
    public void SendGameMessageTo(int slot, ushort kind, ReadOnlySpan<byte> payload, NetChannelReliability reliability)
    {
        byte[] envelope = MoveProtocol.EncodeServerFrame(
            MoveProtocol.ServerFrameKind.GameMessage, MoveProtocol.EncodeGameMessageBody(kind, payload));
        net.SendTo(slot, envelope, reliability);
    }

    /// <summary>Broadcasts a game-defined message to every connected client, surfaced on
    /// <see cref="WorldClient.GameMessageReceived"/>. Same contract as <see cref="WorldServer.BroadcastGameMessage"/>.</summary>
    public void BroadcastGameMessage(ushort kind, ReadOnlySpan<byte> payload, NetChannelReliability reliability)
    {
        byte[] envelope = MoveProtocol.EncodeServerFrame(
            MoveProtocol.ServerFrameKind.GameMessage, MoveProtocol.EncodeGameMessageBody(kind, payload));
        net.Broadcast(envelope, reliability);
    }

    /// <summary>True while a graceful drain is in progress.</summary>
    public bool IsDraining => drain.IsDraining;

    /// <summary>True once a graceful drain's grace has elapsed (host then flushes persistence + closes).</summary>
    public bool IsDrainComplete => drain.IsComplete;

    /// <summary>Begins a graceful drain (broadcast + grace countdown). Same contract as
    /// <see cref="WorldServer.BeginDrain"/>.</summary>
    public void BeginDrain(in ServerNotice notice, float graceSeconds)
    {
        BroadcastNotice(notice);
        drain.Begin(graceSeconds);
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<int> JoinedSlots => netIdBySlot.Keys;

    /// <inheritdoc/>
    public bool TryGetAccountId(int slot, out string accountId) => accountIdBySlot.TryGetValue(slot, out accountId!);

    /// <summary>The current authoritative state for a joined slot, read from its owning cell (cell-agnostic).</summary>
    public bool TryGetPlayerState(int slot, out PlayerMoveState state)
    {
        if (netIdBySlot.TryGetValue(slot, out long netId)
            && host.TryGetOwner(netId, out CellSim cell, out Entity e)
            && cell.World.TryGet(e, out ReplicatedPosition rp))
        {
            cell.World.TryGet(e, out MovementState ms);   // default (grounded, 0) if absent
            state = PlayerMoveState.From(rp.Value, ms);
            return true;
        }
        state = default;
        return false;
    }

    /// <summary>Places a joined player at <paramref name="state"/> (load-on-join, admin/self-rescue teleport). Writes
    /// its owning cell's <see cref="ReplicatedPosition"/>; if that position falls in another cell the next
    /// <see cref="Tick"/>'s handoff relocates the entity there (NetId stable). No-op for an unknown slot. When
    /// <paramref name="teleport"/> is true the player's monotonic teleport epoch (held on the cell's
    /// <see cref="MovementState"/>) is advanced so the client cuts to the new position instead of gliding; otherwise
    /// the current epoch is preserved. The per-cell <see cref="PlayerMovementSystem"/> preserves it in place, so
    /// ordinary movement never advances it.</summary>
    public void SetPlayerState(int slot, in PlayerMoveState state, bool teleport = false)
    {
        if (netIdBySlot.TryGetValue(slot, out long netId) && host.TryGetOwner(netId, out CellSim cell, out Entity e))
        {
            uint baseEpoch = cell.World.TryGet(e, out MovementState prev) ? prev.TeleportEpoch : 0u;
            PlayerMoveState next = state;
            next.TeleportEpoch = teleport ? baseEpoch + 1u : baseEpoch;   // server owns the monotonic epoch
            cell.World.Set(e, new ReplicatedPosition { Value = next.Position });
            cell.World.Set(e, MovementState.From(next));
        }
    }

    /// <summary>Sets the display name replicated for a joined player (added to its owning cell's entity as a
    /// <see cref="PlayerIdentity"/>; it migrates with the entity across cell handoffs since the registered component
    /// is transferred). Cosmetic and independent of the account id; the same seam as
    /// <see cref="WorldServer.SetPlayerDisplayName"/>. No-op for an unknown slot.</summary>
    public void SetPlayerDisplayName(int slot, string name)
    {
        if (netIdBySlot.TryGetValue(slot, out long netId) && host.TryGetOwner(netId, out CellSim cell, out Entity e))
            cell.World.Set(e, new PlayerIdentity { DisplayName = name ?? string.Empty });
    }

    /// <summary>
    /// Spawns a server-owned non-player entity (an NPC, enemy, resource node, …) at world position
    /// (<paramref name="x"/>, <paramref name="z"/>). It allocates a fresh <see cref="NetId"/> from the SAME
    /// authoritative allocator player joins draw from (so it can never collide with a player id or a
    /// <see cref="CellPersistence"/>-restored id, whose high-water mark this allocator honours) and places the
    /// entity in the cell that owns that position. <see cref="ReplicatedPosition"/> is pre-set to
    /// (<paramref name="x"/>, 0, <paramref name="z"/>) so the entity is immediately area-of-interest-visible;
    /// <paramref name="configure"/> then runs against its owning cell's <see cref="World"/> to add game components,
    /// e.g. an NPC-kind / HP / faction component registered on <see cref="Registry"/> at an id >=
    /// <see cref="MoveProtocol.FirstConsumerTypeId"/>, which clients read back via
    /// <see cref="WorldClient.TryGetComponent{T}"/>. The entity replicates through the normal AoI + ghost + handoff
    /// pipeline; being non-player it is persisted with its cell. Move it each tick from <see cref="OnBeforeTick"/>.
    /// Returns the new entity's NetId.
    /// </summary>
    public long SpawnEntity(float x, float z, Action<World, Entity>? configure = null)
    {
        long netId = allocator.Next().Value;
        Entity e = host.SpawnOwned(x, z, netId, out CellSim cell); // eager: registers netId in the O(1) ownership index
        cell.World.Set(e, new ReplicatedPosition { Value = new Vector3(x, 0f, z) });
        configure?.Invoke(cell.World, e);
        return netId;
    }

    /// <summary>Ingests session events (join/leave) and client input. Call once before <see cref="Tick"/>.</summary>
    public void Poll()
    {
        net.Poll();
        foreach (RateLimiter limiter in rateBySlot.Values) limiter.Refill();   // one budget top-up per poll
        while (net.TryDequeueEvent(out ServerSessionEvent ev))
        {
            switch (ev.Kind)
            {
                case ServerSessionEventKind.Joined:
                    OnJoin(ev.Slot, ev.Subject, ev.DisplayName);
                    break;
                case ServerSessionEventKind.Left:
                    OnLeave(ev.Slot);
                    break;
                case ServerSessionEventKind.Data:
                    HandleData(ev.Slot, ev.Data);
                    break;
            }
        }
    }

    private void HandleData(int slot, byte[] data)
    {
        if (!netIdBySlot.ContainsKey(slot)) return;
        // Flood protection: an over-budget message is dropped and flagged (and optionally the connection is kicked).
        if (rateBySlot.TryGetValue(slot, out RateLimiter? limiter) && !limiter.TryConsume())
        {
            Raise(slot, SuspiciousReason.RateLimited);
            if (config.AntiCheat.DisconnectOnRateLimit) net.Disconnect(slot);
            return;
        }
        // Replication ack: advance this client's delta baseline (a dropped ack just self-heals on the next delta).
        if (MoveProtocol.TryDecodeReplicationAck(data, out int appliedSeq))
        {
            deltaReplicator?.Acknowledge(slot, appliedSeq);
            return;
        }
        // Control messages (e.g. self-rescue, delta-capable hello) share the channel with moves; demux by length.
        if (MoveProtocol.TryDecodeClientControl(data, out MoveProtocol.ClientControlKind control))
        {
            if (control == MoveProtocol.ClientControlKind.SelfRescue) HandleSelfRescue(slot);
            else if (control == MoveProtocol.ClientControlKind.DeltaCapable && deltaReplicator is not null) deltaCapableSlots.Add(slot);
            return;
        }
        // Game message: an opaque game-defined frame (attack, interaction, chat, …). Demuxed BEFORE the move (it can
        // never be an 18-byte move - see MoveProtocol's aliasing contract). Over the size cap is dropped + flagged.
        if (MoveProtocol.TryDecodeGameMessage(data, out ushort gameKind, out ReadOnlySpan<byte> gamePayload))
        {
            if (gamePayload.Length > config.MaxGameMessageBytes) { Raise(slot, SuspiciousReason.OversizedMessage, gamePayload.Length); return; }
            OnGameMessage?.Invoke(slot, gameKind, gamePayload);
            return;
        }
        // Malformed / NaN / Inf packets are rejected by the decode and flagged.
        if (!MoveProtocol.TryDecodeMove(data, out int seq, out MoveCommand cmd))
        {
            Raise(slot, SuspiciousReason.MalformedPacket);
            return;
        }
        commands.Store(slot, seq, cmd);
    }

    // A client asked to be teleported to a safe spot. The server owns the destination (the client sends no position),
    // so this can't be a teleport-anywhere. Honored at most once per SelfRescueCooldownSeconds per player; the move
    // reuses the admin Teleport apply path (enqueued here on the host thread, applied at the next Tick's drain).
    private void HandleSelfRescue(int slot)
    {
        if (config.SelfRescueDestination is null) return;                                   // feature off
        if (selfRescueReadyAt.TryGetValue(slot, out double readyAt) && selfRescueClock < readyAt) return;  // cooling down
        Vector3 dest = config.SelfRescueDestination(PlayerRef.Slot(slot));
        admin.Enqueue(new AdminCommand { Kind = AdminCommandKind.Teleport, Target = PlayerRef.Slot(slot), Position = dest });
        selfRescueReadyAt[slot] = selfRescueClock + Math.Max(0f, config.SelfRescueCooldownSeconds);
    }

    /// <summary>Raised at the START of every <see cref="Tick"/> (before movement, handoff, ghosting, and the
    /// snapshot pass), with the tick <c>dt</c>. This is where a consumer runs its server-authoritative
    /// non-player behaviour (an NPC/enemy brain), writing each entity's <see cref="ReplicatedPosition"/> (and any
    /// extension components) so the changes flow through the same handoff/ghost/AoI pipeline as players and reach
    /// clients in the same tick. No-op until subscribed, so existing behaviour is unchanged.</summary>
    public event Action<float>? OnBeforeTick;

    /// <summary>Steps one authoritative server frame across every cell, then serves each client its home-cell AoI.</summary>
    public void Tick(float dt)
    {
        OnBeforeTick?.Invoke(dt);   // consumer NPC/enemy brains run before movement + serving
        admin.Drain(ApplyAdminCommand);
        var slots = new List<int>(netIdBySlot.Keys);
        bool trackCorrection = config.AntiCheat.CorrectionEnabled;
        correctionScratch.Clear();

        // 1. Route each client's input to the cell that owns its player.
        foreach (int slot in slots)
        {
            MoveCommand cmd = commands.Dequeue(slot, out int ack);
            lastAckBySlot[slot] = ack;
            if (host.TryGetOwner(netIdBySlot[slot], out CellSim cell, out Entity e))
            {
                cell.World.Set(e, new PendingMove { Command = cmd });
                // Snapshot the pre-step state so the post-step correction (how far the cell sim denied the move)
                // can be measured after the cells tick. Movement happens inside PlayerMovementSystem, so we bracket
                // host.Tick rather than threading the metric through the ECS.
                if (trackCorrection && cell.World.TryGet(e, out ReplicatedPosition rp))
                {
                    cell.World.TryGet(e, out MovementState ms);
                    correctionScratch.Add((slot, PlayerMoveState.From(rp.Value, ms), cmd));
                }
            }
        }

        // 2. Make sure every (possibly newly-created) cell runs the movement system.
        foreach (CellSim cell in host.Cells) EnsureWired(cell);

        // 3. Authoritative movement: one fixed sub-tick per frame, fanned across the scheduler.
        host.Tick(dt, maxTicksPerFrame: 1);

        // 4. Authority follows entities across boundaries (exactly-once), then refresh border ghosts.
        host.ProcessHandoffs();
        host.SyncGhosts();

        // 4b. Movement-correction anomaly: compare each routed player's post-step position to its intended move.
        foreach ((int slot, PlayerMoveState prev, MoveCommand cmd) in correctionScratch)
        {
            if (!TryGetPlayerState(slot, out PlayerMoveState after)) continue;
            float correction = MovementAnomaly.CorrectionDistance(prev, cmd, after, dt, tuning);
            if (MovementAnomaly.RegisterCorrection(correctionStreakBySlot, slot, correction, config.AntiCheat))
                Raise(slot, SuspiciousReason.MovementCorrection, correction);
        }

        // 5. Serve each client its home-cell area-of-interest, framed for the WorldClient. Delta-capable clients get a
        //    per-client AoI delta (NetId-keyed, so a boundary crossing is a component delta); others a full snapshot.
        deltaReplicator?.BeginTick();
        long serveEpoch = ++interestServeEpoch;   // fresh each tick: each served home cell rebuilds its grid once
        foreach (int slot in slots)
        {
            if (!netIdBySlot.TryGetValue(slot, out long netId)) continue;
            MoveProtocol.ServerFrameKind kind;
            byte[] body;
            if (deltaReplicator is not null && deltaCapableSlots.Contains(slot))
            {
                (World world, HashSet<long> interest) = host.HomeInterest(slot, config.InterestRadius, serveEpoch);
                // Owner-scope the Replicate channel to this client's own player (netId is stable across handoff), so an
                // OwnerOnly component is served only on the client's own entity, never on another player it observes.
                body = deltaReplicator.WriteFor(slot, world, interest, netId);
                kind = MoveProtocol.ServerFrameKind.Delta;
            }
            else
            {
                body = host.SnapshotForClient(slot, config.InterestRadius, serveEpoch);
                kind = MoveProtocol.ServerFrameKind.Snapshot;
            }
            byte[] frame = MoveProtocol.EncodeSnapshotFrame(netId, lastAckBySlot[slot], body);
            byte[] envelope = MoveProtocol.EncodeServerFrame(kind, frame);
            net.SendTo(slot, envelope, NetChannelReliability.ReliableOrdered);
        }

        // 6. Clear each cell's per-tick ECS change-tracking + event sets so they don't accumulate on a long-running
        //    server. One fixed sub-tick ran per cell this frame (maxTicksPerFrame: 1), so one advance per cell here
        //    matches. Opt out via ShardedWorldServerConfig.AdvanceWorldTick if a cell's change sets are consumed.
        if (config.AdvanceWorldTick)
            foreach (CellSim cell in host.Cells) cell.World.AdvanceTick();
        drain.Advance(dt);
        selfRescueClock += dt;   // advance the self-rescue cooldown clock
        admin.Publish(BuildOnlineSnapshot());
    }

    /// <inheritdoc/>
    public IReadOnlyList<OnlinePlayer> ListOnline() => admin.Online;

    /// <inheritdoc/>
    public void Teleport(PlayerRef target, Vector3 position) =>
        admin.Enqueue(new AdminCommand { Kind = AdminCommandKind.Teleport, Target = target, Position = position });

    /// <inheritdoc/>
    public void Kick(PlayerRef target, string reason) =>
        admin.Enqueue(new AdminCommand { Kind = AdminCommandKind.Kick, Target = target, Text = reason ?? string.Empty });

    /// <inheritdoc/>
    public void Broadcast(string text) =>
        admin.Enqueue(new AdminCommand { Kind = AdminCommandKind.Broadcast, Text = text ?? string.Empty });

    private int ResolveSlot(in PlayerRef target)
    {
        if (target.IsSlot) return netIdBySlot.ContainsKey(target.SlotValue) ? target.SlotValue : -1;
        foreach (KeyValuePair<int, string> kv in accountIdBySlot)
            if (kv.Value == target.AccountValue) return kv.Key;
        return -1;
    }

    private void SendNoticeTo(int slot, in ServerNotice notice)
    {
        byte[] envelope = MoveProtocol.EncodeServerFrame(MoveProtocol.ServerFrameKind.Notice, MoveProtocol.EncodeNotice(notice));
        net.SendTo(slot, envelope, NetChannelReliability.ReliableOrdered);
    }

    private void ApplyAdminCommand(AdminCommand cmd)
    {
        switch (cmd.Kind)
        {
            case AdminCommandKind.Teleport:
            {
                int slot = ResolveSlot(cmd.Target);
                if (slot >= 0 && TryGetPlayerState(slot, out PlayerMoveState st))
                {
                    st.Position = cmd.Position;
                    st.VerticalVelocity = 0f;
                    SetPlayerState(slot, st, teleport: true);   // admin + self-rescue teleports cut on the client
                }
                break;
            }
            case AdminCommandKind.Kick:
            {
                int slot = ResolveSlot(cmd.Target);
                if (slot >= 0)
                {
                    SendNoticeTo(slot, new ServerNotice(ServerNoticeKind.Custom, cmd.Text));
                    Disconnect(slot);
                }
                break;
            }
            case AdminCommandKind.Broadcast:
                BroadcastNotice(new ServerNotice(ServerNoticeKind.Custom, cmd.Text));
                break;
        }
    }

    private OnlinePlayer[] BuildOnlineSnapshot()
    {
        var list = new List<OnlinePlayer>(netIdBySlot.Count);
        foreach (int slot in netIdBySlot.Keys)
        {
            if (!netIdBySlot.TryGetValue(slot, out long netId)) continue;
            string acct = accountIdBySlot.TryGetValue(slot, out string? a) ? a : string.Empty;
            TryGetPlayerState(slot, out PlayerMoveState st);
            string name = string.Empty;
            if (host.TryGetOwner(netId, out CellSim cell, out Entity e) && cell.World.TryGet(e, out PlayerIdentity pi))
                name = pi.DisplayName ?? string.Empty;
            list.Add(new OnlinePlayer(slot, acct, name, st.Position, st.Grounded, st.VerticalVelocity, netId));
        }
        return list.ToArray();
    }

    private void OnJoin(int slot, string subject, string displayName)
    {
        string accountId = string.IsNullOrEmpty(subject) ? $"guest:{slot}" : subject;
        if (banStore is not null && banStore.IsBanned(accountId))
        {
            SendNoticeTo(slot, new ServerNotice(ServerNoticeKind.Custom, "banned"));
            net.Disconnect(slot);
            return;
        }

        // Belt-and-suspenders: clear any stale command-queue state on the (recycled) slot before spawning, in case
        // a prior occupant's Left was ever missed. A fresh session's seqs restart at 0; a stale high-water mark
        // would reject every one and freeze the player (see OnLeave).
        commands.Forget(slot);
        // Drop any stale delta baseline / capability on the recycled slot; the new occupant re-advertises + re-baselines.
        deltaReplicator?.Forget(slot);
        deltaCapableSlots.Remove(slot);

        Vector3 spawn = config.SpawnPosition?.Invoke(slot) ?? new Vector3(slot * 2f, 0f, 0f);
        // Ground-clamp the spawn (an idle step settles Y onto the terrain + half-height).
        PlayerMoveState state = spawnClamp.Step(new PlayerMoveState { Position = spawn }, MoveCommand.Idle, config.TickSeconds);

        long netId = allocator.Next().Value;
        Entity e = host.SpawnOwned(state.Position.X, state.Position.Z, netId, out CellSim cell); // eager index register
        cell.World.Set(e, new ReplicatedPosition { Value = state.Position });
        cell.World.Set(e, MovementState.From(state));   // vertical axis: present at spawn, carried across handoff
        // A display name on the connect token rides along the same way (a registered component, migrated on handoff).
        if (!string.IsNullOrEmpty(displayName)) cell.World.Set(e, new PlayerIdentity { DisplayName = displayName });
        EnsureWired(cell);
        netIdBySlot[slot] = netId;
        lastAckBySlot[slot] = -1;
        accountIdBySlot[slot] = accountId;
        // Fresh per-connection anti-cheat state (a recycled slot starts from a full bucket / zero streak).
        RateLimiter? limiter = config.AntiCheat.CreateLimiter(config.TickSeconds);
        if (limiter is not null) rateBySlot[slot] = limiter; else rateBySlot.Remove(slot);
        correctionStreakBySlot[slot] = 0;
        host.BindClient(slot, netId);

        PlayerJoined?.Invoke(slot, accountId);
    }

    // Idempotent by design: safe to call more than once for the same slot. The TryGetValue guards below make a repeat
    // call a no-op (PlayerLeaving cannot double-fire, save-on-leave cannot double-persist), and every Remove is a
    // no-op on a missing key. This is load-bearing: Disconnect(slot) calls OnLeave synchronously, and a real transport
    // may later surface a Left event for the same slot through Poll, calling OnLeave a second time.
    private void OnLeave(int slot)
    {
        if (netIdBySlot.TryGetValue(slot, out long netId))
        {
            if (accountIdBySlot.TryGetValue(slot, out string? acct) && TryGetPlayerState(slot, out PlayerMoveState final))
                PlayerLeaving?.Invoke(slot, acct, final);
            if (host.TryGetOwner(netId, out CellSim cell, out Entity e) && cell.World.IsAlive(e))
            {
                cell.UnregisterOwned(netId); // eager: drop it from the ownership index before despawning
                cell.World.Despawn(e);
            }
        }
        host.UnbindClient(slot);
        netIdBySlot.Remove(slot);
        lastAckBySlot.Remove(slot);
        accountIdBySlot.Remove(slot);
        rateBySlot.Remove(slot);
        correctionStreakBySlot.Remove(slot);
        selfRescueReadyAt.Remove(slot);
        // Drop the slot's delta baseline + capability so the recycled slot starts clean (see OnJoin).
        deltaReplicator?.Forget(slot);
        deltaCapableSlots.Remove(slot);
        // Drop the slot's command-queue state too. The SlotAllocator recycles this slot to the next connection,
        // whose seqs legitimately restart at 0; without this the stale high-water mark rejects every command and
        // freezes the recycled player (it self-heals only once their seq crawls past the dead mark, minutes later).
        commands.Forget(slot);
    }

    private void EnsureWired(CellSim cell)
    {
        if (wiredCells.Add(cell.Coord)) cell.World.AddSystem(movement);
    }

    private static bool PositionAccessor(World world, Entity e, out float x, out float y)
    {
        if (world.TryGet(e, out ReplicatedPosition p)) { x = p.Value.X; y = p.Value.Z; return true; }
        x = y = 0f;
        return false;
    }
}
