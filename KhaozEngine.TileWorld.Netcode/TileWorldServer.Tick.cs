using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The tick half of <see cref="TileWorldServer"/>: the fixed order one server tick runs in, and the plane-filtered
/// serve at the end of it. See the other partial for construction and the player index.
/// </summary>
public sealed partial class TileWorldServer
{
    // The most whole ticks one Tick call runs before it sheds the rest, the rule and the reason FixedTickHost
    // already has (KhaozEngine.Simulation/FixedTickHost.cs, Advance): a host that fell a long way behind (a stall, a
    // debugger break, a long GC) would otherwise try to run every tick it missed, take longer than real time doing
    // it, and be further behind on the frame after. Eight is two seconds at a 250 ms tick, which no healthy head
    // ever reaches.
    const int MaxCatchUpTicks = 8;

    readonly List<int> tickSlots = new();
    // Slots with a pending action this tick, paired with the tick it was issued on, so the resolution order is
    // (IssuedTick, slot) rather than the player index's hash order. Reused, so the sort costs no allocation.
    readonly List<(long issuedTick, int slot)> actionOrder = new();
    static readonly Comparison<(long issuedTick, int slot)> OldestFirst = (a, b) =>
        a.issuedTick != b.issuedTick ? a.issuedTick.CompareTo(b.issuedTick) : a.slot.CompareTo(b.slot);
    float tickAccumulator;
    // task 10: the graceful-drain countdown. Advanced here so the tick order is already the one the session half
    // fills in around, rather than something that has to be threaded through it later.
    float drainRemaining;

    /// <summary>Buffers one command for a slot exactly as an inbound frame would, so a head or a test can drive the
    /// server with no transport. Ignored for an unknown slot, which is what a command arriving one tick after a
    /// disconnect looks like.</summary>
    /// <param name="slot">The player's connection slot.</param>
    /// <param name="seq">The client's own sequence number for this command, which is what a reconciliation replays
    /// from. Must be newer than the last one consumed for the slot, or it is dropped.</param>
    /// <param name="command">The command to buffer.</param>
    public void Enqueue(int slot, int seq, in TileCommand command)
    {
        if (netIdBySlot.ContainsKey(slot)) commands.Store(slot, seq, command);
    }

    /// <summary>
    /// Advances the world by <paramref name="dt"/> seconds of elapsed time, running the whole tick body once per
    /// whole <see cref="TileWorldServerConfig.TickSeconds"/> through the server's own accumulator. A caller may
    /// drive this on any frame clock: a frame shorter than a tick only accumulates and steps nothing, and a longer
    /// one runs the ticks it covers, up to eight, past which the backlog is SHED rather than caught up.
    /// <para>The accumulator is the server's rather than the cells' on purpose. Each cell has a fixed-tick
    /// accumulator of its own, so a body that ran per CALL would drain a command into a cell on a frame the cell
    /// did not step, and the next call would overwrite it with the starvation neutral before any simulator saw it.
    /// Running the whole body per tick is what keeps a drain welded to the step it feeds.</para>
    /// <para>The order inside one tick is the head's own systems, then drain ONE command per player into its owning
    /// cell, then step every cell (which is where movement and the arrival facing happen), then authority handoff
    /// and border ghosting, then the action queue, then serve every client its area of interest. It is not
    /// arbitrary. Commands are routed BEFORE the step so a click takes effect on the tick it arrived rather than
    /// the one after. Handoff runs after the step, because a step is what carries a player over a region boundary,
    /// and ghosting after handoff so the border mirrors reflect the new owners. Actions resolve after both, so an
    /// arrival and its action land on the same tick. The serve is last, so a client sees the whole tick and never
    /// half of it.</para>
    /// </summary>
    /// <param name="dt">Seconds elapsed since the last call. Negative is treated as zero.</param>
    public void Tick(float dt)
    {
        tickAccumulator += MathF.Max(0f, dt);
        int ran = 0;
        while (tickAccumulator >= config.TickSeconds && ran < MaxCatchUpTicks)
        {
            tickAccumulator -= config.TickSeconds;
            RunOneTick();
            ran++;
        }
        // Shed, exactly as FixedTickHost does: keep at most one tick's worth so the very next frame still steps
        // promptly, and throw the rest away rather than owing it forever.
        if (ran >= MaxCatchUpTicks) tickAccumulator = MathF.Min(tickAccumulator, config.TickSeconds);

        // task 10: the graceful-drain countdown is WALL CLOCK rather than tick count, so it runs down on every
        // frame including the ones that stepped nothing. A head asked to drain has a real-time deadline whatever
        // the simulation is doing.
        if (drainRemaining > 0f) drainRemaining = MathF.Max(0f, drainRemaining - MathF.Max(0f, dt));
    }

    // ONE whole tick, always at exactly TickSeconds. Every cell is fed the same one tick's worth, so the cell
    // accumulators stay in phase with this one and the tick stamped on the wire counts what actually stepped.
    void RunOneTick()
    {
        float dt = config.TickSeconds;
        OnBeforeTick?.Invoke(dt);

        // Snapshotted, because everything below may add or drop a player and a dictionary cannot be enumerated
        // while it changes. It is also what makes the serve iterate exactly the players the drain routed.
        tickSlots.Clear();
        tickSlots.AddRange(netIdBySlot.Keys);

        // 1. One command per player per tick, routed to the cell that owns them.
        foreach (int slot in tickSlots)
        {
            TileCommand cmd = commands.Dequeue(slot, out int ack);
            // The ack high-water only advances on a command that was actually buffered, so an unchanged ack IS the
            // starvation signal. Comparing the ack rather than the command is what tells a client's own
            // Continue(Walk), which is a deliberate run-off, apart from the queue's neutral of the same value.
            bool arrived = ack != lastAckBySlot.GetValueOrDefault(slot, -1);
            lastAckBySlot[slot] = ack;
            // Guarded rather than indexed: task 10 lets a slot leave mid tick (a kick, a duplicate session, a
            // rate-limit drop), and the first one raised out of OnBeforeTick or out of this loop would otherwise
            // be a KeyNotFoundException that kills the tick for every other player.
            if (!netIdBySlot.TryGetValue(slot, out long netId)) continue;
            if (!host.TryGetOwner(netId, out CellSim cell, out Entity e)) continue;
            if (!cell.World.TryGet(e, out TileMoveState state)) continue;
            cell.World.Set(e, new PendingTileCommand { Command = Admit(cmd, arrived, state, slot) });
        }

        // 2. Every cell runs the movement system (wired the moment the host creates one), then one fixed sub-tick.
        host.Tick(dt, maxTicksPerFrame: 1);

        // 3. Authority follows a step across a region boundary (exactly once), then refresh the border ghosts.
        host.ProcessHandoffs();
        host.SyncGhosts();

        // 4. Resolve any pending action whose player now stands on a reach tile.
        ResolveActions();

        // 5. Serve each client its home-cell area of interest, filtered to its own plane.
        long serveEpoch = ++interestServeEpoch;
        foreach (int slot in tickSlots)
        {
            if (!netIdBySlot.TryGetValue(slot, out long netId)) continue;
            // A bound player no cell owns would throw out of HomeInterest and take the whole tick, every other
            // player included, down with it. The in-process handoff completes inside ProcessHandoffs, so this is
            // never the ordinary case.
            if (!host.TryGetOwner(netId, out _, out _)) continue;
            (World world, HashSet<long> interest) = HomeInterestFor(slot, netId, serveEpoch);
            byte[] body = SnapshotWriter.WriteFiltered(world, registry, interest,
                ReplicationChannels.Replicate, netId);
            net.SendTo(slot, TileProtocol.EncodeSnapshotFrame(netId, lastAckBySlot[slot], TickCount, body),
                NetChannelReliability.ReliableOrdered);
        }

        // 6. Clear each cell's per-tick change tracking, so it does not accumulate on a long-running server. Exactly
        //    one fixed sub-tick ran per cell, so one advance per cell matches. Indexed over the server's own live
        //    cell list rather than foreach over host.Cells, which is an IReadOnlyCollection and boxes its enumerator
        //    once per tick for nothing.
        for (int i = 0; i < liveCells.Count; i++) liveCells[i].World.AdvanceTick();
        TickCount++;
    }

    // Resolves every pending action whose walk has ENDED. The arrival test is the state's own, not a second reach
    // computation: TileMoveSimulator routes an interact to a reach tile and keeps InteractTarget for exactly as long
    // as that walk is alive, dropping it the moment the route is replaced or cannot be rebuilt. So a route that has
    // emptied with the target still on it IS the arrival, and the same pair of fields answers the abandonment and
    // the failure without the server re-deriving anything the simulator already decided.
    //
    // task 10 adds the other half: the CannotReach notice for the refused case below, and a stale-action cap over
    // TilePendingAction.IssuedTick. What is here is the raise, which is what the tick order needs to be final.
    void ResolveActions()
    {
        if (actions.PendingCount == 0) return;
        foreach (int slot in tickSlots)
        {
            if (!actions.TryPeek(slot, out TilePendingAction pending)) continue;
            if (!netIdBySlot.TryGetValue(slot, out long netId)) continue;
            // Read through TryGetPlayerState, never off the raw component, because the route is what the idle test
            // turns on and a cell handoff leaves the raw state carrying none: the Migrate capture puts the route in
            // TileRouteState, so a player who crossed a region boundary mid walk reads as ARRIVED on the crossing
            // tick and fires the action a region early.
            if (!TryGetPlayerState(slot, out TileMoveState state)) continue;
            if (!state.Route.IsIdle) continue;   // still walking to it

            actions.Clear(slot);
            // The target went with the route: an unreachable click, or a re-path that could not get there. Refused
            // rather than raised, and task 10 is what tells the player so.
            if (state.InteractTarget != pending.Target) continue;
            if (!host.TryGetOwner(netId, out CellSim cell, out Entity e)) continue;
            if (!cell.World.TryGet(e, out TileMoveState live)) continue;

            // Cleared as the action is raised, which is the contract TileMoveState states for the field. Left set,
            // it would re-face the player at the end of every later walk that happened to end on a reach tile.
            live.InteractTarget = 0;
            cell.World.Set(e, live);
            if (pending.Kind == TileActionKind.Interact) OnInteract?.Invoke(slot, netId, pending.Target);
        }
    }

    /// <summary>
    /// What the simulator is actually stepped with, which is not always what the client sent. Three rules, all of
    /// them consequences of the run toggle riding EVERY command:
    /// <list type="bullet">
    /// <item>A tick with no command from the client is <see cref="TileCommand.Continue"/> at the player's CURRENT
    /// mode, never <see cref="TileCommand.None"/>. None is Continue at walk, so a player whose packets stopped
    /// arriving would silently drop out of a run.</item>
    /// <item>A goal farther than <see cref="TileWorldServerConfig.MaxGoalRadius"/> becomes Continue at the mode the
    /// COMMAND carried, so the walk is refused while the toggle the same frame carried still applies.</item>
    /// <item>Everything else is passed through VERBATIM, cross-plane commands included. The simulator drops those
    /// whole, identically on both heads, and rewriting one here would apply a mode the client's own prediction
    /// did not.</item>
    /// </list>
    /// This is also the only place the action queue is written, because whether a command was ACCEPTED is knowable
    /// only before the step: a cross-plane interact leaves no trace in the state afterwards, and an accepted one
    /// whose target has no reachable tile leaves exactly the same trace.
    /// </summary>
    TileCommand Admit(in TileCommand cmd, bool arrived, in TileMoveState state, int slot)
    {
        if (!arrived) return TileCommand.Continue(state.Mode);

        switch (cmd.Kind)
        {
            case TileCommandKind.WalkTo:
                if (!GoalInRange(state, cmd.Goal)) return TileCommand.Continue(cmd.Mode);
                // A walk ABANDONS a pending action, because the simulator clears the state's own InteractTarget on
                // one and the two are records of a single intent. An entry that outlived it would fire the moment
                // the new route happened to pass a reach tile of the thing the player visibly walked away from.
                // Only an APPLIED walk clears: a cross-plane goal is dropped by the simulator, so it abandons
                // nothing.
                if (cmd.Goal.Plane == state.Tile.Plane) actions.Clear(slot);
                return cmd;

            case TileCommandKind.Interact:
                // Only a command the simulator will ACCEPT is issued. A cross-plane target is dropped whole by
                // BeginInteract, so it must never reach the queue: there is no CannotReach for it, and an entry
                // would sit armed against every later step. A target that does not resolve at all, and one that
                // resolves on the player's own plane with no reachable tile, both DO reach the queue, because
                // CannotReach is exactly their answer.
                if (InteractAccepted(cmd.Target, state.Tile.Plane)) actions.Issue(slot, cmd.Target, TickCount);
                return cmd;

            default:
                return cmd;
        }
    }

    // A goal further than MaxGoalRadius away is not pathed at all: the search window is (2r+1)^2 scratch entries,
    // so an unbounded goal is an unbounded allocation a client chooses. Dropped, not clamped, so the client and the
    // server never silently walk to two different tiles. The plane bound is a second, cheaper refusal for a goal
    // naming a plane the world does not have, which the decoder already rejects on a real frame.
    bool GoalInRange(in TileMoveState state, TileCoord goal)
    {
        if (goal.Plane >= config.PlaneCount) return false;
        return Math.Max(Math.Abs(goal.X - state.Tile.X), Math.Abs(goal.Z - state.Tile.Z)) <= config.MaxGoalRadius;
    }

    // The one rule stated in two places, and deliberately: this is the exact condition TileMoveSimulator's
    // BeginInteract returns on, asked of the SAME ITileTargets seam, so the two cannot disagree about a target
    // without disagreeing about the world. Re-derived rather than observed because the step erases the difference
    // (see Admit). An unresolved target is accepted, which is what leaves the CannotReach answer to the resolution.
    bool InteractAccepted(long target, int plane) =>
        targets is null || !targets.TryGetFootprint(target, out _, out int targetPlane) || targetPlane == plane;

    // Planes do not shard: a cell holds every plane of its region, so the plane filter happens here, on the
    // interest set, rather than in the topology. A viewer never receives an entity on another plane.
    (World world, HashSet<long> interest) HomeInterestFor(int slot, long netId, long serveEpoch)
    {
        (World world, HashSet<long> interest) = host.HomeInterest(slot, config.InterestRadius, serveEpoch);
        FilterToPlane(world, interest, netId);
        return (world, interest);
    }

    /// <summary>The net ids one slot would be served this tick, plane filter applied. The test seam for the plane
    /// rule, which is otherwise only observable by decoding a snapshot off a live connection.</summary>
    internal HashSet<long> ServeInterest(int slot)
    {
        if (!netIdBySlot.TryGetValue(slot, out long netId)) return new HashSet<long>();
        return HomeInterestFor(slot, netId, ++interestServeEpoch).interest;
    }

    // One pass over the home cell's world per client, which is the simple form: the interest set is small but the
    // plane of each member is not in it, so the plane has to come off the entity. An entity with no tile state
    // (anything a game replicates that is not on the lattice) has no plane and is kept.
    void FilterToPlane(World world, HashSet<long> interest, long viewerNetId)
    {
        var planeByNetId = new Dictionary<long, int>();
        world.ForEach<NetId>((Entity e, ref NetId id) =>
        {
            if (!interest.Contains(id.Value)) return;
            if (world.TryGet(e, out TileMoveState s)) planeByNetId[id.Value] = s.Tile.Plane;
        });
        // A viewer whose own entity carries no tile state cannot be placed on a plane, so nothing is filtered for
        // it rather than everything above plane 0 being hidden from it.
        if (!planeByNetId.TryGetValue(viewerNetId, out int plane)) return;
        interest.RemoveWhere(id => planeByNetId.TryGetValue(id, out int p) && p != plane);
    }
}
