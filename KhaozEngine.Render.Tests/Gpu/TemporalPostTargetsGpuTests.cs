using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>The display-resolution half of a temporal frame: display-size pings and bloom in the scene's colour
    /// format, the history colour written this frame as the chain source, and the internal-size opaque copy.</summary>
    public sealed class TemporalPostTargetsGpuTests
    {
        [GpuFact]
        public void The_display_chain_is_display_sized_in_the_scene_format_and_reads_the_history_written_this_frame()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            using var res = new RenderResources(gd, 64, 36, hdrColor: true);
            var history = new TemporalHistory();
            using var targets = new TemporalPostTargets(gd);
            try
            {
                history.EnsureTargets(gd, 96, 54, 64, 36);
                targets.Ensure(res, history, 96, 54, bloomEnabled: true);
                IPostChainTargets chain = targets;

                Assert.True(targets.Allocated);
                Assert.Equal((96, 54), (chain.Width, chain.Height));
                Assert.Equal(GpuPixelFormat.R16G16B16A16Float, chain.PingA.Format);
                Assert.Equal((96u, 54u), (chain.PingB.Width, chain.PingB.Height));
                Assert.Equal(new[] { GpuPixelFormat.R16G16B16A16Float }, chain.PingAFB.Outputs.Colour);
                Assert.True(chain.BloomAllocated);
                Assert.Equal((48, 27), (chain.BloomWidth, chain.BloomHeight));
                Assert.Equal((48u, 27u), (chain.BloomA!.Width, chain.BloomA.Height));

                Assert.Equal(2, chain.SourceSlotCount);
                history.BeginResolve(0);
                history.BeginResolve(1);
                Assert.Equal(history.WriteIndex, chain.SourceSlot);
                Assert.Same(history.Color(0), chain.Source(0));
                Assert.Same(history.Color(1), chain.Source(1));
                Assert.Same(res.NormalTex, chain.NormalTex);
                Assert.Same(res.DepthColorTex, chain.DepthColorTex);
                Assert.False(chain.DistortAllocated);

                Assert.Equal((64u, 36u), (targets.OpaqueColor.Width, targets.OpaqueColor.Height));
                Assert.Equal(res.ColorTex.Format, targets.OpaqueColor.Format);
            }
            finally
            {
                history.ReleaseTargets();
            }
        }

        [GpuFact]
        public void Legacy_colour_and_bloom_off_follow_the_scene()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            using var res = new RenderResources(gd, 32, 32, hdrColor: false);
            var history = new TemporalHistory();
            using var targets = new TemporalPostTargets(gd);
            try
            {
                history.EnsureTargets(gd, 48, 48, 32, 32);
                targets.Ensure(res, history, 48, 48, bloomEnabled: false);
                Assert.Equal(GpuPixelFormat.R8G8B8A8UNorm, targets.PingA.Format);
                Assert.Equal(GpuPixelFormat.R8G8B8A8UNorm, targets.OpaqueColor.Format);
                Assert.False(targets.BloomAllocated);
                Assert.Null(targets.BloomA);
                Assert.Null(targets.BloomBFB);
            }
            finally
            {
                history.ReleaseTargets();
            }
        }

        [GpuFact]
        public void The_generation_moves_exactly_when_a_reported_texture_is_replaced()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            using var res = new RenderResources(gd, 64, 36, hdrColor: true);
            var history = new TemporalHistory();
            using var targets = new TemporalPostTargets(gd);
            try
            {
                history.EnsureTargets(gd, 96, 54, 64, 36);
                targets.Ensure(res, history, 96, 54, bloomEnabled: false);
                int g = targets.Generation;

                targets.Ensure(res, history, 96, 54, bloomEnabled: false);
                Assert.Equal(g, targets.Generation);                  // a steady frame rebuilds nothing

                res.EnsureDistortion(true, 2);                       // the apply set's offset field appears
                targets.Ensure(res, history, 96, 54, bloomEnabled: false);
                Assert.True(targets.Generation > g);
                Assert.True(targets.DistortAllocated);
                g = targets.Generation;

                history.EnsureTargets(gd, 128, 72, 64, 36);          // the sources are replaced
                targets.Ensure(res, history, 128, 72, bloomEnabled: false);
                Assert.True(targets.Generation > g);
                g = targets.Generation;

                history.ReleaseTargets();                            // new sources at the same sizes
                history.EnsureTargets(gd, 128, 72, 64, 36);
                targets.Ensure(res, history, 128, 72, bloomEnabled: false);
                Assert.Equal((128, 72), (targets.Width, targets.Height));
                Assert.True(targets.Generation > g);
                g = targets.Generation;

                targets.Ensure(res, history, 128, 72, bloomEnabled: true);
                Assert.True(targets.Generation > g);
                Assert.True(targets.BloomAllocated);
            }
            finally
            {
                history.ReleaseTargets();
            }
        }

        [GpuFact]
        public void The_opaque_copy_holds_the_lit_colour_at_the_moment_it_was_taken()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            using var res = new RenderResources(gd, 16, 8, hdrColor: true);
            var history = new TemporalHistory();
            using var targets = new TemporalPostTargets(gd);
            try
            {
                history.EnsureTargets(gd, 24, 12, 16, 8);
                targets.Ensure(res, history, 24, 12, bloomEnabled: false);
                using IGpuCommandList cl = gd.Factory.CreateCommandList();
                using (GpuRecording.Open(gd, cl, nameof(TemporalPostTargetsGpuTests)))
                {
                    cl.SetFramebuffer(res.ColorDepthFB);
                    cl.ClearColorTarget(0, new Color(0.25f, 0.5f, 0.75f, 1f));
                    cl.SetFramebuffer(res.PingAFB);   // a framebuffer change flushes the clear-only pass before the copy
                    targets.CopyOpaque(cl);
                    cl.SetFramebuffer(res.ColorDepthFB);
                    cl.ClearColorTarget(0, new Color(1f, 0f, 0f, 1f));   // the transparents change ColorTex afterwards
                }
                gd.Submit(cl);
                gd.WaitForIdle();

                float[] opaque = TemporalTextureIo.Read(gd, targets.OpaqueColor);
                float[] scene = TemporalTextureIo.Read(gd, res.ColorTex);
                Assert.Equal(0.25f, opaque[0], 3);
                Assert.Equal(0.5f, opaque[1], 3);
                Assert.Equal(0.75f, opaque[2], 3);
                Assert.Equal(1f, scene[0], 3);
                Assert.Equal(0f, scene[1], 3);
            }
            finally
            {
                history.ReleaseTargets();
            }
        }

        [GpuFact]
        public void Releasing_frees_every_target()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            using var res = new RenderResources(gd, 16, 8, hdrColor: true);
            var history = new TemporalHistory();
            using var targets = new TemporalPostTargets(gd);
            try
            {
                history.EnsureTargets(gd, 24, 12, 16, 8);
                targets.Ensure(res, history, 24, 12, bloomEnabled: true);
                targets.Release();
                Assert.False(targets.Allocated);
                Assert.False(targets.BloomAllocated);
                Assert.Throws<System.InvalidOperationException>(() => targets.OpaqueColor);
                targets.Release();   // idempotent
            }
            finally
            {
                history.ReleaseTargets();
            }
        }

        [GpuFact]
        public void The_display_chain_reports_its_own_bloom_pair_and_the_scene_distortion_field()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            using var res = new RenderResources(gd, 64, 36, hdrColor: true);
            res.Resize(64, 36, mipped: false, sampleCount: 1, bloomEnabled: true, hdrColor: true);
            res.EnsureDistortion(true, 2);
            var history = new TemporalHistory();
            using var targets = new TemporalPostTargets(gd);
            try
            {
                history.EnsureTargets(gd, 96, 54, 64, 36);
                targets.Ensure(res, history, 96, 54, bloomEnabled: true);
                IPostChainTargets chain = targets;

                Assert.Same(targets.BloomA, chain.BloomA);
                Assert.Same(targets.BloomB, chain.BloomB);
                Assert.Same(targets.BloomAFB, chain.BloomAFB);
                Assert.Same(targets.BloomBFB, chain.BloomBFB);
                Assert.Equal((targets.BloomWidth, targets.BloomHeight), (chain.BloomWidth, chain.BloomHeight));
                Assert.Equal((48, 27), (chain.BloomWidth, chain.BloomHeight));
                Assert.True(chain.DistortAllocated);
                Assert.Same(res.DistortTex, chain.DistortTex);

                // The pair is the display chain's own at half the display size, not the scene's internal one.
                Assert.NotSame(res.BloomA, chain.BloomA);
                Assert.NotSame(res.BloomB, chain.BloomB);
                Assert.NotSame(chain.BloomA, chain.BloomB);
                Assert.Equal((48u, 27u), (chain.BloomB!.Width, chain.BloomB.Height));
                Assert.Equal((48u, 27u), (chain.BloomAFB!.Width, chain.BloomAFB.Height));
                Assert.Equal((48u, 27u), (chain.BloomBFB!.Width, chain.BloomBFB.Height));

                // Each framebuffer writes the texture reported beside it.
                using (IGpuCommandList cl = gd.Factory.CreateCommandList())
                {
                    using (GpuRecording.Open(gd, cl, nameof(TemporalPostTargetsGpuTests)))
                    {
                        Clear(cl, chain.PingAFB, 0.125f);
                        Clear(cl, chain.PingBFB, 0.25f);
                        Clear(cl, chain.BloomAFB, 0.5f);
                        Clear(cl, chain.BloomBFB, 0.75f);
                    }
                    gd.Submit(cl);
                    gd.WaitForIdle();
                }
                Assert.Equal(0.125f, TemporalTextureIo.Read(gd, chain.PingA)[0], 3);
                Assert.Equal(0.25f, TemporalTextureIo.Read(gd, chain.PingB)[0], 3);
                Assert.Equal(0.5f, TemporalTextureIo.Read(gd, chain.BloomA!)[0], 3);
                Assert.Equal(0.75f, TemporalTextureIo.Read(gd, chain.BloomB!)[0], 3);
            }
            finally
            {
                history.ReleaseTargets();
            }
        }

        /// <summary>The two history targets hold different colours, so a chain that read a fixed slot fails one of the two
        /// frames. With FXAA the source feeds a ping pass first, without it the blit reads the source directly.</summary>
        [GpuTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void The_chain_reads_the_history_target_the_resolve_wrote_this_frame(bool runFxaa)
        {
            const int W = 24, H = 12;
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            using var res = new RenderResources(gd, 16, 8, hdrColor: false);
            var history = new TemporalHistory();
            using var targets = new TemporalPostTargets(gd);
            using var output = new PostChainRig.Output(gd, W, H);
            try
            {
                history.EnsureTargets(gd, W, H, 16, 8);
                TemporalTextureIo.Upload(gd, history.Color(0), Fill(W, H, Slot0));
                TemporalTextureIo.Upload(gd, history.Color(1), Fill(W, H, Slot1));
                targets.Ensure(res, history, W, H, bloomEnabled: false);
                using var post = new PixelPostProcess(gd, targets.PingAFB.Outputs, output.Framebuffer.Outputs);
                post.BindTargets(targets);
                PixelPostProcessSettings settings = PostChainRig.LegacyPlain();

                history.BeginResolve(0);   // the first frame writes target 1
                Assert.Equal(1, targets.SourceSlot);
                PostChainRig.AssertEveryPixel(PostChainRig.RunChain(gd, post, targets, output, settings, runFxaa), Slot1);

                history.BeginResolve(1);   // the next frame writes target 0
                Assert.Equal(0, targets.SourceSlot);
                PostChainRig.AssertEveryPixel(PostChainRig.RunChain(gd, post, targets, output, settings, runFxaa), Slot0);
            }
            finally
            {
                history.ReleaseTargets();
            }
        }

        public enum Upstream { OffsetFieldAppears, OffsetFieldFreed, SceneTargetsRecreated, HistoryRecreated }

        /// <summary>A texture this reports is replaced upstream after <see cref="TemporalPostTargets.Ensure"/> and before
        /// the chain binds. The generation must move on the next read, so the chain refuses its old sets and a rebind
        /// rebuilds them over the live textures, never over a freed one.</summary>
        [GpuTheory]
        [InlineData(Upstream.OffsetFieldAppears)]
        [InlineData(Upstream.OffsetFieldFreed)]
        [InlineData(Upstream.SceneTargetsRecreated)]
        [InlineData(Upstream.HistoryRecreated)]
        public void A_texture_replaced_upstream_after_Ensure_moves_the_generation_and_the_chain_rebinds(Upstream change)
        {
            const int W = 24, H = 12;
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            using var res = new RenderResources(gd, 16, 8, hdrColor: false);
            if (change == Upstream.OffsetFieldFreed) res.EnsureDistortion(true, 2);
            var history = new TemporalHistory();
            using var targets = new TemporalPostTargets(gd);
            using var output = new PostChainRig.Output(gd, W, H);
            try
            {
                history.EnsureTargets(gd, W, H, 16, 8);
                FillSources(gd, history, Slot0);
                targets.Ensure(res, history, W, H, bloomEnabled: false);
                using var post = new PixelPostProcess(gd, targets.PingAFB.Outputs, output.Framebuffer.Outputs);
                post.BindTargets(targets);
                PixelPostProcessSettings s = PostChainRig.LegacyPlain();
                history.BeginResolve(0);
                int g = targets.Generation;

                Color expected = Slot0;
                switch (change)
                {
                    case Upstream.OffsetFieldAppears:
                        res.EnsureDistortion(true, 2);
                        ClearOffsets(gd, res);   // no offset, so the apply pass reproduces its source
                        break;
                    case Upstream.OffsetFieldFreed:
                        res.EnsureDistortion(false, 2);
                        break;
                    case Upstream.SceneTargetsRecreated:
                        res.Resize(20, 10, mipped: false, sampleCount: 1, bloomEnabled: false, hdrColor: false);
                        break;
                    case Upstream.HistoryRecreated:
                        history.ReleaseTargets();
                        history.EnsureTargets(gd, W, H, 16, 8);
                        FillSources(gd, history, Slot1);
                        expected = Slot1;
                        break;
                }

                Assert.True(targets.Generation > g, $"{change} after Ensure left the generation at {g}");
                int moved = targets.Generation;
                Assert.Equal(moved, targets.Generation);   // recorded, so a second read moves nothing
                PostChainRig.RunRefused(gd, post, targets, output, s, distortionActive: true);

                post.BindTargets(targets);
                PostChainRig.AssertEveryPixel(PostChainRig.RunChain(gd, post, targets, output, s, runFxaa: false,
                    distortionActive: true), expected);
            }
            finally
            {
                history.ReleaseTargets();
            }
        }

        static void FillSources(IGpuDevice gd, TemporalHistory history, Color c)
        {
            for (int slot = 0; slot < 2; slot++)
                TemporalTextureIo.Upload(gd, history.Color(slot), Fill(history.DisplayWidth, history.DisplayHeight, c));
        }

        static void ClearOffsets(IGpuDevice gd, RenderResources res)
        {
            using IGpuCommandList cl = gd.Factory.CreateCommandList();
            using (GpuRecording.Open(gd, cl, nameof(TemporalPostTargetsGpuTests)))
            {
                cl.SetFramebuffer(res.DistortFB!);
                cl.ClearColorTarget(0, new Color(0f, 0f, 0f, 0f));
            }
            gd.Submit(cl);
            gd.WaitForIdle();
        }

        static readonly Color Slot0 = new(0.25f, 0.5f, 0.75f, 1f);
        static readonly Color Slot1 = new(0.75f, 0.25f, 0.5f, 1f);

        static void Clear(IGpuCommandList cl, IGpuFramebuffer? fb, float red)
        {
            cl.SetFramebuffer(fb!);
            cl.ClearColorTarget(0, new Color(red, 0f, 0f, 1f));
        }

        static float[] Fill(int w, int h, Color c)
        {
            var values = new float[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                values[i * 4 + 0] = c.R;
                values[i * 4 + 1] = c.G;
                values[i * 4 + 2] = c.B;
                values[i * 4 + 3] = c.A;
            }
            return values;
        }
    }
}
