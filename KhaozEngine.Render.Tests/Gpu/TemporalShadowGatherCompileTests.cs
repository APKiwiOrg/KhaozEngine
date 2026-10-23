using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Whether the cascade comparison filter can read its 4x4 footprint as four 2x2 gathers, judged on the real
    /// toolchain rather than on what gathers are documented to do. Device-free, so it runs on every leg: the GLSL goes
    /// to SPIR-V through the shipped front end, and from there to MSL and HLSL through the shipped SPIRV-Cross seat
    /// under its pinned options. FXC sees the same HLSL in <c>D3D11FxcValidationTests</c>, on Windows only.
    /// <para>
    /// Compiling is necessary and not sufficient. <see cref="TemporalShadowFilterGpuTests"/> reads the candidate back
    /// against the same oracle as the shipped filter, and pins the gather component order with an asymmetric quad.
    /// The engine ships the explicit fetches until every native backend passes all of it.
    /// </para>
    /// </summary>
    public sealed class TemporalShadowGatherCompileTests(ITestOutputHelper output)
    {
        /// <summary>
        /// The gather candidate, with <c>pcfCascade</c>'s exact signature and result. Each axis folds its four
        /// <c>[1-f, 1, 1, f]</c> weights onto the texel its clamped centre lands on, inside a 4x4 window clamped into
        /// the cascade column, so a footprint at a column border repeats the edge texel exactly as the explicit
        /// clamp does. Four gathers on the window's interior texel corners then read the window once each, with no
        /// overlap and no read outside the column. Gather order is x (i0, j1), y (i1, j1), z (i1, j0), w (i0, j0).
        /// </summary>
        internal const string GatherFilterGlsl = @"
float pcfCascade(texture2D shadowAtlas, sampler shadowSamp, int cascade, int count, vec2 uv, float depth, float texelStep) {
    float atlasScaleX = 1.0 / float(count);
    float atlasBiasX = float(cascade) * atlasScaleX;
    float last = 1.0 / texelStep - 1.0;
    vec2 texel = uv / texelStep - vec2(0.5);
    vec2 base = floor(texel);
    vec2 f = texel - base;
    vec4 wx = vec4(1.0 - f.x, 1.0, 1.0, f.x);
    vec4 wy = vec4(1.0 - f.y, 1.0, 1.0, f.y);
    vec2 origin = clamp(base - vec2(1.0), vec2(0.0), vec2(last - 3.0));
    vec4 ex = vec4(0.0);
    vec4 ey = vec4(0.0);
    for (int k = 0; k < 4; k++) {
        vec2 j = clamp(base + vec2(float(k - 1)), vec2(0.0), vec2(last)) - origin;
        ex += wx[k] * vec4(equal(vec4(j.x), vec4(0.0, 1.0, 2.0, 3.0)));
        ey += wy[k] * vec4(equal(vec4(j.y), vec4(0.0, 1.0, 2.0, 3.0)));
    }
    float lit = 0.0;
    for (int gy = 0; gy < 2; gy++) {
        for (int gx = 0; gx < 2; gx++) {
            vec2 luv = (origin + vec2(float(2 * gx + 1), float(2 * gy + 1))) * texelStep;
            vec2 auv = vec2(luv.x * atlasScaleX + atlasBiasX, luv.y);
            vec4 lit4 = vec4(lessThanEqual(vec4(depth), textureGather(sampler2D(shadowAtlas, shadowSamp), auv, 0)));
            vec2 x01 = gx == 0 ? ex.xy : ex.zw;
            vec2 y01 = gy == 0 ? ey.xy : ey.zw;
            lit += dot(lit4, vec4(x01.x * y01.y, x01.y * y01.y, x01.y * y01.x, x01.x * y01.x));
        }
    }
    return lit / 9.0;
}
";

        const uint OpImageSampleExplicitLod = 88;
        const uint OpImageGather = 96;

        internal static string GatherKernel => TemporalShadowFilterGpuTests.Kernel(GatherFilterGlsl);

        internal static string ShippedKernel => TemporalShadowFilterGpuTests.Kernel(ShaderSources.CascadeShadowFilterGlsl);

        [Fact]
        public void The_gather_candidate_compiles_to_a_spirv_image_gather()
        {
            byte[] spirv = SpirvFrontEnd.ToSpirv(GatherKernel, GpuShaderStages.Fragment, "gather candidate");
            int gathers = Count(spirv, OpImageGather);
            output.WriteLine($"Gather candidate SPIR-V: {gathers} OpImageGather, "
                + $"{Count(spirv, OpImageSampleExplicitLod)} OpImageSampleExplicitLod.");
            Assert.True(gathers >= 1, "The gather candidate emitted no OpImageGather.");
            Assert.Equal(0, Count(spirv, OpImageSampleExplicitLod));
        }

        [Fact]
        public void The_shipped_filter_uses_explicit_fetches_and_no_gather()
        {
            byte[] spirv = SpirvFrontEnd.ToSpirv(ShippedKernel, GpuShaderStages.Fragment, "shipped filter");
            output.WriteLine($"Shipped filter SPIR-V: {Count(spirv, OpImageGather)} OpImageGather, "
                + $"{Count(spirv, OpImageSampleExplicitLod)} OpImageSampleExplicitLod.");
            Assert.Equal(0, Count(spirv, OpImageGather));
            Assert.True(Count(spirv, OpImageSampleExplicitLod) >= 1,
                "The shipped filter has no explicit-LOD fetch, so it is not the point-fetch comparison footprint.");
        }

        [Fact]
        public void The_gather_candidate_cross_compiles_to_msl_and_hlsl_gathers()
        {
            byte[] vertex = SpirvFrontEnd.ToSpirv(ComputeShaders.FullscreenVert, GpuShaderStages.Vertex, "fullscreen");
            byte[] fragment = SpirvFrontEnd.ToSpirv(GatherKernel, GpuShaderStages.Fragment, "gather candidate");

            string msl = SpirvCrossCompile.VertexFragmentToMsl(vertex, fragment, "gather candidate").FragmentSource;
            string hlsl = SpirvCrossCompile.VertexFragmentToHlsl(vertex, fragment, "gather candidate").FragmentSource;

            Assert.Contains(".gather(", msl, StringComparison.Ordinal);
            Assert.Contains(".GatherRed(", hlsl, StringComparison.Ordinal);
            ShaderValidation.ValidatePair(ComputeShaders.FullscreenVert, GatherKernel, "gather candidate");
            ShaderValidation.ValidatePair(ComputeShaders.FullscreenVert, ShippedKernel, "shipped filter");
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
