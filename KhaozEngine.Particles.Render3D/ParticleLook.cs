using KhaozEngine.Render3D;

// Namespace is KhaozEngine.Particles (the sim's namespace), not .Render3D, on purpose: a consumer that has
// `using KhaozEngine.Particles;` for ParticleSystem/EmitterConfig then also gets ParticleLook, the Scene3D
// DrawParticles/DrawEffect extensions, and the VfxPresets in scope. Mirrors the Telegraphs.Render3D precedent.
namespace KhaozEngine.Particles
{
    /// <summary>How the adapter advances a flipbook look's frame over a particle's life.</summary>
    public enum ParticleFlipbookMode
    {
        /// <summary>Frame sweeps across the sheet once as the particle ages (frame = life fraction * frame count).
        /// For one-shot sheets, e.g. an explosion or an impact burst. Playback clamps on the last frame.</summary>
        LifeOneShot = 0,
        /// <summary>Frame advances at <see cref="ParticleLook.FlipbookFps"/> and loops. For continuous sheets, e.g.
        /// looping fire or smoke, optionally phase-staggered per particle by seed (<see cref="ParticleLook.FlipbookRandomStart"/>).</summary>
        TimeLoop = 1,
    }

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

        /// <summary>Camera-facing (default) or flat in the ground plane (shockwave rings, ground glows).</summary>
        public ParticleOrientation Orientation;

        /// <summary>Per-sprite multiplier on <see cref="Scene3D.ParticleSoftFade"/>. 0 means 1 (the default).
        /// Flat-on-ground looks want a small value (around 0.1) so the floor just behind the quad does not fade
        /// it out.</summary>
        public float SoftFadeScale;

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

        /// <summary>Optional authored-atlas playback for this look. The default (an invalid
        /// <see cref="ParticleFlipbook.Texture"/>) keeps the sprites on the procedural <see cref="Shape"/> path.
        /// When active, each sprite's <see cref="ParticleSprite.FlipbookFrame"/> is resolved from this look's
        /// <see cref="FlipbookMode"/> timing and the atlas frame replaces the procedural shape.</summary>
        public ParticleFlipbook Flipbook;

        /// <summary>How the flipbook frame advances (life-swept one-shot or fps-driven loop). Only read when
        /// <see cref="Flipbook"/> is active.</summary>
        public ParticleFlipbookMode FlipbookMode;

        /// <summary>Playback rate for <see cref="ParticleFlipbookMode.TimeLoop"/>, in frames per second. 0 is
        /// treated as 12. Ignored by <see cref="ParticleFlipbookMode.LifeOneShot"/>.</summary>
        public float FlipbookFps;

        // Stored inverted so the struct's zero-value default (a look created without touching this field) reads as
        // "random start on", the desired default. The public property below re-inverts.
        bool _flipbookNoRandomStart;

        /// <summary>For <see cref="ParticleFlipbookMode.TimeLoop"/>, stagger each particle's starting frame by its
        /// seed so a burst of identical looping sprites does not play in lockstep. Defaults to true (set false for a
        /// synchronized loop). Ignored by <see cref="ParticleFlipbookMode.LifeOneShot"/>.</summary>
        public bool FlipbookRandomStart
        {
            readonly get => !_flipbookNoRandomStart;
            set => _flipbookNoRandomStart = !value;
        }
    }
}
