using System;
using System.Collections.Generic;
using System.Numerics;
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
/// <para>Partial across four files, because "the world ticks", "connections come and go" and "a click resolves"
/// are three separate seams and one file for all of them would grow past the size ratchet as each filled in. This
/// file is construction, the host, the player index and state access. <c>TileWorldServer.Tick.cs</c> is the tick
/// order and the serve, <c>TileWorldServer.Sessions.cs</c> the session lifecycle and the persistence-host surface,
/// and <c>TileWorldServer.Actions.cs</c> the pending-action resolution.</para>
/// </summary>
public sealed partial class TileWorldServer : IDisposable
{
    // How many buffered commands a slot may hold before the queue sheds the stale ones and jumps to the newest.
    // The same value ShardedWorldServer takes as its default (its MaxInputBacklog, 8), and here for a sharper
    // reason: the drain is ONE command per player per tick while TileWorldServerConfig.MaxCommandsPerSecond admits
    // ten per tick at 4 Hz, so any sustained excess (a reconnect flush, a delivery burst behind a lag spike, a
    // client ticking faster than the server) buffers up to the queue's per-slot cap and is then replayed one per
    // tick, leaving the server permanently minutes behind live input with no way back. Movement is latest wins, so
    // the skip is correct rather than merely cheap. Eight is two seconds of play, which ordinary one-per-tick input
    // never reaches.
    const int MaxInputBacklog = 8;

    readonly TileWorldServerConfig config;
    readonly NetServer net;
    readonly ShardHost host;
    readonly ReplicationRegistry registry;
    readonly TileMoveSimulator simulator;
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
    // The same set as a list, so the tick can walk it by index. host.Cells is an IReadOnlyCollection whose
    // enumerator boxes on every foreach, and the tick reads it once per tick for the change-tracking advance.
    readonly List<CellSim> liveCells = new();
    // How many ticks a pending action may spend WALKING before it is refused. Derived rather than configured,
    // because there is exactly one legitimate ceiling and it is already in the config: one click produces at most
    // TileMoveOptions.MaxRouteSteps steps, and the slowest mode spends StepTicks ticks on each of them, so a walk
    // still running past that product is not converging on anything. The case is a target that MOVES, which the
    // simulator re-paths toward every tick, so the route never empties and the arrival test is never reached. See
    // ResolveActions in TileWorldServer.Actions.cs. A cap this generous never fires on ordinary play, which is the
    // point: it is a ceiling on a stuck action, not a gameplay timer.
    readonly long maxActionAgeTicks;
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
        this.registry = registry ?? TileProtocol.CreateRegistry();
        maxActionAgeTicks = (long)config.Move.MaxRouteSteps * Math.Max(config.StepTicks.Walk, config.StepTicks.Run);
        simulator = new TileMoveSimulator(map, config.StepTicks, targets, config.Move);
        // The queue's own neutral is never what a starved player is stepped with: Admit replaces it with a Continue
        // at the player's CURRENT mode, because TileCommand.None is a run toggled off. It is supplied because the
        // queue requires one, and it is the right value for a slot with no player behind it.
        commands = new RemoteCommandQueue<TileCommand>(TileCommand.None, maxSlots: Math.Max(1, config.MaxPlayers),
            catchUpThreshold: MaxInputBacklog);
        // The ban predicate is consulted at the DOOR, wrapped around whatever authenticator the head supplied,
        // because a ban has to be answered before a player entity exists. Checked after the join instead, it would
        // spawn the banned account into a cell, serve it to everyone in interest, and despawn it a tick later. The
        // refusal it produces is HandshakeToken.BannedReason, the same token a composed ConnectionGate sends, so a
        // client cannot tell the two paths apart and needs one branch rather than two.
        IConnectionAuthenticator door = authenticator ?? new AllowAllAuthenticator();
        if (config.IsBanned is not null) door = new BanGateAuthenticator(door, config.IsBanned);
        net = new NetServer(transport, config.MaxPlayers, door, duplicateSessions: config.DuplicateSessions);
        host = new ShardHost(config.CellSize, config.TickSeconds, this.registry, config.InterestRadius,
            config.OverlapMargin, PositionOf);
        // Both halves are event driven, and CellCreated is documented to fire synchronously before the new cell can
        // tick or receive a migrated entity, which is exactly the guarantee wiring the mover needs. A per-tick sweep
        // over host.Cells would wire a cell created by a handoff one tick LATE, and cost an enumerator every tick to
        // do it.
        host.CellCreated += EnsureWired;
        host.CellRemoved += cell =>
        {
            if (wiredCells.Remove(cell.Coord)) liveCells.Remove(cell);
        };
    }

    /// <summary>The cell grid, for tests, tooling and a head that persists cells.</summary>
    public ShardHost Host => host;

    /// <summary>The registry both heads must share. Hand a client the one this returns, or build both from
    /// <see cref="TileProtocol.CreateRegistry"/> with the same extension registrations.</summary>
    public ReplicationRegistry Registry => registry;

    /// <summary>Ticks stepped since construction, so this is 1 once the first tick has returned. The snapshot
    /// stamp is the other half of the same convention and is ZERO BASED: a frame carries the INDEX of the tick its
    /// state is the result of, which is this value minus one, so the frame describing the world after the first
    /// tick is stamped 0. <c>TilePendingAction.IssuedTick</c> uses the same index. A client reading a stamp of
    /// <c>n</c> is looking at the world after <c>n + 1</c> ticks.</summary>
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
    /// The player's authoritative state, route included, through <see cref="WithAssembledRoute"/>. NEVER read
    /// <c>TileMoveState.Route</c> off the raw component instead: see that method for what goes wrong.
    /// </summary>
    /// <param name="slot">The player's connection slot.</param>
    /// <param name="state">The authoritative state, default when no cell owns the slot's player.</param>
    public bool TryGetPlayerState(int slot, out TileMoveState state)
    {
        state = default;
        if (!netIdBySlot.TryGetValue(slot, out long netId)) return false;
        if (!host.TryGetOwner(netId, out CellSim cell, out Entity e)) return false;
        if (!cell.World.TryGet(e, out state)) return false;
        cell.World.TryGet(e, out TileRouteState route);
        state = WithAssembledRoute(state, route);
        return true;
    }

    /// <summary>
    /// THE accessor for a player's state, and the one place the route is put back onto it. Every read of
    /// <c>TileMoveState.Route</c> on this server goes through here, because a raw read is WRONG on the tick after a
    /// cell handoff: the destination rebuilds the entity from its Migrate capture, that capture carries the route in
    /// <see cref="TileRouteState"/> (<c>TileMoveState</c>'s own codec deliberately omits it, so an observer is not
    /// shipped every other player's destination), and the rebuilt state therefore reads as IDLE with its
    /// <c>InteractTarget</c> still set. An arrival test on that raw state fires a player's action a whole region
    /// short of the thing they clicked.
    /// <para>The two halves are written together on every step, so this changes nothing for an entity that never
    /// crossed, which is exactly why the bug is invisible until a player walks over a region boundary mid click.
    /// A live route on the state is left alone rather than rebuilt, so a walking player costs no allocation here.
    /// </para>
    /// </summary>
    /// <param name="state">The state as the component holds it.</param>
    /// <param name="route">The entity's <see cref="TileRouteState"/>, default when it has none.</param>
    /// <returns><paramref name="state"/> with its route assembled.</returns>
    internal static TileMoveState WithAssembledRoute(in TileMoveState state, in TileRouteState route)
    {
        TileMoveState s = state;
        if (s.Route.IsIdle && route.Remaining is { Length: > 0 })
            s.Route = TileRoute.FromSteps(s.Tile, route.Remaining);
        return s;
    }

    /// <summary>Overrides a player's authoritative state (a persistence restore, an admin move). With
    /// <paramref name="teleport"/> the monotonic epoch advances, so the client CUTS instead of gliding: a player
    /// moved across the map without one would be smoothed through every tile in between.
    /// <para>This is a DOOR, so it is checked here rather than on a later tick. Every refusal below would otherwise
    /// surface inside <see cref="Tick"/> and take that tick down for every other player on the server: a route over
    /// the cap throws out of the snapshot encoder on the next serve, and a tile on a plane the world does not have,
    /// or in a region the map never loaded, leaves a player nobody can see and who can never step.</para></summary>
    /// <param name="slot">The player's connection slot. An unknown slot is ignored, but the state is validated
    /// first, so a bad one is refused whether or not the slot is live.</param>
    /// <param name="state">The state to write. Its route is written out to <see cref="TileRouteState"/> as well,
    /// so the two halves stay the one answer.</param>
    /// <param name="teleport">Advance the teleport epoch, for a placement the client must not interpolate.</param>
    /// <exception cref="ArgumentException"><paramref name="state"/> stands on a plane at or above
    /// <see cref="TileWorldServerConfig.PlaneCount"/>, on a region the collision map has not loaded, or carries a
    /// route longer than <see cref="TileMoveOptions.MaxRouteSteps"/>. A route whose tiles are not adjacent to each
    /// other is refused too, by <see cref="TileRoute.RemainingSteps"/>, with its own message.</exception>
    public void SetPlayerState(int slot, in TileMoveState state, bool teleport = false)
    {
        TileDirection[] steps = ValidatePlayerState(state);
        if (!netIdBySlot.TryGetValue(slot, out long netId)) return;
        if (!host.TryGetOwner(netId, out CellSim cell, out Entity e)) return;
        TileMoveState next = state;
        if (teleport) next.Epoch = (cell.World.TryGet(e, out TileMoveState old) ? old.Epoch : 0u) + 1u;
        cell.World.Set(e, next);
        cell.World.Set(e, new TileRouteState { Remaining = steps });
    }

    /// <summary>Builds a player entity and binds the slot to it, at the configured spawn or, for an account a
    /// rejoin hint is on file for, on the tile that account left. Returns its net id. Raises
    /// <see cref="PlayerJoined"/> once the entity exists, so a persistence layer loads from there.</summary>
    /// <param name="slot">The connection slot to bind. Binding is by slot rather than by entity, because a slot is
    /// what survives the entity being handed between cells mid walk.</param>
    /// <param name="accountId">The verified account id, which is what persistence keys a record on, and what the
    /// rejoin hint is looked up by.</param>
    /// <param name="displayName">The cosmetic name observers see. Never a rules input.</param>
    public long SpawnPlayer(int slot, string accountId, string displayName)
    {
        // A slot is a seat the next connection recycles, and OnLeave forgets both per-slot queues on the way out.
        // Cleared again here for the seat whose previous occupant's Left was never observed (a transport that
        // dropped it, a head that stopped polling mid session): a stale command high-water mark rejects every
        // sequence number the new player sends and freezes them, and a stale action fires against a player who
        // clicked nothing.
        commands.Forget(slot);
        actions.Forget(slot);

        // A rejoining player is BUILT where they left, so the first snapshot their client sees is already the
        // truth. Built at the configured spawn and moved afterwards, the same rejoin reads to the client as a
        // teleport across the map, and the restore that follows tells it so a second time. The hint is (tileX,
        // plane, tileZ) in the Vector3 the persistence core carries positions in, which TileWorldPersistence's
        // binding writes and reads with the same packing.
        TileCoord at = config.Spawn;
        if (hintProvider is not null && hintProvider(accountId, out Vector3 hint))
        {
            var hinted = new TileCoord((int)hint.X, (int)hint.Z, (int)hint.Y);
            // Checked against the same two rules SetPlayerState refuses a loaded record for. A hint comes from a
            // stored record, and a record naming a plane this world does not have, or a region the map never
            // loaded, would otherwise build a player nobody can see and who can never step. Falling back to the
            // spawn puts them somewhere real, and the load that follows quarantines the record properly.
            if (hinted.Plane >= 0 && hinted.Plane < config.PlaneCount && simulator.Map.HasRegion(hinted.Region))
                at = hinted;
        }

        long netId = allocator.Next().Value;
        Entity e = host.SpawnOwned(at.X, at.Z, netId, out CellSim cell);
        TileMoveState state = TileMoveState.At(at, TileDirection.S);
        cell.World.Set(e, state);
        cell.World.Set(e, new TileRouteState { Remaining = Array.Empty<TileDirection>() });
        cell.World.Set(e, new PendingTileCommand { Command = TileCommand.Continue(state.Mode) });
        cell.World.Set(e, new TileIdentity { DisplayName = displayName });
        netIdBySlot[slot] = netId;
        accountIdBySlot[slot] = accountId;
        lastAckBySlot[slot] = -1;
        rateBySlot[slot] = new RateLimiter(config.CommandBurst, config.MaxCommandsPerSecond * config.TickSeconds);
        host.BindClient(slot, netId);
        PlayerJoined?.Invoke(slot, accountId);
        return netId;
    }

    // Everything a written state can be refused for, taken at the door. This is the pattern the rest of the stack
    // already follows: TileMoveSimulator validates its options at construction "rather than on the first click",
    // and this server checks InterestRadius against OverlapMargin in its constructor rather than on the tick a
    // player first walks near a cell edge. A public setter documented for persistence restores and admin tooling is
    // the same kind of door.
    //
    // The fourth refusal is the RETURN VALUE. RemainingSteps throws when the route's tiles are not adjacent, so
    // computing it here is what takes that refusal at the door alongside the other three, and handing the array
    // back is what stops SetPlayerState recomputing it between its two writes. Left where it was, on the second
    // write, it threw AFTER the first one had landed and left the entity holding a route the simulator cannot
    // walk: TileMoveSimulator.Advance asks TileRoute.Direction for the missing step on the very next tick, and
    // that throw comes out of host.Tick and takes the tick down for every player on the server. Every refusal is
    // ahead of every write now, on every path, which is what the XML doc above promises.
    TileDirection[] ValidatePlayerState(in TileMoveState state)
    {
        if (state.Tile.Plane < 0 || state.Tile.Plane >= config.PlaneCount)
            throw new ArgumentException(
                $"Plane {state.Tile.Plane} is outside the world's {config.PlaneCount} planes.", nameof(state));
        if (!simulator.Map.HasRegion(state.Tile.Region))
            throw new ArgumentException(
                $"Tile {state.Tile} is in region {state.Tile.Region}, which the collision map has not loaded.",
                nameof(state));
        if (state.Route.Remaining > config.Move.MaxRouteSteps)
            throw new ArgumentException(
                $"A route is capped at {config.Move.MaxRouteSteps} steps and this one carries {state.Route.Remaining}."
              + " TileMoveSimulator truncates at TileMoveOptions.MaxRouteSteps, so this route was built elsewhere.",
                nameof(state));
        return state.Route.RemainingSteps(state.Tile);
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

    // A cell created by a spawn, by a handoff, or by the host on demand needs the mover before it ticks. Subscribed
    // to host.CellCreated, so this runs on the creating thread ahead of the cell's first tick. Adding the system
    // twice would step every player in it twice, so the set stays as the guard rather than as a convenience.
    void EnsureWired(CellSim cell)
    {
        if (!wiredCells.Add(cell.Coord)) return;
        liveCells.Add(cell);
        cell.World.AddSystem(new TileMovementSystem(simulator));
    }

    /// <inheritdoc/>
    public void Dispose() => host.Dispose();
}
