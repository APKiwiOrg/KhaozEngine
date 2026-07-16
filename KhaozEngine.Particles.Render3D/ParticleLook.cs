using KhaozEngine.Render3D;

// Namespace is KhaozEngine.Particles (the sim's namespace), not .Render3D, on purpose: a consumer that has
// `using KhaozEngine.Particles;` for ParticleSystem/EmitterConfig then also gets ParticleLook, the Scene3D
// DrawParticles/DrawEffect extensions, and the VfxPresets in scope. Mirrors the Telegraphs.Render3D precedent.
namespace KhaozEngine.Particles
{
    /// <summary>
    /// The per-emitter presentation recipe the adapter uses to turn one <see cref="ParticleSystem"/> (or one
    /// phase of a <see cref="ParticleEffectPlayer"/>) into <see cref="Scene3D"/> draws. The sim stays render-free:
    /// shape, blend, stretch, trails and light links are renderer vocabulary that lives here, not in the particle.
    /// A pool with mixed looks is handled by giving each phase its own <see cref="ParticleLook"/>.
    /// </summary>
    public struct ParticleLook
    {
        /// <summary>Procedural sprite shape evaluated in the particle fragment shader.</summary>
        public ParticleShape Shape;

        /// <summary>Per-shape tuning knob in [0,1]. 0 is the shape's documented default look.</summary>
        public float ShapeParam;

        /// <summary>Alpha or additive compositing for every sprite from this look.</summary>
        public BillboardBlend Blend;

        /// <summary>Velocity-stretch factor: 0 keeps a round camera-facing quad, larger elongates along motion.</summary>
        public float Stretch;

        /// <summary>When true, each live particle's motion history (from <see cref="ParticleSystem.GetTrail"/>) is
        /// forwarded as a tapered ribbon to <see cref="Scene3D.DrawTrail"/>. Ignored when the pool has no trail
        /// capacity (<see cref="ParticleSystem.TrailCapacity"/> 0).</summary>
        public bool Trails;

        /// <summary>Ribbon look for the forwarded trails.</summary>
        public TrailStyle TrailStyle;

        /// <summary>Trail half-width as a multiple of the particle's size (scaled down the tail). &lt;= 0 is treated
        /// as 0.5.</summary>
        public float TrailWidthScale;

        /// <summary>Light-link radius in world units. &gt; 0 (together with a positive <see cref="LightIntensity"/>)
        /// links the brightest live particles as budgeted point lights via <see cref="Scene3D.AddLight"/>.</summary>
        public float LightRadius;

        /// <summary>Light-link base intensity. The per-particle intensity is scaled by the particle's alpha, so a
        /// fading particle dims its light. 0 disables the light link.</summary>
        public float LightIntensity;
    }
}
