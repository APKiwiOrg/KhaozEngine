using System;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// One player's discrete movement state: which tile they stand on, which way they face, how far through the
/// current step they are, and the route they are walking. Both an <see cref="IPredictedState{TSelf}"/> (so
/// <see cref="ClientPrediction{TState,TCommand}"/> reconciles it verbatim) and an ECS <see cref="IComponent"/>
/// (so <see cref="KhaozEngine.Replication.ReplicationRegistry"/> replicates it verbatim). One type on both heads
/// is the whole point: a predicted step and an authoritative step are the same code over the same bytes, so a
/// mismatch is a real disagreement rather than two implementations drifting.
/// <para>The authoritative part is INTEGER. A step commits by changing <see cref="Tile"/>, and progress through a
/// step is a tick COUNT out of a tick TOTAL. Nothing accumulates a float, so replaying the same commands from the
/// same state lands on byte-identical output on any machine, which server-authoritative movement depends on.</para>
/// <para>A STEP COMMITS ITS TILE WHEN IT STARTS. <see cref="Tile"/> names the tile the step is walking INTO from the
/// moment the step begins, and <see cref="StepFrom"/> names the one it is leaving, so the simulation owns the
/// destination for the whole of the walk into it and the drawn body arrives afterwards. Every rules question (reach,
/// region, occupancy, what a click resolves against) is therefore answered about the tile the player is committed to
/// rather than the one they are half off, which is what makes a 250 ms tick feel immediate. The body is at most one
/// step behind the answer, never a whole tile, because the commit and the glide start on the same tick.</para>
/// <para><see cref="Position"/> is DERIVED: the glide from <see cref="StepFrom"/> to <see cref="Tile"/> by the
/// fraction of the current step already spent, in TILE units. <see cref="Vertical"/> is the PLANE INDEX as a float
/// rather than a height in metres, so the state stays document-free and the simulator can produce it without loading
/// a world. <c>TilePresenter</c> multiplies by the document's plane height on the way to the view.</para>
/// <para>Those units are the LATTICE's, not the world's, which matters one level out. Reconciliation measures its
/// position error as a single magnitude over both, so a one-plane difference reads as exactly one tile of error,
/// while <see cref="PredictionSettings.HardSnapDistance"/> defaults to 100 (documented in WORLD units). A tile
/// client therefore hands prediction its own <see cref="PredictionSettings"/> with a tile-scaled snap distance:
/// <see cref="PredictionSettings.Default"/> would need a hundred tiles of misprediction before it ever snapped,
/// which is the same as never snapping at all.</para>
/// <para><see cref="RenderPosition"/> / <see cref="RenderVertical"/> / <see cref="HasRenderOverride"/> are
/// PRESENTATION-only, written by <see cref="WithRenderState"/> on the way out of
/// <c>ClientPrediction.RenderedState</c> and read by nothing else. The simulator never reads them, the codecs
/// never write them, and equality ignores them, so they cannot perturb determinism. They exist because the
/// prediction layer smooths in continuous world space while this state is a lattice, and the smoothed answer has
/// to reach the view without ever becoming the simulation's idea of where the player is.</para>
/// </summary>
public struct TileMoveState : IPredictedState<TileMoveState>, IComponent, IEquatable<TileMoveState>
{
    /// <summary>The tile the player OWNS. A step COMMITS by changing this, never by fractions, so this field alone
    /// answers every rules question (reach, region, occupancy) with no rounding. It changes on the tick a step
    /// STARTS, so while a step is in flight this is the tile being walked INTO and the drawn body is somewhere
    /// between <see cref="StepFrom"/> and here.</summary>
    public TileCoord Tile;

    /// <summary>The tile the step in flight is walking OUT of, equal to <see cref="Tile"/> whenever the body stands
    /// still (standing, spawned, teleported, hard snapped, or the tick a glide finishes). The glide's origin is its
    /// own field rather than derived from <see cref="Facing"/> because the two come apart exactly where it matters:
    /// <c>TileMoveSimulator</c>'s arrival turn rewrites the facing toward an interaction target on the tick the LAST
    /// step starts, while that step's glide still has its whole run left, so a facing-derived origin would send the
    /// body off in the direction of a booth it is not walking from.</summary>
    public TileCoord StepFrom;

    /// <summary>The direction the player faces. Set by the step taken, or by an interaction's target on the tick the
    /// walk to it ends, a zero step walk included, which is why it is its own field rather than read back off the
    /// route. The simulator owns both writes, so the turn at the end of a click is predicted with the rest of it.
    /// </summary>
    public TileDirection Facing;

    /// <summary>Walk or run: which entry of <see cref="TileStepTicks"/> the current route steps at. Held on the
    /// state, and re-read from every command, so a run toggled mid route takes effect at the START of the next step
    /// rather than restarting the walk: <see cref="StepTotal"/> is not re-stamped until the step under way commits,
    /// so a toggle can neither shorten nor stretch a step already in progress.</summary>
    public TileMoveMode Mode;

    /// <summary>Ticks already spent gliding into <see cref="Tile"/>. NEVER above <see cref="StepTotal"/> in a state
    /// the simulator produced, and below it in every state whose step still has ticks to run, because the tick that
    /// reaches the total is the tick the body lands: that tick pulls <see cref="StepFrom"/> up to <see cref="Tile"/>
    /// and resets this to zero, and starts the next step if the route has one. The one produced state where the two
    /// are EQUAL is a cadence of a single tick, where a standing player's step both starts and spends its only tick
    /// on the one tick, so the fraction reads as filled while the step is still in flight. Zero whenever
    /// <see cref="StepFrom"/> equals <see cref="Tile"/>.</summary>
    public byte StepTicks;

    /// <summary>Ticks the CURRENT step takes, stamped from configuration when the step started. Carried on the
    /// state rather than looked up, so an observer with no config still knows how far through a step a remote is
    /// and can glide it, and so a cadence change cannot retroactively stretch a step already under way.</summary>
    public byte StepTotal;

    /// <summary>The walk in progress, <see cref="TileRoute.None"/> when standing.</summary>
    public TileRoute Route;

    /// <summary>The monotonic teleport epoch, advanced by the server on a discontinuous placement. Surfaced as
    /// <see cref="TeleportEpoch"/>, which is what tells the client to cut rather than glide.</summary>
    public uint Epoch;

    /// <summary>The interaction target this route is heading to, 0 when none. Cleared when the action is raised or
    /// the route is replaced, so a target can never outlive the walk that was chasing it.</summary>
    public long InteractTarget;

    /// <summary>The entity this state is locked onto and chasing, 0 when not fighting. A NET ID, from the entity
    /// space, never an object id: the two spaces overlap exactly, which is why the command kind is the
    /// discriminator (see <see cref="TileCommandKind.Attack"/>).
    /// <para>It lives HERE, on the state, and costs 8 bytes on every entity's every snapshot, rather than on a
    /// component present only on entities actually fighting. That was weighed and rejected on the package's
    /// founding property: <see cref="TileMoveSimulator"/> is an
    /// <see cref="KhaozEngine.Netcode.ITickSimulator{TState,TCommand}"/>, and that contract is a state and a command
    /// and nothing else, so a target held anywhere else cannot be followed inside the one stepper both heads run.
    /// Following it elsewhere means a SECOND movement authority the client cannot predict, and a client that cannot
    /// predict its own approach pays a round trip on every re-path of every chase.</para>
    /// <para>Mutually exclusive with <see cref="InteractTarget"/>, each clearing the other, for the reason
    /// <c>TileActionQueue</c> gives about its own pair: two records of one intent, where the one that outlives the
    /// other fires against something the player visibly walked away from.</para></summary>
    public long CombatTarget;

    /// <summary>Presentation only, see the type doc.</summary>
    public Vector2 RenderPosition;

    /// <summary>Presentation only, see the type doc.</summary>
    public float RenderVertical;

    /// <summary>Presentation only, see the type doc.</summary>
    public bool HasRenderOverride;

    /// <summary>A standing state on one tile, facing one way, with no route and no step in flight. Used by every
    /// discontinuous placement there is (spawn, rejoin, teleport, a record restored from the store), which is why it
    /// is the one place <see cref="StepFrom"/> is seeded: a placement has no tile to have come from, so the body is
    /// on its tile rather than gliding onto it. <see cref="StepTotal"/> starts at 1 rather than 0 so the fraction
    /// maths is never dividing by a zero total on a freshly placed player.</summary>
    public static TileMoveState At(TileCoord tile, TileDirection facing) => new()
    {
        Tile = tile,
        StepFrom = tile,
        Facing = facing,
        Mode = TileMoveMode.Walk,
        Route = TileRoute.None,
        StepTotal = 1,
    };

    /// <summary>True while the drawn body is between <see cref="StepFrom"/> and <see cref="Tile"/>. THE definition of
    /// "a step is in flight", asked by the simulator before it starts one and by the presenter before it glides, so
    /// the two cannot disagree. Note it is NOT the same question as a live route: a route empties on the tick its
    /// last step STARTS, so an idle route with a step still in flight is the ordinary end of every walk.</summary>
    public readonly bool IsStepping => !StepFrom.Equals(Tile);

    // THE rule for what a glide's origin may be, stated once and asked by both doors a state can arrive through
    // from outside the simulator: TileProtocol's decoder, of an attacker-controlled frame, and
    // TileWorldServer.SetPlayerState, of a hand-built state. An origin is either the tile itself (nothing in
    // flight) or exactly one Chebyshev step from it on the same plane, because those are the only two shapes the
    // stepper produces: Start writes StepFrom = Tile before flipping Tile to an adjacent tile, and every landing
    // normalizes the pair back together. Anything else is a step no route could contain, and a Position lerped
    // along it walks the avatar over every tile in the gap.
    //
    // The plane is part of the rule rather than assumed: a step never changes plane, so an origin one floor down
    // is as unproducible as one a map away, and TileCoord equality counts it as a step in flight.
    //
    // Measured in LONG for the reason TileWorldServer.GoalInRange is: nothing bounds a replicated or hand-written
    // tile's X and Z, so two coordinates int.MinValue apart make an int subtraction wrap and Math.Abs throw.
    internal static bool IsStepOrigin(TileCoord from, TileCoord tile)
    {
        if (from.Plane != tile.Plane) return false;
        long dx = (long)tile.X - from.X, dz = (long)tile.Z - from.Z;
        return Math.Max(Math.Abs(dx), Math.Abs(dz)) <= 1;
    }

    /// <summary>The fraction of the current step already spent, 0 when the body stands on its tile. The single place
    /// an integer tick count turns into a float, and it is read only: nothing ever writes the result back.</summary>
    public readonly float StepFraction =>
        !IsStepping || StepTotal == 0 ? 0f : (float)StepTicks / StepTotal;

    /// <inheritdoc/>
    /// <remarks>A diagonal step covers sqrt(2) tiles in the same tick count as a cardinal one, which is the tile
    /// rule rather than a defect, so anything differencing this over a tick (<c>ClientPrediction</c> exposes exactly
    /// that as its predicted horizontal speed) reads 1.41x on a diagonal. A HUD or a locomotion blend must not
    /// threshold walk against run on that number.</remarks>
    public readonly Vector2 Position
    {
        get
        {
            if (!IsStepping) return new Vector2(Tile.X, Tile.Z);
            float f = StepFraction;
            // Differenced in FLOAT rather than in int. The two tiles are one step apart in anything this package
            // builds, and the decoder clamps a frame that says otherwise, but the fields are public and an int
            // subtraction of two hand-written coordinates a world apart would overflow into a position on the far
            // side of the map rather than merely a distant one.
            return new Vector2(
                StepFrom.X + ((float)Tile.X - StepFrom.X) * f,
                StepFrom.Z + ((float)Tile.Z - StepFrom.Z) * f);
        }
    }

    /// <inheritdoc/>
    public readonly float Vertical => Tile.Plane;

    /// <inheritdoc/>
    public readonly uint TeleportEpoch => Epoch;

    /// <summary>NOT a position setter, whatever the interface says. <see cref="Position"/> is DERIVED from
    /// <see cref="Tile"/> and <see cref="Route"/>, so <paramref name="position"/> is never applied to it and the
    /// returned state stands exactly where this one does. What the call does do is stamp the PRESENTATION override:
    /// it sets <see cref="RenderVertical"/> to the raw plane index and <see cref="HasRenderOverride"/> to true, so
    /// calling it on a state that already carried a smoothed vertical discards that smoothing. It exists because
    /// <see cref="IPredictedState{TSelf}"/> requires it, and it is dead on the prediction path:
    /// <c>ClientPrediction</c> only ever calls <see cref="WithRenderState"/>, which is the member to use.</summary>
    public readonly TileMoveState WithPosition(Vector2 position) => WithRenderState(position, Vertical);

    /// <inheritdoc/>
    public readonly TileMoveState WithRenderState(Vector2 position, float vertical)
    {
        TileMoveState copy = this;
        copy.RenderPosition = position;
        copy.RenderVertical = vertical;
        copy.HasRenderOverride = true;
        return copy;
    }

    /// <summary>Simulation equality: the presentation fields are excluded on purpose (see the type doc). This is
    /// the comparison a reconciliation uses to decide whether the prediction was right, so folding a smoothed
    /// render position into it would make every smoothed frame read as a misprediction.</summary>
    public readonly bool Equals(TileMoveState other) =>
        Tile.Equals(other.Tile) && StepFrom.Equals(other.StepFrom) && Facing == other.Facing && Mode == other.Mode
        && StepTicks == other.StepTicks && StepTotal == other.StepTotal
        && Route.Equals(other.Route) && Epoch == other.Epoch && InteractTarget == other.InteractTarget
        && CombatTarget == other.CombatTarget;

    /// <inheritdoc/>
    public readonly override bool Equals(object? obj) => obj is TileMoveState s && Equals(s);

    /// <summary>Hashes the same simulation fields <see cref="Equals(TileMoveState)"/> compares.</summary>
    /// <remarks><see cref="HashCode.Combine{T1, T2, T3, T4, T5, T6, T7, T8}"/> takes at most eight arguments, so the
    /// ninth field regroups the existing call rather than being appended to it.</remarks>
    public readonly override int GetHashCode() =>
        HashCode.Combine(HashCode.Combine(Tile, StepFrom), HashCode.Combine(Facing, Mode, StepTicks, StepTotal),
            Route, Epoch, InteractTarget, CombatTarget);

    /// <summary>Equality operator over <see cref="Equals(TileMoveState)"/>.</summary>
    public static bool operator ==(TileMoveState a, TileMoveState b) => a.Equals(b);

    /// <summary>Inequality operator over <see cref="Equals(TileMoveState)"/>.</summary>
    public static bool operator !=(TileMoveState a, TileMoveState b) => !a.Equals(b);
}
