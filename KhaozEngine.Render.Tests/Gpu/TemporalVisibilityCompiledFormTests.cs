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
    /// texel fetches, gathers and structured loops. Each pass-two program is pinned twice: as it ships, and rewritten
    /// to D (<see cref="TemporalVisibilityFrozenD.ToD"/>), which keeps D's counts at 583a3ae7b for good. The shipped
    /// decision keeps D's function, with its 7 fetches and 4 loops, as the slow path and adds the straight-line fast
    /// path's 41 fetches, no gather and no loop. A pixel on the fast path issues those 41 where D issues 101, or 25
    /// where the range skip declines the stored depths, since the current mask the first pass stores replaces this
    /// frame's 18 depth and motion fetches. Device-free.
    /// </summary>
    public sealed class TemporalVisibilityCompiledFormTests(ITestOutputHelper output)
    {
        const uint OpImageFetch = 95, OpImageGather = 96, OpLoopMerge = 246;

        const string HelperBindings = "#version 450\n" + @"
layout(set=0,binding=0) uniform texture2D SceneDepth;
layout(set=0,binding=1) uniform texture2D MotionTex;
layout(set=0,binding=2) uniform texture2D PrevDepth;
layout(set=0,binding=3) uniform texture2D KeyShadowVisibility;
layout(set=0,binding=4) uniform texture2D PreviousKeyShadowVisibility;
layout(set=0,binding=5) uniform sampler LinearClamp;
layout(set=0,binding=6) uniform Probe { vec4 CurrentDepth; vec4 Sizes; vec4 Jitter; vec4 PreviousJitter; vec4 Site; };
" + ShaderSources.TemporalCommonGlsl + ShaderSources.TemporalResolveTuningGlsl;

        // D's helper alone, with every input it reads bound, so its own fetches and loops count apart from the pass.
        const string HelperProgram = HelperBindings + TemporalVisibilityFrozenD.VisibilityGlsl + @"
layout(location=0) out vec4 Result;
void main() {
    float change = 0.0;
    bool valid = temporalVisibilityChange(Site.xy, ivec2(Site.zw), ivec2(Sizes.xy) - 1, Site.xy + Jitter.xy,
        Sizes.z, change);
    Result = vec4(change, valid ? 1.0 : 0.0, 0.0, 1.0);
}";

        /// <summary>The shipped decision alone, D's slow path and the fast path, with the same bindings and its
        /// current mask from the uniforms.</summary>
        internal const string DecisionHelperProgram = HelperBindings + ShaderSources.TemporalVisibilityMaskGlsl
            + ShaderSources.TemporalVisibilityGlsl + @"
layout(location=0) out vec4 Result;
void main() {
    float change = 0.0;
    uint path = 0u;
    bool release = temporalVisibilityReleases(Site.xy, ivec2(Site.zw), ivec2(Sizes.xy) - 1, Site.xy + Jitter.xy,
        Sizes.z, floatBitsToUint(Sizes.w), change, path);
    Result = vec4(change, release ? 1.0 : 0.0, float(path), 1.0);
}";

        /// <summary>Program, whether it is rewritten to D, then its OpImageFetch, OpImageGather and OpLoopMerge
        /// counts. The D rows are D's baseline at 583a3ae7b.</summary>
        public static TheoryData<string, bool, int, int, int> Counts => new()
        {
            { nameof(ShaderSources.TemporalAccumulateFrag), false, AccumulateTarget, 0, 14 },
            { nameof(ShaderSources.TemporalAccumulateHalfFrag), false, AccumulateTarget, 0, 14 },
            { nameof(ShaderSources.TemporalAccumulateAtDisplaySizeFrag), false, AccumulateTarget, 0, 14 },
            { nameof(ShaderSources.TemporalAccumulateAtDisplaySizeHalfFrag), false, AccumulateTarget, 0, 14 },
            { nameof(ShaderSources.TemporalAccumulateFrag), true, 30, 0, 14 },
            { nameof(ShaderSources.TemporalAccumulateHalfFrag), true, 30, 0, 14 },
            { nameof(ShaderSources.TemporalAccumulateAtDisplaySizeFrag), true, 30, 0, 14 },
            { nameof(ShaderSources.TemporalAccumulateAtDisplaySizeHalfFrag), true, 30, 0, 14 },
            { "frozen D helper", false, 7, 0, 4 },
            { "shipped decision helper", false, 7 + TemporalVisibilityFastPath.Fetches, 0, 4 },
        };

        // D's 30 and the fast path's 41.
        const int AccumulateTarget = 30 + TemporalVisibilityFastPath.Fetches;

        static string Program(string name) => name switch
        {
            nameof(ShaderSources.TemporalAccumulateFrag) => ShaderSources.TemporalAccumulateFrag,
            nameof(ShaderSources.TemporalAccumulateHalfFrag) => ShaderSources.TemporalAccumulateHalfFrag,
            nameof(ShaderSources.TemporalAccumulateAtDisplaySizeFrag) => ShaderSources.TemporalAccumulateAtDisplaySizeFrag,
            nameof(ShaderSources.TemporalAccumulateAtDisplaySizeHalfFrag)
                => ShaderSources.TemporalAccumulateAtDisplaySizeHalfFrag,
            "frozen D helper" => HelperProgram,
            "shipped decision helper" => DecisionHelperProgram,
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "not a counted program"),
        };

        [Theory]
        [MemberData(nameof(Counts))]
        public void PassTwoHasItsPinnedOpcodeCounts(string program, bool asD, int fetches, int gathers, int loops)
        {
            string source = asD ? TemporalVisibilityFrozenD.ToD(Program(program)) : Program(program);
            byte[] spirv = SpirvFrontEnd.ToSpirv(source, GpuShaderStages.Fragment, "visibility form " + program);
            (int f, int g, int l) = (Count(spirv, OpImageFetch), Count(spirv, OpImageGather), Count(spirv, OpLoopMerge));
            output.WriteLine($"{program}{(asD ? " as D" : "")}: {f} OpImageFetch, {g} OpImageGather, {l} OpLoopMerge, "
                + $"{spirv.Length / 4} words");
            Assert.Equal((fetches, gathers, loops), (f, g, l));
        }

        /// <summary>The decision helper compiles through the shipped front end and both emitters, so the hosted DXBC
        /// check meets no text the other legs never compiled.</summary>
        [Fact]
        public void TheDecisionHelperCompilesForEveryBackend()
            => ShaderValidation.ValidatePair(ShaderSources.FullscreenVert, DecisionHelperProgram, "visibility decision helper");

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
