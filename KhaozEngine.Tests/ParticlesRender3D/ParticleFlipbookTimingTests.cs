using KhaozEngine.Particles;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.ParticlesRender3D
{
    /// <summary>Headless coverage of the adapter's flipbook timing (continuous frame resolution). The render-side
    /// frame split + blend is covered by ParticleRendererPackTests, and the on-GPU look by the flipbook GpuFacts.</summary>
    public sealed class ParticleFlipbookTimingTests
    {
        [Fact]
        public void LifeOneShot_SweepsSheetOverLife()
        {
            Assert.Equal(0f, ParticleSceneExtensions.ResolveFlipbookFrame(
                ParticleFlipbookMode.LifeOneShot, 0f, 0f, 0f, 12f, 16, randomStart: false));
            Assert.Equal(8f, ParticleSceneExtensions.ResolveFlipbookFrame(
                ParticleFlipbookMode.LifeOneShot, 0.5f, 0f, 0f, 12f, 16, randomStart: false));
        }

        [Fact]
        public void LifeOneShot_FrameNeverIndexesPastSheet()
        {
            // Across the whole life, the resolved cell index stays inside the sheet (the one-shot resolve clamps the
            // last frame), so the atlas taps never wrap past the authored frames.
            for (float ln = 0f; ln <= 1.0001f; ln += 0.05f)
            {
                float frame = ParticleSceneExtensions.ResolveFlipbookFrame(
                    ParticleFlipbookMode.LifeOneShot, ln, 0f, 0f, 12f, 16, randomStart: false);
                (float fa, _, _) = ParticleRenderer.ResolveFrames(frame, 16, loop: false);
                Assert.InRange(fa, 0f, 15f);
            }
        }

        [Fact]
        public void TimeLoop_AdvancesAtFps()
        {
            Assert.Equal(12f, ParticleSceneExtensions.ResolveFlipbookFrame(
                ParticleFlipbookMode.TimeLoop, 0f, 0f, 1f, 12f, 16, randomStart: false));
            Assert.Equal(6f, ParticleSceneExtensions.ResolveFlipbookFrame(
                ParticleFlipbookMode.TimeLoop, 0f, 0f, 0.5f, 12f, 16, randomStart: false));
        }

        [Fact]
        public void TimeLoop_RandomStart_OffsetsBySeedTimesFrameCount()
        {
            // seed 0.5 on a 16-frame sheet staggers the phase by 8 frames, deterministically per particle.
            float f = ParticleSceneExtensions.ResolveFlipbookFrame(
                ParticleFlipbookMode.TimeLoop, 0f, 0.5f, 0f, 12f, 16, randomStart: true);
            Assert.Equal(8f, f);
        }

        [Fact]
        public void TimeLoop_RandomStartOff_NoOffset()
        {
            float f = ParticleSceneExtensions.ResolveFlipbookFrame(
                ParticleFlipbookMode.TimeLoop, 0f, 0.5f, 0f, 12f, 16, randomStart: false);
            Assert.Equal(0f, f);
        }
    }
}
