using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class TemporalVisibilityCostProgramsTests
{
    // Compilation catches a partial rewrite. ValidatePair also checks both emitters and their resource layouts without a device.
    [Fact]
    public void EveryVariantCompilesThroughBothBackendEmitters()
    {
        foreach (string source in new[]
        {
            ShaderSources.TemporalPrepareFrag, ShaderSources.TemporalDepthStoreFrag,
            ShaderSources.TemporalResolveFrag, ShaderSources.TemporalResolveHalfFrag,
            ShaderSources.TemporalAccumulateFrag, ShaderSources.TemporalAccumulateHalfFrag,
            ShaderSources.TemporalAccumulateAtDisplaySizeFrag, ShaderSources.TemporalAccumulateAtDisplaySizeHalfFrag,
        })
            foreach (Func<string, string> rewrite in new Func<string, string>[]
            {
                TemporalVisibilityCostPrograms.WithoutCheck, TemporalVisibilityCostPrograms.WithoutBarriers,
                TemporalVisibilityFrozenD.ToD, TemporalVisibilityFrozenD.ToShippedD,
                source => TemporalVisibilityCostPrograms.WithoutBarriers(TemporalVisibilityCostPrograms.WithoutCheck(source)),
            })
                ShaderValidation.ValidatePair(ShaderSources.FullscreenVert, rewrite(source), "visibility cost variant");
    }

    /// <summary>The barrier-free rewrite keeps production's program apart from the twelve barriers, which it removes
    /// from the compiled module.</summary>
    [Fact]
    public void TheBarrierFreeRewriteRemovesExactlyTheTwelveCompiledBarriers()
    {
        foreach (string source in new[] { ShaderSources.TemporalResolveFrag, ShaderSources.TemporalResolveHalfFrag,
            ShaderSources.TemporalAccumulateFrag, ShaderSources.TemporalAccumulateHalfFrag,
            ShaderSources.TemporalAccumulateAtDisplaySizeFrag, ShaderSources.TemporalAccumulateAtDisplaySizeHalfFrag })
        {
            string rewritten = TemporalVisibilityCostPrograms.WithoutBarriers(source);
            Assert.Equal(source.Replace(TemporalVisibilityCostPrograms.Barrier, TemporalVisibilityCostPrograms.NoBarrier,
                StringComparison.Ordinal), rewritten);
            Assert.Equal(TemporalLockReleaseSpirv.BarriersPerRelease, TemporalLockReleaseSpirv.BitwiseOrs(source)
                - TemporalLockReleaseSpirv.BitwiseOrs(rewritten));
        }
        Assert.Equal(ShaderSources.TemporalPrepareFrag,
            TemporalVisibilityCostPrograms.WithoutBarriers(ShaderSources.TemporalPrepareFrag));
        Assert.Throws<InvalidOperationException>(() => TemporalVisibilityCostPrograms.WithoutBarriers(
            ShaderSources.TemporalAccumulateFrag.Replace(TemporalVisibilityCostPrograms.Barrier, "",
                StringComparison.Ordinal)));
    }

    /// <summary>The decomposition keeps only the variants the renderer's fragment source hook can express.</summary>
    [Fact]
    public void TheDecompositionRunsOnlyHookRewrites()
    {
        Assert.Equal(["V0 shipped", "V1 check removed", "V3 D", "V6 no check, identity barriers"],
            Array.ConvertAll(TemporalVisibilityCostDecompositionCostPerfGpuTests.Variants, v => v.Label));
        Assert.Null(TemporalVisibilityCostDecompositionCostPerfGpuTests.Variants[0].Rewrite);
    }
}
