using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The authoritative tile server: a <see cref="ShardHost"/> whose cell grid IS the tile region grid, one
/// <see cref="TileMoveSimulator"/> shared by every cell, and a per-cell tick order of drain, step, hand off, act
/// and serve.
/// <para>Tile coordinates ARE the plane the shard grid runs on. Every <see cref="CellCoord.FromWorld"/>, interest
/// insert and query takes (tileX, tileZ) as its floats, with a cell edge of <see cref="TileCells.CellSize"/>, so a
/// cell is exactly a region and a crossing is exactly a region crossing. See <see cref="TileCells"/> for why
/// <c>TileWorldSpace</c> is never consulted here and why planes are filtered in the serve rather than sharded.</para>
/// <para>Deliberately NOT built on <c>ShardedWorldServer</c>: that type constructs the float integrator inside
/// itself and takes terrain samplers as required constructor parameters, so a discrete stepper cannot be handed to
/// it. The connection lifecycle, residency and rate-limiting plumbing here is therefore a re-implementation rather
/// than an extraction, which is a known and accepted cost. When the two servers are seen to converge, the generic
/// core comes out of BOTH of them rather than out of the shipping one alone.</para>
/// <para>Partial across two files up front, because the split between "the world ticks" and "connections come and
/// go" is the natural seam and one file for both would grow past the size ratchet as each half filled in. This
/// file is construction, the host, the player index and state access. <c>TileWorldServer.Tick.cs</c> is the tick
/// order and the serve.</para>
/// </summary>
public sealed partial class TileWorldServer : IDisposable
{
    readonly TileWorldServerConfig config;
    readonly NetServer net;
    readonly ShardHost host;
    readonly ReplicationRegistry registry;
    readonly TileMoveSimulator simulator;
    // Held as well as handed to the simulator, so the command path can ask the SAME seam the simulator will ask
    // whether a target is on the player's plane. See Admit for why that answer has to be known before the step.
    readonly ITileTargets? targets;
    readonly TileActionQueue actions = new();
    readonly RemoteCommandQueue<TileCommand> commands;
    readonly NetIdAllocator allocator = new();
    readonly Dictionary<int, long> netIdBySlot = new();
    readonly Dictionary<int, string> accountIdBySlot = new();
    readonly Dictionary<int, int> lastAckBySlot = new();
    // Built per player at spawn so the budget is already in place, and consulted at the door by the session half.
    readonly Dictionary<int, RateLimiter> rateBySlot = new();
    // Coordinates whose cell already has the movement system. Keyed by coordinate rather than by CellSim because
    // that is what an eviction hands back, and the CellRemoved subscription below drops the entry so a cell
    // recreated at the same coordinate is wired again rather than ticking without a mover.
    readonly HashSet<CellCoord> wiredCells = new();
    long interestServeEpoch;

    /// <summary>Builds a tile server over a transport and a baked collision map.</summary>
    /// <param name="transport">The listening transport sessions arrive on.</param>
    /// <param name="config">The clock, the cadence, the spawn and the knobs. See <see cref="TileWorldServerConfig"/>.</param>
    /// <param name="map">The collision map both heads bake from the same world files.</param>
    /// <param name="targets">Resolves interaction targets, null on a head with no interactions wired.</param>
    /// <param name="authenticator">Gate for inbound connect tokens. Null admits every token.</param>
    /// <param name="registry">The replication registry both heads share. Null builds
    /// <see cref="TileProtocol.CreateRegistry"/>, which is the one a client with no game components needs.</param>
    /// <exception cref="ArgumentNullException"><paramref name="transport"/>, <paramref name="config"/> or
    /// <paramref name="map"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="config"/> asks for a tick of zero seconds or
    /// less.</exception>
    /// <exception cref="ArgumentException"><paramref name="config"/> asks for an interest radius wider than its
    /// overlap margin.</exception>
    public TileWorldServer(INetTransport transport, TileWorldServerConfig config, TileCollisionMap map,
        ITileTargets? targets = null, IConnectionAuthenticator? authenticator = null,
        ReplicationRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(map);
        if (config.TickSeconds <= 0f)
            throw new ArgumentOutOfRangeException(nameof(config), config.TickSeconds, "TickSeconds must be > 0.");
        // Checked here rather than at the first serve. ShardHost throws the same refusal out of HomeInterest, which
        // would land on the tick a player first walked near a cell edge, on a live server, hours after the
        // misconfiguration shipped.
        if (config.InterestRadius > config.OverlapMargin)
            throw new ArgumentException(
                $"InterestRadius {config.InterestRadius} exceeds OverlapMargin {config.OverlapMargin}: the home cell "
              + "cannot hold the full area of interest as ghosts.", nameof(config));

        this.config = config;
        this.targets = targets;
        this.registry = registry ?? TileProtocol.CreateRegistry();
        simulator = new TileMoveSimulator(map, config.StepTicks, targets, config.Move);
        // The queue's own neutral is never what a starved player is stepped with: Admit replaces it with a Continue
        // at the player's CURRENT mode, because TileCommand.None is a run toggled off. It is supplied because the
        // queue requires one, and it is the right value for a slot with no player behind it.
        commands = new RemoteCommandQueue<TileCommand>(TileCommand.None, maxSlots: Math.Max(1, config.MaxPlayers));
        net = new NetServer(transport, config.MaxPlayers, authenticator ?? new AllowAllAuthenticator(),
            duplicateSessions: config.DuplicateSessions);
        host = new ShardHost(config.CellSize, config.TickSeconds, this.registry, config.InterestRadius,
            config.OverlapMargin, PositionOf);
        host.CellRemoved += cell => wiredCells.Remove(cell.Coord);
    }

    /// <summary>The cell grid, for tests, tooling and a head that persists cells.</summary>
    public ShardHost Host => host;

    /// <summary>The registry both heads must share. Hand a client the one this returns, or build both from
    /// <see cref="TileProtocol.CreateRegistry"/> with the same extension registrations.</summary>
    public ReplicationRegistry Registry => registry;

    /// <summary>Ticks stepped since construction. Stamped on every snapshot, so a client can name the
    /// authoritative tick a reconciliation basis belongs to.</summary>
    public long TickCount { get; private set; }

    /// <summary>Players currently joined.</summary>
    public int PlayerCount => netIdBySlot.Count;

    /// <summary>Runs before movement each tick, for a head's own systems (npc brains, spawners, timed content).
    /// Anything it writes ships in the SAME tick's snapshot, which is why it is before the step rather than
    /// after.</summary>
    public event Action<float>? OnBeforeTick;

    /// <summary>Raised as (slot, playerNetId, target) when a validated interaction resolves, which is the tick the
    /// player is standing on a reach tile of the thing they clicked. The engine knows nothing about what an
    /// interaction DOES, so this is where a game takes over.</summary>
    public event Action<int, long, long>? OnInteract;

    /// <summary>Raised as (slot, accountId) when a player entity has been built and bound to a slot.</summary>
    // task 10: the session half raises this from the join path as well, once a connection can produce a player.
    public event Action<int, string>? PlayerJoined;

    /// <summary>The one-deep pending action per player. The test seam for the abandonment rule, which is a
    /// property of the COMMAND PATH rather than of the queue: the queue cannot see a walk, so nothing inside it can
    /// prove it was cleared by one.</summary>
    internal TileActionQueue Actions => actions;

    /// <summary>The player's net id, stable across a cell handoff, which is what lets an observer read a crossing
    /// as movement rather than as a despawn followed by a spawn.</summary>
    /// <param name="slot">The player's connection slot.</param>
    /// <param name="netId">The player entity's net id, 0 when the slot holds no player.</param>
    public bool TryGetPlayerNetId(int slot, out long netId) => netIdBySlot.TryGetValue(slot, out netId);

    /// <summary>The verified account id a slot is bound to.</summary>
    /// <param name="slot">The player's connection slot.</param>
    /// <param name="accountId">The account id, null when the slot holds no player.</param>
    public bool TryGetAccountId(int slot, out string accountId) =>
        accountIdBySlot.TryGetValue(slot, out accountId!);

    /// <summary>
    /// The player's authoritative state, route included. The route is REASSEMBLED from
    /// <see cref="TileRouteState"/> rather than read off the state, because a cell handoff rebuilds the entity from
    /// its Migrate capture and that capture carries the route in the component (the move state's own codec omits
    /// it). The two are written together on every step, so the answer is the same either way for an entity that
    /// never crossed.
    /// </summary>
    /// <param name="slot">The player's connection slot.</param>
    /// <param name="state">The authoritative state, default when no cell owns the slot's player.</param>
    public bool TryGetPlayerState(int slot, out TileMoveState state)
    {
        state = default;
        if (!netIdBySlot.TryGetValue(slot, out long netId)) return false;
        if (!host.TryGetOwner(netId, out CellSim cell, out Entity e)) return false;
        if (!cell.World.TryGet(e, out state)) return false;
        state.Route = TileRoute.FromSteps(state.Tile,
            cell.World.TryGet(e, out TileRouteState r) ? r.Remaining : Array.Empty<TileDirection>());
        return true;
    }

    /// <summary>Overrides a player's authoritative state (a persistence restore, an admin move). With
    /// <paramref name="teleport"/> the monotonic epoch advances, so the client CUTS instead of gliding: a player
    /// moved across the map without one would be smoothed through every tile in between.</summary>
    /// <param name="slot">The player's connection slot. An unknown slot is ignored.</param>
    /// <param name="state">The state to write. Its route is written out to <see cref="TileRouteState"/> as well,
    /// so the two halves stay the one answer.</param>
    /// <param name="teleport">Advance the teleport epoch, for a placement the client must not interpolate.</param>
    public void SetPlayerState(int slot, in TileMoveState state, bool teleport = false)
    {
        if (!netIdBySlot.TryGetValue(slot, out long netId)) return;
        if (!host.TryGetOwner(netId, out CellSim cell, out Entity e)) return;
        TileMoveState next = state;
        if (teleport) next.Epoch = (cell.World.TryGet(e, out TileMoveState old) ? old.Epoch : 0u) + 1u;
        cell.World.Set(e, next);
        cell.World.Set(e, new TileRouteState { Remaining = next.Route.RemainingSteps(next.Tile) });
    }

    /// <summary>Builds a player entity at the configured spawn and binds the slot to it. Returns its net id.</summary>
    /// <param name="slot">The connection slot to bind. Binding is by slot rather than by entity, because a slot is
    /// what survives the entity being handed between cells mid walk.</param>
    /// <param name="accountId">The verified account id, which is what persistence keys a record on.</param>
    /// <param name="displayName">The cosmetic name observers see. Never a rules input.</param>
    public long SpawnPlayer(int slot, string accountId, string displayName)
    {
        long netId = allocator.Next().Value;
        Entity e = host.SpawnOwned(config.Spawn.X, config.Spawn.Z, netId, out CellSim cell);
        TileMoveState state = TileMoveState.At(config.Spawn, TileDirection.S);
        cell.World.Set(e, state);
        cell.World.Set(e, new TileRouteState { Remaining = Array.Empty<TileDirection>() });
        cell.World.Set(e, new PendingTileCommand { Command = TileCommand.Continue(state.Mode) });
        cell.World.Set(e, new TileIdentity { DisplayName = displayName });
        netIdBySlot[slot] = netId;
        accountIdBySlot[slot] = accountId;
        lastAckBySlot[slot] = -1;
        rateBySlot[slot] = new RateLimiter(config.CommandBurst, config.MaxCommandsPerSecond * config.TickSeconds);
        host.BindClient(slot, netId);
        EnsureWired(cell);
        PlayerJoined?.Invoke(slot, accountId);
        return netId;
    }

    // Tile coordinates ARE the plane the shard grid runs on, so the accessor hands the host the tile itself. The
    // plane is deliberately not folded in: a cell holds every plane of its region (see TileCells).
    static bool PositionOf(World world, Entity entity, out float x, out float y)
    {
        if (world.TryGet(entity, out TileMoveState s)) { x = s.Tile.X; y = s.Tile.Z; return true; }
        x = 0f;
        y = 0f;
        return false;
    }

    // A cell created by a spawn, by a handoff, or by the host on demand needs the mover before it ticks. Adding the
    // system twice would step every player in it twice, so the set is the guard rather than a convenience.
    void EnsureWired(CellSim cell)
    {
        if (!wiredCells.Add(cell.Coord)) return;
        cell.World.AddSystem(new TileMovementSystem(simulator));
    }

    /// <inheritdoc/>
    public void Dispose() => host.Dispose();
}
