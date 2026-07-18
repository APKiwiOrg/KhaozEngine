using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Particles;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.ParticlesRender3D
{
    // The adapter extensions queue onto a live Scene3D (its ctor needs a GPU device), so these run gated behind
    // KE_GPU_TESTS=1, mirroring Scene3DParticleQueueTests' WithScene helper. They assert the queue accounting the
    // adapter drives (sprites, trails, lights). The preset content and determinism are covered headlessly by
    // VfxPresetsTests.
    public sealed class ParticleSceneExtensionsTests
    {
        static void WithScene(Action<Scene3D> body)
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            var f = gpu.GpuDevice.Factory;
            using IGpuTexture tex = f.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = f.CreateFramebuffer(null, tex);
            using var scene = new Scene3D(gpu.GpuDevice, fb.Outputs);
            body(scene);
        }

        static EmitterConfig Config(float life) => new()
        {
            LifetimeMin = life, LifetimeMax = life,
            SpeedMin = 1f, SpeedMax = 1f,
            Direction = Vector3.UnitY, SpreadDegrees = 10f,
            StartSize = 0.3f, EndSize = 0.1f,
            StartColor = new Color(1f, 0.8f, 0.4f, 1f),
            EndColor = new Color(1f, 0.4f, 0.1f, 0f),
        };

        [GpuFact]
        public void DrawParticles_MapsOneSpritePerLiveParticle() => WithScene(scene =>
        {
            var sys = new ParticleSystem(64, seed: 7);
            sys.Emit(Config(5f), Vector3.Zero, 10);
            var look = new ParticleLook { Shape = ParticleShape.SoftGlow, Blend = BillboardBlend.Additive };

            scene.Begin();
            scene.DrawParticles(sys, in look);

            Assert.Equal(10, scene.ParticleSpriteCount);
            Assert.Equal(sys.ActiveCount, scene.ParticleSpriteCount);
        });

        [GpuFact]
        public void DrawParticles_ActiveDistortion_EmitsDistortionSpritesNotParticles() => WithScene(scene =>
        {
            var sys = new ParticleSystem(64, seed: 7);
            sys.Emit(Config(5f), Vector3.Zero, 10);
            var look = new ParticleLook
            {
                Orientation = ParticleOrientation.FlatGround,
                Distortion = new DistortionLook { Shape = DistortionShape.Ripple, Strength = 1.5f, SoftFadeScale = 0.12f },
            };

            scene.Begin();
            scene.DrawParticles(sys, in look);

            Assert.Equal(sys.ActiveCount, scene.DistortionSpriteCount);
            Assert.Equal(0, scene.ParticleSpriteCount);   // an active-distortion look draws no visible sprite
        });

        [GpuFact]
        public void DrawParticles_InactiveDistortion_EmitsParticlesAsBefore() => WithScene(scene =>
        {
            var sys = new ParticleSystem(64, seed: 7);
            sys.Emit(Config(5f), Vector3.Zero, 10);
            // Default look (no distortion) still queues particle sprites, none as distortion.
            var look = new ParticleLook { Shape = ParticleShape.SoftGlow, Blend = BillboardBlend.Additive };

            scene.Begin();
            scene.DrawParticles(sys, in look);

            Assert.Equal(sys.ActiveCount, scene.ParticleSpriteCount);
            Assert.Equal(0, scene.DistortionSpriteCount);
        });

        [GpuFact]
        public void DrawParticles_ForwardsTrails_WhenEnabledAndCapacityPresent() => WithScene(scene =>
        {
            var sys = new ParticleSystem(64, seed: 7, trailSamples: 8);
            sys.Emit(Config(5f), Vector3.Zero, 5);
            // Step past the 1/30 s sample interval a few times so each particle accrues >= 2 history points.
            for (int i = 0; i < 3; i++) sys.Update(0.05f);

            var look = new ParticleLook
            {
                Shape = ParticleShape.Spark, Blend = BillboardBlend.Additive,
                Trails = true, TrailStyle = TrailStyle.Default, TrailWidthScale = 0.5f,
            };

            scene.Begin();
            Assert.Equal(0, scene.TrailCount);
            scene.DrawParticles(sys, in look);

            Assert.True(scene.TrailCount > 0, "expected forwarded trails");
            Assert.Equal(sys.ActiveCount, scene.TrailCount);
        });

        [GpuFact]
        public void DrawParticles_NoTrails_WhenPoolHasNoTrailCapacity() => WithScene(scene =>
        {
            var sys = new ParticleSystem(64, seed: 7); // trailSamples 0
            sys.Emit(Config(5f), Vector3.Zero, 5);
            var look = new ParticleLook { Shape = ParticleShape.Spark, Blend = BillboardBlend.Additive, Trails = true };

            scene.Begin();
            scene.DrawParticles(sys, in look);

            Assert.Equal(0, scene.TrailCount);
        });

        [GpuFact]
        public void DrawParticles_LinksTopKLights_ByBudget() => WithScene(scene =>
        {
            var sys = new ParticleSystem(64, seed: 7);
            sys.Emit(Config(5f), Vector3.Zero, 8);
            var look = new ParticleLook
            {
                Shape = ParticleShape.Ember, Blend = BillboardBlend.Additive,
                LightRadius = 2.5f, LightIntensity = 1.2f,
            };

            scene.Begin();
            scene.DrawParticles(sys, in look, lightBudget: 3);

            Assert.Equal(Math.Min(3, sys.ActiveCount), scene.LightCount);
        });

        [GpuFact]
        public void DrawParticles_LightBudgetZero_AddsNoLights() => WithScene(scene =>
        {
            var sys = new ParticleSystem(64, seed: 7);
            sys.Emit(Config(5f), Vector3.Zero, 8);
            var look = new ParticleLook
            {
                Shape = ParticleShape.Ember, Blend = BillboardBlend.Additive,
                LightRadius = 2.5f, LightIntensity = 1.2f,
            };

            scene.Begin();
            scene.DrawParticles(sys, in look, lightBudget: 0);

            Assert.Equal(0, scene.LightCount);
        });

        [GpuFact]
        public void DrawParticles_NoLights_WhenLookHasNoLightLink() => WithScene(scene =>
        {
            var sys = new ParticleSystem(64, seed: 7);
            sys.Emit(Config(5f), Vector3.Zero, 8);
            var look = new ParticleLook { Shape = ParticleShape.Ember, Blend = BillboardBlend.Additive };

            scene.Begin();
            scene.DrawParticles(sys, in look, lightBudget: 4);

            Assert.Equal(0, scene.LightCount);
        });

        [GpuFact]
        public void DrawEffect_ThrowsOnLooksLengthMismatch() => WithScene(scene =>
        {
            VfxPreset preset = VfxPresets.FireBurst;
            var player = new ParticleEffectPlayer(preset.Effect, maxInstances: 4, seed: 1);

            var wrong = new ParticleLook[player.PhaseCount + 1];
            scene.Begin();
            Assert.Throws<ArgumentException>(() => scene.DrawEffect(player, wrong));
        });

        [GpuFact]
        public void DrawEffect_QueuesAcrossPhases() => WithScene(scene =>
        {
            VfxPreset preset = VfxPresets.FireBurst;
            ParticleLook[] looks = preset.Looks.ToArray();
            var player = new ParticleEffectPlayer(preset.Effect, maxInstances: 4, seed: 1);
            player.Play(Vector3.Zero, Vector3.UnitY);
            // Fire the zero-delay burst phases so multiple pools hold particles.
            for (int i = 0; i < 3; i++) player.Update(1f / 60f);

            scene.Begin();
            scene.DrawEffect(player, looks);

            Assert.True(scene.ParticleSpriteCount > 0, "expected sprites queued across phases");
        });
    }
}
