using System;
using System.Collections.Generic;
using KhaozEngine.Netcode;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The ONE discrete stepper both heads run. Pure over its inputs and integer-only, so a client replay of the same
/// commands over the same collision map reproduces the server's state exactly, which is what lets prediction snap
/// only on a genuine disagreement.
/// <para>A STEP COMMITS ITS TILE WHEN IT STARTS, not when the body arrives. On the tick a step begins,
/// <see cref="TileCollision.CanStep"/> is checked against the live map FROM the tile stood on, and then
/// <see cref="TileMoveState.Tile"/> flips to the step's target, <see cref="TileMoveState.StepFrom"/> records the
/// tile being left, the facing takes the step's direction and the route pops. The remaining ticks of the step are
/// spent gliding this state's derived position from one to the other, so the simulation leads that position by at
/// most one grid step and every rules question is answered about the tile the player is committed to. The local
/// client's inter-tick render easing adds one tick of travel outside this simulator, and an active reconciliation
/// offset adds a separate presentation term. That is the
/// trade a slow tick asks for: a click resolves against where the player is GOING, which is what makes 250 ms
/// gameplay feel like a response rather than a wait. A blocker that appeared in front of the step is caught at the
/// moment it would start rather than at the moment the foot lands: the route is re-pathed ONCE from the current
/// tile to the same end, and if that also fails the route is dropped and the player stands. The tick a route with
/// a pending interaction empties ALSO turns the player toward the target, which is now the tick the LAST step
/// starts, so the whole outcome of a click is written by the one stepper both heads run and it is written a step
/// sooner than the body gets there.</para>
/// <para>The tick that carries a command is a FULL tick: it starts the walk and advances step progress by one, so
/// a click never costs a tick of standing still. That is the rule the step-cadence tests pin, and it is why a
/// freshly issued route reads one tick into its first step rather than zero.</para>
/// <para>THE STEP IN PROGRESS IS NEVER ABANDONED, which is OSRS's rule and the one a re-click while moving is
/// judged by. It needs no special case here any more: a route is always pathed from
/// <see cref="TileMoveState.Tile"/>, and that tile is the one the step in flight is walking INTO, so a click
/// arriving mid-glide continues from where the foot is about to land without touching the glide. Step progress,
/// its total and its origin all ride through a re-click untouched, the mode still lands at the start of the NEXT
/// step, and a cross-plane goal is still dropped whole.</para>
/// <para>Mode rides on EVERY command, <see cref="TileCommandKind.None"/> included, because the wire frame is a
/// fixed size and carries <see cref="TileCommand.Mode"/> whatever the kind. A toggle takes effect at the START of
/// the next step: the step already under way keeps the total it was stamped with, so holding run halfway through a
/// walking step never shortens that step, it only makes the one after it a run. A client sends
/// <see cref="TileCommand.Continue"/> carrying the run state it is holding, on every tick.</para>
/// <para>A HELD DIRECTION enters through the SECOND DOOR into a step, <c>StartSteered</c>, which resolves
/// <see cref="TileCommandKind.Steer"/> against the live map through <see cref="TileSteerResolver"/> at step
/// boundaries only and commits the answer through the same body a routed step does, so a steered step and a
/// routed one are stamped with one cadence and the pace of the whole game stays one line.</para>
/// <para>A LOCKED COMBAT TARGET IS CHASED ON EVERY TICK, by the follow at the top of <c>Advance</c>. That is one
/// more thing the tick does than an interaction, which routes once at the click and never again: a chase re-paths
/// whenever the target's committed tile moves out from under the route it already has, drops its route on the tick
/// it arrives in reach, STEPS THE BODY OUT of the target's footprint when a catch leaves the two overlapping, and
/// clears the lock when the target stops resolving, turns up on another plane, or is the attacker itself. Every
/// one of those questions is asked at the body's own size, <see cref="FootprintOf"/>. The whole of it lives here
/// rather than in the server, because a target followed anywhere else is a second movement authority the client
/// cannot predict. <see cref="Step(in TileMoveState, in TileCommand, float, long)"/>'s <c>self</c> is read by that
/// follow and by nothing else, so a head with combat wired hands it the net id of whatever entity it is
/// stepping.</para>
/// <para>Nothing here is stateful. Every answer comes from the state handed in plus the map, so one instance is
/// shared by every player on a server and by the prediction and reconcile paths on a client, and replaying a tick
/// twice gives the same state twice. The two TARGET SEAMS are the caller's to keep still: the combat resolver a
/// server hands over is refreshed once per tick and answers the same tile for the whole of it, which is what keeps
/// a movement pass order-independent. This class holds no state of its own either way.</para>
/// </summary>
public sealed class TileMoveSimulator : ITickSimulator<TileMoveState, TileCommand>
{
    readonly ITileTargets? targets;
    readonly ITileTargets? combatTargets;

    /// <summary>Builds a stepper over a baked collision map and a step cadence. A <see cref="TileStepTicks"/>
    /// with EITHER count zero (which is what an unset field or a decoded blank gives) falls back to
    /// <see cref="TileStepTicks.Default"/> rather than stepping every tick forever.</summary>
    /// <param name="map">The baked collision map to step and path over.</param>
    /// <param name="stepTicks">Ticks per step, per mode.</param>
    /// <param name="targets">Resolves interaction targets, null on a head that has no interactions wired.</param>
    /// <param name="options">Pathfinder knobs, null for the defaults.</param>
    /// <param name="combatTargets">Resolves combat and entity-interaction targets in the ENTITY space, null on a
    /// head with neither wired. Deliberately a SECOND seam rather than a second lookup inside the first: object ids
    /// and net ids overlap exactly, so one resolver could not tell which space a target named. Appended last rather
    /// than placed beside <paramref name="targets"/> so an existing positional call keeps meaning what it said.</param>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="options"/> asks for a path radius outside the
    /// range <see cref="TilePathfinder.FindPath"/> accepts, or for a route cap outside
    /// 1..<see cref="TileProtocol.MaxRouteSteps"/>.</exception>
    public TileMoveSimulator(TileCollisionMap map, TileStepTicks stepTicks, ITileTargets? targets = null,
        TileMoveOptions? options = null, ITileTargets? combatTargets = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        TileMoveOptions o = options ?? new TileMoveOptions();
        // Checked HERE rather than on the first click. TilePathfinder.FindPath throws for a radius outside its own
        // range, and that throw would otherwise land inside a server tick, on the first move by the first player.
        if (o.MaxPathRadius < 1 || o.MaxPathRadius > TilePathfinder.MaxSearchRadius)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"MaxPathRadius must be 1..{TilePathfinder.MaxSearchRadius}.");
        // Same reasoning, one step further: a cap above the wire's would build routes the encoder refuses, and that
        // refusal would land in a server tick on the first long click rather than here at construction.
        if (o.MaxRouteSteps < 1 || o.MaxRouteSteps > TileProtocol.MaxRouteSteps)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"MaxRouteSteps must be 1..{TileProtocol.MaxRouteSteps}.");
        Map = map;
        // Either count zero is a blank cadence, and a zero RUN is the dangerous half: it survives a Walk-only
        // check as a StepTotal of zero, which commits a tile every tick.
        StepTicks = stepTicks.Walk == 0 || stepTicks.Run == 0 ? TileStepTicks.Default : stepTicks;
        this.targets = targets;
        this.combatTargets = combatTargets;
        MaxPathRadius = o.MaxPathRadius;
        MaxRouteSteps = o.MaxRouteSteps;
    }

    /// <summary>The collision map both heads bake from the same world files. Held rather than copied, so an edit
    /// rebaked into it is seen by the next step, which is exactly how a dynamic blocker reaches this code.</summary>
    public TileCollisionMap Map { get; }

    /// <summary>Ticks per step, per mode.</summary>
    public TileStepTicks StepTicks { get; }

    /// <summary>Half width of the pathfinder's search window.</summary>
    public int MaxPathRadius { get; }

    /// <summary>Longest route one click may produce, in steps. See <see cref="TileMoveOptions.MaxRouteSteps"/>.</summary>
    public int MaxRouteSteps { get; }

    /// <summary>The tiles a state covers as THIS simulator steps it, which is its own
    /// <see cref="TileMoveState.FootprintSize"/> and nothing else: there is no simulator-wide size any more, so every
    /// body is stepped, pathed and reached at the size its state carries. Kept as a member rather than folded into
    /// <see cref="TileMoveState.Footprint"/> because it is the one definition of an ATTACKER's size, which the
    /// server's combat roll asks of the attacker's own simulator so the follow and the roll cannot disagree.</summary>
    /// <param name="state">The state whose anchor and size are read.</param>
    /// <returns>The square of the state's size anchored on its tile, the south-west corner.</returns>
    public TileRect FootprintOf(in TileMoveState state) => state.Footprint;

    /// <summary>
    /// Whether <c>Step</c> would APPLY this command rather than drop it whole. THE definition of acceptance,
    /// and the only one: <c>Step</c>, <c>BeginWalk</c> and <see cref="BeginInteract"/> all ask this
    /// rather than repeating the rule, and so does the server's command path, which has to know BEFORE the step
    /// (afterwards a dropped command and an applied one that achieved nothing leave identical state).
    /// <para>Accepted is NOT "will succeed". An <see cref="TileCommandKind.Interact"/> naming a target that does not
    /// resolve at all, or one that resolves on the player's OWN plane with no reachable tile, is accepted here and
    /// answered with a CannotReach later. That distinction is the whole point: the cleared-route answer is reserved
    /// for a target the player can see and cannot get to, never for one on another floor.</para>
    /// <para>A dropped command applies nothing, its MODE included, so a rejected tick reads exactly as though no
    /// command arrived. Both heads run this, so a client that pre-checks with it refuses the same clicks.</para>
    /// </summary>
    /// <param name="state">The state the command would be applied to. Only its tile's plane is consulted.</param>
    /// <param name="command">The command to weigh.</param>
    /// <returns>False for a cross-plane walk goal, a resolved cross-plane object interaction, an entity
    /// interaction that does not resolve on this plane, a resolved cross-plane combat target, or a
    /// <see cref="TileCommandKind.Steer"/> naming a direction outside the eight. True otherwise,
    /// including <see cref="TileCommandKind.None"/> because its mode is always applied.</returns>
    public bool Accepts(in TileMoveState state, in TileCommand command) => command.Kind switch
    {
        TileCommandKind.WalkTo => command.Goal.Plane == state.Tile.Plane,
        TileCommandKind.Interact =>
            targets is null
            || !targets.TryGetFootprint(command.Target, out _, out int plane)
            || plane == state.Tile.Plane,
        TileCommandKind.InteractEntity =>
            command.Target > 0
            && combatTargets is not null
            && combatTargets.TryGetFootprint(command.Target, out _, out int entityPlane)
            && entityPlane == state.Tile.Plane,
        // A distinct out name because the arms of a switch expression share one scope. The rule is the interaction's
        // rule over the OTHER seam: a target this head cannot resolve at all is accepted and answered later, and one
        // resolved on another plane is refused outright.
        TileCommandKind.Attack =>
            combatTargets is null
            || !combatTargets.TryGetFootprint(command.Target, out _, out int combatPlane)
            || combatPlane == state.Tile.Plane,
        // A steer has no plane to disagree with. The decoder already refused an undefined direction, and this
        // is the backstop for a command built in process.
        TileCommandKind.Steer => (ulong)command.Target <= (ulong)TileDirection.NE,
        _ => true,
    };

    /// <summary>Advances one tick for an entity this stepper is not told the identity of, which is
    /// <see cref="Step(in TileMoveState, in TileCommand, float, long)"/> with a self of 0. THE INTERFACE FORM, and
    /// the one to avoid on a head that has combat wired: a follow that does not know whose state it is stepping
    /// cannot tell a SELF target from another entity standing on the same tile, and the two want opposite answers
    /// (see <c>Follow</c>'s rule 4). Every caller in this package passes the id.</summary>
    /// <param name="state">The state to advance.</param>
    /// <param name="command">This tick's command.</param>
    /// <param name="dt">Ignored, see the overload.</param>
    /// <returns>The advanced state, with the presentation override cleared.</returns>
    public TileMoveState Step(in TileMoveState state, in TileCommand command, float dt) =>
        Step(state, command, dt, 0L);

    /// <summary>Advances one tick: the command replaces the route, then movement runs. A step that STARTS on this
    /// tick commits its tile on it. <paramref name="dt"/> is unused: a tile step is counted in TICKS, not seconds,
    /// which is what keeps two heads on slightly different frame times byte-identical.</summary>
    /// <param name="state">The state to advance.</param>
    /// <param name="command">This tick's command.</param>
    /// <param name="dt">Ignored, see the summary.</param>
    /// <param name="self">The NET ID of the entity <paramref name="state"/> belongs to, 0 when the caller has none
    /// to give. Read by ONE rule, the follow's, and only when a combat target is held: a lock naming this id is the
    /// self lock the follow CLEARS (#741), where a lock naming another entity that happens to stand on the same tile
    /// steps the body out of it. Both heads must pass the SAME id for the same entity or the client mispredicts that
    /// one case, which is why the client binds its own <c>TileWorldClient.LocalNetId</c> to this rather than leaving
    /// it at 0.</param>
    /// <returns>The advanced state, with the presentation override cleared.</returns>
    public TileMoveState Step(in TileMoveState state, in TileCommand command, float dt, long self) =>
        Step(state, command, dt, self, null);

    /// <summary>Advances one tick with caller-owned pathfinder working memory. The scratch changes no movement
    /// result and must not be shared across concurrent calls.</summary>
    /// <param name="state">The state to advance.</param>
    /// <param name="command">This tick's command.</param>
    /// <param name="dt">Ignored because tile movement is counted in ticks.</param>
    /// <param name="self">The net id of the entity being stepped, or 0 when unavailable.</param>
    /// <param name="scratch">Reusable pathfinder working memory, or null to allocate per search.</param>
    /// <returns>The advanced state.</returns>
    public TileMoveState Step(in TileMoveState state, in TileCommand command, float dt, long self,
        TilePathfinderScratch? scratch)
    {
        TileMoveState s = state;
        s.HasRenderOverride = false;

        switch (command.Kind)
        {
            // A goal on another plane is DROPPED whole rather than coerced onto the player's own (see BeginWalk),
            // which is why the guard sits on the case label: an unmatched WalkTo falls past every case here and
            // the tick runs as though no command arrived at all, its mode included.
            case TileCommandKind.WalkTo when Accepts(s, command):
                s = BeginWalk(s, command.Goal, command.Mode, scratch);
                s.InteractTarget = 0;
                s.InteractDomain = TileInteractionDomain.AuthoredObject;
                // A WALK BREAKS A FIGHT, which is how a player disengages and is the same rule OSRS uses. Both
                // targets go for one reason: each is a record of an intent the player has visibly replaced, and one
                // that outlived the walk would keep steering the route back at something they walked away from.
                s.CombatTarget = 0;
                break;
            // A STEER IS A WALK for every rule that asks: it replaces the route and breaks the interaction and the
            // fight exactly as WalkTo does. What it does not do is search. The direction is handed to Advance,
            // which reads it ONLY on a boundary tick, so a steer arriving mid step has done all its work here.
            case TileCommandKind.Steer when Accepts(s, command):
                s.Route = TileRoute.None;
                s.InteractTarget = 0;
                s.InteractDomain = TileInteractionDomain.AuthoredObject;
                s.CombatTarget = 0;
                s.Mode = command.Mode;
                return Advance(s, self, scratch, command.SteerDirection);
            case TileCommandKind.Interact:
                s = BeginInteract(s, command.Target, command.Mode, TileCommandKind.Interact, targets, scratch);
                break;
            case TileCommandKind.InteractEntity:
                s = BeginInteract(s, command.Target, command.Mode, TileCommandKind.InteractEntity, combatTargets,
                    scratch);
                break;
            case TileCommandKind.Attack:
                s = BeginAttack(s, command.Target, command.Mode);
                break;
            // The run toggle rides on every command, so holding run is a property of the TICK rather than of the
            // click that started the route. StepTotal is deliberately not re-stamped: the step already under way
            // keeps the cadence it began with, and the START of the next one stamps the new one.
            case TileCommandKind.None:
                s.Mode = command.Mode;
                break;
        }

        return Advance(s, self, scratch);
    }

    /// <summary>Paths to <paramref name="goal"/> and starts walking it. An unreachable goal walks to the nearest
    /// reachable tile, the rule <see cref="TilePathfinder"/> already implements. A goal the player already stands
    /// on just stands.
    /// <para>The walk is pathed from <see cref="TileMoveState.Tile"/>, always, and that is the whole of the
    /// re-click rule now: the tile is the one the step in flight is walking INTO, so a click landing mid-glide
    /// paths from where the foot is about to land and the step under way is neither restarted nor abandoned.
    /// Nothing about step progress is written here, so the glide keeps its ticks, its total and its origin, and
    /// the new route's first step begins on the tick the body lands.</para>
    /// <para>A goal on ANOTHER PLANE is refused, the same answer <see cref="TileCommandKind.Interact"/> gives a
    /// target on another plane: the state comes back untouched, route, step progress and mode all as they were.
    /// Planes are separate walkable surfaces with no step between them, so pathing the goal on the player's plane
    /// instead would walk them to an X and Z they never clicked.</para></summary>
    /// <param name="state">The state to re-route.</param>
    /// <param name="goal">The tile clicked.</param>
    /// <param name="mode">Walk or run, which decides how many ticks each step of the new route takes.</param>
    /// <returns>The state carrying the new route, or the state unchanged when <paramref name="goal"/> is on
    /// another plane.</returns>
    public TileMoveState BeginWalk(in TileMoveState state, TileCoord goal, TileMoveMode mode)
        => BeginWalk(state, goal, mode, null);

    /// <summary>Paths to a goal using caller-owned working memory and starts walking the resulting route.</summary>
    /// <param name="state">The state to re-route.</param>
    /// <param name="goal">The tile clicked.</param>
    /// <param name="mode">Walk or run.</param>
    /// <param name="scratch">Reusable pathfinder working memory, or null to allocate per search.</param>
    /// <returns>The state carrying the new route.</returns>
    public TileMoveState BeginWalk(in TileMoveState state, TileCoord goal, TileMoveMode mode,
        TilePathfinderScratch? scratch)
    {
        TileMoveState s = state;
        if (!Accepts(s, TileCommand.WalkTo(goal, mode))) return s;
        s.Mode = mode;
        s.Route = RouteFor(TilePathfinder.FindPath(Map, s.Tile.Plane, s.Tile, goal, s.FootprintSize, MaxPathRadius,
            scratch));
        return s;
    }

    // Routes to a reach tile of the target and remembers both its id and domain, so the tick the walk's last step
    // starts faces it through the same resolver. An unknown object target, or a same-plane target with no reachable
    // tile at all, drops the route and clears the target. Entity targets are required to resolve at admission.
    //
    // The reach search runs from TileMoveState.Tile, exactly as BeginWalk's path does, and that tile is the one the
    // step in flight is walking INTO, so a booth clicked mid-glide is measured from where the foot is about to land.
    // The dropped-route answer leaves the glide alone too: nothing here writes step progress, so "cannot reach" is
    // not a reason to yank the avatar back onto the tile it is half off, and the CannotReach lands on THIS tick
    // rather than waiting for the body. The player is already committed to the tile it is answered from.
    //
    // A target on ANOTHER PLANE is not that case. It is refused the way BeginWalk refuses a cross-plane goal, with
    // the state untouched, and that is why acceptance is asked BEFORE the first write. Mode, cadence, route and
    // pending target all have to survive a click the player cannot even see, so the tick reads exactly as a
    // Continue at the mode already held. Reserving the cleared-route answer for a target on the player's OWN plane
    // is what keeps it meaningful: CannotReach then says "I know what you clicked and you cannot get to it", never
    // "that was on another floor". The refusal itself is Accepts, which is the one definition of it.
    TileMoveState BeginInteract(in TileMoveState state, long target, TileMoveMode mode, TileCommandKind kind,
        ITileTargets? resolver, TilePathfinderScratch? scratch)
    {
        TileMoveState s = state;
        var command = new TileCommand(kind, default, mode, target);
        if (!Accepts(s, command)) return s;
        // Seeded, because the resolve is short circuited on a null seam and the compiler cannot see that the
        // branches below only read these once it answered true.
        TileRect footprint = default;
        int plane = 0;
        bool resolved = resolver is not null && resolver.TryGetFootprint(target, out footprint, out plane);

        s.Mode = mode;
        s.InteractTarget = 0;
        s.InteractDomain = TileInteractionDomain.AuthoredObject;
        // The other half of the mutual exclusion BeginAttack states: an interaction and a fight are two records of
        // one intent, and a lock left set here would have the follow re-path the interaction's own route on this very
        // tick, since the follow runs inside the Advance below.
        s.CombatTarget = 0;

        int size = s.FootprintSize;
        if (!resolved || !TileReach.TryNearest(Map, footprint, plane, s.Tile, size, MaxPathRadius,
                out TileCoord reachTile, out TilePath path, scratch))
        {
            s.Route = TileRoute.None;
            return s;
        }

        // The target is remembered on a WALK, so the tick the last step starts can act on it, and a zero step
        // interaction faces the target here because no step will ever run to set the facing for it. Zero step
        // includes the click made while gliding INTO a reach tile: that tile is already committed, so the turn and
        // the action are both due now.
        s.InteractTarget = target;
        s.InteractDomain = TileInteractionTarget.DomainOf(kind);
        s.Route = RouteFor(path);
        if (s.Route.IsIdle) s.Facing = TileReach.FacingToward(Map, footprint, plane, reachTile, size);
        return s;
    }

    // Sets the lock and nothing else. Unlike BeginInteract this routes NOTHING here, because a chase is not a walk
    // to where something is: the FOLLOW inside Advance runs on this tick and on every tick after it, and it is what
    // paths, re-paths and stops. Doing it here as well would path twice on the click's own tick.
    //
    // The two targets clear each other for the reason TileActionQueue gives about its own pair, and a cross-plane
    // target is refused with the state UNTOUCHED, the same answer BeginWalk and BeginInteract give: mode, cadence,
    // route and both targets have to survive a click the player cannot even see.
    TileMoveState BeginAttack(in TileMoveState state, long target, TileMoveMode mode)
    {
        TileMoveState s = state;
        if (!Accepts(s, TileCommand.Attack(target, mode))) return s;
        s.Mode = mode;
        s.InteractTarget = 0;
        s.InteractDomain = TileInteractionDomain.AuthoredObject;
        s.CombatTarget = target;
        return s;
    }

    // THE CHASE, one tick of it, and it lives in the stepper for the reason TileMoveState.CombatTarget's doc gives:
    // anywhere else is a second movement authority a client cannot predict.
    //
    // Rule 5 is what keeps the pathfinding budget honest, and the memo it needs is already on the state. The trigger
    // is "the route end is no longer in range of the TARGET'S FOOTPRINT", never "the target's committed tile moved":
    // the route END is an in-range anchor of wherever the target stood when this last re-pathed, so a target that
    // slides within that anchor's range costs ZERO FindPath calls however far its anchor travels, and one that
    // leaves it re-paths even if its anchor did not move at all (a body that GREW or shrank). Nothing new is stored
    // for it, which is the whole reason the memo is the route rather than a remembered tile.
    //
    // The step in flight is never abandoned here either. Dropping the ROUTE is not abandoning a STEP: a step was
    // committed when it started and its tile is not in the route any more.
    TileMoveState Follow(in TileMoveState state, long self, TilePathfinderScratch? scratch)
    {
        TileMoveState s = state;
        if (s.CombatTarget == 0) return s;

        // 4, ASKED FIRST because it is the one rule here that needs nothing resolved. A lock on ITSELF can never be
        //    in range: its footprint moves with the body, so no tile it could step to is off it. It clears, which is
        //    the answer rule 5 gives any target with no reach tile, and the server says CannotReach (#741). Holding
        //    it, which R1 did, left the body permanently in combat with no roll ever possible.
        //
        //    Ahead of rule 2 so the answer does not depend on the SEAM resolving the local id. Both heads resolve it
        //    today, the server through its entity space and the client through TileRemoteTargets, so neither changes
        //    its answer. What goes away is the client's coupling to that: an id the seam could not resolve used to
        //    fall into rule 2, which clears the lock and KEEPS the route, so a self attack clicked mid walk would
        //    have predicted a walk the server had stopped. Identity is knowable without the seam, so it is asked
        //    without it.
        if (self != 0 && s.CombatTarget == self)
        {
            s.CombatTarget = 0;
            s.Route = TileRoute.None;
            return s;
        }

        // 2. A target that no longer resolves is dead, despawned or out of this head's view. This is the free half
        //    of death handling: the seam's contract already says an id stops resolving the moment the thing it
        //    named stops existing, so nothing has to tell the follow that its target died.
        if (combatTargets is null
            || !combatTargets.TryGetFootprint(s.CombatTarget, out TileRect footprint, out int plane))
        {
            s.CombatTarget = 0;
            return s;
        }

        // 3. Reach never crosses planes, and the rest of the package refuses cross-plane rather than coercing, so a
        //    fight broken by a staircase is broken rather than chased through the floor.
        if (plane != s.Tile.Plane)
        {
            s.CombatTarget = 0;
            return s;
        }

        int size = s.FootprintSize;

        // In range is ONE predicate everywhere a range question is asked: no overlap with the target's footprint, and
        // some tile of this body in the target's one-tile reach set. A body overlapping the target is not in range and
        // falls through to rule 5, whose search routes it out, which is the OSRS answer for a monster under you
        // (#751).
        //
        // A combatant in range faces what it is fighting, on every tick, because the target moves around it
        // mid-fight (#753). Written here so both heads predict the turn and the server's own write is an idempotent
        // backstop.
        if (TileReach.Contains(Map, footprint, plane, s.Tile, size))
        {
            s.Route = TileRoute.None;
            s.Facing = TileReach.FacingToward(Map, footprint, plane, s.Tile, size);
            return s;
        }

        // 5. Re-path only when the route end is no longer in range of the target's footprint, which is the target
        //    moving out from under the route we already have.
        if (!s.Route.IsIdle && TileReach.Contains(Map, footprint, plane, s.Route.End, size)) return s;

        if (!TileReach.TryNearest(Map, footprint, plane, s.Tile, size, MaxPathRadius, out _, out TilePath path,
                scratch))
        {
            // Cannot get there at all. The lock clears and the body stands. The server turns this into the same
            // TileServerReason.CannotReach an unreachable interaction gets, because it is the same fact. A body
            // standing INSIDE a target penned in on every side arrives here too, and the answer is the same one for
            // the same reason: there is no tile it could swing from.
            s.CombatTarget = 0;
            s.Route = TileRoute.None;
            return s;
        }
        s.Route = RouteFor(path);
        return s;
    }

    // One tick of movement, in the order the model demands: the body finishes the step it is walking, and the tile
    // it lands on is the tile that was committed when that step STARTED, so the only thing left to decide is
    // whether the next step starts now.
    //
    // The two doors into Start are asymmetric on purpose, and the asymmetry is the "a click never costs a tick of
    // standing still" rule. Landing spends the tick on the glide that just finished, so the step it starts is left
    // at zero progress and the next tick is its first. Starting from a STANDING state spends the tick on the new
    // step itself, so it counts one immediately, and a freshly clicked route therefore reads one tick in.
    // The chase runs FIRST, before anything about this tick's step is decided, so a route it drops or rebuilds is
    // the route the step below reads. See Follow.
    //
    // A HELD DIRECTION arrives as steer, and it is read at the two doors and nowhere else, which is the whole of
    // "resolution happens on a boundary tick". A steer on any other tick falls through the early return above the
    // landing, so it can neither restart nor redirect the step in flight. Where a route is consulted a steer takes
    // precedence over it, because the command that carried the steer already emptied the route: the ordering is
    // there so the two doors read the same way rather than because the two could ever both be live.
    TileMoveState Advance(in TileMoveState state, long self, TilePathfinderScratch? scratch,
        TileDirection? steer = null)
    {
        TileMoveState s = Follow(state, self, scratch);
        if (s.StepTotal == 0) s.StepTotal = StepTotalFor(s);

        if (s.IsStepping)
        {
            s.StepTicks++;
            if (s.StepTicks < s.StepTotal) return s;
            // The body landed on the tile the simulation already owned. Both fields are normalized rather than left
            // filled, so "standing on Tile" has exactly one spelling everywhere: on the wire, in equality and in
            // every predicate that asks IsStepping.
            s.StepFrom = s.Tile;
            s.StepTicks = 0;
            if (steer is { } landing) return StartSteered(s, landing);
            return s.Route.IsIdle ? s : Start(s, scratch);
        }

        if (steer is { } standing)
        {
            s = StartSteered(s, standing);
            if (s.IsStepping) s.StepTicks++;
            return s;
        }

        if (s.Route.IsIdle) { s.StepTicks = 0; return s; }
        s = Start(s, scratch);
        // Not when Start refused: a step the map blocked re-paths instead, and that tick was already spent deciding
        // so, exactly as the failed commit it replaces was.
        if (s.IsStepping) s.StepTicks++;
        return s;
    }

    // Commits the next step of the route: the CanStep re-check against the live map, then the tile flip that is the
    // whole point of the model. Called only with the body standing on its tile, from both of Advance's doors.
    TileMoveState Start(in TileMoveState state, TilePathfinderScratch? scratch)
    {
        TileMoveState s = state;
        TileCoord next = s.Route.Next;
        TileDirection dir = TileRoute.Direction(s.Tile, next);
        if (!TileCollision.CanStep(Map, s.Tile.X, s.Tile.Z, s.Tile.Plane, dir, s.FootprintSize))
            return Repath(s, scratch);

        s = Commit(s, next, dir);
        s.Route = s.Route.Advanced();
        return s.Route.IsIdle && s.InteractTarget != 0 ? FaceTarget(s) : s;
    }

    // THE PACE SEAM. Every StepTotal this class writes comes from here: a starting step through Commit, Advance's
    // blank backstop and Repath's re-stamp of a standing state. It reads the STATE rather than the mode alone on
    // purpose. Today the answer is the configured pair for the mode, and a game that one day needs a body to move
    // faster or slower than its mode says changes this one method, and a click, a held key, an actor and a chase
    // all inherit it together. Nothing else in the package may ask StepTicks how long a step takes.
    byte StepTotalFor(in TileMoveState s) => StepTicks.For(s.Mode);

    // THE one place a step begins, for a routed step and a steered one alike, so neither can start one the other
    // could not, and neither can stamp a cadence the other would not.
    TileMoveState Commit(in TileMoveState state, TileCoord next, TileDirection dir)
    {
        TileMoveState s = state;
        s.StepFrom = s.Tile;
        s.Tile = next;
        s.Facing = dir;
        s.StepTicks = 0;
        s.StepTotal = StepTotalFor(s);
        return s;
    }

    // The steer door. Called only with the body standing on its tile, from both of Advance's doors. A blocked
    // steer still turns the body, so a key pressed into a wall reads as an answer rather than a dropped input.
    //
    // No route is built and none is consulted: the resolver answers from the committed tile and the live map, which
    // is why a key held into a fence stands there instead of taking the detour a WalkTo at the adjacent tile would
    // path. Nothing is remembered between ticks either, so the next held tick asks the same question again.
    TileMoveState StartSteered(in TileMoveState state, TileDirection held)
    {
        TileMoveState s = state;
        TileDirection? dir = TileSteerResolver.Resolve(Map, s.Tile, held, s.FootprintSize);
        if (dir is not { } step)
        {
            s.Facing = held;
            s.StepTicks = 0;
            return s;
        }
        (int dx, int dz) = TileDirections.Delta(step);
        return Commit(s, new TileCoord(s.Tile.X + dx, s.Tile.Z + dz, s.Tile.Plane), step);
    }

    // The tick a walked interaction's route empties, the player turns to face what the walk was for. That tick is
    // the one the LAST step starts, so the turn now lands while the body is still gliding into the booth's reach
    // tile rather than once it is standing on it: the facing is a fact about the tile the player is committed to,
    // and the drawn body catches up. It is also why the glide's origin is its own field: this write moves Facing
    // away from the step's own direction while the step still has ticks left to run.
    //
    // This lives in the SIMULATOR rather than in the server's action resolution because facing is simulation state:
    // it is compared by TileMoveState.Equals and it rides the wire, so a facing only the server writes reaches the
    // client one snapshot after the arrival it belongs to, and the player watches the avatar stand wrong at the
    // booth for a round trip and then rotate. Both heads run this, so the turn is predicted with the rest of the
    // click and the server's own write of the same value becomes an idempotent backstop.
    //
    // TileReach.Contains is a real guard, not a formality. Repath rebuilds a route through FindPath, whose
    // nearest-reachable fallback can leave a live route that stops SHORT of a reach tile, and FacingToward answers
    // W for a tile that touches no footprint tile at all. Unguarded, that pair turns a player who never got there
    // to face west for no reason. Contains also refuses a target on another plane, so the plane needs no second
    // check here.
    //
    // BOTH doors also DROP the target, and that is the load-bearing half: a walk whose last step is not INTO a reach
    // tile is not an arrival, and the pending target is the one record of that. The server's action resolution
    // reads exactly this pair of fields (an idle route still naming the target IS the arrival), so a target left
    // set here raises the interaction for a player standing arbitrarily far away, which a client aims by clicking
    // the target with the longest reach path and letting MaxRouteSteps cut the walk short. Cleared, both cases fall
    // through to the server's CannotReach, which is the correct answer for a click that could not be walked. Both
    // heads run this, so the client predicts the same drop and no second copy of the reach rule appears anywhere.
    TileMoveState FaceTarget(in TileMoveState state)
    {
        TileMoveState s = state;
        ITileTargets? resolver = s.InteractDomain == TileInteractionDomain.Entity ? combatTargets : targets;
        if (resolver is null || !resolver.TryGetFootprint(s.InteractTarget, out TileRect footprint, out int plane))
        {
            // The target stopped resolving part way through the walk (deleted, despawned, no longer interactive).
            s.InteractTarget = 0;
            s.InteractDomain = TileInteractionDomain.AuthoredObject;
            return s;
        }
        int size = s.FootprintSize;
        if (!TileReach.Contains(Map, footprint, plane, s.Tile, size))
        {
            // The walk ended off the reach set, which is what a route truncated at MaxRouteSteps leaves behind.
            s.InteractTarget = 0;
            s.InteractDomain = TileInteractionDomain.AuthoredObject;
            return s;
        }
        s.Facing = TileReach.FacingToward(Map, footprint, plane, s.Tile, size);
        return s;
    }

    // A dynamic blocker landed on the tile the next step was about to be committed to. Re-path ONCE toward the same
    // end from where we stand. If that path cannot move either, drop the route and stand. Both heads run this
    // identically, so it only diverges when the two heads saw different blockers, which is exactly what the
    // reconcile snap is for.
    //
    // Nothing is committed on a refusal: the check runs BEFORE the tile flips, so a blocked step leaves the player
    // standing on the tile they were already on rather than owning one they cannot enter.
    //
    // Progress goes to zero and this tick does NOT advance the replacement step, unlike the tick that carries a
    // command. That is deliberate: the tick was already spent on the step whose start the map refused, so charging
    // it to the replacement would pay for it twice. The player hesitates for one step when the way closes in front
    // of them, which is the one place the class doc's "a click never costs a tick" does not hold.
    //
    // The total is re-stamped with the progress, so the state this returns agrees with its own Mode. Start stamps
    // the replacement step as it commits, so the CADENCE was already right without this: what was wrong was the
    // standing state in between, whose StepTotal still held the cadence of the step the map had just refused, on
    // the wire and in equality, for every tick until the next step started.
    TileMoveState Repath(in TileMoveState state, TilePathfinderScratch? scratch)
    {
        TileMoveState s = state;
        TileCoord end = s.Route.End;
        s.StepTicks = 0;
        s.StepTotal = StepTotalFor(s);
        TilePath path = TilePathfinder.FindPath(Map, s.Tile.Plane, s.Tile, end, s.FootprintSize, MaxPathRadius, scratch);
        s.Route = RouteFor(path);
        if (s.Route.IsIdle)
        {
            s.InteractTarget = 0;
            s.InteractDomain = TileInteractionDomain.AuthoredObject;
        }
        return s;
    }

    // EVERY route this class builds comes through here, which is what makes the cap a property of the simulation
    // rather than of the wire. Both heads truncate the same deterministic FindPath result to the same tiles, so the
    // walk ends at the truncated route's last tile on both of them and the owner is told where it is actually
    // going. A player who clicked further away walks as far as one click carries and clicks again.
    //
    // The cap counts the steps STILL TO TAKE from the committed tile, which is the whole of it now that a route is
    // always pathed from that tile. A step in flight is not among them: it was committed when it started and its
    // tile is already the one this path leaves from, so a re-click every step cannot ratchet a route past the cap,
    // it just re-spends it from one tile further on. That is the same protection the old spliced cap bought by
    // charging the inherited step against the limit, without the second route builder.
    //
    // The interaction case truncates too, and it is FaceTarget that makes a cut-short interact safe: the walk ends
    // off the target's reach set, where that method declines to turn the player AND drops the pending target, so
    // the click is answered as a CannotReach rather than as an arrival from wherever the route ran out.
    TileRoute RouteFor(TilePath path)
    {
        IReadOnlyList<TileCoord> tiles = path.Tiles;
        if (tiles.Count <= MaxRouteSteps) return TileRoute.FromPath(path);
        var cut = new TileCoord[MaxRouteSteps];
        for (int i = 0; i < MaxRouteSteps; i++) cut[i] = tiles[i];
        return new TileRoute(cut, 0);
    }
}
