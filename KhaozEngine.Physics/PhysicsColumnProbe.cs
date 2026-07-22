using System;
using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>One standable surface a <see cref="PhysicsColumnProbe"/> sweep found: its world-space top
/// Y and the clear vertical space above it (<see cref="float.PositiveInfinity"/> when nothing hangs
/// overhead within the probe's range).</summary>
/// <param name="Height">World Y of the surface top.</param>
/// <param name="Headroom">Clear vertical space above the surface, world units.</param>
public readonly record struct ColumnSurface(float Height, float Headroom);

/// <summary>
/// The multi-surface widening of <see cref="PhysicsGroundProbe"/>: sweeps a vertical column at an XZ
/// point with repeated downward raycasts through an <see cref="IPhysicsWorld"/>, re-casting from just
/// below each hit, and reports every STANDABLE surface in the column bottom-up with its headroom.
/// A hit is standable when its surface normal passes the walkable-slope gate (its Y component is at
/// least cos(<see cref="MaxSlopeRadians"/>)); a non-standable hit (a wall, a bridge underside, a
/// too-steep face) still counts as the ceiling of whatever lies beneath it, which is how each
/// surface's headroom is measured. This is the physics half of the layered nav bake: a game glues
/// <see cref="Sample"/> to KhaozEngine.Navigation's <c>INavColumnProvider</c> with a one-line
/// delegate (Navigation and Physics deliberately never reference each other, per
/// docs/DEPENDENCY-SEAMS.md's surface-source seam). Statics-only by default, the same stance as
/// <see cref="PhysicsGroundProbe"/>: a crate parked under a bridge is not a nav surface.
/// Deterministic for a fixed physics world.
/// </summary>
public sealed class PhysicsColumnProbe
{
    /// <summary>The vertical nudge below each hit the next cast starts from, in world units. Large
    /// enough to escape the surface just hit, small enough that no two real surfaces fit inside it.</summary>
    const float DescendEpsilon = 0.01f;

    readonly IPhysicsWorld _world;

    /// <summary>Y the sweep's first downward ray starts from (world units). Must sit above the
    /// tallest geometry in range, same contract as <see cref="PhysicsGroundProbe.ProbeHeight"/>.</summary>
    public float ProbeHeight { get; init; } = 1000f;

    /// <summary>How far down the sweep reaches from <see cref="ProbeHeight"/> (world units).</summary>
    public float ProbeRange { get; init; } = 2000f;

    /// <summary>Max walkable slope in radians: a hit is standable when its normal's Y component is at
    /// least cos of this. Default 50 degrees, a common walkable-slope gate.</summary>
    public float MaxSlopeRadians { get; init; } = 50f * MathF.PI / 180f;

    /// <summary>Which body mobilities the sweep may hit. Defaults to <see cref="QueryMobility.Statics"/>
    /// so only terrain / props / buildings shape the column, matching <see cref="PhysicsGroundProbe"/>.</summary>
    public QueryMobility GroundMobility { get; init; } = QueryMobility.Statics;

    /// <summary>Sweeps the column in <paramref name="world"/>.</summary>
    public PhysicsColumnProbe(IPhysicsWorld world) => _world = world ?? throw new ArgumentNullException(nameof(world));

    QueryFilter Filter => new(GroundMobility);

    /// <summary>
    /// Sweeps the column at (<paramref name="x"/>, <paramref name="z"/>) and writes every standable
    /// surface into <paramref name="surfaces"/> bottom-up (ascending height), returning how many were
    /// written. Zero means nothing standable in the column. Each surface's headroom is the gap to the
    /// hit directly above it, standable or not (<see cref="float.PositiveInfinity"/> for the topmost
    /// hit). When the column holds more standable surfaces than the buffer, the LOWEST ones are kept
    /// and the highest dropped, deterministically, matching the <c>INavColumnProvider</c> convention
    /// (the ground is the surface navigation can least afford to lose).
    /// </summary>
    public int Sample(float x, float z, Span<ColumnSurface> surfaces)
    {
        if (surfaces.Length == 0) return 0;

        float minWalkableNormalY = MathF.Cos(MaxSlopeRadians);
        float castY = ProbeHeight;
        float remaining = ProbeRange;
        float ceilingAbove = float.PositiveInfinity;

        // The sweep walks top-down, so results land here highest-first and are reversed into
        // ascending order at the end. On overflow the FIRST (highest) entry is shifted out, so the
        // lowest surfaces survive.
        int found = 0;

        while (remaining > 0f
            && _world.Raycast(new Vector3(x, castY, z), -Vector3.UnitY, remaining, out RayHit hit, Filter))
        {
            bool standable = hit.Normal.LengthSquared() > 1e-12f
                && Vector3.Normalize(hit.Normal).Y >= minWalkableNormalY;

            if (standable)
            {
                float headroom = float.IsPositiveInfinity(ceilingAbove)
                    ? float.PositiveInfinity
                    : ceilingAbove - hit.Point.Y;

                if (found == surfaces.Length)
                {
                    for (int i = 1; i < found; i++) surfaces[i - 1] = surfaces[i];
                    found--;
                }
                surfaces[found++] = new ColumnSurface(hit.Point.Y, headroom);
            }

            ceilingAbove = hit.Point.Y;
            float nextY = hit.Point.Y - DescendEpsilon;
            remaining -= castY - nextY;
            castY = nextY;
        }

        // Reverse the top-down fill into the ascending order the nav bake expects.
        for (int i = 0; i < found / 2; i++)
        {
            (surfaces[i], surfaces[found - 1 - i]) = (surfaces[found - 1 - i], surfaces[i]);
        }

        return found;
    }
}
