using System;
using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>Turns an <see cref="IPhysicsWorld"/> into the <c>groundHeight</c> / <c>groundNormal</c> delegates the
/// character controller (<c>CharacterMovement.Step</c>) expects, by casting a ray straight down and reading the
/// surface it hits. This is the OPT-IN unified-terrain path: once a game registers its terrain surface as physics
/// geometry (e.g. via <c>Scene3DChunkSink</c> with <c>collideTerrain: true</c>, which adds each chunk's surface as
/// a static triangle mesh), it passes <see cref="HeightDelegate"/> / <see cref="NormalDelegate"/> here INSTEAD OF
/// the analytic <c>TerrainCollision.GroundHeight</c> / <c>GroundNormal</c> delegates, so terrain, props, and
/// buildings all resolve through the one physics world.
/// <para>The downward probe is STATICS-ONLY by default (<see cref="GroundMobility"/>): only the static terrain /
/// props / buildings count as ground, so a dynamic body (a crate, a barrel) under the character is not read as
/// ground and the probe returns the terrain height beneath it. Set <see cref="GroundMobility"/> to
/// <see cref="QueryMobility.All"/> to let dynamic bodies act as standable ground.</para>
/// <para>Additive, not breaking: a game that has not adopted the unified path keeps handing the analytic
/// <c>TerrainCollision</c> delegates to the controller and nothing here runs. Both paths coexist.</para>
/// <para>This probe queries the world in the WORLD'S OWN space: <see cref="Height"/>/<see cref="Normal"/> pass
/// (x, z) straight into <see cref="IPhysicsWorld.Raycast"/> with no conversion of their own. When the world has
/// been rebased (<see cref="IPhysicsWorld.Origin"/> is non-zero), pass coordinates already reduced by
/// <c>Origin</c>. On a framed <c>WorldServer</c> that means <c>SamplerSpace.Frame</c>, not the default
/// <c>SamplerSpace.World</c>, which wraps the call back out to absolute coordinates and makes every ray miss.</para>
/// <para>The probe casts from <see cref="ProbeHeight"/> down over <see cref="ProbeRange"/>. When the ray misses
/// (no terrain registered under that XZ, or a hole), the height delegate returns <see cref="FallbackHeight"/> and
/// the normal delegate returns +Y, so the controller degrades to flat-at-fallback rather than throwing. Choose
/// <see cref="ProbeHeight"/> above the tallest terrain and <see cref="ProbeRange"/> to reach the lowest.</para></summary>
public sealed class PhysicsGroundProbe
{
    readonly IPhysicsWorld _world;

    /// <summary>Y the downward ray starts from (world units). Must sit above the tallest terrain in range.</summary>
    public float ProbeHeight { get; init; } = 1000f;

    /// <summary>How far down the ray reaches from <see cref="ProbeHeight"/> (world units). Must reach the lowest
    /// terrain (i.e. <c>ProbeHeight - ProbeRange</c> below the lowest surface).</summary>
    public float ProbeRange { get; init; } = 2000f;

    /// <summary>Ground height returned when the downward ray hits nothing (a hole, or unloaded terrain).</summary>
    public float FallbackHeight { get; init; }

    /// <summary>Which body mobilities the downward ground ray may hit. Defaults to <see cref="QueryMobility.Statics"/>
    /// so ONLY the terrain / props / buildings (the static geometry) count as ground: a dynamic body such as a crate
    /// sitting under the character is NOT read as ground, so the probe returns the terrain height beneath it rather
    /// than the crate's top. Set this to <see cref="QueryMobility.All"/> if a game deliberately wants to stand on
    /// dynamic bodies through the probe.</summary>
    public QueryMobility GroundMobility { get; init; } = QueryMobility.Statics;

    /// <summary>Cast against the terrain/props in <paramref name="world"/>.</summary>
    public PhysicsGroundProbe(IPhysicsWorld world) => _world = world ?? throw new ArgumentNullException(nameof(world));

    QueryFilter Filter => new(GroundMobility);

    /// <summary>Terrain height at (x, z): the Y of the nearest surface a downward ray hits, or
    /// <see cref="FallbackHeight"/> when nothing is under that point.</summary>
    public float Height(float x, float z)
    {
        if (_world.Raycast(new Vector3(x, ProbeHeight, z), -Vector3.UnitY, ProbeRange, out RayHit hit, Filter))
            return hit.Point.Y;
        return FallbackHeight;
    }

    /// <summary>Surface normal at (x, z): the normal of the nearest surface a downward ray hits, or +Y when
    /// nothing is under that point. Feed this as the controller's slope gate so steep terrain cannot be walked
    /// up, exactly as the analytic <c>TerrainCollision.GroundNormal</c> delegate did.</summary>
    public Vector3 Normal(float x, float z)
    {
        if (_world.Raycast(new Vector3(x, ProbeHeight, z), -Vector3.UnitY, ProbeRange, out RayHit hit, Filter) &&
            hit.Normal.LengthSquared() > 1e-12f)
            return Vector3.Normalize(hit.Normal);
        return Vector3.UnitY;
    }

    /// <summary>The <c>groundHeight</c> delegate to pass to <c>CharacterMovement.Step</c>.</summary>
    public Func<float, float, float> HeightDelegate => Height;

    /// <summary>The <c>groundNormal</c> delegate to pass to <c>CharacterMovement.Step</c> (slope gate).</summary>
    public Func<float, float, Vector3> NormalDelegate => Normal;
}
