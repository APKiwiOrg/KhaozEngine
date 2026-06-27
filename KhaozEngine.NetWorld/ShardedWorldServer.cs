using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using KhaozEngine.Collision;
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
public sealed class ShardedWorldServer : IWorldPersistenceHost
{
    private readonly ShardedWorldServerConfig config;
    private readonly ReplicationRegistry registry = MoveProtocol.CreateRegistry();
    private readonly ShardHost host;
    private readonly NetServer net;
    private readonly RemoteCommandQueue<MoveCommand> commands = new(neutralCommand: default);
    private readonly PlayerMovementSystem movement;
    private readonly PlayerMoveSimulator spawnClamp;

    private readonly Dictionary<int, int> netIdBySlot = new();
    private readonly Dictionary<int, int> lastAckBySlot = new();
    private readonly Dictionary<int, string> accountIdBySlot = new();
    private readonly HashSet<CellCoord> wiredCells = new();
    private int nextNetId = 1;

    public ShardedWorldServer(INetTransport transport, ShardedWorldServerConfig config,
        Func<float, float, float> groundHeight, MoveTuning tuning, Func<float, float, Vector3>? groundNormal = null,
        WorldBounds? bounds = null, WorldColliders? colliders = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        if (groundHeight is null) throw new ArgumentNullException(nameof(groundHeight));
        if (config.InterestRadius > config.OverlapMargin)
            throw new ArgumentException(
                $"InterestRadius {config.InterestRadius} must be <= OverlapMargin {config.OverlapMargin} so the home cell can hold the full AoI as ghosts.",
                nameof(config));

        movement = new PlayerMovementSystem(groundHeight, tuning, groundNormal, bounds, colliders);
        spawnClamp = new PlayerMoveSimulator(groundHeight, tuning, groundNormal, bounds, colliders);
        host = new ShardHost(
            cellSize: config.CellSize,
            tickSeconds: config.TickSeconds,
            registry: registry,
            interestCellSize: config.CellSize,
            overlapMargin: config.OverlapMargin,
            positionAccessor: PositionAccessor);
        net = new NetServer(transport, config.MaxPlayers, new AllowAllAuthenticator());
    }

    /// <summary>The shard topology (cells, ownership, ghosts).</summary>
    public ShardHost Host => host;
    /// <summary>The replicated-component registry; clients build the matching one via MoveProtocol.</summary>
    public ReplicationRegistry Registry => registry;
    /// <summary>Number of joined players.</summary>
    public int PlayerCount => netIdBySlot.Count;
    /// <summary>The net id of the player entity for a joined slot.</summary>
    public bool TryGetPlayerNetId(int slot, out int netId) => netIdBySlot.TryGetValue(slot, out netId);

    /// <summary>The worker pool the per-cell movement tick fans across (defaults to single-threaded).</summary>
    public IJobScheduler Scheduler { get => host.Scheduler; set => host.Scheduler = value; }

    /// <inheritdoc/>
    public event Action<int, string>? PlayerJoined;
    /// <inheritdoc/>
    public event Action<int, string, PlayerMoveState>? PlayerLeaving;

    /// <inheritdoc/>
    public IReadOnlyCollection<int> JoinedSlots => netIdBySlot.Keys;

    /// <inheritdoc/>
    public bool TryGetAccountId(int slot, out string accountId) => accountIdBySlot.TryGetValue(slot, out accountId!);

    /// <summary>The current authoritative state for a joined slot, read from its owning cell (cell-agnostic).</summary>
    public bool TryGetPlayerState(int slot, out PlayerMoveState state)
    {
        if (netIdBySlot.TryGetValue(slot, out int netId)
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

    /// <summary>Places a joined player at <paramref name="state"/> (load-on-join). Writes its owning cell's
    /// <see cref="ReplicatedPosition"/>; if that position falls in another cell the next <see cref="Tick"/>'s
    /// handoff relocates the entity there (NetId stable). No-op for an unknown slot.</summary>
    public void SetPlayerState(int slot, in PlayerMoveState state)
    {
        if (netIdBySlot.TryGetValue(slot, out int netId) && host.TryGetOwner(netId, out CellSim cell, out Entity e))
        {
            cell.World.Set(e, new ReplicatedPosition { Value = state.Position });
            cell.World.Set(e, MovementState.From(state));
        }
    }

    /// <summary>Ingests session events (join/leave) and client input. Call once before <see cref="Tick"/>.</summary>
    public void Poll()
    {
        net.Poll();
        while (net.TryDequeueEvent(out ServerSessionEvent ev))
        {
            switch (ev.Kind)
            {
                case ServerSessionEventKind.Joined:
                    OnJoin(ev.Slot, ev.Data);
                    break;
                case ServerSessionEventKind.Left:
                    OnLeave(ev.Slot);
                    break;
                case ServerSessionEventKind.Data:
                    if (netIdBySlot.ContainsKey(ev.Slot)
                        && MoveProtocol.TryDecodeMove(ev.Data, out int seq, out MoveCommand cmd))
                        commands.Store(ev.Slot, seq, cmd);
                    break;
            }
        }
    }

    /// <summary>Steps one authoritative server frame across every cell, then serves each client its home-cell AoI.</summary>
    public void Tick(float dt)
    {
        var slots = new List<int>(netIdBySlot.Keys);

        // 1. Route each client's input to the cell that owns its player.
        foreach (int slot in slots)
        {
            MoveCommand cmd = commands.Dequeue(slot, out int ack);
            lastAckBySlot[slot] = ack;
            if (host.TryGetOwner(netIdBySlot[slot], out CellSim cell, out Entity e))
                cell.World.Set(e, new PendingMove { Command = cmd });
        }

        // 2. Make sure every (possibly newly-created) cell runs the movement system.
        foreach (CellSim cell in host.Cells) EnsureWired(cell);

        // 3. Authoritative movement: one fixed sub-tick per frame, fanned across the scheduler.
        host.Tick(dt, maxTicksPerFrame: 1);

        // 4. Authority follows entities across boundaries (exactly-once), then refresh border ghosts.
        host.ProcessHandoffs();
        host.SyncGhosts();

        // 5. Serve each client its home-cell area-of-interest, framed for the unchanged WorldClient.
        foreach (int slot in slots)
        {
            if (!netIdBySlot.TryGetValue(slot, out int netId)) continue;
            byte[] snapshot = host.SnapshotForClient(slot, config.InterestRadius);
            byte[] frame = MoveProtocol.EncodeSnapshotFrame(netId, lastAckBySlot[slot], snapshot);
            net.SendTo(slot, frame, NetChannelReliability.ReliableOrdered);
        }
    }

    private void OnJoin(int slot, byte[] token)
    {
        Vector3 spawn = config.SpawnPosition?.Invoke(slot) ?? new Vector3(slot * 2f, 0f, 0f);
        // Ground-clamp the spawn (an idle step settles Y onto the terrain + half-height).
        PlayerMoveState state = spawnClamp.Step(new PlayerMoveState { Position = spawn }, MoveCommand.Idle, config.TickSeconds);

        int netId = nextNetId++;
        Entity e = host.SpawnAt(state.Position.X, state.Position.Z, out CellSim cell);
        cell.World.Set(e, new NetId(netId));
        cell.World.Set(e, new ReplicatedPosition { Value = state.Position });
        cell.World.Set(e, MovementState.From(state));   // vertical axis: present at spawn, carried across handoff
        EnsureWired(cell);

        string accountId = token is { Length: > 0 } ? Encoding.UTF8.GetString(token) : $"guest:{slot}";
        netIdBySlot[slot] = netId;
        lastAckBySlot[slot] = -1;
        accountIdBySlot[slot] = accountId;
        host.BindClient(slot, netId);

        PlayerJoined?.Invoke(slot, accountId);
    }

    private void OnLeave(int slot)
    {
        if (netIdBySlot.TryGetValue(slot, out int netId))
        {
            if (accountIdBySlot.TryGetValue(slot, out string? acct) && TryGetPlayerState(slot, out PlayerMoveState final))
                PlayerLeaving?.Invoke(slot, acct, final);
            if (host.TryGetOwner(netId, out CellSim cell, out Entity e) && cell.World.IsAlive(e))
                cell.World.Despawn(e);
        }
        host.UnbindClient(slot);
        netIdBySlot.Remove(slot);
        lastAckBySlot.Remove(slot);
        accountIdBySlot.Remove(slot);
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
