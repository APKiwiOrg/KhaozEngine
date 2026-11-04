using System;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>The internal post chain through the <see cref="IPostChainTargets"/> seam: one source slot, the lit
    /// colour, and exactly the textures the chain read directly before the seam existed.</summary>
    public sealed class PostChainTargetsGpuTests
    {
        [GpuFact]
        public void The_internal_chain_has_one_source_the_lit_colour_and_its_own_targets()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            using var res = new RenderResources(gpu.GpuDevice, 64, 48, hdrColor: true);
            IPostChainTargets chain = res;

            Assert.Equal(1, chain.SourceSlotCount);
            Assert.Equal(0, chain.SourceSlot);
            Assert.Same(res.ColorTex, chain.Source(0));
            Assert.Same(res.NormalTex, chain.NormalTex);
            Assert.Same(res.DepthColorTex, chain.DepthColorTex);
            Assert.Same(res.PingA, chain.PingA);
            Assert.Same(res.PingB, chain.PingB);
            Assert.Same(res.PingAFB, chain.PingAFB);
            Assert.Same(res.PingBFB, chain.PingBFB);
            Assert.False(chain.BloomAllocated);
            Assert.Null(chain.BloomA);
            Assert.Null(chain.BloomBFB);
            Assert.Equal((64, 48), (chain.Width, chain.Height));
            Assert.Equal(chain.Width, (int)chain.NormalTex.Width);
            Assert.False(chain.DistortAllocated);
            Assert.Null(chain.DistortTex);

            int before = chain.Generation;
            res.Resize(80, 48, mipped: false, sampleCount: 1, bloomEnabled: false, hdrColor: true);
            Assert.True(chain.Generation > before, "a resize must bump the generation the chain's sets are keyed on");
            Assert.Equal((80, 48), (chain.Width, chain.Height));
        }

        [GpuFact]
        public void The_internal_chain_reports_its_bloom_pair_and_distortion_field()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            using var res = new RenderResources(gpu.GpuDevice, 64, 48, hdrColor: true);
            res.Resize(64, 48, mipped: false, sampleCount: 1, bloomEnabled: true, hdrColor: true);
            res.EnsureDistortion(true, 2);
            IPostChainTargets chain = res;

            Assert.True(chain.BloomAllocated);
            Assert.Same(res.BloomA, chain.BloomA);
            Assert.Same(res.BloomB, chain.BloomB);
            Assert.Same(res.BloomAFB, chain.BloomAFB);
            Assert.Same(res.BloomBFB, chain.BloomBFB);
            Assert.NotNull(chain.BloomA);
            Assert.NotSame(chain.BloomA, chain.BloomB);
            Assert.Equal((res.BloomWidth, res.BloomHeight), (chain.BloomWidth, chain.BloomHeight));
            Assert.Equal((32, 24), (chain.BloomWidth, chain.BloomHeight));
            Assert.True(chain.DistortAllocated);
            Assert.NotNull(chain.DistortTex);
            Assert.Same(res.DistortTex, chain.DistortTex);
        }

        /// <summary>A refused count leaves the chain bound to its last targets, so their sets are neither disposed nor
        /// rebuilt over the refused set's textures, and the chain still reads the bound target's colour.</summary>
        [GpuTheory]
        [InlineData(0)]
        [InlineData(PixelPostProcess.MaxSourceSlots + 1)]
        public void Binding_a_source_slot_count_the_chain_cannot_build_throws_and_keeps_the_bound_sets(int slots)
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            using var bound = new RenderResources(gd, 16, 8, hdrColor: false);
            using var other = new RenderResources(gd, 16, 8, hdrColor: false);
            using var output = new PostChainRig.Output(gd, 16, 8);
            using var post = new PixelPostProcess(gd, bound.PingAFB.Outputs, output.Framebuffer.Outputs);
            post.BindTargets(bound);
            FillColour(gd, bound, Bound);
            FillColour(gd, other, Other);

            PostChainArgumentException refused = Assert.Throws<PostChainArgumentException>(
                () => post.BindTargets(new PostChainTestTargets(other, slots)));
            Assert.Equal(PostChainRefusal.SourceSlotCountUnsupported, refused.Reason);
            Assert.Equal(slots, refused.SourceSlotCount);
            Assert.Equal(1, refused.MinimumSourceSlotCount);
            Assert.Equal(PixelPostProcess.MaxSourceSlots, refused.MaximumSourceSlotCount);
            Assert.Equal("res", refused.ParamName);

            post.BindTargets(bound);
            PostChainRig.AssertEveryPixel(PostChainRig.RunChain(gd, post, bound, output, PostChainRig.LegacyPlain(), runFxaa: false), Bound);
        }

        [GpuFact]
        public void Run_refuses_targets_the_chain_has_not_bound_at_their_current_generation()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            using var bound = new RenderResources(gd, 16, 8, hdrColor: false);
            using var other = new RenderResources(gd, 16, 8, hdrColor: false);
            using var output = new PostChainRig.Output(gd, 16, 8);
            using var post = new PixelPostProcess(gd, bound.PingAFB.Outputs, output.Framebuffer.Outputs);
            PixelPostProcessSettings s = PostChainRig.LegacyPlain();
            post.BindTargets(bound);

            PostChainStateException another = PostChainRig.RunRefused(gd, post, other, output, s);
            Assert.Equal(PostChainRefusal.TargetsNotBoundAtCurrentGeneration, another.Reason);
            Assert.Equal(bound.Generation, another.BoundGeneration);
            Assert.Equal(other.Generation, another.ActualGeneration);
            Assert.False(another.TargetsMatch);

            int boundGeneration = bound.Generation;
            bound.Resize(20, 8, mipped: false, sampleCount: 1, bloomEnabled: false, hdrColor: false);
            PostChainStateException stale = PostChainRig.RunRefused(gd, post, bound, output, s);
            Assert.Equal(PostChainRefusal.TargetsNotBoundAtCurrentGeneration, stale.Reason);
            Assert.Equal(boundGeneration, stale.BoundGeneration);
            Assert.Equal(bound.Generation, stale.ActualGeneration);
            Assert.True(stale.TargetsMatch);

            post.BindTargets(bound);   // rebinding at the new generation is accepted again
            PostChainRig.RunChain(gd, post, bound, output, s, runFxaa: false);
        }

        static readonly Color Bound = new(0.25f, 0.5f, 0.75f, 1f);
        static readonly Color Other = new(0.75f, 0.25f, 0.5f, 1f);

        static void FillColour(IGpuDevice gd, RenderResources res, Color c)
        {
            using IGpuCommandList cl = gd.Factory.CreateCommandList();
            using (GpuRecording.Open(gd, cl, nameof(PostChainTargetsGpuTests)))
            {
                cl.SetFramebuffer(res.ColorDepthFB);
                cl.ClearColorTarget(0, c);
            }
            gd.Submit(cl);
            gd.WaitForIdle();
        }

    }
}
