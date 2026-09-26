using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
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
    }
}
