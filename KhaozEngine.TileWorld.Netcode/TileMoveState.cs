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
/// <para><see cref="Position"/> is DERIVED: the tile plus the fraction of the current step already spent, in TILE
/// units. <see cref="Vertical"/> is the PLANE INDEX as a float rather than a height in metres, so the state stays
/// document-free and the simulator can produce it without loading a world. <c>TilePresenter</c> multiplies by the
/// document's plane height on the way to the view.</para>
/// <para><see cref="RenderPosition"/> / <see cref="RenderVertical"/> / <see cref="HasRenderOverride"/> are
/// PRESENTATION-only, written by <see cref="WithRenderState"/> on the way out of
/// <c>ClientPrediction.RenderedState</c> and read by nothing else. The simulator never reads them, the codecs
/// never write them, and equality ignores them, so they cannot perturb determinism. They exist because the
/// prediction layer smooths in continuous world space while this state is a lattice, and the smoothed answer has
/// to reach the view without ever becoming the simulation's idea of where the player is.</para>
/// </summary>
public struct TileMoveState : IPredictedState<TileMoveState>, IComponent, IEquatable<TileMoveState>
{
    /// <summary>The tile the player stands on. A step COMMITS by changing this, never by fractions, so this field
    /// alone answers every rules question (reach, region, occupancy) with no rounding.</summary>
    public TileCoord Tile;

    /// <summary>The direction the player faces. Set by the step taken, or by an interaction's target when standing
    /// still, which is why it is its own field rather than read back off the route.</summary>
    public TileDirection Facing;

    /// <summary>Walk or run: which entry of <c>TileStepTicks</c> the current route steps at. Held on the state so a
    /// mode change mid route takes effect on the next step rather than restarting the walk.</summary>
    public TileMoveMode Mode;

    /// <summary>Ticks already spent in the current step. Always below <see cref="StepTotal"/>, because the tick that
    /// would reach it is the tick that commits the step and resets this to zero.</summary>
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

    /// <summary>Presentation only, see the type doc.</summary>
    public Vector2 RenderPosition;

    /// <summary>Presentation only, see the type doc.</summary>
    public float RenderVertical;

    /// <summary>Presentation only, see the type doc.</summary>
    public bool HasRenderOverride;

    /// <summary>A standing state on one tile, facing one way, with no route. <see cref="StepTotal"/> starts at 1
    /// rather than 0 so the fraction maths is never dividing by a zero total on a freshly placed player.</summary>
    public static TileMoveState At(TileCoord tile, TileDirection facing) => new()
    {
        Tile = tile,
        Facing = facing,
        Mode = TileMoveMode.Walk,
        Route = TileRoute.None,
        StepTotal = 1,
    };

    /// <summary>The fraction of the current step already spent, 0 when standing. The single place an integer tick
    /// count turns into a float, and it is read only: nothing ever writes the result back.</summary>
    public readonly float StepFraction =>
        Route.IsIdle || StepTotal == 0 ? 0f : (float)StepTicks / StepTotal;

    /// <inheritdoc/>
    public readonly Vector2 Position
    {
        get
        {
            if (Route.IsIdle) return new Vector2(Tile.X, Tile.Z);
            TileCoord next = Route.Next;
            float f = StepFraction;
            return new Vector2(Tile.X + (next.X - Tile.X) * f, Tile.Z + (next.Z - Tile.Z) * f);
        }
    }

    /// <inheritdoc/>
    public readonly float Vertical => Tile.Plane;

    /// <inheritdoc/>
    public readonly uint TeleportEpoch => Epoch;

    /// <inheritdoc/>
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
        Tile.Equals(other.Tile) && Facing == other.Facing && Mode == other.Mode
        && StepTicks == other.StepTicks && StepTotal == other.StepTotal
        && Route.Equals(other.Route) && Epoch == other.Epoch && InteractTarget == other.InteractTarget;

    /// <inheritdoc/>
    public readonly override bool Equals(object? obj) => obj is TileMoveState s && Equals(s);

    /// <summary>Hashes the same simulation fields <see cref="Equals(TileMoveState)"/> compares.</summary>
    public readonly override int GetHashCode() =>
        HashCode.Combine(Tile, Facing, Mode, StepTicks, StepTotal, Route, Epoch, InteractTarget);

    /// <summary>Equality operator over <see cref="Equals(TileMoveState)"/>.</summary>
    public static bool operator ==(TileMoveState a, TileMoveState b) => a.Equals(b);

    /// <summary>Inequality operator over <see cref="Equals(TileMoveState)"/>.</summary>
    public static bool operator !=(TileMoveState a, TileMoveState b) => !a.Equals(b);
}
