namespace KhaozEngine.NetWorld;

/// <summary>The coordinate space a head's SAMPLER DELEGATES read (ground height, ground normal, medium). It says
/// nothing about the physics world: an island's <c>IPhysicsWorld</c> is always in the island's frame by definition
/// (its <c>Origin</c> is the frame anchor), because a physics world IS a coordinate space and cannot be in two.
/// <para>It says nothing about <see cref="WorldBounds"/> either. A play area is authored content and stays
/// absolute, so the step always converts for it, in both modes.</para></summary>
public enum SamplerSpace
{
    /// <summary>Samplers take ABSOLUTE world coordinates. The stepper wraps each one so the anchor is added back
    /// before the call and subtracted from any returned coordinate. Correct, and it fixes the ACCUMULATION half of
    /// the problem (the carried state is frame-local, which is the term that grows with running time), but each
    /// sample coordinate is still evaluated at world magnitude, so the sampling quantum at 100 km is still 7.8 mm.
    /// The zero-work adoption step, and the default.</summary>
    World = 0,

    /// <summary>Samplers take FRAME-LOCAL coordinates and the stepper passes them straight through. The full fix. A
    /// consumer whose ground follow comes from a rebased <c>IPhysicsWorld</c> with chunk-local terrain collision
    /// meshes gets this for free.</summary>
    Frame = 1,
}
