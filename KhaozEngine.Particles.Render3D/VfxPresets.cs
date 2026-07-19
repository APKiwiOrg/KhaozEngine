using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

// See ParticleLook.cs for why the namespace is KhaozEngine.Particles, not .Render3D.
namespace KhaozEngine.Particles
{
    /// <summary>
    /// One authored modern VFX preset: a <see cref="ParticleEffect"/> (the sim schedule) paired with one
    /// <see cref="ParticleLook"/> per phase (the presentation). Play the effect through a
    /// <see cref="ParticleEffectPlayer"/> and draw it with <see cref="ParticleSceneExtensions.DrawEffect"/>,
    /// handing it <see cref="Looks"/>.
    /// </summary>
    public sealed class VfxPreset
    {
        /// <summary>Build a preset. <paramref name="looks"/> must have one entry per phase of <paramref name="effect"/>.</summary>
        public VfxPreset(ParticleEffect effect, IReadOnlyList<ParticleLook> looks)
        {
            Effect = effect;
            Looks = looks;
        }

        /// <summary>The scheduled multi-phase effect.</summary>
        public ParticleEffect Effect { get; }

        /// <summary>One presentation look per phase, index-aligned with the effect's phases.</summary>
        public IReadOnlyList<ParticleLook> Looks { get; }
    }

    /// <summary>
    /// A library of modern, ready-to-use VFX presets used by the showcase and as consumer on-ramps. Authored to
    /// read at roughly 8 to 12 world units from the camera, with the effect origin on the ground (y 0) and +Y up.
    /// Each property returns a fresh <see cref="VfxPreset"/> (new effect + looks) per call, so a caller can mutate
    /// what it gets without disturbing the next caller, mirroring <see cref="EmitterConfig.Spark"/>.
    /// </summary>
    public static class VfxPresets
    {
        /// <summary>A punchy impact: white flash, radial sparks, light-linked embers, and a short smoke puff.</summary>
        public static VfxPreset FireBurst
        {
            get
            {
                var phases = new[]
                {
                    // Flash: one bright disc that blooms and dies fast.
                    new ParticleEffectPhase
                    {
                        Config = new EmitterConfig
                        {
                            LifetimeMin = 0.3f, LifetimeMax = 0.3f,
                            StartSize = 1.4f, EndSize = 0.25f,
                            StartColor = new Color(1f, 0.95f, 0.8f, 1f),
                            EndColor = new Color(1f, 0.7f, 0.3f, 0f),
                            SizeCurve = ParticleCurve.EaseOut,
                            AlphaCurve = ParticleCurve.Flash(0.15f),
                        },
                        BurstCount = 1, PoolCapacity = 4,
                    },
                    // Sparks: fast radial streaks pulled down by gravity.
                    new ParticleEffectPhase
                    {
                        Config = new EmitterConfig
                        {
                            LifetimeMin = 0.3f, LifetimeMax = 0.6f,
                            SpeedMin = 5f, SpeedMax = 9f,
                            Shape = EmissionShape.Sphere, ShapeRadius = 0.2f, ShapeShell = 0.5f,
                            VelocityMode = ParticleVelocityMode.Radial,
                            Gravity = new Vector3(0f, -9f, 0f), Drag = 1.5f,
                            StartSize = 0.28f, EndSize = 0.05f,
                            StartColor = new Color(1f, 0.9f, 0.6f, 1f),
                            EndColor = new Color(1f, 0.4f, 0.1f, 0f),
                            SizeCurve = ParticleCurve.EaseOut,
                            AlphaCurve = ParticleCurve.EaseOut,
                        },
                        BurstCount = 24, PoolCapacity = 40,
                    },
                    // Embers: slower radial coals that drift up and light the scene.
                    new ParticleEffectPhase
                    {
                        Config = new EmitterConfig
                        {
                            LifetimeMin = 0.6f, LifetimeMax = 1.2f,
                            SpeedMin = 1.5f, SpeedMax = 3.5f,
                            Shape = EmissionShape.Sphere, ShapeRadius = 0.25f, ShapeShell = 0.4f,
                            VelocityMode = ParticleVelocityMode.Radial,
                            Gravity = new Vector3(0f, 1.2f, 0f), Drag = 1.2f,
                            StartSize = 0.3f, EndSize = 0.12f,
                            StartColor = new Color(1f, 0.5f, 0.15f, 1f),
                            EndColor = new Color(0.5f, 0.08f, 0.02f, 0f),
                            SizeCurve = ParticleCurve.EaseOut,
                            AlphaCurve = ParticleCurve.EaseInOut,
                            SpinMin = -1.5f, SpinMax = 1.5f,
                        },
                        BurstCount = 8, PoolCapacity = 16,
                    },
                    // Smoke: a short alpha wisp that grows and rises after the flash.
                    new ParticleEffectPhase
                    {
                        Config = new EmitterConfig
                        {
                            LifetimeMin = 0.8f, LifetimeMax = 1.5f,
                            SpeedMin = 0.5f, SpeedMax = 1.2f,
                            Direction = Vector3.UnitY, SpreadDegrees = 25f,
                            Gravity = new Vector3(0f, 0.6f, 0f), Drag = 0.5f,
                            StartSize = 0.25f, EndSize = 0.9f,
                            StartColor = new Color(0.4f, 0.4f, 0.42f, 0.5f),
                            UseMidColor = true, MidColor = new Color(0.2f, 0.2f, 0.22f, 0.5f),
                            EndColor = new Color(0.3f, 0.3f, 0.32f, 0f),
                            SizeCurve = ParticleCurve.EaseOut,
                            AlphaCurve = ParticleCurve.FadeInOut(0.25f),
                            TurbulenceStrength = 0.8f, TurbulenceFrequency = 0.6f,
                        },
                        Delay = 0.1f, Duration = 0.4f, RatePerSecond = 25f, PoolCapacity = 32,
                    },
                };

                var looks = new[]
                {
                    // The flash carries its own brief light: the strongest single cue that something hit.
                    new ParticleLook
                    {
                        Shape = ParticleShape.SoftGlow, ShapeParam = 0.3f, Blend = BillboardBlend.Additive,
                        LightRadius = 3.5f, LightIntensity = 2.2f,
                    },
                    new ParticleLook { Shape = ParticleShape.Spark, Blend = BillboardBlend.Additive, Stretch = 0.3f },
                    new ParticleLook
                    {
                        Shape = ParticleShape.Ember, Blend = BillboardBlend.Additive,
                        LightRadius = 2.5f, LightIntensity = 1.2f,
                    },
                    new ParticleLook { Shape = ParticleShape.Wisp, Blend = BillboardBlend.Alpha },
                };

                return new VfxPreset(new ParticleEffect(phases), looks);
            }
        }

        /// <summary>Icy shatter: star + spark shards fly out, an expanding ice ring, and a faint mist.</summary>
        public static VfxPreset FrostShatter
        {
            get
            {
                var phases = new[]
                {
                    // Star shards: cool glints spun out from the impact.
                    new ParticleEffectPhase
                    {
                        Config = new EmitterConfig
                        {
                            LifetimeMin = 0.3f, LifetimeMax = 0.6f,
                            SpeedMin = 4f, SpeedMax = 8f,
                            Shape = EmissionShape.Sphere, ShapeRadius = 0.2f, ShapeShell = 0.5f,
                            VelocityMode = ParticleVelocityMode.Radial,
                            Gravity = new Vector3(0f, -4f, 0f), Drag = 3f,
                            StartSize = 0.3f, EndSize = 0.08f,
                            StartColor = new Color(0.8f, 0.95f, 1f, 1f),
                            EndColor = new Color(0.4f, 0.7f, 1f, 0f),
                            SizeCurve = ParticleCurve.EaseOut,
                            AlphaCurve = ParticleCurve.EaseOut,
                            RandomStartRotation = true, SpinMin = -3f, SpinMax = 3f,
                        },
                        BurstCount = 14, PoolCapacity = 24,
                    },
                    // Spark shards: thin fast splinters with drag.
                    new ParticleEffectPhase
                    {
                        Config = new EmitterConfig
                        {
                            LifetimeMin = 0.25f, LifetimeMax = 0.5f,
                            SpeedMin = 5f, SpeedMax = 9f,
                            Shape = EmissionShape.Sphere, ShapeRadius = 0.15f, ShapeShell = 0.6f,
                            VelocityMode = ParticleVelocityMode.Radial,
                            Gravity = new Vector3(0f, -5f, 0f), Drag = 3.5f,
                            StartSize = 0.22f, EndSize = 0.04f,
                            StartColor = new Color(0.9f, 0.97f, 1f, 1f),
                            EndColor = new Color(0.5f, 0.75f, 1f, 0f),
                            SizeCurve = ParticleCurve.EaseOut,
                            AlphaCurve = ParticleCurve.EaseOut,
                        },
                        BurstCount = 12, PoolCapacity = 24,
                    },
                    // Ring: an expanding ice annulus.
                    new ParticleEffectPhase
                    {
                        Config = new EmitterConfig
                        {
                            LifetimeMin = 0.4f, LifetimeMax = 0.4f,
                            StartSize = 0.3f, EndSize = 1.6f,
                            StartColor = new Color(0.7f, 0.9f, 1f, 0.9f),
                            EndColor = new Color(0.6f, 0.85f, 1f, 0f),
                            SizeCurve = ParticleCurve.EaseOut,
                            AlphaCurve = ParticleCurve.EaseOut,
                        },
                        BurstCount = 1, PoolCapacity = 4,
                    },
                    // Mist: a faint alpha haze that lingers a moment.
                    new ParticleEffectPhase
                    {
                        Config = new EmitterConfig
                        {
                            LifetimeMin = 0.4f, LifetimeMax = 0.8f,
                            SpeedMin = 0.3f, SpeedMax = 0.8f,
                            Direction = Vector3.UnitY, SpreadDegrees = 40f,
                            Gravity = new Vector3(0f, 0.3f, 0f), Drag = 1f,
                            StartSize = 0.3f, EndSize = 0.7f,
                            StartColor = new Color(0.8f, 0.9f, 1f, 0.35f),
                            EndColor = new Color(0.7f, 0.85f, 1f, 0f),
                            SizeCurve = ParticleCurve.EaseOut,
                            AlphaCurve = ParticleCurve.FadeInOut(0.3f),
                            TurbulenceStrength = 0.5f, TurbulenceFrequency = 0.6f,
                        },
                        Delay = 0.05f, Duration = 0.25f, RatePerSecond = 30f, PoolCapacity = 24,
                    },
                };

                var looks = new[]
                {
                    new ParticleLook { Shape = ParticleShape.Star, ShapeParam = 0.4f, Blend = BillboardBlend.Additive },
                    new ParticleLook { Shape = ParticleShape.Spark, Blend = BillboardBlend.Additive, Stretch = 0.35f },
                    new ParticleLook { Shape = ParticleShape.Ring, ShapeParam = 0.2f, Blend = BillboardBlend.Additive },
                    new ParticleLook { Shape = ParticleShape.Wisp, Blend = BillboardBlend.Alpha },
                };

                return new VfxPreset(new ParticleEffect(phases), looks);
            }
        }

        /// <summary>Gentle rising motes for a heal: warm green-gold star sparkles that softly light the target.</summary>
        public static VfxPreset HealMotes
        {
            get
            {
                var phases = new[]
                {
                    new ParticleEffectPhase
                    {
                        Config = new EmitterConfig
                        {
                            LifetimeMin = 1f, LifetimeMax = 1.8f,
                            SpeedMin = 0.6f, SpeedMax = 1.4f,
                            Direction = Vector3.UnitY, SpreadDegrees = 15f,
                            Shape = EmissionShape.Disc, ShapeRadius = 0.8f, ShapeShell = 0.3f,
                            Gravity = new Vector3(0f, 0.4f, 0f), Drag = 0.4f,
                            StartSize = 0.12f, EndSize = 0.2f,
                            StartColor = new Color(0.5f, 1f, 0.5f, 1f),
                            StartColorB = new Color(1f, 0.9f, 0.4f, 1f),
                            EndColor = new Color(0.4f, 0.9f, 0.4f, 0f),
                            EndColorB = new Color(0.9f, 0.8f, 0.3f, 0f),
                            VaryColor = true,
                            SizeCurve = ParticleCurve.EaseInOut,
                            AlphaCurve = ParticleCurve.FadeInOut(0.25f),
                            SpinMin = -2f, SpinMax = 2f, RandomStartRotation = true,
                        },
                        Duration = 1.2f, RatePerSecond = 14f, PoolCapacity = 48,
                    },
                };

                var looks = new[]
                {
                    new ParticleLook
                    {
                        Shape = ParticleShape.Star, ShapeParam = 0.3f, Blend = BillboardBlend.Additive,
                        LightRadius = 1.8f, LightIntensity = 0.5f,
                    },
                };

                return new VfxPreset(new ParticleEffect(phases), looks);
            }
        }

        /// <summary>Ambient warm embers drifting up on turbulence, for braziers and campfires.</summary>
        public static VfxPreset EmberDrift
        {
            get
            {
                var phases = new[]
                {
                    new ParticleEffectPhase
                    {
                        Config = new EmitterConfig
                        {
                            LifetimeMin = 1.5f, LifetimeMax = 2.5f,
                            SpeedMin = 0.3f, SpeedMax = 0.8f,
                            Direction = Vector3.UnitY, SpreadDegrees = 20f,
                            Shape = EmissionShape.Disc, ShapeRadius = 1.5f, ShapeShell = 0.2f,
                            Gravity = new Vector3(0f, 0.5f, 0f), Drag = 0.3f,
                            StartSize = 0.1f, EndSize = 0.06f,
                            StartColor = new Color(1f, 0.6f, 0.2f, 1f),
                            StartColorB = new Color(1f, 0.4f, 0.1f, 1f),
                            EndColor = new Color(0.8f, 0.3f, 0.1f, 0f),
                            EndColorB = new Color(0.6f, 0.2f, 0.05f, 0f),
                            VaryColor = true,
                            SizeCurve = ParticleCurve.EaseOut,
                            AlphaCurve = ParticleCurve.FadeInOut(0.2f),
                            TurbulenceStrength = 0.8f, TurbulenceFrequency = 0.5f,
                        },
                        Duration = 2.5f, RatePerSecond = 8f, PoolCapacity = 48,
                    },
                };

                var looks = new[]
                {
                    new ParticleLook { Shape = ParticleShape.Ember, ShapeParam = 0.2f, Blend = BillboardBlend.Additive },
                };

                return new VfxPreset(new ParticleEffect(phases), looks);
            }
        }

        /// <summary>A single fountain of stretched sparks arcing up and falling under gravity.</summary>
        public static VfxPreset SparkShower
        {
            get
            {
                var phases = new[]
                {
                    new ParticleEffectPhase
                    {
                        Config = new EmitterConfig
                        {
                            LifetimeMin = 0.4f, LifetimeMax = 0.9f,
                            SpeedMin = 4f, SpeedMax = 9f,
                            Direction = Vector3.UnitY, SpreadDegrees = 40f,
                            Gravity = new Vector3(0f, -9f, 0f), Drag = 1.5f,
                            StartSize = 0.25f, EndSize = 0.05f,
                            StartColor = new Color(1f, 0.95f, 0.6f, 1f),
                            EndColor = new Color(1f, 0.5f, 0.15f, 0f),
                            SizeCurve = ParticleCurve.EaseOut,
                            AlphaCurve = ParticleCurve.EaseOut,
                        },
                        BurstCount = 26, PoolCapacity = 32,
                    },
                };

                var looks = new[]
                {
                    new ParticleLook { Shape = ParticleShape.Spark, Blend = BillboardBlend.Additive, Stretch = 0.35f },
                };

                return new VfxPreset(new ParticleEffect(phases), looks);
            }
        }

        /// <summary>A ground shockwave: a fast expanding ring plus a low outward puff of dust.</summary>
        public static VfxPreset Shockwave
        {
            get
            {
                var phases = new[]
                {
                    // Ring: expands hard from a point in half a second.
                    new ParticleEffectPhase
                    {
                        Config = new EmitterConfig
                        {
                            LifetimeMin = 0.5f, LifetimeMax = 0.5f,
                            StartSize = 0.2f, EndSize = 2.2f,
                            StartColor = new Color(1f, 0.9f, 0.7f, 0.9f),
                            EndColor = new Color(1f, 0.8f, 0.5f, 0f),
                            SizeCurve = ParticleCurve.EaseOut,
                            AlphaCurve = ParticleCurve.EaseOut,
                        },
                        // Lifted slightly off the surface so the flat-ground quad wins the depth test against the
                        // coplanar floor (an exactly-coplanar quad would z-fight it). The soft depth fade is skipped
                        // for flat-ground sprites, so no fade-descale is needed.
                        BurstCount = 1, PoolCapacity = 4, OriginOffset = new Vector3(0f, 0.09f, 0f),
                    },
                    // Dust: a shell of alpha wisps pushed outward low to the ground.
                    new ParticleEffectPhase
                    {
                        Config = new EmitterConfig
                        {
                            LifetimeMin = 0.4f, LifetimeMax = 0.8f,
                            SpeedMin = 1.5f, SpeedMax = 3f,
                            Shape = EmissionShape.Disc, ShapeRadius = 1.2f, ShapeShell = 1f,
                            VelocityMode = ParticleVelocityMode.Radial,
                            Gravity = new Vector3(0f, 0.2f, 0f), Drag = 2f,
                            StartSize = 0.3f, EndSize = 0.8f,
                            StartColor = new Color(0.5f, 0.45f, 0.4f, 0.5f),
                            UseMidColor = true, MidColor = new Color(0.35f, 0.32f, 0.3f, 0.5f),
                            EndColor = new Color(0.4f, 0.38f, 0.35f, 0f),
                            SizeCurve = ParticleCurve.EaseOut,
                            AlphaCurve = ParticleCurve.FadeInOut(0.25f),
                            TurbulenceStrength = 0.4f, TurbulenceFrequency = 0.6f,
                        },
                        BurstCount = 10, PoolCapacity = 24,
                    },
                    // Refraction ring: a lifted flat-ground quad that expands with the nova and warps the scene
                    // behind it (a refractive shockwave), driven by the distortion look below. Same schedule as the
                    // visual ring so the warp tracks it.
                    new ParticleEffectPhase
                    {
                        Config = new EmitterConfig
                        {
                            LifetimeMin = 0.5f, LifetimeMax = 0.5f,
                            StartSize = 0.2f, EndSize = 2.2f,
                            StartColor = new Color(1f, 1f, 1f, 1f),
                            EndColor = new Color(1f, 1f, 1f, 0f),
                            SizeCurve = ParticleCurve.EaseOut,
                            AlphaCurve = ParticleCurve.EaseOut,
                        },
                        BurstCount = 1, PoolCapacity = 4, OriginOffset = new Vector3(0f, 0.09f, 0f),
                    },
                };

                var looks = new[]
                {
                    // The nova ring lies flat in the ground plane, the ARPG read. Flat-ground sprites skip the soft
                    // depth fade (it would erase a quad coplanar with the floor), so no SoftFadeScale is needed.
                    new ParticleLook
                    {
                        Shape = ParticleShape.Ring, ShapeParam = 0.15f, Blend = BillboardBlend.Additive,
                        Orientation = ParticleOrientation.FlatGround,
                    },
                    new ParticleLook { Shape = ParticleShape.Wisp, Blend = BillboardBlend.Alpha },
                    // The refraction ring: flat on the ground, a Ripple offset band that fades with the particle's
                    // alpha. Active distortion, so this phase warps the scene instead of drawing a visible sprite.
                    // Flat-ground, so it skips the depth occlusion too (same coplanar-floor reason as the nova ring).
                    new ParticleLook
                    {
                        Orientation = ParticleOrientation.FlatGround,
                        Distortion = new DistortionLook
                        {
                            Shape = DistortionShape.Ripple, ShapeParam = 0.15f, Strength = 1.5f,
                        },
                    },
                };

                return new VfxPreset(new ParticleEffect(phases), looks);
            }
        }

        /// <summary>A shimmering heat haze: a slow rising column that warps the scene (heat distortion) under a faint
        /// warm additive shimmer. For braziers, lava, desert air, engine exhaust.</summary>
        public static VfxPreset HeatHaze
        {
            get
            {
                var phases = new[]
                {
                    // Heat column: slow, large, soft sprites rising, driving the distortion wobble (no visible sprite).
                    new ParticleEffectPhase
                    {
                        Config = new EmitterConfig
                        {
                            LifetimeMin = 1.5f, LifetimeMax = 2.5f,
                            SpeedMin = 0.4f, SpeedMax = 0.9f,
                            Direction = Vector3.UnitY, SpreadDegrees = 12f,
                            Shape = EmissionShape.Disc, ShapeRadius = 0.8f, ShapeShell = 0.2f,
                            Gravity = new Vector3(0f, 0.6f, 0f), Drag = 0.3f,
                            StartSize = 1.2f, EndSize = 1.8f,
                            StartColor = new Color(1f, 1f, 1f, 0.6f),
                            EndColor = new Color(1f, 1f, 1f, 0f),
                            SizeCurve = ParticleCurve.EaseOut,
                            AlphaCurve = ParticleCurve.FadeInOut(0.3f),
                            TurbulenceStrength = 0.4f, TurbulenceFrequency = 0.4f,
                        },
                        Duration = 2.5f, RatePerSecond = 6f, PoolCapacity = 32,
                    },
                    // Faint warm shimmer: a subtle additive wisp over the warped air, so the effect reads even on a
                    // flat background where refraction alone is invisible.
                    new ParticleEffectPhase
                    {
                        Config = new EmitterConfig
                        {
                            LifetimeMin = 1f, LifetimeMax = 1.8f,
                            SpeedMin = 0.3f, SpeedMax = 0.7f,
                            Direction = Vector3.UnitY, SpreadDegrees = 18f,
                            Shape = EmissionShape.Disc, ShapeRadius = 0.7f, ShapeShell = 0.3f,
                            Gravity = new Vector3(0f, 0.5f, 0f), Drag = 0.4f,
                            StartSize = 0.5f, EndSize = 0.9f,
                            StartColor = new Color(1f, 0.85f, 0.7f, 0.12f),
                            EndColor = new Color(1f, 0.8f, 0.6f, 0f),
                            SizeCurve = ParticleCurve.EaseOut,
                            AlphaCurve = ParticleCurve.FadeInOut(0.3f),
                            TurbulenceStrength = 0.5f, TurbulenceFrequency = 0.5f,
                        },
                        Duration = 2.5f, RatePerSecond = 10f, PoolCapacity = 32,
                    },
                };

                var looks = new[]
                {
                    // Active Heat distortion: this phase warps the scene, drawing no visible sprite.
                    new ParticleLook
                    {
                        Distortion = new DistortionLook
                        {
                            Shape = DistortionShape.Heat, ShapeParam = 0.5f, Strength = 0.8f, SoftFadeScale = 1f,
                        },
                    },
                    new ParticleLook { Shape = ParticleShape.Wisp, Blend = BillboardBlend.Additive },
                };

                return new VfxPreset(new ParticleEffect(phases), looks);
            }
        }

        /// <summary>A steady rising column of soft, turbulent smoke.</summary>
        public static VfxPreset SmokePlume
        {
            get
            {
                var phases = new[]
                {
                    new ParticleEffectPhase
                    {
                        Config = new EmitterConfig
                        {
                            LifetimeMin = 1.5f, LifetimeMax = 2.5f,
                            SpeedMin = 0.8f, SpeedMax = 1.6f,
                            Direction = Vector3.UnitY, SpreadDegrees = 18f,
                            Gravity = new Vector3(0f, 0.8f, 0f), Drag = 0.4f,
                            StartSize = 0.35f, EndSize = 1.1f,
                            StartColor = new Color(0.45f, 0.45f, 0.47f, 0.5f),
                            UseMidColor = true, MidColor = new Color(0.28f, 0.28f, 0.3f, 0.5f),
                            EndColor = new Color(0.35f, 0.35f, 0.37f, 0f),
                            SizeCurve = ParticleCurve.EaseOut,
                            AlphaCurve = ParticleCurve.FadeInOut(0.25f),
                            TurbulenceStrength = 0.6f, TurbulenceFrequency = 0.5f,
                        },
                        Duration = 2.5f, RatePerSecond = 16f, PoolCapacity = 64,
                    },
                };

                var looks = new[]
                {
                    new ParticleLook { Shape = ParticleShape.Wisp, Blend = BillboardBlend.Alpha },
                };

                return new VfxPreset(new ParticleEffect(phases), looks);
            }
        }

        /// <summary>Swirling violet-to-cyan magic sparkles that pulse and faintly light the caster.</summary>
        public static VfxPreset ArcaneSparkle
        {
            get
            {
                var phases = new[]
                {
                    new ParticleEffectPhase
                    {
                        Config = new EmitterConfig
                        {
                            LifetimeMin = 0.8f, LifetimeMax = 1.5f,
                            SpeedMin = 0.4f, SpeedMax = 1f,
                            Direction = Vector3.UnitY, SpreadDegrees = 60f,
                            Shape = EmissionShape.Sphere, ShapeRadius = 1f, ShapeShell = 0.7f,
                            Gravity = new Vector3(0f, 0.2f, 0f), Drag = 0.5f,
                            StartSize = 0.1f, EndSize = 0.14f,
                            StartColor = new Color(0.7f, 0.4f, 1f, 1f),
                            StartColorB = new Color(0.4f, 0.9f, 1f, 1f),
                            EndColor = new Color(0.5f, 0.3f, 0.9f, 0f),
                            EndColorB = new Color(0.3f, 0.8f, 0.95f, 0f),
                            VaryColor = true,
                            SizeCurve = ParticleCurve.EaseInOut,
                            AlphaCurve = ParticleCurve.Pulse(2f),
                            SpinMin = -6f, SpinMax = 6f, RandomStartRotation = true,
                        },
                        Duration = 1.5f, RatePerSecond = 18f, PoolCapacity = 48,
                    },
                };

                var looks = new[]
                {
                    new ParticleLook
                    {
                        Shape = ParticleShape.Star, ShapeParam = 0.5f, Blend = BillboardBlend.Additive,
                        LightRadius = 1.5f, LightIntensity = 0.35f,
                    },
                };

                return new VfxPreset(new ParticleEffect(phases), looks);
            }
        }
    }
}
