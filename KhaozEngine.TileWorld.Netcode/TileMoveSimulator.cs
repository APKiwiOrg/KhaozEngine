using System;
using System.Collections.Generic;
using KhaozEngine.Netcode;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The ONE discrete stepper both heads run. Pure over its inputs and integer-only, so a client replay of the same
/// commands over the same collision map reproduces the server's state exactly, which is what lets prediction snap
/// only on a genuine disagreement.
/// <para>Order inside one tick: a WalkTo or Interact command replaces the route (and resets step progress), then
/// step progress advances, and a step COMMITS when it fills. A commit re-checks <see cref="TileCollision.CanStep"/>
/// against the live map, so a blocker that appeared mid-route is caught at the moment the foot lands: the route is
/// re-pathed ONCE from the current tile to the same end, and if that also fails the route is dropped and the player
/// stands. The tick a route with a pending interaction empties ALSO turns the player toward the target, so the whole
/// outcome of a click is written by the one stepper both heads run.</para>
/// <para>The tick that carries a command is a FULL tick: it starts the walk and advances step progress by one, so
/// a click never costs a tick of standing still. That is the rule the step-cadence tests pin, and it is why a
/// freshly issued route reads one tick into its first step rather than zero.</para>
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

    /// <summary>Paths from where the player stands to <paramref name="goal"/> and starts walking it. An
    /// unreachable goal walks to the nearest reachable tile, the rule <see cref="TilePathfinder"/> already
    /// implements. A goal the player already stands on just stands.
    /// <para>A goal on ANOTHER PLANE is refused, the same answer <see cref="TileCommandKind.Interact"/> gives a
    /// target on another plane: the state comes back untouched, route, step progress and mode all as they were.
    /// Planes are separate walkable surfaces with no step between them, so pathing the goal on the player's plane
    /// instead would walk them to an X and Z they never clicked.</para></summary>
    /// <param name="state">The state to re-route.</param>
    /// <param name="goal">The tile clicked.</param>
    /// <param name="mode">Walk or run, which decides how many ticks each step of the new route takes.</param>
    /// <returns>The state carrying the new route, with step progress reset to the start of a step, or the state
    /// unchanged when <paramref name="goal"/> is on another plane.</returns>
    public TileMoveState BeginWalk(in TileMoveState state, TileCoord goal, TileMoveMode mode)
    {
        TileMoveState s = state;
        if (!Accepts(s, TileCommand.WalkTo(goal, mode))) return s;
        s.Mode = mode;
        s.StepTicks = 0;
        s.StepTotal = StepTicks.For(mode);
        TilePath path = TilePathfinder.FindPath(Map, s.Tile.Plane, s.Tile, goal, AgentSize, MaxPathRadius);
        s.Route = RouteFor(path);
        return s;
    }

    // Routes to a reach tile of the target and remembers it, so the arrival tick faces the target and raises the
    // action. An unknown target, or a same-plane one with no reachable tile at all, drops the route and clears the
    // target: the server answers that case with a CannotReach game message, and the client pre-checks the same map
    // on click.
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

        s.Mode = mode;
        s.StepTicks = 0;
        s.StepTotal = StepTicks.For(mode);
        s.InteractTarget = 0;
        s.Route = TileRoute.None;

        if (!resolved) return s;
        if (!TileReach.TryNearest(Map, footprint, plane, s.Tile, AgentSize, MaxPathRadius,
                out TileCoord reachTile, out TilePath path))
            return s;

        // The target is remembered on a WALK, so the arrival tick can act on it, and a zero step interaction faces
        // the target here because no step will ever run to set the facing for it.
        s.InteractTarget = target;
        s.Route = RouteFor(path);
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
    TileMoveState FaceTarget(in TileMoveState state)
    {
        TileMoveState s = state;
        if (targets is null || !targets.TryGetFootprint(s.InteractTarget, out TileRect footprint, out int plane))
            return s;
        if (!TileReach.Contains(Map, footprint, plane, s.Tile)) return s;
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
    // The interaction case truncates too, and is safe: an interact route cut short leaves the player standing off
    // the target's reach set, where FaceTarget's TileReach.Contains guard already declines to turn them.
    TileRoute RouteFor(TilePath path)
    {
        IReadOnlyList<TileCoord> tiles = path.Tiles;
        if (tiles.Count <= MaxRouteSteps) return TileRoute.FromPath(path);
        var cut = new TileCoord[MaxRouteSteps];
        for (int i = 0; i < MaxRouteSteps; i++) cut[i] = tiles[i];
        return new TileRoute(cut, 0);
    }
}
