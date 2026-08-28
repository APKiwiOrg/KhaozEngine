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
    // The one viewer's worth of combat events the serve is building, reused across every client in a pass for the
    // same reason the plane filter's scratch is: a fresh list per client per tick is the whole serve's allocation
    // profile at a couple of hundred players homed in one cell.
    readonly List<TileCombatEvent> viewerCombat = new();
    float tickAccumulator;
    // The serve's plane filter, reused across every client in a pass. planeByNetId is the map, and the three
    // fields under it are what the two delegates would otherwise capture, hoisted so the delegates can be cached
    // instead of rebuilt per client per tick. See FilterToPlane.
    readonly Dictionary<long, int> planeByNetId = new();
    RefAction<NetId>? collectPlanes;
    Predicate<long>? offViewerPlane;
    World? filterWorld;
    HashSet<long>? filterInterest;
    int filterPlane;

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
    /// cell, then the actor step (every spawner ticks and every live actor's command and tag are written), then step
    /// every cell (which is where movement and the arrival facing happen), then authority handoff and border
    /// ghosting, then the action queue, then combat, then serve every client its area of interest, and last the
    /// despawn every actor killed this tick owes. It is not
    /// arbitrary. Commands are routed BEFORE the step so a click takes effect on the tick it arrived rather than
    /// the one after. The actor step sits between the two for both halves of that reason: it is after the drain so
    /// a behaviour reads the tick's player commands, and before the step so an actor's decision moves it on this
    /// tick rather than the next. Handoff runs after the step, because a step is what carries a player over a region boundary,
    /// and ghosting after handoff so the border mirrors reflect the new owners. Actions resolve after both, so an
    /// arrival and its action land on the same tick. Combat resolves after those, so a swing is judged on where
    /// both bodies ended the tick. The serve comes after all of it, so a client sees the whole tick and never half
    /// of it. The one thing that follows the serve is the despawn a death owes an ACTOR, held back so the corpse is
    /// still in the world when each viewer's interest set is built and the blow that killed it therefore reaches
    /// everyone watching the fight.</para>
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

        // The graceful-drain countdown is WALL CLOCK rather than tick count, so it runs down on every frame
        // including the ones that stepped nothing. See BeginDrain in TileWorldServer.Sessions.cs, which owns the
        // field and the reason.
        if (drainRemaining > 0f) drainRemaining = MathF.Max(0f, drainRemaining - MathF.Max(0f, dt));
        // Once, on the first frame the grace is spent. Never inside RunOneTick: closing a session mutates the
        // player index the tick body is iterating, and the serve above has already gone out for this tick. Gated on
        // the grace rather than on IsDrainComplete, which now waits for this close to have happened.
        if (IsDrainGraceSpent && !drainClosed)
        {
            drainClosed = true;
            CloseDrainedSessions();
        }
        // Same rule and the same reason as the close above: releasing a lingering session removes it from the player
        // index, which the tick body iterates. See ExpireLingeringSessions in TileWorldServer.Sessions.cs.
        ExpireLingeringSessions();
    }

    // ONE whole tick, always at exactly TickSeconds. Every cell is fed the same one tick's worth, so the cell
    // accumulators stay in phase with this one and the tick stamped on the wire counts what actually stepped.
    void RunOneTick()
    {
        float dt = config.TickSeconds;
        OnBeforeTick?.Invoke(dt);

        // 0c. The entity target space, snapshotted ONCE. Everything for the rest of this tick resolves a net id to
        //     the same tile: the actor decisions in 1b, the follow inside the movement pass in 2, and a player's own
        //     Attack acceptance in 1. Taken after OnBeforeTick so a head's own spawns are in it. See
        //     TileEntityTargets for why a read-through resolver would let the ECS iteration order decide a fight.
        //
        //     What is NOT in it is anything 1b spawns, since the spawner pass runs after this: a monster built by a
        //     spawner on this tick does not resolve until the next one. Harmless while a lock can only name an
        //     entity a click or a behaviour already saw, which means an entity that existed last tick.
        combatTargets.Refresh(liveCells);

        // Snapshotted, because everything below may add or drop a player and a dictionary cannot be enumerated
        // while it changes. It is also what makes the serve iterate exactly the players the drain routed.
        tickSlots.Clear();
        tickSlots.AddRange(netIdBySlot.Keys);

        // 1. One command per player per tick, routed to the cell that owns them.
        //
        //    The watch list is emptied HERE rather than after it is reported in 4b, so a tick that throws between
        //    the two cannot leave an entry behind for the next tick to report as a freshly broken lock.
        watchedLocks.Clear();
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
            TileCommand admitted = Admit(cmd, arrived, state, slot);
            cell.World.Set(e, new PendingTileCommand { Command = admitted });
            // The lock this player will hold going INTO the movement pass, unless this tick's own command is what
            // breaks it. A WalkTo or an Interact is the player DISENGAGING, which is not a failure to reach and must
            // not produce a notice. An Attack is watched by the target it NAMES rather than by the one on the state,
            // because the click's own tick is the commonest tick for a lock to be refused on: the simulator sets the
            // lock and the follow can clear it again inside that same Advance. See ReportBrokenLocks in
            // TileWorldServer.Combat.cs.
            if (admitted.Kind == TileCommandKind.Attack) watchedLocks.Add((slot, admitted.Target, true));
            else if (state.CombatTarget != 0 && admitted.Kind != TileCommandKind.WalkTo
                && admitted.Kind != TileCommandKind.Interact)
                watchedLocks.Add((slot, state.CombatTarget, false));
        }

        // 1b. Every spawner ticks, then every live actor's command and tag are written. BEFORE the movement pass, so
        //     an actor's decision moves it on this tick and ships in this tick's snapshot, and AFTER the player
        //     drain, so both kinds of entity reach the stepper with the tick's commands already on them. The pass
        //     reads tick-START tiles for every actor, so no actor's decision can depend on another having moved and
        //     the ECS iteration order cannot reach a decision.
        Actors.Tick();

        // 2. Every cell runs the movement system (wired the moment the host creates one), then one fixed sub-tick.
        host.Tick(dt, maxTicksPerFrame: 1);

        // 3. Authority follows a step across a region boundary (exactly once), then refresh the border ghosts.
        host.ProcessHandoffs();
        host.SyncGhosts();

        // 4. Resolve any pending action whose player is now COMMITTED to a reach tile, which is the tick their
        //    walk's last step started rather than the tick their body gets there, and refuse any that cannot get
        //    there at all. See TileWorldServer.Actions.cs.
        ResolveActions();

        // 4b. Roll, then apply, then die. After movement and handoff, so a swing is judged on where both bodies
        //     ended the tick, and before the serve, so a death and the blow that caused it ship together. That
        //     second half is only true because the DESPAWN a death owes an actor waits for step 5b: a corpse taken
        //     out of the world here is gone from the interest set the serve builds, and its killing blow is filtered
        //     out of every viewer's frame. See ReapDeadActors.
        ResolveCombat();
        ReportBrokenLocks();

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
            // The swings, after the snapshot and on the same reliable ordered channel, so a client applies the
            // health this tick produced and then the events that explain it. Only the events whose TARGET this
            // viewer can see, so an ordinary tick costs nothing and a fight on the far side of the world costs
            // nothing either.
            if (combatEvents.Count > 0) SendCombatTo(slot, interest);
        }

        // 5b. The despawn every actor killed at 4b owes, now that every client holding it in interest has been served
        //     the blow that killed it. BEFORE step 6, so the removal's own change tracking is cleared on the tick it
        //     happened, exactly as it was when the despawn ran inside 4b.
        ReapDeadActors();

        // 6. Clear each cell's per-tick change tracking, so it does not accumulate on a long-running server. Exactly
        //    one fixed sub-tick ran per cell, so one advance per cell matches. Indexed over the server's own live
        //    cell list rather than foreach over host.Cells, which is an IReadOnlyCollection and boxes its enumerator
        //    once per tick for nothing.
        for (int i = 0; i < liveCells.Count; i++) liveCells[i].World.AdvanceTick();
        TickCount++;
    }

    // One viewer's slice of the tick's swings. The TARGET is what the interest set is asked about rather than the
    // attacker, because a hitsplat is drawn on the thing being hit: a viewer who can see the target can see the
    // splat, whether or not the attacker is anywhere in view. A frame is sent only when something survived the
    // filter, so a viewer standing away from every fight pays one pass over a short list and no packet.
    void SendCombatTo(int slot, HashSet<long> interest)
    {
        viewerCombat.Clear();
        for (int i = 0; i < combatEvents.Count; i++)
            if (interest.Contains(combatEvents[i].TargetNetId)) viewerCombat.Add(combatEvents[i]);
        if (viewerCombat.Count == 0) return;
        net.SendTo(slot, TileProtocol.EncodeCombat(viewerCombat), NetChannelReliability.ReliableOrdered);
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
    /// <item>An <see cref="TileCommandKind.Attack"/> naming target 0 becomes Continue the same way. Zero is
    /// <see cref="TileMoveState.CombatTarget"/>'s own value for NOT fighting, so it is a malformed command rather
    /// than a click at an entity that went away, and the two do not get the same answer.</item>
    /// <item>Everything else is passed through VERBATIM, cross-plane commands included. The simulator drops those
    /// whole, identically on both heads, and rewriting one here would apply a mode the client's own prediction
    /// did not.</item>
    /// </list>
    /// This is also the only place the action queue is written, because whether a command was ACCEPTED is knowable
    /// only before the step: a cross-plane interact leaves no trace in the state afterwards, and an accepted one
    /// whose target has no reachable tile leaves exactly the same trace. The question itself is
    /// <see cref="TileMoveSimulator.Accepts"/>, so the rule has one definition and the server holds no second copy
    /// of the target seam to re-derive it from.
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
                if (simulator.Accepts(state, cmd)) actions.Clear(slot);
                return cmd;

            case TileCommandKind.Interact:
                // Only a command the simulator will ACCEPT is issued. A cross-plane target is dropped whole by
                // BeginInteract, so it must never reach the queue: there is no CannotReach for it, and an entry
                // would sit armed against every later step. A target that does not resolve at all, and one that
                // resolves on the player's own plane with no reachable tile, both DO reach the queue, because
                // CannotReach is exactly their answer.
                if (simulator.Accepts(state, cmd)) actions.Issue(slot, cmd.Target, TickCount);
                return cmd;

            case TileCommandKind.Attack:
                // TARGET 0 IS NOT AN ID THE WORLD FAILED TO HOLD, it is TileMoveState.CombatTarget's own value for
                // NOT FIGHTING, so an Attack carrying it is a malformed command rather than a click at something
                // that went away. Past this point it is watched, and the clicked branch of ReportBrokenLocks
                // deliberately skips the resolution test, so every crafted frame would be answered with a
                // CannotReach naming an id no world can ever hold, and would spend the player's pending interaction
                // on the way. Refused the way an out-of-range walk goal is rather than dropped on the wire: the
                // frame is otherwise well formed, so the run toggle it carried still applies and its sequence is
                // still acknowledged, which is the answer a client can predict.
                if (cmd.Target == 0) return TileCommand.Continue(cmd.Mode);
                // An attack ABANDONS a pending interaction, exactly as a walk does and for the same reason: the
                // simulator clears the state's own InteractTarget on one, and an entry that outlived it would fire
                // the moment the CHASE happened to pass a reach tile of the thing the player walked away from. Only
                // an APPLIED attack clears, so a cross-plane target abandons nothing.
                if (simulator.Accepts(state, cmd)) actions.Clear(slot);
                return cmd;

            default:
                return cmd;
        }
    }

    // A goal further than MaxGoalRadius away is not pathed at all: the search window is (2r+1)^2 scratch entries,
    // so an unbounded goal is an unbounded allocation a client chooses. Dropped, not clamped, so the client and the
    // server never silently walk to two different tiles. The plane bound is a second, cheaper refusal for a goal
    // naming a plane the world does not have, which the decoder already rejects on a real frame.
    //
    // The two refusals are one predicate but NOT one behaviour, and task 11 has to reproduce the pair exactly or
    // the client mispredicts the run toggle. A goal over PlaneCount is refused here and rewritten to
    // Continue(cmd.Mode), so the toggle it carried still applies. A goal naming a plane the world DOES have but the
    // player is not on passes here, reaches the simulator verbatim, and is dropped whole by Accepts, applying no
    // mode at all. So a client needs BOTH PlaneCount and MaxGoalRadius to predict what the server will do.
    bool GoalInRange(in TileMoveState state, TileCoord goal)
    {
        if (goal.Plane >= config.PlaneCount) return false;
        // In LONG, because both operands are attacker-chosen. TryDecodeCommand deliberately leaves the goal's X and
        // Z unbounded (the radius is measured from where the player stands, so only this side can judge it), so a
        // goal of int.MinValue + tileX makes the subtraction exactly int.MinValue in int, and Math.Abs cannot
        // negate that: the OverflowException comes out of Admit in step 1 of the tick body and takes the whole
        // tick down for every player on the server. A long cannot overflow for int operands, and Math.Abs(long)
        // only refuses long.MinValue, which no pair of ints can reach.
        long dx = (long)goal.X - state.Tile.X, dz = (long)goal.Z - state.Tile.Z;
        return Math.Max(Math.Abs(dx), Math.Abs(dz)) <= config.MaxGoalRadius;
    }

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
    // plane of each member is not in it, so the plane has to come off the entity.
    //
    // Nothing here allocates. A fresh dictionary and three fresh closures per client per tick is what the naive
    // form costs, and at a couple of hundred players homed in one cell that is the whole serve's allocation
    // profile. The map and both delegates are reused, and the three fields below are the captures the delegates
    // would otherwise have closed over: they are live only for the duration of one call, which nothing re-enters.
    void FilterToPlane(World world, HashSet<long> interest, long viewerNetId)
    {
        collectPlanes ??= CollectPlane;
        offViewerPlane ??= IsOffViewerPlane;
        planeByNetId.Clear();
        filterWorld = world;
        filterInterest = interest;
        world.ForEach(collectPlanes);
        // Defensive, not a case the system produces: ShardHost.HomeInterest throws for a player with no position
        // before this is ever reached, and a viewer is always in its own interest set. Kept so that a head which
        // one day serves an unpositioned viewer sees everything rather than only the ground floor.
        if (!planeByNetId.TryGetValue(viewerNetId, out filterPlane)) return;
        interest.RemoveWhere(offViewerPlane);
    }

    // An entity with no tile state (anything a game replicates that is not on the lattice) gets no entry, and an
    // entry is what the filter refuses on, so it is kept. Defensive in the same way as the guard above: nothing
    // without a position reaches the interest grid in the first place, since CellSim's rebuild skips whatever the
    // position accessor refuses.
    void CollectPlane(Entity e, ref NetId id)
    {
        if (!filterInterest!.Contains(id.Value)) return;
        if (filterWorld!.TryGet(e, out TileMoveState s)) planeByNetId[id.Value] = s.Tile.Plane;
    }

    bool IsOffViewerPlane(long netId) => planeByNetId.TryGetValue(netId, out int plane) && plane != filterPlane;
}
