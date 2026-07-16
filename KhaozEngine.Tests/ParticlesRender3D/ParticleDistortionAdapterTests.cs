using System.Linq;
using System.Numerics;
using KhaozEngine.Particles;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.ParticlesRender3D
{
    // Headless coverage of the distortion adapter mapping (ParticleSceneExtensions.BuildDistortionSprite is pure and
    // internal, like ResolveFlipbookFrame) and the distortion phases the presets author. The GPU-side queue routing
    // (distortion sprites vs particle sprites) is covered by the GpuFacts in ParticleSceneExtensionsTests.
    public sealed class ParticleDistortionAdapterTests
    {
        static Particle Particle(float alpha, float norm) => new()
        {
            Position = new Vector3(1f, 2f, 3f),
            Size = 0.7f,
            Rotation = 0.5f,
            Seed = 0.25f,
            Age = norm,            // Life = 1 below, so Norm == Age == norm
            Life = 1f,
            Color = new Color(1f, 0.8f, 0.4f, alpha),
        };

        static ParticleLook Look() => new()
        {
            Orientation = ParticleOrientation.FlatGround,
            Distortion = new DistortionLook
            {
                Shape = DistortionShape.Ripple, ShapeParam = 0.3f, Strength = 2f, SoftFadeScale = 0.12f,
            },
        };

        [Fact]
        public void BuildDistortionSprite_MapsEveryField()
        {
            DistortionSprite s = ParticleSceneExtensions.BuildDistortionSprite(Look(), Particle(alpha: 1f, norm: 0.4f));

            Assert.Equal(new Vector3(1f, 2f, 3f), s.Position);
            Assert.Equal(0.7f, s.Size);
            Assert.Equal(0.5f, s.Rotation);
            Assert.Equal(DistortionShape.Ripple, s.Shape);
            Assert.Equal(0.3f, s.ShapeParam);
            Assert.Equal(0.25f, s.Seed);
            Assert.Equal(0.4f, s.LifeNorm, 5);
            Assert.Equal(ParticleOrientation.FlatGround, s.Orientation);
            Assert.Equal(0.12f, s.SoftFadeScale);
        }

        [Fact]
        public void BuildDistortionSprite_ScalesStrengthByParticleAlpha()
        {
            // Strength 2 authored; a half-alpha particle halves the offset field, so fields fade with life.
            DistortionSprite full = ParticleSceneExtensions.BuildDistortionSprite(Look(), Particle(alpha: 1f, norm: 0f));
            DistortionSprite half = ParticleSceneExtensions.BuildDistortionSprite(Look(), Particle(alpha: 0.5f, norm: 0f));
            DistortionSprite dead = ParticleSceneExtensions.BuildDistortionSprite(Look(), Particle(alpha: 0f, norm: 1f));

            Assert.Equal(2f, full.Strength);
            Assert.Equal(1f, half.Strength);
            Assert.Equal(0f, dead.Strength);
        }

        [Fact]
        public void DistortionLook_IsActive_TracksStrength()
        {
            Assert.False(default(DistortionLook).IsActive);
            Assert.False(new DistortionLook { Shape = DistortionShape.Heat, Strength = 0f }.IsActive);
            Assert.True(new DistortionLook { Shape = DistortionShape.Heat, Strength = 0.5f }.IsActive);
            Assert.True(new DistortionLook { Shape = DistortionShape.Lens, Strength = -0.5f }.IsActive);   // pinch
        }

        [Fact]
        public void Shockwave_HasOneActiveDistortionRingPhase()
        {
            // The refraction ring look is active distortion; the visual ring + dust looks are not.
            var looks = VfxPresets.Shockwave.Looks;
            Assert.Equal(1, looks.Count(l => l.Distortion.IsActive));
            Assert.Contains(looks, l => l.Distortion.IsActive && l.Distortion.Shape == DistortionShape.Ripple);
        }

        [Fact]
        public void HeatHaze_HasActiveHeatDistortionAndAVisualPhase()
        {
            var preset = VfxPresets.HeatHaze;
            Assert.Equal(preset.Effect.PhaseCount, preset.Looks.Count);
            Assert.Contains(preset.Looks, l => l.Distortion.IsActive && l.Distortion.Shape == DistortionShape.Heat);
            Assert.Contains(preset.Looks, l => !l.Distortion.IsActive);   // the faint additive shimmer wisp
        }
    }
}
