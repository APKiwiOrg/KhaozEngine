using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Collision;
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
}

/// <summary>
/// Reference single-<see cref="World"/> authoritative movement server. A <see cref="NetServer"/> session layer
/// spawns one player entity per connection; each tick it drains that client's queued <see cref="MoveCommand"/>,
/// runs the shared <see cref="PlayerMoveSimulator"/> (ground-clamped), and serves every client a per-area-of-
/// interest snapshot (<see cref="SnapshotWriter.WriteFiltered"/> over an <see cref="InterestGrid"/>) prefixed
/// with that client's net id + last-acked move seq so the client can reconcile. Headless, transport-injected.
/// The multi-cell variant is <see cref="ShardedWorldServer"/> (the same movement stack run across a cell grid);
/// this is the single-world slice. Both share <see cref="WorldPersistence"/> via <see cref="IWorldPersistenceHost"/>.
/// </summary>
public sealed class WorldServer : IWorldPersistenceHost
{
    private readonly WorldServerConfig config;
    private readonly ReplicationRegistry registry = MoveProtocol.CreateRegistry();
    private readonly World world = new();
    private readonly NetServer net;
    private readonly InterestGrid interest;
    private readonly RemoteCommandQueue<MoveCommand> commands = new(neutralCommand: default);
    private readonly PlayerMoveSimulator simulator;

    private readonly Dictionary<int, int> netIdBySlot = new();
    private readonly Dictionary<int, Entity> entityBySlot = new();
    private readonly Dictionary<int, PlayerMoveState> stateBySlot = new();
    private readonly Dictionary<int, int> lastAckBySlot = new();
    private readonly Dictionary<int, string> accountIdBySlot = new();
    private readonly Dictionary<int, RateLimiter> rateBySlot = new();
    private readonly Dictionary<int, int> correctionStreakBySlot = new();
    private readonly MoveTuning tuning;
    private int nextNetId = 1;

    public WorldServer(INetTransport transport, WorldServerConfig config,
        Func<float, float, float> groundHeight, MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal = null, WorldBounds? bounds = null, WorldColliders? colliders = null,
        WorldSurfaces? surfaces = null, IConnectionAuthenticator? authenticator = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        if (groundHeight is null) throw new ArgumentNullException(nameof(groundHeight));
        this.tuning = tuning;
        simulator = new PlayerMoveSimulator(groundHeight, tuning, groundNormal, bounds, colliders, surfaces);
        net = new NetServer(transport, config.MaxPlayers, authenticator ?? new AllowAllAuthenticator());
        interest = new InterestGrid(MathF.Max(1f, config.InterestRadius));
    }

    /// <summary>Raised when the server flags a connection as suspicious: a malformed/NaN move packet, a per-
    /// connection message-rate trip, or a sustained streak of large authoritative movement corrections. The engine
    /// signals; the game decides the policy (log / kick via <see cref="Disconnect"/> / ban). Allocation-free.</summary>
    public event Action<SuspiciousActivity>? OnSuspiciousActivity;

    private void Raise(int slot, SuspiciousReason reason, float magnitude = 0f) =>
        OnSuspiciousActivity?.Invoke(new SuspiciousActivity(slot, reason, magnitude));

    /// <summary>Disconnects a player's connection (a kick) - the policy seam a game's <see cref="OnSuspiciousActivity"/>
    /// handler calls. No-op for an unknown slot.</summary>
    public void Disconnect(int slot) => net.Disconnect(slot);

    /// <summary>The authoritative ECS world.</summary>
    public World World => world;
    /// <summary>The replicated-component registry; clients build the matching one via MoveProtocol.</summary>
    public ReplicationRegistry Registry => registry;
    /// <summary>Number of joined players.</summary>
    public int PlayerCount => netIdBySlot.Count;
    /// <summary>The net id of the player entity for a joined slot.</summary>
    public bool TryGetPlayerNetId(int slot, out int netId) => netIdBySlot.TryGetValue(slot, out netId);

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
        // Malformed / NaN / Inf packets are rejected by the decode and flagged.
        if (!MoveProtocol.TryDecodeMove(data, out int seq, out MoveCommand cmd))
        {
            Raise(slot, SuspiciousReason.MalformedPacket);
            return;
        }
        commands.Store(slot, seq, cmd);
    }

    private void TrackCorrection(int slot, in PlayerMoveState prev, in MoveCommand cmd, in PlayerMoveState after, float dt)
    {
        float correction = MovementAnomaly.CorrectionDistance(prev, cmd, after, dt, tuning);
        if (MovementAnomaly.RegisterCorrection(correctionStreakBySlot, slot, correction, config.AntiCheat))
            Raise(slot, SuspiciousReason.MovementCorrection, correction);
    }

    /// <summary>Steps one authoritative frame: apply each client's queued input, then serve every client its AoI.</summary>
    public void Tick(float dt)
    {
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

        // Serve each client its area-of-interest snapshot, headered with its own net id + ack.
        foreach (int slot in slots)
        {
            int netId = netIdBySlot[slot];
            Vector3 p = stateBySlot[slot].Position;
            HashSet<int> set = interest.Query(p.X, p.Z, config.InterestRadius);
            byte[] snapshot = SnapshotWriter.WriteFiltered(world, registry, set);
            byte[] frame = MoveProtocol.EncodeSnapshotFrame(netId, lastAckBySlot[slot], snapshot);
            net.SendTo(slot, frame, NetChannelReliability.ReliableOrdered);
        }

        // Clear the per-tick ECS change-tracking + event sets so they don't accumulate on a long-running server.
        // The authoritative path serves full AoI snapshots (SnapshotWriter.WriteFiltered), never the change sets,
        // so clearing here is safe; a consumer that reads them opts out via WorldServerConfig.AdvanceWorldTick.
        if (config.AdvanceWorldTick) world.AdvanceTick();
    }

    private void OnJoin(int slot, string subject, string displayName)
    {
        // Belt-and-suspenders: clear any stale command-queue state on the (recycled) slot before spawning, in case
        // a prior occupant's Left was ever missed. A fresh session's seqs restart at 0; a stale high-water mark
        // would reject every one and freeze the player (see OnLeave).
        commands.Forget(slot);

        Vector3 spawn = config.SpawnPosition?.Invoke(slot) ?? new Vector3(slot * 2f, 0f, 0f);
        // Ground-clamp the spawn (an idle step settles Y onto the terrain + half-height).
        PlayerMoveState state = simulator.Step(new PlayerMoveState { Position = spawn }, MoveCommand.Idle, config.TickSeconds);

        int netId = nextNetId++;
        Entity e = world.Spawn();
        world.Set(e, new NetId(netId));
        world.Set(e, new ReplicatedPosition { Value = state.Position });
        world.Set(e, MovementState.From(state));   // vertical axis present from the first snapshot

        string accountId = string.IsNullOrEmpty(subject) ? $"guest:{slot}" : subject;
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
        // Drop the slot's command-queue state too. The SlotAllocator recycles this slot to the next connection,
        // whose seqs legitimately restart at 0; without this the stale high-water mark rejects every command and
        // freezes the recycled player (it self-heals only once their seq crawls past the dead mark, minutes later).
        commands.Forget(slot);
    }
}
