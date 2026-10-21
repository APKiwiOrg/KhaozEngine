using System;
using System.Linq;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class TemporalVisibilityCostProgramsTests
{
    // Compilation catches a partial rewrite leaving visibility reads after their bindings are removed. ValidatePair also checks both emitters and their resource layouts without a device.
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
                TemporalVisibilityCostPrograms.WithoutCheck, TemporalVisibilityCostPrograms.WithoutVisibility,
                TemporalVisibilityCostPrograms.WithoutVisibilityOrBarriers,
                TemporalVisibilityFrozenD.ToD, TemporalVisibilityFrozenD.ToShippedD,
            })
                ShaderValidation.ValidatePair(ShaderSources.FullscreenVert, rewrite(source), "visibility cost variant");
    }
    [Fact]
    public void VisibilityFreeVariantsBindOnlyTheOriginalResolveInputs()
    {
        foreach (string source in new[] { ShaderSources.TemporalResolveFrag, ShaderSources.TemporalAccumulateFrag,
            ShaderSources.TemporalAccumulateAtDisplaySizeFrag })
            foreach (Func<string, string> rewrite in new Func<string, string>[]
                { TemporalVisibilityCostPrograms.WithoutVisibility, TemporalVisibilityCostPrograms.WithoutVisibilityOrBarriers })
            {
                string[] names = SpirvCrossCompile.GlslPairToHlsl(ShaderSources.FullscreenVert, rewrite(source),
                    "visibility-free inputs").Reflection.ResourceLayouts.SelectMany(l => l.Elements)
                    .Select(e => e.Name).ToArray();
                Assert.DoesNotContain("KeyShadowVisibility", names);
                Assert.DoesNotContain("PreviousKeyShadowVisibility", names);
                Assert.DoesNotContain("VisibilityHistory", names);
                Assert.Equal(source == ShaderSources.TemporalResolveFrag ? 9 : 12, names.Length);
            }
    }

    /// <summary>V4 keeps V5's program apart from the twelve barriers, which it removes from the compiled module.</summary>
    [Fact]
    public void TheBarrierVariantRemovesExactlyTheTwelveCompiledBarriers()
    {
        foreach (string source in new[] { ShaderSources.TemporalResolveFrag, ShaderSources.TemporalResolveHalfFrag,
            ShaderSources.TemporalAccumulateFrag, ShaderSources.TemporalAccumulateHalfFrag,
            ShaderSources.TemporalAccumulateAtDisplaySizeFrag, ShaderSources.TemporalAccumulateAtDisplaySizeHalfFrag })
        {
            string v5 = TemporalVisibilityCostPrograms.WithoutVisibility(source);
            string v4 = TemporalVisibilityCostPrograms.WithoutVisibilityOrBarriers(source);
            Assert.Equal(v5.Replace(TemporalVisibilityCostPrograms.Barrier, TemporalVisibilityCostPrograms.NoBarrier,
                StringComparison.Ordinal), v4);
            Assert.Equal(TemporalLockReleaseSpirv.BarriersPerRelease, TemporalLockReleaseSpirv.BitwiseOrs(v5)
                - TemporalLockReleaseSpirv.BitwiseOrs(v4));
        }
        Assert.Equal(ShaderSources.TemporalPrepareFrag,
            TemporalVisibilityCostPrograms.WithoutBarriers(ShaderSources.TemporalPrepareFrag));
        Assert.Throws<InvalidOperationException>(() => TemporalVisibilityCostPrograms.WithoutBarriers(
            ShaderSources.TemporalAccumulateFrag.Replace(TemporalVisibilityCostPrograms.Barrier, "",
                StringComparison.Ordinal)));
    }
}
