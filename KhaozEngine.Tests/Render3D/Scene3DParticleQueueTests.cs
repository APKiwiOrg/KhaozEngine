using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    // DrawParticle/DrawParticles queue onto a live Scene3D (its ctor needs a GPU device), so these run gated
    // behind KE_GPU_TESTS=1, mirroring Scene3DTrailQueueTests. They assert queue accounting + the host-owned
    // knobs only; instance packing is covered headlessly by ParticleRendererPackTests and the on-screen look
    // by the particle showcase dumps + golden.
    public sealed class Scene3DParticleQueueTests
    {
        static void WithScene(System.Action<Scene3D> body)
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            var f = gpu.GpuDevice.Factory;
            using IGpuTexture tex = f.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = f.CreateFramebuffer(null, tex);
            using var scene = new Scene3D(gpu.GpuDevice, fb.Outputs);
            body(scene);
        }

        static ParticleSprite Sprite(float x) => new()
        {
            Position = new Vector3(x, 0f, 0f),
            Size = 0.5f,
            Color = Color.White,
            Shape = ParticleShape.SoftGlow,
            Blend = BillboardBlend.Additive,
        };

        [GpuFact]
        public void DrawParticle_Queues_Then_Begin_Clears() => WithScene(scene =>
        {
            scene.Begin();
            Assert.Equal(0, scene.ParticleSpriteCount);

            scene.DrawParticle(Sprite(1f));
            scene.DrawParticle(Sprite(2f));
            Assert.Equal(2, scene.ParticleSpriteCount);

            scene.Begin();
            Assert.Equal(0, scene.ParticleSpriteCount);
        });

        [GpuFact]
        public void DrawParticles_QueuesWholeSpan() => WithScene(scene =>
        {
            scene.Begin();
            var batch = new[] { Sprite(1f), Sprite(2f), Sprite(3f) };
            scene.DrawParticles(batch);
            Assert.Equal(3, scene.ParticleSpriteCount);
        });

        [GpuFact]
        public void ParticleKnobs_AreHostOwned_NotClearedByBegin() => WithScene(scene =>
        {
            Assert.Equal(ParticleQuality.Full, scene.ParticleQuality);
            Assert.Equal(0.35f, scene.ParticleSoftFade);

            scene.ParticleQuality = ParticleQuality.Reduced;
            scene.ParticleSoftFade = 1.5f;
            scene.Begin();

            Assert.Equal(ParticleQuality.Reduced, scene.ParticleQuality);
            Assert.Equal(1.5f, scene.ParticleSoftFade);
        });
    }
}
