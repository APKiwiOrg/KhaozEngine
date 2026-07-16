using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Collision;
using KhaozEngine.Terrain;

namespace KhaozEngine.Navigation;

/// <summary>
/// The default overworld <see cref="INavSurfaceProvider"/>: the analytic terrain height as the base
/// standable surface, raised to any <see cref="WorldSurfaces"/> prop top covering the point (a low
/// rock, a ramp, a platform), so a creature stands on the prop instead of routing around it. A cell
/// reports not standable (blocked) when the terrain slope at its center exceeds
/// <see cref="MaxSlopeRadians"/> AND no surface covers it (a prop top rescues slope-blocked ground,
/// since the agent stands on the prop, not the hillside), or when a solid <see cref="WorldCollider"/>
/// with no covering surface overlaps a probe circle at its center. Reports open-sky headroom
/// (<see cref="float.PositiveInfinity"/>): height-aware headroom is a later pass and a game that wants
/// it supplies its own provider. Deterministic.
/// </summary>
public sealed class TerrainSurfaceProvider : INavSurfaceProvider
{
    readonly TerrainCollision _terrain;
    readonly WorldSurfaces? _surfaces;
    readonly WorldColliders? _colliders;
    readonly float _colliderProbeRadius;

    /// <summary>Max walkable terrain slope (radians), passed to <see cref="TerrainCollision.IsWalkable"/>.</summary>
    public float MaxSlopeRadians { get; }

    /// <summary>
    /// Builds the provider over <paramref name="terrain"/> and <paramref name="maxSlopeRadians"/>, with
    /// optional standable <paramref name="surfaces"/> (prop tops that raise the height) and solid
    /// <paramref name="colliders"/> (obstacles that block where no surface covers). A null
    /// <paramref name="surfaces"/> means terrain only, a null <paramref name="colliders"/> means no
    /// obstacles. <paramref name="colliderProbeRadius"/> is the query/resolve radius used against
    /// <paramref name="colliders"/> in <see cref="TrySample"/>: the baker computes it as
    /// <c>cellSize * 0.70710678f</c> (half the cell diagonal) and passes it in, since the provider is
    /// sampled at a point and does not know the cell size. <see cref="TrySample"/> floors the radius at
    /// <c>1e-4f</c> so a point-in-collider test still works even at the default of 0.
    /// </summary>
    public TerrainSurfaceProvider(
        TerrainCollision terrain,
        float maxSlopeRadians,
        WorldSurfaces? surfaces = null,
        WorldColliders? colliders = null,
        float colliderProbeRadius = 0f)
    {
        _terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        MaxSlopeRadians = maxSlopeRadians;
        _surfaces = surfaces;
        _colliders = colliders;
        _colliderProbeRadius = colliderProbeRadius;
    }

    /// <inheritdoc/>
    public bool TrySample(float x, float z, out float height, out float headroom)
    {
        headroom = float.PositiveInfinity;

        // A prop top rescues slope-blocked ground and wins over colliders: the agent stands on the
        // prop, not the hillside or the obstacle beneath it, so neither is tested when a surface covers.
        float? propTop = _surfaces?.Query(x, z);
        if (propTop.HasValue)
        {
            height = propTop.Value;
            return true;
        }

        if (!_terrain.IsWalkable(x, z, MaxSlopeRadians))
        {
            height = 0f;
            return false;
        }

        if (_colliders is not null)
        {
            float radius = MathF.Max(_colliderProbeRadius, 1e-4f);
            IReadOnlyList<WorldCollider> candidates = _colliders.Query(x, z, radius);
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].Resolve(new Vector2(x, z), radius, out _))
                {
                    height = 0f;
                    return false;
                }
            }
        }

        height = _terrain.GroundHeight(x, z);
        return true;
    }
}
