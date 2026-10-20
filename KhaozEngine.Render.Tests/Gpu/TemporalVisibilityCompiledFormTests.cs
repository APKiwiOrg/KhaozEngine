using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The compiled form of the split's second pass, through the shipped GLSL front end, as SPIR-V instruction counts:
    /// texel fetches, gathers and structured loops. The rows pin D's counts. The cost cut (plan Task 7f.2) turns the
    /// four production rows into its targets. The frozen D helper's row stays D's for good. Device-free.
    /// </summary>
    public sealed class TemporalVisibilityCompiledFormTests(ITestOutputHelper output)
    {
        const uint OpImageFetch = 95, OpImageGather = 96, OpLoopMerge = 246;

        // D's helper alone, with every input it reads bound, so its own fetches and loops count apart from the pass.
        const string HelperProgram = "#version 450\n" + @"
layout(set=0,binding=0) uniform texture2D SceneDepth;
layout(set=0,binding=1) uniform texture2D MotionTex;
layout(set=0,binding=2) uniform texture2D PrevDepth;
layout(set=0,binding=3) uniform texture2D KeyShadowVisibility;
layout(set=0,binding=4) uniform texture2D PreviousKeyShadowVisibility;
layout(set=0,binding=5) uniform sampler LinearClamp;
layout(set=0,binding=6) uniform Probe { vec4 CurrentDepth; vec4 Sizes; vec4 Jitter; vec4 PreviousJitter; vec4 Site; };
" + ShaderSources.TemporalCommonGlsl + ShaderSources.TemporalResolveTuningGlsl + TemporalVisibilityFrozenD.VisibilityGlsl
            + @"
layout(location=0) out vec4 Result;
void main() {
    float change = 0.0;
    bool valid = temporalVisibilityChange(Site.xy, ivec2(Site.zw), ivec2(Sizes.xy) - 1, Site.xy + Jitter.xy,
        Sizes.z, change);
    Result = vec4(change, valid ? 1.0 : 0.0, 0.0, 1.0);
}";

        /// <summary>Program, then D's OpImageFetch, OpImageGather and OpLoopMerge counts at 583a3ae7b.</summary>
        public static TheoryData<string, int, int, int> DBaseline => new()
        {
            { nameof(ShaderSources.TemporalAccumulateFrag), 30, 0, 14 },
            { nameof(ShaderSources.TemporalAccumulateHalfFrag), 30, 0, 14 },
            { nameof(ShaderSources.TemporalAccumulateAtDisplaySizeFrag), 30, 0, 14 },
            { nameof(ShaderSources.TemporalAccumulateAtDisplaySizeHalfFrag), 30, 0, 14 },
            { "frozen D helper", 7, 0, 4 },
        };

        static string Program(string name) => name switch
        {
            nameof(ShaderSources.TemporalAccumulateFrag) => ShaderSources.TemporalAccumulateFrag,
            nameof(ShaderSources.TemporalAccumulateHalfFrag) => ShaderSources.TemporalAccumulateHalfFrag,
            nameof(ShaderSources.TemporalAccumulateAtDisplaySizeFrag) => ShaderSources.TemporalAccumulateAtDisplaySizeFrag,
            nameof(ShaderSources.TemporalAccumulateAtDisplaySizeHalfFrag)
                => ShaderSources.TemporalAccumulateAtDisplaySizeHalfFrag,
            "frozen D helper" => HelperProgram,
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "not a counted program"),
        };

        [Theory]
        [MemberData(nameof(DBaseline))]
        public void PassTwoKeepsTheDBaselineOpcodeCounts(string program, int fetches, int gathers, int loops)
        {
            byte[] spirv = SpirvFrontEnd.ToSpirv(Program(program), GpuShaderStages.Fragment, "visibility form " + program);
            (int f, int g, int l) = (Count(spirv, OpImageFetch), Count(spirv, OpImageGather), Count(spirv, OpLoopMerge));
            output.WriteLine($"{program}: {f} OpImageFetch, {g} OpImageGather, {l} OpLoopMerge");
            Assert.Equal((fetches, gathers, loops), (f, g, l));
        }

        /// <summary>The equivalence oracle's program compiles through the shipped front end and both emitters, so a
        /// GPU run cannot fail on its text alone.</summary>
        [Fact]
        public void TheEquivalenceProbeCompilesForEveryBackend()
            => ShaderValidation.ValidatePair(ShaderSources.FullscreenVert, TemporalVisibilityEquivalenceProbe.FragmentSource,
                "visibility equivalence probe");

        /// <summary>Instructions with <paramref name="opcode"/> in a SPIR-V module, walked by word count after the
        /// five-word header.</summary>
        static int Count(byte[] spirv, uint opcode)
        {
            int n = 0;
            for (int i = 5 * 4; i + 4 <= spirv.Length;)
            {
                uint word = BitConverter.ToUInt32(spirv, i);
                if ((word & 0xFFFF) == opcode) n++;
                int words = (int)(word >> 16);
                if (words == 0) throw new InvalidOperationException("A zero-length SPIR-V instruction.");
                i += words * 4;
            }
            return n;
        }
    }
}
