using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class TemporalVisibilityCostProgramsTests
{
    // Compilation catches a partial rewrite leaving a call to the removed mask or a decision helper naming its
    // removed constants. ValidatePair also checks both emitters and their resource layouts without a device.
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
                TemporalVisibilityCostPrograms.WithoutCheck, TemporalVisibilityCostPrograms.WithoutCheckOrMask,
                TemporalVisibilityFrozenD.ToD,
            })
                ShaderValidation.ValidatePair(ShaderSources.FullscreenVert, rewrite(source), "visibility cost variant");
    }
}
