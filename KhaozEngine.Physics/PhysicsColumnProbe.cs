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
/// <para>A SOLID convex static (box, hull, or compound) yields exactly one standable surface per
/// exposed top face, never a stack: the sweep recognises the inside-solid self-hits BepuPhysics reports
/// while the ray descends through the body's interior and skips them, so only real faces become
/// surfaces. The body's underside bounds the headroom of the first real surface beneath it.</para>
/// <para>This probe queries the world in the WORLD'S OWN space: <see cref="Sample"/> passes (x, z) straight into
/// <see cref="IPhysicsWorld.Raycast"/> with no conversion of its own. When the world has been rebased (<see
/// cref="IPhysicsWorld.Origin"/> is non-zero), pass coordinates already reduced by <c>Origin</c>. On a framed
/// <c>WorldServer</c> that means <c>SamplerSpace.Frame</c>, not the default <c>SamplerSpace.World</c>, which wraps
/// the call back out to absolute coordinates and makes every ray miss.</para>
/// </summary>
public sealed class PhysicsColumnProbe
{
    /// <summary>The vertical nudge below each hit the next cast starts from, in world units. Large
    /// enough to escape the surface just hit, small enough that no two real surfaces fit inside it.</summary>
    const float DescendEpsilon = 0.01f;

    /// <summary>Distance below which a downward hit is treated as an INSIDE-SOLID self-hit rather than a
    /// surface the column crosses. BepuPhysics' convex ray test returns a hit at t == 0 with the hit
    /// point sitting on the cast origin whenever that origin lies inside a solid convex (box, hull, or
    /// any convex child of a compound). Re-casting <see cref="DescendEpsilon"/> below such a hit lands
    /// still inside the same solid, so left unchecked the sweep re-hits it every centimetre and stacks a
    /// run of phantom surfaces through the solid's interior. A genuine face - including every tread of a
    /// staircase mesh and the top of each disjoint child of a compound across the gap between children -
    /// is reported at a clearly positive distance, so this threshold filters ONLY the interior
    /// self-hits, not legitimate same-body surfaces. Verified against BepuPhysics 2.4.</summary>
    const float InsideSolidEpsilon = 1e-4f;

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
        // Y of the last surface written, so each accepted surface stays strictly below the previous one
        // by at least DescendEpsilon (keeps the results monotonic even if a backend ever reports two
        // faces at the same height). Starts at +inf so the first hit is always eligible.
        float lastAcceptedY = float.PositiveInfinity;

        // The sweep walks top-down, so results land here highest-first and are reversed into
        // ascending order at the end. On overflow the FIRST (highest) entry is shifted out, so the
        // lowest surfaces survive.
        int found = 0;

        while (remaining > 0f
            && _world.Raycast(new Vector3(x, castY, z), -Vector3.UnitY, remaining, out RayHit hit, Filter))
        {
            // A hit that barely travelled is the cast origin itself, sitting inside a solid convex (Bepu
            // returns t == 0 there; see InsideSolidEpsilon). It is not a surface the column crosses, so it
            // can never be standable - skipping it here is what stops one solid stacking a run of phantom
            // surfaces down its interior (issue #273). It still advances the descent below.
            bool insideSolid = hit.Distance <= InsideSolidEpsilon;

            if (!insideSolid)
            {
                bool standable = hit.Normal.LengthSquared() > 1e-12f
                    && Vector3.Normalize(hit.Normal).Y >= minWalkableNormalY;

                if (standable && hit.Point.Y <= lastAcceptedY - DescendEpsilon)
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
                    lastAcceptedY = hit.Point.Y;
                }
            }

            // Every hit bounds the clear space below it. For a genuine face that is the face itself; while
            // the sweep descends through a solid's interior the successive inside-solid samples trace the
            // solid down to its underside, so the first real surface below the solid measures its headroom
            // to that underside (a solid deck's underside becomes the ground's ceiling), not to the deck top.
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
