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

    /// <summary>Run the simulation in an ISLAND FRAME that follows the anchored player, so the movement step's
    /// carried state stays small however far the world extends. <b>ON by default</b> since the wire carries the
    /// frame stamp: a framed server and its client now step in the SAME space, so framing is a straight precision
    /// win at range and costs nothing at the origin (a game that never leaves it never re-anchors). See
    /// <see cref="WorldServer.IslandFrame"/> for what it changes and what it deliberately does not.
    /// <para>A flat server is ONE island and follows ONE player. A world with players spread across it needs an
    /// island per region, which is <see cref="ShardedWorldServer"/> and its
    /// <see cref="ShardedWorldServerConfig.FrameAnchoring"/>.</para>
    /// <para>Requires a physics world that can rebase (<c>IPhysicsWorld.CanRebase</c>) when one is supplied at all:
    /// a framed step querying an unframed world is a wrong answer, not an imprecise one, so the constructor
    /// refuses the combination instead of producing it. Set this false to keep a custom non-rebasable backend, at
    /// the cost of the precision.</para></summary>
    public bool FrameAnchoring { get; init; } = true;

    /// <summary>The coordinate space this server's sampler delegates (ground height, ground normal, medium) read.
    /// Only meaningful with <see cref="FrameAnchoring"/> on. <see cref="NetWorld.SamplerSpace.World"/> (the default)
    /// means they keep taking absolute coordinates and the step converts for them, which is the zero-work adoption
    /// step and still fixes the accumulating half of the problem. <see cref="NetWorld.SamplerSpace.Frame"/> is the
    /// full fix, for a game whose ground follow already comes from the island's own physics world.</summary>
    public SamplerSpace SamplerSpace { get; init; } = SamplerSpace.World;

    /// <summary>Maximum payload size (bytes) accepted on a client-to-server game message
    /// (<see cref="WorldClient.SendGameMessage"/>). A larger payload is DROPPED (never dispatched to
    /// <see cref="WorldServer.OnGameMessage"/>) and flagged <see cref="SuspiciousReason.OversizedMessage"/> so a
    /// hostile client can't exhaust the host with an outsized frame. The payload is opaque bytes to the engine; this
    /// is the only bound it enforces on it. Default 1024. The rate limiter (<see cref="AntiCheat"/>) runs in front of
    /// this, so game messages share the per-connection flood budget with moves.</summary>
    public int MaxGameMessageBytes { get; init; } = 1024;

    /// <summary>What happens when a client presents a connect token whose authenticated subject ALREADY holds a slot
    /// on this server (one account, two clients). Default <see cref="DuplicateSessionPolicy.KickOlder"/>: the new
    /// session wins and the older one is disconnected with a distinct reason the client surfaces
    /// (<see cref="DisconnectReason.SignedInElsewhere"/>), its leave running before the new join is admitted so
    /// persistence sees leave-then-join rather than two live sessions sharing one account record. Set
    /// <see cref="DuplicateSessionPolicy.RefuseNewer"/> to keep the existing session and refuse the newcomer instead.
    /// A TOKENLESS connection has no subject and is never deduped.</summary>
    public DuplicateSessionPolicy DuplicateSessions { get; init; } = DuplicateSessionPolicy.KickOlder;
}

/// <summary>
/// Reference single-<see cref="World"/> authoritative movement server. A <see cref="NetServer"/> session layer
/// spawns one player entity per connection; each tick it drains that client's queued <see cref="MoveCommand"/>,
/// runs the shared <see cref="PlayerMoveSimulator"/> (ground-clamped), and serves every client a per-area-of-
/// interest snapshot (the indexed <see cref="SnapshotWriter.WriteFiltered(WorldSnapshotIndex, SnapshotScratch, World, ReplicationRegistry, IReadOnlySet{long}, ReplicationChannels, long?)"/>
/// over an <see cref="InterestGrid"/>, off a <see cref="WorldSnapshotIndex"/> rebuilt at most once per tick and shared
/// across every non-delta client served that tick) prefixed with that client's net id + last-acked move seq so the
/// client can reconcile. Headless, transport-injected.
/// The multi-cell variant is <see cref="ShardedWorldServer"/> (the same movement stack run across a cell grid);
/// this is the single-world slice. Both share <see cref="WorldPersistence"/> via <see cref="IWorldPersistenceHost"/>.
/// </summary>
public sealed partial class WorldServer : IWorldPersistenceHost, IAdminControllable, IWorldPickupHost
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
    // Indexed-snapshot scratch for the non-delta fallback path (a client that never advertised DeltaCapable, or the
    // whole server has DeltaReplication off): reused across every such client served in one Tick, so N clients share
    // one O(worldPop) index rebuild instead of N full-world scans (see WorldSnapshotIndex / SnapshotScratch, and
    // ShardHost's per-tick shared index for the multi-cell analogue). A single-world server has exactly one World,
    // so unlike ShardHost's per-world epoch dictionary this only needs a per-tick "already rebuilt" flag.
    private readonly WorldSnapshotIndex snapshotIndex = new();
    private readonly SnapshotScratch snapshotScratch = new();
    private bool snapshotIndexFreshThisTick;

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
    // The island's physics world, held (not just handed to the simulator) because a re-anchor rebases it in the
    // same gap between two steps that the entities move in. Null when the game supplied none.
    private readonly IPhysicsWorld? physics;
    // The single NetId allocator (node 0 for this single-process server) that both player joins and SpawnEntity draw
    // from, so ids never collide. Replaces the pre-10.0.0 raw ++int counter; see NetIdAllocator for the node-prefix scheme.
    private readonly NetIdAllocator allocator = new();

    public WorldServer(INetTransport transport, WorldServerConfig config,
        Func<float, float, float> groundHeight, MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal = null, WorldBounds? bounds = null, IPhysicsWorld? physics = null,
        IConnectionAuthenticator? authenticator = null, IBanStore? banStore = null,
        ReplicationRegistry? registry = null, Func<float, float, float, MovementMedium>? medium = null)
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
        // A framed step queries the island's physics world in the island's space, so that world has to be able to
        // follow the frame. Refuse the combination at construction rather than serve queries from another space:
        // the failure that would produce is a character standing on nothing, not a rounding artifact.
        if (this.config.FrameAnchoring && physics is not null && !physics.CanRebase)
            throw new ArgumentException(
                "WorldServerConfig.FrameAnchoring needs an IPhysicsWorld that can rebase (CanRebase), or no physics " +
                "world at all: the island's frame and its physics world's Origin move together or the step queries " +
                "colliders in a space its state is not in.", nameof(physics));
        this.physics = physics;
        simulator = new PlayerMoveSimulator(groundHeight, tuning, groundNormal, bounds, physics, medium,
            this.config.SamplerSpace);
        // Always enforce the engine wire generation at connect (independent of any consumer version gate), so a
        // wire-skewed or version-less client is rejected cleanly instead of admitted and left to misparse the wire.
        net = new NetServer(transport, config.MaxPlayers, WireGenerationAuthenticator.Install(authenticator),
            duplicateSessions: config.DuplicateSessions);
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
    /// the <see cref="IConnectionAuthenticator"/> bound the connection to, or
    /// <c><see cref="ResumePositionCache.GuestAccountPrefix"/>{slot}</c> when that subject is empty. A persistence
    /// layer loads the saved record here. That guest key names a RECYCLED slot rather than a person, so
    /// <see cref="ResumePositionCache"/> holds no resume hint for it and <see cref="WorldPersistence"/> files no
    /// record under it either (#647): a guest join is always built on the configured spawn and, unless the game sets
    /// <see cref="WorldPersistenceConfig.PersistGuests"/>, is not persisted at all. Give a game durable identity (a
    /// connect token) if returning players matter.</summary>
    public event Action<int, string>? PlayerJoined;

    /// <summary>Raised just before a player despawns: (slot, accountId, final state). A persistence layer
    /// serializes and saves the final state here (the entity is gone after this returns).</summary>
    public event Action<int, string, PlayerMoveState>? PlayerLeaving;

    /// <summary>The account id for a joined slot (connect token or <c>guest:{slot}</c> fallback).</summary>
    public bool TryGetAccountId(int slot, out string accountId) => accountIdBySlot.TryGetValue(slot, out accountId!);

    /// <summary>The current authoritative movement state for a joined slot, in ABSOLUTE world metres (with a zero
    /// <see cref="PlayerMoveState.FrameAnchor"/>), whatever frame the island happens to be simulating in.</summary>
    public bool TryGetPlayerState(int slot, out PlayerMoveState state)
    {
        if (!stateBySlot.TryGetValue(slot, out state)) return false;
        state = ToAbsolute(state);
        return true;
    }

    /// <summary>The slots of all currently joined players.</summary>
    public IReadOnlyCollection<int> JoinedSlots => netIdBySlot.Keys;

    /// <summary>Overrides a joined player's authoritative state (and its replicated position). Used by
    /// load-on-join to place the player at the saved position, and by admin/self-rescue teleports; no-op for an
    /// unknown slot. When <paramref name="teleport"/> is true the player's monotonic teleport epoch is advanced
    /// (from the server-held value, ignoring any epoch on the incoming state) so the client cuts to the new position
    /// instead of gliding; otherwise the current epoch is preserved. The per-tick movement path bypasses this and
    /// never advances the epoch.
    /// <para>The incoming position is ABSOLUTE world metres unless the state carries a
    /// <see cref="PlayerMoveState.FrameAnchor"/> saying otherwise (one read back from this server does not: it is
    /// handed out absolute). A framed island converts it in, exactly.</para></summary>
    public void SetPlayerState(int slot, in PlayerMoveState state, bool teleport = false)
    {
        if (!entityBySlot.TryGetValue(slot, out Entity e)) return;
        uint baseEpoch = TeleportEpochGuard.BaseEpoch(stateBySlot, slot);   // reports rather than silently zeroing
        PlayerMoveState next = ToIsland(state);
        next.TeleportEpoch = teleport ? baseEpoch + 1u : baseEpoch;   // server owns the monotonic epoch
        stateBySlot[slot] = next;
        world.Set(e, ReplicatedPosition.InFrame(islandFrame, next.Position));
        world.Set(e, MovementState.From(next));
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
    /// Returns the new entity's NetId. Remove it again with <see cref="DespawnEntity"/> (or drive a walk-over
    /// collectible's whole lifecycle with <see cref="WorldPickups"/>). (The multi-cell equivalent is
    /// <see cref="ShardedWorldServer.SpawnEntity"/>.)
    /// </summary>
    public long SpawnEntity(float x, float z, Action<World, Entity>? configure = null)
    {
        long netId = allocator.Next().Value;
        Entity e = world.Spawn();
        world.Set(e, new NetId(netId));
        world.Set(e, ReplicatedPosition.FromWorld(new Vector3(x, 0f, z), islandFrame));   // authored absolute
        spawnedEntities[netId] = e;   // the netId -> entity index TryGetEntity / DespawnEntity resolve through
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

    private void TrackCorrection(int slot, in PlayerMoveState prev, in PlayerMoveState after, float dt)
    {
        float correction = MovementAnomaly.CorrectionDistance(prev, after, dt);
        if (MovementAnomaly.RegisterCorrection(correctionStreakBySlot, slot, correction, config.AntiCheat))
            Raise(slot, SuspiciousReason.MovementCorrection, correction);
    }

    /// <summary>Raised at the START of every <see cref="Tick"/> (before movement and the snapshot pass), with the
    /// tick <c>dt</c>, where a consumer runs its server-authoritative non-player (NPC/enemy) behaviour, writing each
    /// entity's <see cref="ReplicatedPosition"/> so the change reaches clients in the same tick. No-op until
    /// subscribed. The multi-cell equivalent is <see cref="ShardedWorldServer.OnBeforeTick"/>.</summary>
    public event Action<float>? OnBeforeTick;

    /// <summary>Raised at the end of every <see cref="Tick"/> IN WHICH AUTHORITATIVE MOVEMENT RAN, with the tick
    /// <c>dt</c>: after the movement step AND after every client has been served, on the SAME tick. This head steps
    /// every player on every <see cref="Tick"/> whatever the <c>dt</c>, so every Tick IS a movement frame and the
    /// qualifier never withholds an event here - it is stated because it is the shared semantic with
    /// <see cref="ShardedWorldServer.OnAfterTick"/>, whose cells run off a fixed-tick accumulator and therefore skip
    /// frames shorter than one tick. This is the mirror of <see cref="OnBeforeTick"/>
    /// and the place to read POST-STEP state - most usefully the one-tick landing impact
    /// (<see cref="TryGetPlayerState"/> then <c>state.Move.LandingImpactSpeed</c>), which the next tick overwrites, so a
    /// game applying fall damage from <see cref="OnBeforeTick"/> would always be reading the previous tick's world. A
    /// write made here reaches clients on the NEXT tick's snapshot (this tick's has already gone out), which is the
    /// deliberate trade: the hook exists to OBSERVE a settled tick, while <see cref="OnBeforeTick"/> remains the place to
    /// author state that must ship in the same frame. No-op until subscribed. The multi-cell equivalent is
    /// <see cref="ShardedWorldServer.OnAfterTick"/>.</summary>
    public event Action<float>? OnAfterTick;

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
            // Everything this island owns is stepped in the island's frame. Unframed (the default) that frame is
            // WorldFrame.Origin, whose anchor is exactly zero, so every line below is byte-identical to the
            // pre-frame server: a state converted from Origin to Origin is the same state, and InFrame(Origin, p)
            // is the same component as { Value = p }.
            PlayerMoveState prev = ToIsland(stateBySlot[slot]);
            PlayerMoveState state = simulator.Step(prev, cmd, dt);
            stateBySlot[slot] = state;
            world.Set(entityBySlot[slot], ReplicatedPosition.InFrame(islandFrame, state.Position));
            world.Set(entityBySlot[slot], MovementState.From(state));   // replicate the vertical axis
            if (config.AntiCheat.CorrectionEnabled) TrackCorrection(slot, prev, state, dt);
        }

        // Re-anchor the island once the tick's movement has settled, before anything reads a position back.
        ReanchorIsland();

        // Rebuild AoI index from current positions, healing any stale frame stamp on the way past.
        RebuildInterestAndHealFrames();

        // Serve each client its area-of-interest, headered with its own net id + move ack. Delta-capable clients get
        // a per-client AoI delta (only what changed since their acknowledged baseline); everyone else a full snapshot.
        deltaReplicator?.BeginTick();
        // The fallback snapshot index is rebuilt lazily on the first non-delta client this tick (not unconditionally
        // here), so a tick with only delta-capable clients pays no extra world scan. Every client thereafter this
        // tick reuses it, sharing one O(worldPop) rebuild across the tick's fallback clients.
        snapshotIndexFreshThisTick = false;
        foreach (int slot in slots)
        {
            long netId = netIdBySlot[slot];
            Vector3 p = AbsolutePositionOf(slot);   // the interest grid is keyed absolute, so the query is too
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
                // Indexed path: resolves `set` off a netId -> entity index in O(set) instead of a full world scan.
                // Rebuilt once per tick (on the first fallback client) and shared with every other fallback client
                // served this tick. Byte-identical to the old full-scan WriteFiltered (see
                // SnapshotWriterIndexedParityTests and WorldServerSnapshotIndexParityTests).
                if (!snapshotIndexFreshThisTick) { snapshotIndex.Rebuild(world); snapshotIndexFreshThisTick = true; }
                body = SnapshotWriter.WriteFiltered(snapshotIndex, snapshotScratch, world, registry, set, ReplicationChannels.Replicate, netId);
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
        OnAfterTick?.Invoke(dt);   // consumer post-step observers (landing impacts, …) run after movement + serving
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

    /// <summary>Sets a player's per-entity HORIZONTAL speed multiplier: haste (&gt; 1), slow (&lt; 1), root (0),
    /// unmodified (1). The engine owns the multiplier and its plumbing (authoritative step, client prediction,
    /// reconcile replay, and the anti-cheat correction check all read the same value). The GAME owns duration,
    /// stacking, and what granted it, and ends a buff by calling this again with <c>1f</c>. Queued and applied on the
    /// host thread at the top of the next <see cref="Tick"/>, so it is safe from any thread. <paramref name="scale"/>
    /// is clamped to <c>[0, MovementState.MaxSpeedScale]</c> and quantized to the wire's granularity before it reaches
    /// the sim, so both heads run the identical value. No-op for an unknown player. The single-<see cref="World"/>
    /// twin of <see cref="ShardedWorldServer.SetSpeedScale"/>, which documents the seam in full.</summary>
    /// <param name="target">The player, by slot or account id.</param>
    /// <param name="scale">The horizontal speed multiplier (1 = unmodified).</param>
    public void SetSpeedScale(PlayerRef target, float scale) =>
        admin.Enqueue(new AdminCommand { Kind = AdminCommandKind.SpeedScale, Target = target, Scale = scale });

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
                    SetPlayerState(slot, st, teleport: true);   // admin + self-rescue teleports cut on the client
                }
                break;
            }
            case AdminCommandKind.SpeedScale:
            {
                // Store the DECODED quantized value, not the caller's raw float: this head steps from the per-slot
                // PlayerMoveState while the client replays from the wire byte, so rounding here is what makes the two
                // bit-identical instead of leaving a permanent sub-percent drift for the whole duration of the buff.
                int slot = ResolveSlot(cmd.Target);
                if (slot >= 0 && stateBySlot.TryGetValue(slot, out PlayerMoveState st))
                {
                    st.Move.SpeedScale = MovementState.DecodeSpeedScale(MovementState.QuantizeSpeedScale(cmd.Scale));
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
            PlayerMoveState st = stateBySlot.TryGetValue(slot, out PlayerMoveState s) ? ToAbsolute(s) : default;
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
        string accountId = string.IsNullOrEmpty(subject) ? $"{ResumePositionCache.GuestAccountPrefix}{slot}" : subject;
        if (banStore is not null && banStore.IsBanned(accountId))
        {
            // Typed rejection, no engine-authored text: the client maps ServerNoticeKind.Banned to its own localized
            // string (the server owns no string catalog), the same way it does for Maintenance / Shutdown.
            SendNoticeTo(slot, new ServerNotice(ServerNoticeKind.Banned, string.Empty));
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

        Vector3 spawn = JoinSpawn(slot, accountId);   // a known rejoiner is built where it left (see JoinSpawn)
        // Ground-clamp the spawn (an idle step settles Y onto the terrain + half-height). The spawn position is
        // authored ABSOLUTE, so it converts into the island first: the clamp step queries the island's physics
        // world and samplers, which speak the island's space.
        PlayerMoveState state = simulator.Step(ToIsland(new PlayerMoveState { Position = spawn }), MoveCommand.Idle, config.TickSeconds);

        long netId = allocator.Next().Value;
        Entity e = world.Spawn();
        world.Set(e, new NetId(netId));
        world.Set(e, ReplicatedPosition.InFrame(islandFrame, state.Position));
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
        // The final state goes out ABSOLUTE: a persistence layer writes world metres, and a save that carried a
        // runtime frame would break the moment the grid constant changed.
        if (accountIdBySlot.TryGetValue(slot, out string? acct) && stateBySlot.TryGetValue(slot, out PlayerMoveState final))
            PlayerLeaving?.Invoke(slot, acct, ToAbsolute(final));

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
