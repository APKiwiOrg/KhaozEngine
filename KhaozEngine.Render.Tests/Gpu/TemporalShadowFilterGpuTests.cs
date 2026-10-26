using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The shipped cascade comparison filter, read back from the GPU and held to <see cref="ShadowFilterOracle"/>.
    /// <para>
    /// The kernel splices <see cref="ShaderSources.CascadeShadowFilterGlsl"/> verbatim, binds an R32F atlas through
    /// the receivers' own clamp and point sampler, and evaluates one case per pixel. Each pixel stores the bits of its
    /// float result across the four RGBA8 bytes, so the readback is exact and the comparison tolerance only covers the
    /// final division and the hand-off mix.
    /// </para>
    /// <para>
    /// The same harness runs the <see cref="TemporalShadowGatherCompileTests.GatherFilterGlsl"/> candidate against the
    /// same oracle, and reads an asymmetric 2x2 through <c>textureGather</c> to pin its component order on this backend.
    /// Neither gather fact decides what ships on its own: gathers ship only once every native backend passes both.
    /// </para>
    /// </summary>
    public sealed class TemporalShadowFilterGpuTests(ITestOutputHelper output)
    {
        const int Capacity = 128;
        const float Tolerance = 1e-6f;

        const string KernelHeader = @"#version 450
layout(set = 0, binding = 0) uniform texture2D ShadowAtlas;
layout(set = 0, binding = 1) uniform sampler ShadowSamp;
layout(set = 0, binding = 2) uniform Cases { vec4 Items[384]; };
layout(location = 0) out vec4 oColor;
";

        const string KernelMain = @"
void main() {
    int i = int(gl_FragCoord.x) * 3;
    vec4 lookup = Items[i];
    vec4 select = Items[i + 1];
    vec4 next = Items[i + 2];
    int cascade = int(select.x + 0.5);
    int count = int(select.y + 0.5);
    float lit = pcfCascade(ShadowAtlas, ShadowSamp, cascade, count, lookup.xy, lookup.z, lookup.w);
    if (select.z > 0.5) {
        float lit2 = pcfCascade(ShadowAtlas, ShadowSamp, cascade + 1, count, next.xy, next.z, lookup.w);
        lit = mix(lit2, lit, select.w);
    }
    uint bits = floatBitsToUint(lit);
    oColor = vec4(float(bits & 255u), float((bits >> 8) & 255u), float((bits >> 16) & 255u), float(bits >> 24)) / 255.0;
}
";

        const string GatherOrderFrag = @"#version 450
layout(set = 0, binding = 0) uniform texture2D Quad;
layout(set = 0, binding = 1) uniform sampler QuadSamp;
layout(location = 0) out vec4 oColor;

void main() {
    oColor = textureGather(sampler2D(Quad, QuadSamp), vec2(0.5, 0.5), 0);
}
";

        /// <summary>The readback kernel around <paramref name="filterGlsl"/>, which must define <c>pcfCascade</c>.</summary>
        internal static string Kernel(string filterGlsl) => KernelHeader + filterGlsl + KernelMain;

        [GpuFact]
        public void The_cascade_filter_matches_the_sixteen_comparison_oracle()
            => AssertMatchesOracle(ShaderSources.CascadeShadowFilterGlsl, "shipped cascade filter");

        [GpuFact]
        public void The_gather_candidate_reproduces_the_explicit_footprint()
            => AssertMatchesOracle(TemporalShadowGatherCompileTests.GatherFilterGlsl, "gather candidate");

        /// <summary>
        /// Texels (column, row) hold 0.2 at (0,0), 0.4 at (1,0), 0.6 at (0,1) and 0.8 at (1,1), every one distinct, so a
        /// swapped pair of components shows. The documented order is x (0,1), y (1,1), z (1,0), w (0,0), with row 1 the
        /// higher V. The values are k/255 for whole k, so RGBA8 holds them exactly.
        /// </summary>
        [GpuFact]
        public void Gather_returns_an_asymmetric_quad_in_the_documented_component_order()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice dev = gpu.GpuDevice;
            byte[] rgba = Render(dev, GatherOrderFrag, new[] { 0.2f, 0.4f, 0.6f, 0.8f }, 2, 2, null, 1);
            string got = $"({rgba[0]}, {rgba[1]}, {rgba[2]}, {rgba[3]})";
            output.WriteLine($"Gather order on {dev.Backend} {dev.Capabilities.DeviceName}, {TemporalDeviceClass.Name}: {got}");
            Assert.True(rgba[0] == 153 && rgba[1] == 204 && rgba[2] == 102 && rgba[3] == 51,
                $"{dev.Backend}: textureGather returned {got} where the documented order reads (153, 204, 102, 51).");
        }

        void AssertMatchesOracle(string filterGlsl, string what)
        {
            float[] atlas = ShadowFilterOracle.Atlas();
            IReadOnlyList<ShadowFilterCase> cases = ShadowFilterOracle.Cases();
            Assert.True(cases.Count <= Capacity, $"{cases.Count} cases exceed the kernel's {Capacity}.");

            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice dev = gpu.GpuDevice;
            byte[] rgba = Render(dev, Kernel(filterGlsl), atlas, ShadowFilterOracle.AtlasWidth,
                ShadowFilterOracle.Resolution, Pack(cases), cases.Count);

            var misses = new List<string>();
            float worst = 0f;
            for (int i = 0; i < cases.Count; i++)
            {
                float got = BitConverter.Int32BitsToSingle(
                    rgba[i * 4] | rgba[i * 4 + 1] << 8 | rgba[i * 4 + 2] << 16 | rgba[i * 4 + 3] << 24);
                float expected = ShadowFilterOracle.Expected(atlas, cases[i]);
                float error = MathF.Abs(got - expected);
                if (!(error <= Tolerance))
                    misses.Add($"  {cases[i].Name}: GPU {Format(got)}, oracle {Format(expected)}");
                else
                    worst = MathF.Max(worst, error);
            }

            output.WriteLine($"{what} on {dev.Backend} {dev.Capabilities.DeviceName}, {TemporalDeviceClass.Name}: "
                + $"{cases.Count} cases, {misses.Count} misses, worst matching error {Format(worst)}.");
            foreach (string miss in misses) output.WriteLine(miss);
            Assert.True(misses.Count == 0, $"{dev.Backend}: the {what} missed the 16-comparison oracle in "
                + $"{misses.Count} of {cases.Count} cases, beyond {Format(Tolerance)}.\n" + string.Join("\n", misses));
        }

        /// <summary>Three vec4 per case: lookup (uv, depth, texelStep), select (cascade, count, blend, weight) and the
        /// next cascade's (uv, depth).</summary>
        static Vector4[] Pack(IReadOnlyList<ShadowFilterCase> cases)
        {
            var items = new Vector4[Capacity * 3];
            for (int i = 0; i < cases.Count; i++)
            {
                ShadowFilterCase c = cases[i];
                items[i * 3] = new Vector4(c.Uv, c.Depth, ShadowFilterOracle.TexelStep);
                items[i * 3 + 1] = new Vector4(c.Cascade, ShadowFilterOracle.Count, c.Blend ? 1f : 0f, c.Weight);
                items[i * 3 + 2] = new Vector4(c.NextUv, c.NextDepth, 0f);
            }
            return items;
        }

        /// <summary>One fullscreen draw of <paramref name="fragment"/> over an R32F source through the receivers'
        /// clamp and point sampler, read back as RGBA8 from a <paramref name="width"/> x 1 target.</summary>
        internal static byte[] Render(IGpuDevice dev, string fragment, float[] texels, int texWidth, int texHeight,
            Vector4[]? items, int width)
        {
            IGpuResourceFactory f = dev.Factory;
            using IGpuTexture source = f.CreateTexture(GpuTextureDescription.Texture2D(
                (uint)texWidth, (uint)texHeight, GpuPixelFormat.R32Float, GpuTextureUsage.Sampled));
            dev.UpdateTexture(source, MemoryMarshal.AsBytes(texels.AsSpan()).ToArray(), 0, 0, (uint)texWidth,
                (uint)texHeight);
            using IGpuSampler sampler = f.CreateSampler(new GpuSamplerDescription(GpuSamplerFilter.MinPointMagPointMipPoint,
                GpuSamplerAddress.Clamp, GpuSamplerAddress.Clamp, GpuSamplerAddress.Clamp));
            using IGpuBuffer? uniforms = items is null ? null
                : f.CreateBuffer(new GpuBufferDescription((uint)(items.Length * 16), GpuBufferUsage.UniformBuffer));
            if (uniforms is not null) dev.UpdateBuffer(uniforms, 0, items!);

            var elements = new List<GpuResourceLayoutElement>
            {
                new("Source", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new("Sampler", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
            };
            if (uniforms is not null)
                elements.Add(new("Cases", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment));

            using IGpuTexture colour = f.CreateTexture(GpuTextureDescription.Texture2D(
                (uint)width, 1, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = f.CreateFramebuffer(null, colour);
            using IGpuResourceLayout layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(elements.ToArray()));
            using IGpuShaderSet shaders = f.CreateShadersFromSpirv(ComputeShaders.FullscreenVert, fragment);
            using IGpuPipeline pipe = f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend },
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise,
                    false, false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { layout },
                ShaderSet = shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription>(),
                Outputs = fb.Outputs,
            });
            using IGpuResourceSet set = uniforms is null
                ? f.CreateResourceSet(new GpuResourceSetDescription(layout, source, sampler))
                : f.CreateResourceSet(new GpuResourceSetDescription(layout, source, sampler, uniforms));
            using (IGpuCommandList cl = f.CreateCommandList())
            {
                cl.Begin();
                cl.SetFramebuffer(fb);
                cl.ClearColorTarget(0, Color.Black);
                cl.SetPipeline(pipe);
                cl.SetGraphicsResourceSet(0, set);
                cl.Draw(3);
                cl.End();
                dev.Submit(cl);
                dev.WaitForIdle();
            }
            return GpuReadback.ToRgba(dev, colour, width, 1);
        }

        static string Format(float v) => v.ToString("0.0000000", CultureInfo.InvariantCulture);
    }
}
