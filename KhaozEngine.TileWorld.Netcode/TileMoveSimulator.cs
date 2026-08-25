using System;
using System.Collections.Generic;
using KhaozEngine.Netcode;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The ONE discrete stepper both heads run. Pure over its inputs and integer-only, so a client replay of the same
/// commands over the same collision map reproduces the server's state exactly, which is what lets prediction snap
/// only on a genuine disagreement.
/// <para>Order inside one tick: a WalkTo or Interact command replaces the route, then step progress advances, and a
/// step COMMITS when it fills. A commit re-checks <see cref="TileCollision.CanStep"/>
/// against the live map, so a blocker that appeared mid-route is caught at the moment the foot lands: the route is
/// re-pathed ONCE from the current tile to the same end, and if that also fails the route is dropped and the player
/// stands. The tick a route with a pending interaction empties ALSO turns the player toward the target, so the whole
/// outcome of a click is written by the one stepper both heads run.</para>
/// <para>The tick that carries a command is a FULL tick: it starts the walk and advances step progress by one, so
/// a click never costs a tick of standing still. That is the rule the step-cadence tests pin, and it is why a
/// freshly issued route reads one tick into its first step rather than zero.</para>
/// <para>THE STEP IN PROGRESS IS NEVER ABANDONED, which is OSRS's rule and the one a re-click while moving is
/// judged by. A WalkTo or an Interact arriving part way through a step (<see cref="TileMoveState.StepTicks"/>
/// above zero, so the avatar is drawn between two tiles) keeps that progress, its total, and the tile it is
/// entering. The new route is pathed from THAT tile and SPLICED behind it, so the step in flight commits exactly
/// as it would have and the new walk continues from where the foot lands. Re-pathing from
/// <see cref="TileMoveState.Tile"/> instead, which names the tile being LEFT, drags the drawn position back toward
/// it before setting off: a visible stutter on every direction change while moving, and predicted, so both heads
/// produce it and no correction ever cleans it up. A command arriving on a step BOUNDARY (progress at zero,
/// standing included) has nothing in flight and starts its step from the tile stood on, as it always did. The
/// route cap counts the spliced step, the mode still lands at the start of the NEXT step, and a cross-plane goal
/// is still dropped whole.</para>
/// <para>Mode rides on EVERY command, <see cref="TileCommandKind.None"/> included, because the wire frame is a
/// fixed size and carries <see cref="TileCommand.Mode"/> whatever the kind. A toggle takes effect at the START of
/// the next step: the step already under way keeps the total it was stamped with, so holding run halfway through a
/// walking step never shortens that step, it only makes the one after it a run. A client sends
/// <see cref="TileCommand.Continue"/> carrying the run state it is holding, on every tick.</para>
/// <para>Nothing here is stateful. Every answer comes from the state handed in plus the map, so one instance is
/// shared by every player on a server and by the prediction and reconcile paths on a client, and replaying a tick
/// twice gives the same state twice.</para>
/// </summary>
public sealed class TileMoveSimulator : ITickSimulator<TileMoveState, TileCommand>
{
    readonly ITileTargets? targets;

    /// <summary>Builds a stepper over a baked collision map and a step cadence. A <see cref="TileStepTicks"/>
    /// with EITHER count zero (which is what an unset field or a decoded blank gives) falls back to
    /// <see cref="TileStepTicks.Default"/> rather than stepping every tick forever.</summary>
    /// <param name="map">The baked collision map to step and path over.</param>
    /// <param name="stepTicks">Ticks per step, per mode.</param>
    /// <param name="targets">Resolves interaction targets, null on a head that has no interactions wired.</param>
    /// <param name="options">Pathfinder knobs, null for the defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="options"/> asks for an agent smaller than
    /// one tile, for a path radius outside the range <see cref="TilePathfinder.FindPath"/> accepts, or for a route
    /// cap outside 1..<see cref="TileProtocol.MaxRouteSteps"/>.</exception>
    public TileMoveSimulator(TileCollisionMap map, TileStepTicks stepTicks, ITileTargets? targets = null,
        TileMoveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        TileMoveOptions o = options ?? new TileMoveOptions();
        if (o.AgentSize < 1) throw new ArgumentOutOfRangeException(nameof(options), "AgentSize must be >= 1.");
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
        AgentSize = o.AgentSize;
        MaxPathRadius = o.MaxPathRadius;
        MaxRouteSteps = o.MaxRouteSteps;
    }

    /// <summary>The collision map both heads bake from the same world files. Held rather than copied, so an edit
    /// rebaked into it is seen by the next step, which is exactly how a dynamic blocker reaches this code.</summary>
    public TileCollisionMap Map { get; }

    /// <summary>Ticks per step, per mode.</summary>
    public TileStepTicks StepTicks { get; }

    /// <summary>Footprint edge of a moving agent, in tiles.</summary>
    public int AgentSize { get; }

    /// <summary>Half width of the pathfinder's search window.</summary>
    public int MaxPathRadius { get; }

    /// <summary>Longest route one click may produce, in steps. See <see cref="TileMoveOptions.MaxRouteSteps"/>.</summary>
    public int MaxRouteSteps { get; }

    /// <summary>
    /// Whether <see cref="Step"/> would APPLY this command rather than drop it whole. THE definition of acceptance,
    /// and the only one: <see cref="Step"/>, <see cref="BeginWalk"/> and <see cref="BeginInteract"/> all ask this
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
    /// <returns>False for a cross-plane walk goal or a resolved cross-plane interaction target, true otherwise,
    /// <see cref="TileCommandKind.None"/> included (its mode is always applied).</returns>
    public bool Accepts(in TileMoveState state, in TileCommand command) => command.Kind switch
    {
        TileCommandKind.WalkTo => command.Goal.Plane == state.Tile.Plane,
        TileCommandKind.Interact =>
            targets is null
            || !targets.TryGetFootprint(command.Target, out _, out int plane)
            || plane == state.Tile.Plane,
        _ => true,
    };

    /// <summary>Advances one tick. <paramref name="dt"/> is unused: a tile step is counted in TICKS, not seconds,
    /// which is what keeps two heads on slightly different frame times byte-identical.</summary>
    /// <param name="state">The state to advance.</param>
    /// <param name="command">This tick's command.</param>
    /// <param name="dt">Ignored, see the summary.</param>
    /// <returns>The advanced state, with the presentation override cleared.</returns>
    public TileMoveState Step(in TileMoveState state, in TileCommand command, float dt)
    {
        TileMoveState s = state;
        s.HasRenderOverride = false;

        switch (command.Kind)
        {
            // A goal on another plane is DROPPED whole rather than coerced onto the player's own (see BeginWalk),
            // which is why the guard sits on the case label: an unmatched WalkTo falls past every case here and
            // the tick runs as though no command arrived at all, its mode included.
            case TileCommandKind.WalkTo when Accepts(s, command):
                s = BeginWalk(s, command.Goal, command.Mode);
                s.InteractTarget = 0;
                break;
            case TileCommandKind.Interact:
                s = BeginInteract(s, command.Target, command.Mode);
                break;
            // The run toggle rides on every command, so holding run is a property of the TICK rather than of the
            // click that started the route. StepTotal is deliberately not re-stamped: the step already under way
            // keeps the cadence it began with, and the commit that ends it stamps the new one.
            case TileCommandKind.None:
                s.Mode = command.Mode;
                break;
        }

        return Advance(s);
    }

    /// <summary>Paths to <paramref name="goal"/> and starts walking it. An unreachable goal walks to the nearest
    /// reachable tile, the rule <see cref="TilePathfinder"/> already implements. A goal the player already stands
    /// on just stands.
    /// <para>The walk is pathed from the tile the player is ENTERING when a step is in flight, and from the tile
    /// they stand on otherwise, and the in-flight case splices rather than restarting (see the type doc). Step
    /// progress and its total survive a splice untouched, so the step under way keeps both its cadence and its
    /// destination.</para>
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
    {
        TileMoveState s = state;
        if (!Accepts(s, TileCommand.WalkTo(goal, mode))) return s;
        s.Mode = mode;
        bool splice = StepInFlight(s, out TileCoord from);
        if (!splice)
        {
            s.StepTicks = 0;
            s.StepTotal = StepTicks.For(mode);
        }
        TilePath path = TilePathfinder.FindPath(Map, s.Tile.Plane, from, goal, AgentSize, MaxPathRadius);
        s.Route = splice ? SplicedRouteFor(from, path.Tiles) : RouteFor(path);
        return s;
    }

    // Whether a step is part way through, which is the whole trigger for the splice. Progress ABOVE ZERO is the
    // test rather than a live route: a route whose step has not started yet draws the avatar exactly on its tile,
    // so re-pathing from there moves nothing on screen and the ordinary path is correct for it.
    //
    // The entering tile is the route's next, which is where a spliced walk has to be pathed FROM. A route never
    // changes plane, so it carries the player's own and no plane check is repeated here.
    static bool StepInFlight(in TileMoveState state, out TileCoord entering)
    {
        if (state.Route.IsIdle || state.StepTicks == 0) { entering = state.Tile; return false; }
        entering = state.Route.Next;
        return true;
    }

    // Routes to a reach tile of the target and remembers it, so the arrival tick faces the target and raises the
    // action. An unknown target, or a same-plane one with no reachable tile at all, drops the route and clears the
    // target: the server answers that case with a CannotReach game message, and the client pre-checks the same map
    // on click.
    //
    // A click arriving MID-STEP splices exactly as a WalkTo does: the reach search runs from the tile being
    // ENTERED and the route it produces is spliced behind that tile. The dropped-route answers splice too, down to
    // a route of just the step in flight, because "cannot reach" is not a reason to yank the avatar back onto the
    // tile it is walking off. Both then reach the same standing state one commit later.
    //
    // A target on ANOTHER PLANE is not that case. It is refused the way BeginWalk refuses a cross-plane goal, with
    // the state untouched, and that is why acceptance is asked BEFORE the first write. Mode, cadence, route and
    // pending target all have to survive a click the player cannot even see, so the tick reads exactly as a
    // Continue at the mode already held. Reserving the cleared-route answer for a target on the player's OWN plane
    // is what keeps it meaningful: CannotReach then says "I know what you clicked and you cannot get to it", never
    // "that was on another floor". The refusal itself is Accepts, which is the one definition of it.
    TileMoveState BeginInteract(in TileMoveState state, long target, TileMoveMode mode)
    {
        TileMoveState s = state;
        if (!Accepts(s, TileCommand.Interact(target, mode))) return s;
        // Seeded, because the resolve is short circuited on a null seam and the compiler cannot see that the
        // branches below only read these once it answered true.
        TileRect footprint = default;
        int plane = 0;
        bool resolved = targets is not null && targets.TryGetFootprint(target, out footprint, out plane);

        bool splice = StepInFlight(s, out TileCoord from);

        s.Mode = mode;
        if (!splice)
        {
            s.StepTicks = 0;
            s.StepTotal = StepTicks.For(mode);
        }
        s.InteractTarget = 0;

        if (!resolved || !TileReach.TryNearest(Map, footprint, plane, from, AgentSize, MaxPathRadius,
                out TileCoord reachTile, out TilePath path))
        {
            // Built only on this exit: the resolved-and-reachable path below replaces the route anyway.
            s.Route = splice ? SplicedRouteFor(from, Array.Empty<TileCoord>()) : TileRoute.None;
            return s;
        }

        // The target is remembered on a WALK, so the arrival tick can act on it, and a zero step interaction faces
        // the target here because no step will ever run to set the facing for it. A SPLICED route always has the
        // step in flight left to walk, so it is never idle here and its turn falls to FaceTarget at the commit,
        // which is the same member the ordinary walked arrival goes through.
        s.InteractTarget = target;
        s.Route = splice ? SplicedRouteFor(from, path.Tiles) : RouteFor(path);
        if (s.Route.IsIdle) s.Facing = TileReach.FacingToward(Map, footprint, plane, reachTile);
        return s;
    }

    // One tick of step progress. A step commits only on the tick that fills it.
    TileMoveState Advance(in TileMoveState state)
    {
        TileMoveState s = state;
        if (s.Route.IsIdle) { s.StepTicks = 0; return s; }
        if (s.StepTotal == 0) s.StepTotal = StepTicks.For(s.Mode);

        s.StepTicks++;
        if (s.StepTicks < s.StepTotal) return s;

        TileCoord next = s.Route.Next;
        TileDirection dir = TileRoute.Direction(s.Tile, next);
        if (!TileCollision.CanStep(Map, s.Tile.X, s.Tile.Z, s.Tile.Plane, dir, AgentSize))
            return Repath(s);

        s.Tile = next;
        s.Facing = dir;
        s.StepTicks = 0;
        s.Route = s.Route.Advanced();
        s.StepTotal = StepTicks.For(s.Mode);
        return s.Route.IsIdle && s.InteractTarget != 0 ? FaceTarget(s) : s;
    }

    // The tick a walked interaction's route empties, the player turns to face what the walk was for. This lives in
    // the SIMULATOR rather than in the server's action resolution because facing is simulation state: it is compared
    // by TileMoveState.Equals and it rides the wire, so a facing only the server writes reaches the client one
    // snapshot after the arrival it belongs to, and the player watches the avatar stand wrong at the booth for a
    // round trip and then rotate. Both heads run this, so the turn is predicted with the rest of the click and the
    // server's own write of the same value becomes an idempotent backstop.
    //
    // TileReach.Contains is a real guard, not a formality. Repath rebuilds a route through FindPath, whose
    // nearest-reachable fallback can leave a live route that stops SHORT of a reach tile, and FacingToward answers
    // W for a tile that touches no footprint tile at all. Unguarded, that pair turns a player who never got there
    // to face west for no reason. Contains also refuses a target on another plane, so the plane needs no second
    // check here.
    //
    // BOTH doors also DROP the target, and that is the load-bearing half: a walk that ended anywhere but on a reach
    // tile is not an arrival, and the pending target is the one record of that. The server's action resolution
    // reads exactly this pair of fields (an idle route still naming the target IS the arrival), so a target left
    // set here raises the interaction for a player standing arbitrarily far away, which a client aims by clicking
    // the target with the longest reach path and letting MaxRouteSteps cut the walk short. Cleared, both cases fall
    // through to the server's CannotReach, which is the correct answer for a click that could not be walked. Both
    // heads run this, so the client predicts the same drop and no second copy of the reach rule appears anywhere.
    TileMoveState FaceTarget(in TileMoveState state)
    {
        TileMoveState s = state;
        if (targets is null || !targets.TryGetFootprint(s.InteractTarget, out TileRect footprint, out int plane))
        {
            // The target stopped resolving part way through the walk (deleted, despawned, no longer interactive).
            s.InteractTarget = 0;
            return s;
        }
        if (!TileReach.Contains(Map, footprint, plane, s.Tile))
        {
            // The walk ended off the reach set, which is what a route truncated at MaxRouteSteps leaves behind.
            s.InteractTarget = 0;
            return s;
        }
        s.Facing = TileReach.FacingToward(Map, footprint, plane, s.Tile);
        return s;
    }

    // A dynamic blocker landed on the next tile. Re-path ONCE toward the same end from where we stand. If that
    // path cannot move either, drop the route and stand. Both heads run this identically, so it only diverges
    // when the two heads saw different blockers, which is exactly what the reconcile snap is for.
    //
    // Progress goes to zero and this tick does NOT advance the new step, unlike the tick that carries a command.
    // That is deliberate: the tick was already spent filling the step that just failed its collision re-check, so
    // charging it to the replacement step would pay for it twice. The player hesitates for one step when the way
    // closes in front of them, which is the one place the class doc's "a click never costs a tick" does not hold.
    TileMoveState Repath(in TileMoveState state)
    {
        TileMoveState s = state;
        TileCoord end = s.Route.End;
        s.StepTicks = 0;
        TilePath path = TilePathfinder.FindPath(Map, s.Tile.Plane, s.Tile, end, AgentSize, MaxPathRadius);
        s.Route = RouteFor(path);
        if (s.Route.IsIdle) s.InteractTarget = 0;
        return s;
    }

    // EVERY route this class builds comes through here, which is what makes the cap a property of the simulation
    // rather than of the wire. Both heads truncate the same deterministic FindPath result to the same tiles, so the
    // walk ends at the truncated route's last tile on both of them and the owner is told where it is actually
    // going. A player who clicked further away walks as far as one click carries and clicks again.
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

    // The route a click landing mid-step produces: the tile the step in flight is entering, then the walk pathed
    // from that tile. Built at index 0, so the step already part way through stays the CURRENT step and its
    // progress keeps counting toward the same destination it was counting toward before the click.
    //
    // The cap counts the inherited step, which is why this does not simply defer to RouteFor. Counted off the new
    // walk alone, every re-click would hand the player one tile of walk the cap never charged for, and a head
    // clicking once a step would ratchet a route arbitrarily far past MaxRouteSteps. The ctor pins the cap at one
    // or more, so there is always room for the inherited step itself.
    TileRoute SplicedRouteFor(TileCoord entering, IReadOnlyList<TileCoord> after)
    {
        int keep = Math.Min(after.Count, MaxRouteSteps - 1);
        var spliced = new TileCoord[keep + 1];
        spliced[0] = entering;
        for (int i = 0; i < keep; i++) spliced[i + 1] = after[i];
        return new TileRoute(spliced, 0);
    }
}
