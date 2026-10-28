using System;
using System.Linq;
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
    /// to D as shipped (<see cref="TemporalVisibilityFrozenD.ToShippedD"/>), which keeps D's counts at 583a3ae7b for good. The shipped
    /// decision keeps D's function, with its 7 fetches and 4 loops, as the slow path and adds the straight-line fast
    /// path's twelve gathers, no point fetch and no loop. A pixel on the fast path issues twelve gathers where D
    /// issues 101 loads, or eight where the range skip declines the stored depths. The current mask the first pass
    /// stores replaces this frame's eighteen depth and motion fetches. Metal's selected programs retain the nine
    /// current loads and eight history gathers, also pinned below. The shimmer score's shadow evidence
    /// (<see cref="ShaderSources.TemporalShimmerVisibilityGlsl"/>) adds one point fetch to every shipped program and
    /// one filtered sample, which is not counted, and D's rewrite removes both. Device-free.
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
            { nameof(ShaderSources.TemporalAccumulateFrag), false, 31, 12, 14 },
            { nameof(ShaderSources.TemporalAccumulateHalfFrag), false, 31, 12, 14 },
            { nameof(ShaderSources.TemporalAccumulateAtDisplaySizeFrag), false, 31, 12, 14 },
            { nameof(ShaderSources.TemporalAccumulateAtDisplaySizeHalfFrag), false, 31, 12, 14 },
            { nameof(ShaderSources.TemporalAccumulateFrag), true, 30, 0, 14 },
            { nameof(ShaderSources.TemporalAccumulateHalfFrag), true, 30, 0, 14 },
            { nameof(ShaderSources.TemporalAccumulateAtDisplaySizeFrag), true, 30, 0, 14 },
            { nameof(ShaderSources.TemporalAccumulateAtDisplaySizeHalfFrag), true, 30, 0, 14 },
            { "frozen D helper", false, 7, 0, 4 },
            { "shipped decision helper", false, 7, 12, 4 },
            { nameof(ShaderSources.TemporalAccumulateFrag) + " (Metal)", false, 40, 8, 14 },
            { nameof(ShaderSources.TemporalAccumulateHalfFrag) + " (Metal)", false, 40, 8, 14 },
            { nameof(ShaderSources.TemporalAccumulateAtDisplaySizeFrag) + " (Metal)", false, 40, 8, 14 },
            { nameof(ShaderSources.TemporalAccumulateAtDisplaySizeHalfFrag) + " (Metal)", false, 40, 8, 14 },
            { "shipped decision helper (Metal)", false, 16, 8, 4 },
        };

        static string Program(string name) => name.EndsWith(" (Metal)", StringComparison.Ordinal)
            ? TemporalVisibilityGatherPolicy.ForBackend(Program(name[..^8]), GpuBackendKind.MetalNative) : name switch
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
            string source = asD ? TemporalVisibilityFrozenD.ToShippedD(Program(program)) : Program(program);
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

        /// <summary>The equivalence oracle's four programs compile through the shipped front end and both emitters, so
        /// a GPU run cannot fail on their text alone.</summary>
        [Fact]
        public void TheEquivalenceProbeCompilesForEveryBackend()
        {
            foreach ((string name, string source) in TemporalVisibilityEquivalenceProbe.Sources)
                ShaderValidation.ValidatePair(ShaderSources.FullscreenVert, source, "visibility equivalence " + name);
        }

        /// <summary>The A/B fact's two path exports rewrite every program that runs the decision, leave the others alone,
        /// compile through the shipped front end and both emitters, and reflect as many resources as the program they
        /// rewrite, so a backend binds them with production's layout.</summary>
        [Fact]
        public void TheProductionPathExportCompilesForEveryBackend()
        {
            foreach (string program in new[]
            {
                ShaderSources.TemporalAccumulateFrag, ShaderSources.TemporalAccumulateHalfFrag,
                ShaderSources.TemporalAccumulateAtDisplaySizeFrag, ShaderSources.TemporalAccumulateAtDisplaySizeHalfFrag,
                ShaderSources.TemporalResolveFrag, ShaderSources.TemporalResolveHalfFrag,
            })
            {
                foreach (Func<string, string> export in new Func<string, string>[]
                {
                    TemporalVisibilityProductionPaths.Rewrite, TemporalVisibilityProductionPaths.RewriteKeepingTheDecision,
                })
                {
                    string rewritten = export(program);
                    Assert.NotEqual(program, rewritten);
                    ShaderValidation.ValidatePair(ShaderSources.FullscreenVert, rewritten, "path export");
                    Assert.Equal(Reflected(program), Reflected(rewritten));
                }
                // The second export keeps production's visibility functions word for word.
                Assert.Contains(ShaderSources.TemporalVisibilityGlsl,
                    TemporalVisibilityProductionPaths.RewriteKeepingTheDecision(program), StringComparison.Ordinal);
            }
            Assert.Same(ShaderSources.TemporalPrepareFrag, TemporalVisibilityProductionPaths.Rewrite(ShaderSources.TemporalPrepareFrag));
            Assert.Same(ShaderSources.TemporalDepthStoreFrag,
                TemporalVisibilityProductionPaths.Rewrite(ShaderSources.TemporalDepthStoreFrag));
            Assert.Same(ShaderSources.TemporalPrepareFrag,
                TemporalVisibilityProductionPaths.RewriteKeepingTheDecision(ShaderSources.TemporalPrepareFrag));
        }

        // The resource elements Metal's and Direct3D's emitters reflect for a fragment program.
        static (int Msl, int Hlsl) Reflected(string fragment)
        {
            byte[] vertex = SpirvFrontEnd.ToSpirv(ShaderSources.FullscreenVert, GpuShaderStages.Vertex, "reflected");
            byte[] spirv = SpirvFrontEnd.ToSpirv(fragment, GpuShaderStages.Fragment, "reflected");
            static int Elements(ShaderReflection r) => r.ResourceLayouts.Sum(l => l.Elements.Length);
            return (Elements(SpirvCrossCompile.VertexFragmentToMsl(vertex, spirv, "reflected").Reflection),
                Elements(SpirvCrossCompile.VertexFragmentToHlsl(vertex, spirv, "reflected").Reflection));
        }

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
