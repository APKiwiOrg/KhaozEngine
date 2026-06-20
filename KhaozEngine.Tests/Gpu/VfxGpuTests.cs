using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render2D.Vfx;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Backend-agnostic GPU coverage for the 2D VFX additive path: it renders to a CPU RGBA buffer on a headless
    /// device and asserts pixel <em>properties</em> (no committed golden, so it runs on any backend in CI without a
    /// per-backend baked reference). Proves additive blending composites and the beam/particle/glow draws light
    /// real pixels. Skipped unless KE_GPU_TESTS=1.
    /// </summary>
    public sealed class VfxGpuTests
    {
        const int W = 200, H = 120;

        static byte Lum(byte[] rgba, int x, int y) => rgba[(y * W + x) * 4]; // R channel; VFX here is grey/white

        [GpuFact]
        public void Additive_Stacks_BrighterThanSingle()
        {
            // One dim glow vs two stacked dim glows at the same point: additive must sum (brighter), not replace.
            Color dim = new(0.3f, 0.3f, 0.3f, 1f);
            Vector2 c = new(W / 2f, H / 2f);

            byte[] single = Render2DSnapshot.Capture(W, H, Color.Black, ctx =>
            {
                using var vfx = new VfxRenderer(ctx, glowSize: 64);
                ctx.Batch.Begin();
                vfx.DrawGlow(ctx.Batch, c, 24f, dim);
                ctx.Batch.End();
            });

            byte[] stacked = Render2DSnapshot.Capture(W, H, Color.Black, ctx =>
            {
                using var vfx = new VfxRenderer(ctx, glowSize: 64);
                ctx.Batch.Begin();
                vfx.DrawGlow(ctx.Batch, c, 24f, dim);
                vfx.DrawGlow(ctx.Batch, c, 24f, dim);
                ctx.Batch.End();
            });

            byte one = Lum(single, W / 2, H / 2);
            byte two = Lum(stacked, W / 2, H / 2);
            Assert.True(one > 10, $"single glow centre should be lit, was {one}");
            Assert.True(two > one + 20, $"stacked additive glow ({two}) should be clearly brighter than single ({one})");
        }

        [GpuFact]
        public void Beam_LightsPixelsAlongAxis()
        {
            Vector2 a = new(20, H / 2f), b = new(W - 20, H / 2f);
            var bp = BeamParams.Default with { FlareRadius = 0f };

            byte[] rgba = Render2DSnapshot.Capture(W, H, Color.Black, ctx =>
            {
                using var vfx = new VfxRenderer(ctx);
                ctx.Batch.Begin();
                vfx.DrawBeam(ctx.Batch, a, b, bp, timeSeconds: 0f);
                ctx.Batch.End();
            });

            Assert.True(Lum(rgba, W / 2, H / 2) > 20, "beam midpoint should be lit");
            Assert.Equal(0, Lum(rgba, W / 2, 8)); // far above the beam stays background
        }

        [GpuFact]
        public void RoundCaps_LightPixelsBeyondTheSquareEnd()
        {
            // Endpoint b at x = W-20; sample a few px beyond it on-axis. FlareRadius 0 isolates the cap (no flare),
            // so a square-ended beam leaves that pixel dark while a round core cap (radius = CoreWidth/2) lights it.
            // A wide bright core (16px -> 8px cap) makes the differential unambiguous on the R channel.
            Vector2 a = new(20, H / 2f), b = new(W - 20, H / 2f);
            int yMid = H / 2, xPastEnd = (int)b.X + 3;
            var bp = BeamParams.Default with { FlareRadius = 0f, CoreWidth = 16f };

            byte[] square = Render2DSnapshot.Capture(W, H, Color.Black, ctx =>
            {
                using var vfx = new VfxRenderer(ctx);
                ctx.Batch.Begin();
                vfx.DrawBeam(ctx.Batch, a, b, bp with { Caps = BeamCap.None }, 0f);
                ctx.Batch.End();
            });

            byte[] round = Render2DSnapshot.Capture(W, H, Color.Black, ctx =>
            {
                using var vfx = new VfxRenderer(ctx);
                ctx.Batch.Begin();
                vfx.DrawBeam(ctx.Batch, a, b, bp with { Caps = BeamCap.Round }, 0f);
                ctx.Batch.End();
            });

            Assert.Equal(0, Lum(square, xPastEnd, yMid)); // square end: nothing past the endpoint
            Assert.True(Lum(round, xPastEnd, yMid) > 10, "round cap should light pixels just beyond the endpoint");
        }

        [GpuFact]
        public void AdditiveParticles_RenderLitPixels()
        {
            var cfg = new Particle2DEmitterConfig
            {
                MinLife = 1f, MaxLife = 1f,
                MinSpeed = 0f, MaxSpeed = 0f,
                StartSize = 40f, EndSize = 40f,
                StartColor = new Color(0.5f, 0.5f, 0.5f, 1f),
                EndColor = new Color(0.5f, 0.5f, 0.5f, 1f),
                Blend = BlendMode.Additive,
            };
            var sys = new Particle2DSystem(8, seed: 1);
            sys.Emit(cfg, new Vector2(W / 2f, H / 2f), 1);

            byte[] rgba = Render2DSnapshot.Capture(W, H, Color.Black, ctx =>
            {
                var white = VfxTextures.White(ctx);
                ctx.Batch.Begin();
                sys.Draw(ctx.Batch, white);
                ctx.Batch.End();
                white.Dispose();
            });

            Assert.True(Lum(rgba, W / 2, H / 2) > 20, "additive particle centre should be lit");
        }

        [GpuFact]
        public void AttentionBeacon_LightsPixelsAroundCenter()
        {
            Vector2 c = new(W / 2f, H / 2f);
            // A frozen time where ring 0 is partway out; rings + glints should light pixels off-center.
            var p = AttentionBeaconParams.Default with { MaxRadius = 40f, GlintRadius = 24f };

            byte[] rgba = Render2DSnapshot.Capture(W, H, Color.Black, ctx =>
            {
                using var vfx = new VfxRenderer(ctx);
                ctx.Batch.Begin();
                vfx.DrawAttentionBeacon(ctx.Batch, c, p, timeSeconds: 0.6f);
                ctx.Batch.End();
            });

            // Somewhere on the ring band away from the exact center should be lit.
            bool anyLit = false;
            for (int x = W / 2; x < W / 2 + 40 && !anyLit; x++)
                if (Lum(rgba, x, H / 2) > 10) anyLit = true;
            Assert.True(anyLit, "beacon should light pixels out from the center");
            Assert.Equal(0, Lum(rgba, 2, 2)); // far corner stays background
        }

        [GpuFact]
        public void AttentionBeacon_ZeroCounts_DrawNothing()
        {
            Vector2 c = new(W / 2f, H / 2f);
            var p = AttentionBeaconParams.Default with { RingCount = 0, GlintCount = 0 };

            byte[] rgba = Render2DSnapshot.Capture(W, H, Color.Black, ctx =>
            {
                using var vfx = new VfxRenderer(ctx);
                ctx.Batch.Begin();
                vfx.DrawAttentionBeacon(ctx.Batch, c, p, timeSeconds: 0.6f);
                ctx.Batch.End();
            });

            Assert.Equal(0, Lum(rgba, W / 2, H / 2)); // nothing drawn anywhere
            Assert.Equal(0, Lum(rgba, W / 2 + 20, H / 2));
        }
    }
}
