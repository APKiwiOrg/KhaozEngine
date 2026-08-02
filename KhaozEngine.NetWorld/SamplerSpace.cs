namespace KhaozEngine.NetWorld;

/// <summary>The coordinate space a head's SAMPLER DELEGATES read (ground height, ground normal, medium). It says
/// nothing about the physics world: an island's <c>IPhysicsWorld</c> is always in the island's frame by definition
/// (its <c>Origin</c> is the frame anchor), because a physics world IS a coordinate space and cannot be in two.
/// <para>It says nothing about <see cref="WorldBounds"/> either. A play area is authored content and stays
/// absolute, so the step always converts for it, in both modes.</para></summary>
public enum SamplerSpace
{
    /// <summary>Samplers take ABSOLUTE world coordinates. The stepper wraps each one so the anchor is added back
    /// before the call and subtracted from any returned coordinate. That is correct only for a sampler that
    /// genuinely reads absolute world space (the analytic <c>TerrainCollision</c> delegates): it fixes the
    /// ACCUMULATION half of the problem (the carried state is frame-local, which is the term that grows with
    /// running time), but each sample coordinate is still evaluated at world magnitude, so the sampling quantum at
    /// 100 km is still 7.8 mm. A sampler backed by the island's own rebased physics world (e.g. a
    /// <c>PhysicsGroundProbe</c> over a rebased <c>IPhysicsWorld</c>) REQUIRES <see cref="Frame"/> instead: that
    /// world raycasts in its own rebased space, so wrapping the call back out to absolute coordinates makes every
    /// ray miss and the probe silently return its fallback height and a +Y normal, flattening the ground and
    /// disabling the steep-terrain rules entirely. The zero-work adoption step for a genuinely absolute sampler, and the
    /// default.</summary>
    World = 0,

    /// <summary>Samplers take FRAME-LOCAL coordinates and the stepper passes them straight through. The full fix. A
    /// consumer whose ground follow comes from a rebased <c>IPhysicsWorld</c> with chunk-local terrain collision
    /// meshes gets this for free.</summary>
    Frame = 1,
}
