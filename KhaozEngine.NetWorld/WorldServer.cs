using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

/// <summary>Tunables for <see cref="WorldServer"/>.</summary>
public sealed class WorldServerConfig
{
    /// <summary>Fixed server tick, seconds.</summary>
    public float TickSeconds { get; init; } = 1f / 30f;
    /// <summary>Per-client area-of-interest radius (world units).</summary>
    public float InterestRadius { get; init; } = 200f;
    /// <summary>Maximum concurrent players.</summary>
    public int MaxPlayers { get; init; } = 16;
    /// <summary>Per-slot spawn position (XZ used; Y is ground-clamped). Default spreads players along +X.</summary>
    public Func<int, Vector3>? SpawnPosition { get; init; }

    /// <summary>When true (the default), each <see cref="WorldServer.Tick"/> calls <see cref="World.AdvanceTick"/>
    /// once on the authoritative world, clearing the per-tick change-tracking sets
    /// (<see cref="World.Added{T}"/>/<see cref="World.Changed{T}"/>/<see cref="World.Removed{T}"/>) and the event
    /// buffer (<see cref="World.Events{T}"/>). The authoritative movement path does not consume those, and without
    /// the clear they grow unbounded on a long-running server (one entry per <c>Set</c>/<c>Despawn</c>, never
    /// reclaimed). Set false ONLY if your own systems read change-tracking/events and you call
    /// <see cref="World.AdvanceTick"/> yourself at your chosen point in the frame.</summary>
    public bool AdvanceWorldTick { get; init; } = true;

    /// <summary>Opt-in server-side anti-cheat / input-hardening knobs (rate limiting, movement-correction anomaly).
    /// All off by default, so behaviour is unchanged until a consumer tightens it.</summary>
    public AntiCheatConfig AntiCheat { get; init; } = new();

    /// <summary>Server-owned destination for a client <see cref="WorldClient.RequestSelfRescue"/> ("unstuck" /
    /// return-to-spawn): given the requesting player, returns the safe world position to teleport it to. The teleport
    /// reuses the admin path (position set, vertical velocity zeroed) and reconciles to the client. The client never
    /// supplies a position - the server alone decides - so a hostile client can't teleport anywhere. A fixed point is
    /// just <c>_ =&gt; point</c>. <b>Null (the default) =&gt; the feature is OFF</b>: a self-rescue request is ignored.</summary>
    public Func<PlayerRef, Vector3>? SelfRescueDestination { get; init; }

    /// <summary>Per-player minimum interval (seconds) between honored self-rescues; further requests inside the window
    /// are dropped. Default 5s. Ignored when <see cref="SelfRescueDestination"/> is null.</summary>
    public float SelfRescueCooldownSeconds { get; init; } = 5f;

    /// <summary>Per-player input-backlog catch-up cap, in commands (ticks). When a client's queued move backlog grows
    /// deeper than this, the server skips the stale moves and applies only the most recent, so it stays at most this
    /// many ticks behind live input instead of replaying a deep backlog one move per tick. This bounds the
    /// reconnect/lag-burst freeze where a player was driven by minutes-old input on rejoin (see
    /// <see cref="WorldClient.SendInput"/>). Movement is latest-wins, so skipping intermediate moves is correct.
    /// Default 8 (~0.27s at 30Hz) - well clear of normal one-per-tick play, which never reaches it. Set 0 to disable
    /// (strict one-move-per-tick drain, the pre-8.8.0 behaviour).</summary>
    public int MaxInputBacklog { get; init; } = 8;

    /// <summary>Serve each client per-tick area-of-interest DELTAS (only components changed since that client's
    /// acknowledged baseline; entered entities in full, left entities as despawns) instead of a full snapshot every
    /// tick. Default true. A client opts in on join (<see cref="WorldClientConfig.RequestDeltaReplication"/>) via the
    /// <see cref="MoveProtocol.ClientControlKind.DeltaCapable"/> hello; a client that does not advertise it (an older
    /// build) keeps receiving full snapshots, so client and server upgrade independently with no disconnect. Set false
    /// to force full snapshots for every client (the pre-9.17.0 behaviour). The delta is built from the client's last
    /// <see cref="MoveProtocol.EncodeReplicationAck"/>, so a dropped delta on the reliable-ordered channel self-heals.</summary>
    public bool DeltaReplication { get; init; } = true;

    /// <summary>Maximum payload size (bytes) accepted on a client-to-server game message
    /// (<see cref="WorldClient.SendGameMessage"/>). A larger payload is DROPPED (never dispatched to
    /// <see cref="WorldServer.OnGameMessage"/>) and flagged <see cref="SuspiciousReason.OversizedMessage"/> so a
    /// hostile client can't exhaust the host with an outsized frame. The payload is opaque bytes to the engine; this
    /// is the only bound it enforces on it. Default 1024. The rate limiter (<see cref="AntiCheat"/>) runs in front of
    /// this, so game messages share the per-connection flood budget with moves.</summary>
    public int MaxGameMessageBytes { get; init; } = 1024;
}

/// <summary>
/// Reference single-<see cref="World"/> authoritative movement server. A <see cref="NetServer"/> session layer
/// spawns one player entity per connection; each tick it drains that client's queued <see cref="MoveCommand"/>,
/// runs the shared <see cref="PlayerMoveSimulator"/> (ground-clamped), and serves every client a per-area-of-
/// interest snapshot (<see cref="SnapshotWriter.WriteFiltered(World, ReplicationRegistry, IReadOnlySet{long}, ReplicationChannels, long?)"/> over an <see cref="InterestGrid"/>) prefixed
/// with that client's net id + last-acked move seq so the client can reconcile. Headless, transport-injected.
/// The multi-cell variant is <see cref="ShardedWorldServer"/> (the same movement stack run across a cell grid);
/// this is the single-world slice. Both share <see cref="WorldPersistence"/> via <see cref="IWorldPersistenceHost"/>.
/// </summary>
public sealed class WorldServer : IWorldPersistenceHost, IAdminControllable
{
    private readonly WorldServerConfig config;
    private readonly ReplicationRegistry registry;
    private readonly World world = new();
    private readonly NetServer net;
    private readonly InterestGrid interest;
    private readonly RemoteCommandQueue<MoveCommand> commands;
    private readonly PlayerMoveSimulator simulator;
    private readonly DrainController drain = new();
    private readonly AdminCommandBuffer admin = new();
    private readonly IBanStore? banStore;
    // Per-client AoI delta encoder (null when DeltaReplication is off). A slot is served deltas only once it is in
    // deltaCapableSlots (it advertised DeltaCapable); until then, and for older clients, it gets full snapshots.
    private readonly AoiDeltaReplicator? deltaReplicator;
    private readonly HashSet<int> deltaCapableSlots = new();

    private readonly Dictionary<int, long> netIdBySlot = new();
    private readonly Dictionary<int, Entity> entityBySlot = new();
    private readonly Dictionary<int, PlayerMoveState> stateBySlot = new();
    private readonly Dictionary<int, int> lastAckBySlot = new();
    private readonly Dictionary<int, string> accountIdBySlot = new();
    private readonly Dictionary<int, RateLimiter> rateBySlot = new();
    private readonly Dictionary<int, int> correctionStreakBySlot = new();
    // Self-rescue cooldown: a monotonic server-time accumulator (advanced one dt per Tick) plus, per slot, the
    // earliest clock time the next self-rescue is honored. No wall-clock; double so a long-uptime server keeps
    // sub-second resolution. Entries are seeded only when a rescue is honored and cleared on leave.
    private double selfRescueClock;
    private readonly Dictionary<int, double> selfRescueReadyAt = new();
    private readonly MoveTuning tuning;
    // The single NetId allocator (node 0 for this single-process server) that both player joins and SpawnEntity draw
    // from, so ids never collide. Replaces the pre-10.0.0 raw ++int counter; see NetIdAllocator for the node-prefix scheme.
    private readonly NetIdAllocator allocator = new();

    public WorldServer(INetTransport transport, WorldServerConfig config,
        Func<float, float, float> groundHeight, MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal = null, WorldBounds? bounds = null, IPhysicsWorld? physics = null,
        IConnectionAuthenticator? authenticator = null, IBanStore? banStore = null,
        ReplicationRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        if (groundHeight is null) throw new ArgumentNullException(nameof(groundHeight));
        // Consumer-injectable registry so a game can replicate its own components (NPC kind, HP, …) on top of the
        // movement built-ins; it MUST be the same registry the client is built with. Default = movement-only.
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
        simulator = new PlayerMoveSimulator(groundHeight, tuning, groundNormal, bounds, physics);
        // Always enforce the engine wire generation at connect (independent of any consumer version gate), so a
        // wire-skewed or version-less client is rejected cleanly instead of admitted and left to misparse the wire.
        net = new NetServer(transport, config.MaxPlayers, WireGenerationAuthenticator.Install(authenticator));
        this.banStore = banStore;
        interest = new InterestGrid(MathF.Max(1f, config.InterestRadius));
    }

    /// <summary>Raised when the server flags a connection as suspicious: a malformed/NaN move packet, a per-
    /// connection message-rate trip, or a sustained streak of large authoritative movement corrections. The engine
    /// signals; the game decides the policy (log / kick via <see cref="Disconnect"/> / ban). Allocation-free.</summary>
    public event Action<SuspiciousActivity>? OnSuspiciousActivity;

    /// <summary>Raised on the host thread during <see cref="Poll"/> when a joined client sends a game message
    /// (<see cref="WorldClient.SendGameMessage"/>): the sender's slot, the game-defined kind, and the opaque payload.
    /// The engine never interprets the payload - deserialize it in the handler. Rides the same 0xC5 marker family as
    /// the move/control frames, demuxed ahead of the move so it never disturbs the movement stream (see the aliasing
    /// contract in <see cref="MoveProtocol"/>). The rate limiter and the <see cref="WorldServerConfig.MaxGameMessageBytes"/>
    /// cap run in front of it; an oversize payload is dropped and flagged, never dispatched. The span is only valid for
    /// the duration of the call - copy it (<c>payload.ToArray()</c>) to keep the bytes. The multi-cell equivalent is
    /// <see cref="ShardedWorldServer.OnGameMessage"/>.</summary>
    public event ServerGameMessageHandler? OnGameMessage;

    private void Raise(int slot, SuspiciousReason reason, float magnitude = 0f) =>
        OnSuspiciousActivity?.Invoke(new SuspiciousActivity(slot, reason, magnitude));

    /// <summary>Disconnects a player's connection (a kick) - the policy seam a game's <see cref="OnSuspiciousActivity"/>
    /// handler calls. Immediately removes the slot from the authoritative state so <see cref="PlayerCount"/> reflects
    /// the kick without waiting for the transport event. No-op for an unknown slot.</summary>
    public void Disconnect(int slot) { net.Disconnect(slot); OnLeave(slot); }

    /// <summary>Broadcasts a <see cref="ServerNotice"/> to every connected client (reliable-ordered), surfaced on
    /// <see cref="WorldClient.NoticeReceived"/>. Out-of-band: rides the Data channel alongside snapshots via the
    /// frame envelope, so it never disturbs the movement stream.</summary>
    public void BroadcastNotice(in ServerNotice notice)
    {
        byte[] envelope = MoveProtocol.EncodeServerFrame(MoveProtocol.ServerFrameKind.Notice, MoveProtocol.EncodeNotice(notice));
        net.Broadcast(envelope, NetChannelReliability.ReliableOrdered);
    }

    /// <summary>Sends a game-defined message to one connected client's slot, surfaced on
    /// <see cref="WorldClient.GameMessageReceived"/> with the same <paramref name="kind"/> and <paramref name="payload"/>.
    /// The engine never interprets the payload (opaque bytes). <paramref name="reliability"/> chooses the transport
    /// channel: <see cref="NetChannelReliability.ReliableOrdered"/> gives ordered exactly-once delivery (a command/event
    /// consumers can rely on without their own seq), <see cref="NetChannelReliability.UnreliableSequenced"/> a lossy
    /// latest-wins state ping. Rides the Data channel via the frame envelope, so it never disturbs the movement stream.
    /// No-op for an unknown slot. The multi-cell equivalent is <see cref="ShardedWorldServer.SendGameMessageTo"/>.</summary>
    public void SendGameMessageTo(int slot, ushort kind, ReadOnlySpan<byte> payload, NetChannelReliability reliability)
    {
        byte[] envelope = MoveProtocol.EncodeServerFrame(
            MoveProtocol.ServerFrameKind.GameMessage, MoveProtocol.EncodeGameMessageBody(kind, payload));
        net.SendTo(slot, envelope, reliability);
    }

    /// <summary>Broadcasts a game-defined message to every connected client, surfaced on
    /// <see cref="WorldClient.GameMessageReceived"/>. Same opaque-payload + reliability contract as
    /// <see cref="SendGameMessageTo"/>.</summary>
    public void BroadcastGameMessage(ushort kind, ReadOnlySpan<byte> payload, NetChannelReliability reliability)
    {
        byte[] envelope = MoveProtocol.EncodeServerFrame(
            MoveProtocol.ServerFrameKind.GameMessage, MoveProtocol.EncodeGameMessageBody(kind, payload));
        net.Broadcast(envelope, reliability);
    }

    /// <summary>True while a graceful drain is in progress (see <see cref="BeginDrain"/>).</summary>
    public bool IsDraining => drain.IsDraining;

    /// <summary>True once a graceful drain's grace period has elapsed. The host then flushes persistence
    /// (<c>WorldPersistence.FlushAsync</c>) and disposes the transport to close the sockets.</summary>
    public bool IsDrainComplete => drain.IsComplete;

    /// <summary>Begins a graceful drain: broadcasts <paramref name="notice"/> now (warn players), then runs a
    /// <paramref name="graceSeconds"/> countdown over <see cref="Tick"/> while still serving snapshots, so clients
    /// see the warning. When <see cref="IsDrainComplete"/> flips, the host should flush persistence and close.</summary>
    public void BeginDrain(in ServerNotice notice, float graceSeconds)
    {
        BroadcastNotice(notice);
        drain.Begin(graceSeconds);
    }

    /// <summary>The authoritative ECS world.</summary>
    public World World => world;
    /// <summary>The replicated-component registry (the injected one, or the movement-only default). Build the
    /// <see cref="WorldClient"/> with the SAME registry so both ends agree on the component ids.</summary>
    public ReplicationRegistry Registry => registry;
    /// <summary>Number of joined players.</summary>
    public int PlayerCount => netIdBySlot.Count;
    /// <summary>The net id of the player entity for a joined slot.</summary>
    public bool TryGetPlayerNetId(int slot, out long netId) => netIdBySlot.TryGetValue(slot, out netId);

    /// <summary>Raised after a player entity has spawned: (slot, accountId). The accountId is the verified subject
    /// the <see cref="IConnectionAuthenticator"/> bound the connection to, or <c>guest:{slot}</c> when that subject
    /// is empty. A persistence layer loads the saved record here.</summary>
    public event Action<int, string>? PlayerJoined;

    /// <summary>Raised just before a player despawns: (slot, accountId, final state). A persistence layer
    /// serializes and saves the final state here (the entity is gone after this returns).</summary>
    public event Action<int, string, PlayerMoveState>? PlayerLeaving;

    /// <summary>The account id for a joined slot (connect token or <c>guest:{slot}</c> fallback).</summary>
    public bool TryGetAccountId(int slot, out string accountId) => accountIdBySlot.TryGetValue(slot, out accountId!);

    /// <summary>The current authoritative movement state for a joined slot.</summary>
    public bool TryGetPlayerState(int slot, out PlayerMoveState state) => stateBySlot.TryGetValue(slot, out state);

    /// <summary>The slots of all currently joined players.</summary>
    public IReadOnlyCollection<int> JoinedSlots => netIdBySlot.Keys;

    /// <summary>Overrides a joined player's authoritative state (and its replicated position). Used by
    /// load-on-join to place the player at the saved position; no-op for an unknown slot.</summary>
    public void SetPlayerState(int slot, in PlayerMoveState state)
    {
        if (!entityBySlot.TryGetValue(slot, out Entity e)) return;
        stateBySlot[slot] = state;
        world.Set(e, new ReplicatedPosition { Value = state.Position });
        world.Set(e, MovementState.From(state));
    }

    /// <summary>Sets the display name replicated for a joined player (added to its entity as a
    /// <see cref="PlayerIdentity"/>, so the next snapshot carries it to every client in range). Cosmetic and
    /// independent of the account id. Call it from a <see cref="PlayerJoined"/> handler (e.g. resolved from the
    /// game's DB), or rely on the <see cref="KhaozEngine.Netcode.SignedToken"/> display-name claim auto-applied at
    /// join. No-op for an unknown slot.</summary>
    public void SetPlayerDisplayName(int slot, string name)
    {
        if (!entityBySlot.TryGetValue(slot, out Entity e)) return;
        world.Set(e, new PlayerIdentity { DisplayName = name ?? string.Empty });
    }

    /// <summary>
    /// Spawns a server-owned non-player entity (an NPC, enemy, …) at world position (<paramref name="x"/>,
    /// <paramref name="z"/>) in the single authoritative <see cref="World"/>. It allocates a fresh <see cref="NetId"/>
    /// from the SAME allocator player joins draw from, so the id never collides with a player. It pre-sets
    /// <see cref="ReplicatedPosition"/> to (<paramref name="x"/>, 0, <paramref name="z"/>) so the entity is
    /// immediately area-of-interest-visible; <paramref name="configure"/> then runs against the world to add game
    /// components, e.g. an NPC-kind / HP / faction component registered on <see cref="Registry"/> at an id >=
    /// <see cref="MoveProtocol.FirstConsumerTypeId"/>, which clients read via
    /// <see cref="WorldClient.TryGetComponent{T}"/>. Drive its behaviour each tick from <see cref="OnBeforeTick"/>.
    /// Returns the new entity's NetId. (The multi-cell equivalent is
    /// <see cref="ShardedWorldServer.SpawnEntity"/>.)
    /// </summary>
    public long SpawnEntity(float x, float z, Action<World, Entity>? configure = null)
    {
        long netId = allocator.Next().Value;
        Entity e = world.Spawn();
        world.Set(e, new NetId(netId));
        world.Set(e, new ReplicatedPosition { Value = new Vector3(x, 0f, z) });
        configure?.Invoke(world, e);
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

    private void TrackCorrection(int slot, in PlayerMoveState prev, in MoveCommand cmd, in PlayerMoveState after, float dt)
    {
        float correction = MovementAnomaly.CorrectionDistance(prev, cmd, after, dt, tuning);
        if (MovementAnomaly.RegisterCorrection(correctionStreakBySlot, slot, correction, config.AntiCheat))
            Raise(slot, SuspiciousReason.MovementCorrection, correction);
    }

    /// <summary>Raised at the START of every <see cref="Tick"/> (before movement and the snapshot pass), with the
    /// tick <c>dt</c>, where a consumer runs its server-authoritative non-player (NPC/enemy) behaviour, writing each
    /// entity's <see cref="ReplicatedPosition"/> so the change reaches clients in the same tick. No-op until
    /// subscribed. The multi-cell equivalent is <see cref="ShardedWorldServer.OnBeforeTick"/>.</summary>
    public event Action<float>? OnBeforeTick;

    /// <summary>Steps one authoritative frame: apply each client's queued input, then serve every client its AoI.</summary>
    public void Tick(float dt)
    {
        OnBeforeTick?.Invoke(dt);   // consumer NPC/enemy brains run before movement + serving
        admin.Drain(ApplyAdminCommand);
        // Authoritative movement: one command per player per tick.
        var slots = new List<int>(netIdBySlot.Keys);
        foreach (int slot in slots)
        {
            MoveCommand cmd = commands.Dequeue(slot, out int ack);
            lastAckBySlot[slot] = ack;
            PlayerMoveState prev = stateBySlot[slot];
            PlayerMoveState state = simulator.Step(prev, cmd, dt);
            stateBySlot[slot] = state;
            world.Set(entityBySlot[slot], new ReplicatedPosition { Value = state.Position });
            world.Set(entityBySlot[slot], MovementState.From(state));   // replicate the vertical axis
            if (config.AntiCheat.CorrectionEnabled) TrackCorrection(slot, prev, cmd, state, dt);
        }

        // Rebuild AoI index from current positions.
        interest.Clear();
        world.ForEach<NetId>((Entity e, ref NetId id) =>
        {
            if (world.TryGet(e, out ReplicatedPosition p)) interest.Insert(id.Value, p.Value.X, p.Value.Z);
        });

        // Serve each client its area-of-interest, headered with its own net id + move ack. Delta-capable clients get
        // a per-client AoI delta (only what changed since their acknowledged baseline); everyone else a full snapshot.
        deltaReplicator?.BeginTick();
        foreach (int slot in slots)
        {
            long netId = netIdBySlot[slot];
            Vector3 p = stateBySlot[slot].Position;
            HashSet<long> set = interest.Query(p.X, p.Z, config.InterestRadius);
            MoveProtocol.ServerFrameKind kind;
            byte[] body;
            if (deltaReplicator is not null && deltaCapableSlots.Contains(slot))
            {
                // Owner-scope the Replicate channel to this client's own player, so an OwnerOnly component is served
                // only on the client's own entity, never on another player it observes.
                body = deltaReplicator.WriteFor(slot, world, set, netId);
                kind = MoveProtocol.ServerFrameKind.Delta;
            }
            else
            {
                body = SnapshotWriter.WriteFiltered(world, registry, set, ReplicationChannels.Replicate, netId);
                kind = MoveProtocol.ServerFrameKind.Snapshot;
            }
            byte[] frame = MoveProtocol.EncodeSnapshotFrame(netId, lastAckBySlot[slot], body);
            byte[] envelope = MoveProtocol.EncodeServerFrame(kind, frame);
            net.SendTo(slot, envelope, NetChannelReliability.ReliableOrdered);
        }

        // Clear the per-tick ECS change-tracking + event sets so they don't accumulate on a long-running server.
        // Neither serve path reads them - a full snapshot re-serializes present components and the delta path diffs
        // its own captured baseline - so clearing here is safe; a consumer that reads them opts out via
        // WorldServerConfig.AdvanceWorldTick.
        if (config.AdvanceWorldTick) world.AdvanceTick();
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
                if (slot >= 0 && stateBySlot.TryGetValue(slot, out PlayerMoveState st))
                {
                    st.Position = cmd.Position;
                    st.VerticalVelocity = 0f;
                    SetPlayerState(slot, st);
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
            string acct = accountIdBySlot.TryGetValue(slot, out string? a) ? a : string.Empty;
            PlayerMoveState st = stateBySlot.TryGetValue(slot, out PlayerMoveState s) ? s : default;
            long netId = netIdBySlot[slot];
            string name = string.Empty;
            if (entityBySlot.TryGetValue(slot, out Entity e) && world.TryGet(e, out PlayerIdentity pi))
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
        // Drop any stale delta baseline / capability on the recycled slot: the new occupant re-advertises and
        // re-baselines from scratch (it starts on full snapshots until its own DeltaCapable hello arrives).
        deltaReplicator?.Forget(slot);
        deltaCapableSlots.Remove(slot);

        Vector3 spawn = config.SpawnPosition?.Invoke(slot) ?? new Vector3(slot * 2f, 0f, 0f);
        // Ground-clamp the spawn (an idle step settles Y onto the terrain + half-height).
        PlayerMoveState state = simulator.Step(new PlayerMoveState { Position = spawn }, MoveCommand.Idle, config.TickSeconds);

        long netId = allocator.Next().Value;
        Entity e = world.Spawn();
        world.Set(e, new NetId(netId));
        world.Set(e, new ReplicatedPosition { Value = state.Position });
        world.Set(e, MovementState.From(state));   // vertical axis present from the first snapshot

        netIdBySlot[slot] = netId;
        entityBySlot[slot] = e;
        stateBySlot[slot] = state;
        lastAckBySlot[slot] = -1;
        accountIdBySlot[slot] = accountId;
        // Fresh per-connection anti-cheat state (a recycled slot starts from a full bucket / zero streak).
        RateLimiter? limiter = config.AntiCheat.CreateLimiter(config.TickSeconds);
        if (limiter is not null) rateBySlot[slot] = limiter; else rateBySlot.Remove(slot);
        correctionStreakBySlot[slot] = 0;

        // A display name carried on the connect token (a SignedToken claim) is applied here so token games get
        // nameplates for free; a DB-sourced name is set from the PlayerJoined handler instead (or overrides this).
        if (!string.IsNullOrEmpty(displayName)) world.Set(e, new PlayerIdentity { DisplayName = displayName });

        PlayerJoined?.Invoke(slot, accountId);
    }

    // Idempotent by design: safe to call more than once for the same slot. The TryGetValue guards below make a repeat
    // call a no-op (PlayerLeaving cannot double-fire, save-on-leave cannot double-persist), and every Remove is a
    // no-op on a missing key. This is load-bearing: Disconnect(slot) calls OnLeave synchronously, and a real transport
    // may later surface a Left event for the same slot through Poll, calling OnLeave a second time.
    private void OnLeave(int slot)
    {
        if (accountIdBySlot.TryGetValue(slot, out string? acct) && stateBySlot.TryGetValue(slot, out PlayerMoveState final))
            PlayerLeaving?.Invoke(slot, acct, final);

        if (entityBySlot.TryGetValue(slot, out Entity e) && world.IsAlive(e)) world.Despawn(e);
        netIdBySlot.Remove(slot);
        entityBySlot.Remove(slot);
        stateBySlot.Remove(slot);
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
}
