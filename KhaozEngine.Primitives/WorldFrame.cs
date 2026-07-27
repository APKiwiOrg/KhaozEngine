using System;
using System.Numerics;

namespace KhaozEngine.Primitives;

/// <summary>
/// A quantized planar simulation/render frame: the anchor is <c>(X, 0, Z) * <see cref="Grid"/></c> metres, always
/// exactly representable in float32. Y is NEVER framed. <c>default</c> is the world origin, so a game that never
/// leaves the origin is byte-identical to the pre-frame engine.
/// <para>
/// A frame belongs to a SIMULATION ISLAND (one world plus one physics world), never to an individual entity: an
/// entity's stored frame is a stamp of its island's frame, not an independent choice. The rendering release uses
/// only <see cref="Nearest(Vector3)"/> and <see cref="Anchor"/> (through <c>Scene3D.RenderOrigin</c>). The
/// simulation-side members exist so the frame math has a single home and a single set of exactness tests.
/// </para>
/// <para>
/// The exactness lemma this type is built around: let <c>L</c> be a float32 local coordinate with
/// <c>|L| &lt; Grid</c> and let <c>k * Grid</c> be an integer multiple of the grid. If
/// <c>|L + k * Grid| &lt;= |L|</c> then <c>L + k * Grid</c> is exactly representable and the addition introduces no
/// error at all. <c>|L| &lt; 128</c> puts <c>L</c> in a binade no higher than <c>[64, 128)</c>, so <c>L</c> is an
/// integer multiple of <c>ULP(L) &lt;= 2^-17</c>, <c>k * 128</c> is an integer and hence also a multiple of
/// <c>2^-17</c>, and a sum whose magnitude does not grow cannot need a finer quantum than its operands had. That is
/// why <see cref="Nearest(Vector3)"/> rounds to NEAREST rather than flooring: rounding leaves a freshly anchored
/// local in <c>[-Grid/2, Grid/2]</c> and the <see cref="ReanchorRadius"/> trigger only fires past 96 m, so a
/// re-anchor strictly reduces the per-axis magnitude and the lemma applies unconditionally.
/// </para>
/// </summary>
/// <param name="X">Anchor index along world X. The anchor is <c>X * <see cref="Grid"/></c> metres.</param>
/// <param name="Z">Anchor index along world Z. The anchor is <c>Z * <see cref="Grid"/></c> metres.</param>
public readonly record struct WorldFrame(short X, short Z)
{
    /// <summary>Frame spacing in metres. A CONSTANT, not a knob: two peers on different grids silently decode
    /// different world positions from the same bytes, and the value is derived from a measured float32 divergence
    /// budget rather than from anything a game authors. A power of two, so an anchor is exactly representable and
    /// the rebase arithmetic is exact.</summary>
    public const float Grid = 128f;

    /// <summary>The local-axis magnitude that triggers a re-anchor on a single-island head. Guarantees a minimum of
    /// 64 m of travel between consecutive re-anchors (a reversal must retrace 64 m, a straight line must cover
    /// 128 m), which is what bounds how often a rebase can run.</summary>
    public const float ReanchorRadius = 96f;

    /// <summary>The largest local PLANAR MAGNITUDE the 10 mm per-window divergence budget tolerates: the top of the
    /// last float32 binade that fits. Used to VALIDATE island sizing, never as a runtime bound. A shard cell's worst
    /// per-axis local is <c>CellSize/2 + OverlapMargin + Grid/2</c>, and the corner case is that times
    /// <c>sqrt(2)</c>, which is what must fit under this.</summary>
    public const float MaxLocalRadius = 512f;

    /// <summary>Divergence per 20 s window, in ULPs of the coordinate, measured on production movement code at two
    /// offsets a factor of two apart (210.4 at 50 km, 220.7 at 100 km). The conservative value of the pair.
    /// <see cref="MaxLocalRadius"/> is the top of the last binade whose <c>Divergence20sUlps * ULP</c> fits
    /// <see cref="DivergenceBudgetMetres"/>.</summary>
    public const float Divergence20sUlps = 215f;

    /// <summary>The per-20-s-window divergence budget, in metres, that <see cref="MaxLocalRadius"/> is derived
    /// from. A budget on the RATE at which divergence grows, never a steady-state bound on divergence: a re-anchor
    /// is an exact translation and therefore carries accumulated error forward completely unchanged.</summary>
    public const float DivergenceBudgetMetres = 0.010f;

    /// <summary>The world origin: <c>default</c>, whose <see cref="Anchor"/> is exactly <see cref="Vector3.Zero"/>.</summary>
    public static WorldFrame Origin => default;

    /// <summary>The frame's world-space anchor point. Exact in float32 for every representable X/Z.</summary>
    public Vector3 Anchor => new(X * Grid, 0f, Z * Grid);

    /// <summary>The frame whose anchor is NEAREST <paramref name="world"/> (round, not floor), so a freshly
    /// anchored local coordinate lies in <c>[-Grid/2, Grid/2]</c> per axis. Y is ignored.</summary>
    public static WorldFrame Nearest(Vector3 world) => Nearest(world.X, world.Z);

    /// <summary>The frame whose anchor is NEAREST <c>(worldX, worldZ)</c>. Ties round to even, the IEEE default, so
    /// two peers evaluating this on the same inputs always agree. Saturates at the <see cref="short"/> range
    /// (roughly plus or minus 4,194 km), because a hard bound is better than a silent wrap.</summary>
    public static WorldFrame Nearest(float worldX, float worldZ) =>
        new(RoundToIndex(worldX), RoundToIndex(worldZ));

    static short RoundToIndex(float world)
    {
        float i = MathF.Round(world / Grid);
        if (i >= short.MaxValue) return short.MaxValue;
        if (i <= short.MinValue) return short.MinValue;
        return (short)i;
    }

    /// <summary>World to frame-local. X and Z are shifted, Y passes through unchanged.</summary>
    public Vector3 ToLocal(Vector3 world) => new(world.X - X * Grid, world.Y, world.Z - Z * Grid);

    /// <summary>World to frame-local, planar only.</summary>
    public Vector2 ToLocalXz(float worldX, float worldZ) => new(worldX - X * Grid, worldZ - Z * Grid);

    /// <summary>Frame-local to world. X and Z are shifted, Y passes through unchanged.</summary>
    public Vector3 ToWorld(Vector3 local) => new(local.X + X * Grid, local.Y, local.Z + Z * Grid);

    /// <summary>Frame-local to world, planar only.</summary>
    public Vector2 ToWorldXz(float localX, float localZ) => new(localX + X * Grid, localZ + Z * Grid);

    /// <summary>The translation that carries a local coordinate in THIS frame into <paramref name="target"/>: add it
    /// to the local. Both anchors are integer multiples of <see cref="Grid"/>, so the delta is an integer multiple of
    /// the grid and the addition is exact whenever the magnitude does not grow (the lemma on the type doc). A
    /// re-anchor guarantees that by construction. A conversion between two arbitrary frames does not, and is exact
    /// only to half a ULP of the destination magnitude.</summary>
    public Vector3 DeltaTo(WorldFrame target) => new((X - target.X) * Grid, 0f, (Z - target.Z) * Grid);

    /// <summary>True when <paramref name="local"/> has drifted past <see cref="ReanchorRadius"/> on either planar
    /// axis. Y is ignored. The re-anchor POLICY for a single-island head, not a per-entity test.</summary>
    public static bool ShouldReanchor(Vector3 local) =>
        MathF.Abs(local.X) > ReanchorRadius || MathF.Abs(local.Z) > ReanchorRadius;
}
