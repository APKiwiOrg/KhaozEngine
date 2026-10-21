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
                TemporalVisibilityCostPrograms.WithoutVisibilityOrPrecise,
                TemporalVisibilityFrozenD.ToD,
            })
                ShaderValidation.ValidatePair(ShaderSources.FullscreenVert, rewrite(source), "visibility cost variant");
    }
    [Fact]
    public void VisibilityFreeVariantsBindOnlyTheOriginalResolveInputs()
    {
        foreach (string source in new[] { ShaderSources.TemporalResolveFrag, ShaderSources.TemporalAccumulateFrag,
            ShaderSources.TemporalAccumulateAtDisplaySizeFrag })
            foreach (Func<string, string> rewrite in new Func<string, string>[]
                { TemporalVisibilityCostPrograms.WithoutVisibility, TemporalVisibilityCostPrograms.WithoutVisibilityOrPrecise })
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

    [Fact]
    public void ThePreciseVariantRemovesTheCompiledNoContractionMarkings()
    {
        foreach (string source in new[] { ShaderSources.TemporalResolveFrag, ShaderSources.TemporalResolveHalfFrag,
            ShaderSources.TemporalAccumulateFrag, ShaderSources.TemporalAccumulateHalfFrag,
            ShaderSources.TemporalAccumulateAtDisplaySizeFrag, ShaderSources.TemporalAccumulateAtDisplaySizeHalfFrag })
        {
            Assert.True(Markings(TemporalVisibilityCostPrograms.WithoutVisibility(source)) > 0);
            Assert.Equal(0, Markings(TemporalVisibilityCostPrograms.WithoutVisibilityOrPrecise(source)));
        }

        static int Markings(string source)
        {
            byte[] spirv = SpirvFrontEnd.ToSpirv(source, GpuShaderStages.Fragment, "precise cost variant");
            int count = 0;
            for (int offset = 20; offset < spirv.Length;)
            {
                uint word = BitConverter.ToUInt32(spirv, offset);
                int words = (int)(word >> 16);
                Assert.True(words > 0);
                // OpDecorate target NoContraction (SPIR-V decoration 42).
                if ((word & 0xffff) == 71 && words >= 3 && BitConverter.ToUInt32(spirv, offset + 8) == 42) count++;
                offset += words * 4;
            }
            return count;
        }
    }

}
