using System;
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
/// stands.</para>
/// <para>The tick that carries a command is a FULL tick: it starts the walk and advances step progress by one, so
/// a click never costs a tick of standing still. That is the rule the step-cadence tests pin, and it is why a
/// freshly issued route reads one tick into its first step rather than zero.</para>
/// <para>Nothing here is stateful. Every answer comes from the state handed in plus the map, so one instance is
/// shared by every player on a server and by the prediction and reconcile paths on a client, and replaying a tick
/// twice gives the same state twice.</para>
/// </summary>
public sealed class TileMoveSimulator : ITickSimulator<TileMoveState, TileCommand>
{
    readonly ITileTargets? targets;

    /// <summary>Builds a stepper over a baked collision map and a step cadence. A defaulted
    /// <see cref="TileStepTicks"/> (every count zero, which is what an unset field or a decoded blank gives)
    /// falls back to <see cref="TileStepTicks.Default"/> rather than stepping every tick forever.</summary>
    /// <param name="map">The baked collision map to step and path over.</param>
    /// <param name="stepTicks">Ticks per step, per mode.</param>
    /// <param name="targets">Resolves interaction targets, null on a head that has no interactions wired.</param>
    /// <param name="options">Pathfinder knobs, null for the defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="options"/> asks for an agent smaller than
    /// one tile.</exception>
    public TileMoveSimulator(TileCollisionMap map, TileStepTicks stepTicks, ITileTargets? targets = null,
        TileMoveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        TileMoveOptions o = options ?? new TileMoveOptions();
        if (o.AgentSize < 1) throw new ArgumentOutOfRangeException(nameof(options), "AgentSize must be >= 1.");
        Map = map;
        StepTicks = stepTicks.Walk == 0 ? TileStepTicks.Default : stepTicks;
        this.targets = targets;
        AgentSize = o.AgentSize;
        MaxPathRadius = o.MaxPathRadius;
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
            case TileCommandKind.WalkTo:
                s = BeginWalk(s, command.Goal, command.Mode);
                s.InteractTarget = 0;
                break;
            case TileCommandKind.Interact:
                s = BeginInteract(s, command.Target, command.Mode);
                break;
        }

        return Advance(s);
    }

    /// <summary>Paths from where the player stands to <paramref name="goal"/> and starts walking it. An
    /// unreachable goal walks to the nearest reachable tile, the rule <see cref="TilePathfinder"/> already
    /// implements. A goal the player already stands on just stands.</summary>
    /// <param name="state">The state to re-route.</param>
    /// <param name="goal">The tile clicked.</param>
    /// <param name="mode">Walk or run, which decides how many ticks each step of the new route takes.</param>
    /// <returns>The state carrying the new route, with step progress reset to the start of a step.</returns>
    public TileMoveState BeginWalk(in TileMoveState state, TileCoord goal, TileMoveMode mode)
    {
        TileMoveState s = state;
        s.Mode = mode;
        s.StepTicks = 0;
        s.StepTotal = StepTicks.For(mode);
        TilePath path = TilePathfinder.FindPath(Map, s.Tile.Plane, s.Tile, goal, AgentSize, MaxPathRadius);
        s.Route = TileRoute.FromPath(path);
        return s;
    }

    // Routes to a reach tile of the target and remembers it, so the arrival tick faces the target and raises the
    // action. An unknown target, or one with no reachable tile at all, drops the route and clears the target: the
    // server answers that case with a CannotReach game message, and the client pre-checks the same map on click.
    TileMoveState BeginInteract(in TileMoveState state, long target, TileMoveMode mode)
    {
        TileMoveState s = state;
        s.Mode = mode;
        s.StepTicks = 0;
        s.StepTotal = StepTicks.For(mode);
        s.InteractTarget = 0;
        s.Route = TileRoute.None;

        // task 4 wires the reach call. TileReach does not exist yet, so a target's footprint cannot become a reach
        // tile and every Interact stands where it is, clearing its target. That is the same answer an unknown or
        // unreachable target keeps once the call is wired in, so this seam is a missing capability rather than a
        // half-applied branch, and the targets field is read for the first time when task 4 fills the hole in.
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
        return s;
    }

    // A dynamic blocker landed on the next tile. Re-path ONCE toward the same end from where we stand; if that
    // path cannot move either, drop the route and stand. Both heads run this identically, so it only diverges
    // when the two heads saw different blockers, which is exactly what the reconcile snap is for.
    TileMoveState Repath(in TileMoveState state)
    {
        TileMoveState s = state;
        TileCoord end = s.Route.End;
        s.StepTicks = 0;
        TilePath path = TilePathfinder.FindPath(Map, s.Tile.Plane, s.Tile, end, AgentSize, MaxPathRadius);
        s.Route = TileRoute.FromPath(path);
        if (s.Route.IsIdle) s.InteractTarget = 0;
        return s;
    }
}
