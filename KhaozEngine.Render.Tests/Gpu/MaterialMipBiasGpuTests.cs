using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The material mip bias on the device, through the shipped snippets
    /// (<see cref="ShaderSources.MaterialLodSampleGlsl"/> and <see cref="ShaderSources.MaterialLodGradGlsl"/>) spliced
    /// behind a <c>Params</c> lane exactly as the material programs splice them behind the frame block. The texture is
    /// mip-banded: every level has its own hue, so the hue of a tap names the level it read. A negative bias reads a
    /// finer level and a positive one a coarser level, on the implicit-LOD tap and on the explicit-gradient tap the
    /// ground passes take. A zero bias is the unbiased tap to the byte, against a program that samples with the plain
    /// call, over a field that crosses every level with fractional LODs, magnification and minification, under the
    /// point sampler, the trilinear sampler the model programs use and the anisotropic sampler the ground passes use.
    /// </summary>
    public sealed class MaterialMipBiasGpuTests
    {
        const uint TexSize = 64, Levels = 7;

        /// <summary>The hue of each mip level, 0 to 6, as the channels that are on.</summary>
        static readonly (bool R, bool G, bool B)[] Hues =
        {
            (true, false, false), (false, true, false), (false, false, true), (true, true, false),
            (false, true, true), (true, false, true), (true, true, true),
        };

        /// <summary>A 16 by 16 target over the 64 texel texture: four texels a pixel on both axes, so LOD 2
        /// everywhere.</summary>
        const uint BandTarget = 16;
        const string BandUv = "vUv";

        /// <summary>A 64 by 64 field whose LOD runs from magnification at the top to past the last level at the bottom,
        /// anisotropic and fractional almost everywhere.</summary>
        const uint FieldTarget = 64;
        const string FieldUv = "vec2(vUv.x, vUv.y * 0.37) * exp2(vUv.y * 9.0 - 3.0)";

        public enum Path { Implicit, Gradient }

        public enum SamplerKind { Point, Trilinear, Anisotropic }

        readonly ITestOutputHelper _out;

        public MaterialMipBiasGpuTests(ITestOutputHelper output) => _out = output;

        [GpuTheory]
        [InlineData(Path.Implicit, SamplerKind.Point)]
        [InlineData(Path.Implicit, SamplerKind.Trilinear)]
        [InlineData(Path.Gradient, SamplerKind.Point)]
        [InlineData(Path.Gradient, SamplerKind.Trilinear)]
        public void ABiasMovesTheTapByExactlyThatManyLevels(Path path, SamplerKind kind)
        {
            using var rig = new Rig();
            foreach (int bias in new[] { -2, -1, 0, 1 })
            {
                byte[] rgba = rig.Draw(path, biased: true, kind, BandTarget, BandUv, bias);
                int want = 2 + bias;
                int[] seen = LevelHistogram(rgba);
                _out.WriteLine($"{path} {kind} bias {bias}: levels {string.Join(",", seen)}");
                Assert.Equal((int)(BandTarget * BandTarget), seen[want]);
            }
        }

        [GpuTheory]
        [InlineData(Path.Implicit, SamplerKind.Point)]
        [InlineData(Path.Implicit, SamplerKind.Trilinear)]
        [InlineData(Path.Implicit, SamplerKind.Anisotropic)]
        [InlineData(Path.Gradient, SamplerKind.Point)]
        [InlineData(Path.Gradient, SamplerKind.Trilinear)]
        [InlineData(Path.Gradient, SamplerKind.Anisotropic)]
        public void AZeroBiasIsTheUnbiasedTapToTheByte(Path path, SamplerKind kind)
        {
            using var rig = new Rig();
            byte[] plain = rig.Draw(path, biased: false, kind, FieldTarget, FieldUv, 0);
            byte[] zero = rig.Draw(path, biased: true, kind, FieldTarget, FieldUv, 0);

            int[] seen = LevelHistogram(plain);
            int levels = 0;
            foreach (int n in seen) if (n > 0) levels++;
            _out.WriteLine($"{path} {kind}: levels {string.Join(",", seen)}, distinct texels {Distinct(plain)}");
            Assert.True(levels >= 5, $"the field reached only {levels} levels, so it proves too little");

            int differing = 0;
            for (int i = 0; i < plain.Length; i += 4)
                if (plain.AsSpan(i, 4).SequenceEqual(zero.AsSpan(i, 4)) == false) differing++;
            Assert.True(differing == 0, $"{differing} of {plain.Length / 4} pixels moved under a zero bias");
        }

        /// <summary>How many pixels read each level, by hue: a channel is on when it is above half the
        /// brightest.</summary>
        static int[] LevelHistogram(byte[] rgba)
        {
            var seen = new int[Levels];
            for (int i = 0; i < rgba.Length; i += 4)
            {
                int max = Math.Max(rgba[i], Math.Max(rgba[i + 1], rgba[i + 2]));
                var hue = (rgba[i] * 2 > max, rgba[i + 1] * 2 > max, rgba[i + 2] * 2 > max);
                int level = Array.IndexOf(Hues, hue);
                if (max > 0 && level >= 0) seen[level]++;
            }
            return seen;
        }

        static int Distinct(byte[] rgba)
        {
            var set = new HashSet<int>();
            for (int i = 0; i < rgba.Length; i += 4) set.Add(BitConverter.ToInt32(rgba, i));
            return set.Count;
        }

        static string Fragment(Path path, bool biased, string uv)
        {
            string decl = biased
                ? "layout(set=0, binding=0) uniform U { vec4 Params; };\n"
                  + "layout(set=0, binding=1) uniform texture2D Albedo;\n"
                  + "layout(set=0, binding=2) uniform sampler Samp;\n"
                : "layout(set=0, binding=0) uniform texture2D Albedo;\n"
                  + "layout(set=0, binding=1) uniform sampler Samp;\n";
            string body = path == Path.Implicit
                ? (biased ? "    oColor = materialSample(Albedo, Samp, uv);\n"
                          : "    oColor = texture(sampler2D(Albedo, Samp), uv);\n")
                : "    vec3 w = vec3(uv.x, 0.0, uv.y);\n"
                  + (biased
                      ? "    vec3 dWx = dFdx(w) * materialGradScale();\n    vec3 dWy = dFdy(w) * materialGradScale();\n"
                      : "    vec3 dWx = dFdx(w);\n    vec3 dWy = dFdy(w);\n")
                  + "    oColor = textureGrad(sampler2D(Albedo, Samp), w.xz, dWx.xz, dWy.xz);\n";
            string snippet = !biased ? "" : path == Path.Implicit
                ? ShaderSources.MaterialLodSampleGlsl
                : ShaderSources.MaterialLodGradGlsl;
            return "#version 450\n" + decl + "layout(location=0) in vec2 vUv;\nlayout(location=0) out vec4 oColor;\n"
                + snippet + "void main() {\n    vec2 uv = " + uv + ";\n" + body + "}\n";
        }

        /// <summary>The device, the mip-banded texture, the <c>Params</c> buffer and the three samplers.</summary>
        sealed class Rig : IDisposable
        {
            readonly GpuDeviceContext _gpu = GpuDeviceContext.CreateHeadless();
            readonly IGpuTexture _texture;
            readonly IGpuBuffer _params;
            readonly IGpuSampler _anisotropic;

            public Rig()
            {
                IGpuDevice device = _gpu.GpuDevice;
                IGpuResourceFactory f = device.Factory;
                _texture = f.CreateTexture(new GpuTextureDescription(
                    TexSize, TexSize, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled, Levels));
                for (uint level = 0; level < Levels; level++)
                {
                    uint size = TexSize >> (int)level;
                    device.UpdateTexture(_texture, Band(level, size), 0, 0, size, size, level, 0);
                }
                _params = f.CreateBuffer(new GpuBufferDescription(16, GpuBufferUsage.UniformBuffer));
                // The ground passes' default sampler: 16x anisotropy and a +1 sampler bias where the backend has one.
                _anisotropic = f.CreateSampler(new GpuSamplerDescription(GpuSamplerFilter.Anisotropic,
                    GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap,
                    maximumAnisotropy: 16, mipLodBias: 1));
                device.WaitForIdle();
            }

            /// <summary>Level <paramref name="level"/>: its hue at a brightness from one half to one that changes
            /// from texel to texel, so bilinear filtering has something to weigh.</summary>
            static byte[] Band(uint level, uint size)
            {
                var px = new byte[size * size * 4];
                (bool r, bool g, bool b) = Hues[level];
                for (uint y = 0; y < size; y++)
                    for (uint x = 0; x < size; x++)
                    {
                        byte v = (byte)(128 + ((x * 7 + y * 13) & 15) * 127 / 15);
                        uint i = (y * size + x) * 4;
                        px[i] = r ? v : (byte)0;
                        px[i + 1] = g ? v : (byte)0;
                        px[i + 2] = b ? v : (byte)0;
                        px[i + 3] = 255;
                    }
                return px;
            }

            IGpuSampler Sampler(SamplerKind kind) => kind switch
            {
                SamplerKind.Point => _gpu.GpuDevice.PointSampler,
                SamplerKind.Trilinear => _gpu.GpuDevice.LinearSampler,
                _ => _anisotropic,
            };

            /// <summary>One fullscreen draw into a fresh <paramref name="size"/> square target, read back as RGBA8.
            /// A biased program reads <c>Params</c> as the frame block carries it: z the bias in levels and w
            /// <c>exp2(bias) - 1</c>.</summary>
            public byte[] Draw(Path path, bool biased, SamplerKind kind, uint size, string uv, float bias)
            {
                IGpuDevice device = _gpu.GpuDevice;
                IGpuResourceFactory f = device.Factory;
                device.UpdateBuffer(_params, 0, new Vector4(0f, 0f, bias, MathF.Pow(2f, bias) - 1f));

                var elements = new List<GpuResourceLayoutElement>();
                if (biased)
                    elements.Add(new GpuResourceLayoutElement("U", GpuResourceKind.UniformBuffer,
                        GpuShaderStages.Fragment));
                elements.Add(new GpuResourceLayoutElement("Albedo", GpuResourceKind.TextureReadOnly,
                    GpuShaderStages.Fragment));
                elements.Add(new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Fragment));
                var resources = new List<IGpuBindableResource>();
                if (biased) resources.Add(_params);
                resources.Add(_texture);
                resources.Add(Sampler(kind));

                using IGpuTexture target = f.CreateTexture(GpuTextureDescription.Texture2D(
                    size, size, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
                using IGpuFramebuffer framebuffer = f.CreateFramebuffer(null, target);
                using IGpuResourceLayout layout = f.CreateResourceLayout(
                    new GpuResourceLayoutDescription(elements.ToArray()));
                using IGpuResourceSet set = f.CreateResourceSet(
                    new GpuResourceSetDescription(layout, resources.ToArray()));
                using IGpuShaderSet shaders = f.CreateShadersFromSpirv(
                    ShaderSources.FullscreenVert, Fragment(path, biased, uv));
                using IGpuPipeline pipeline = f.CreateGraphicsPipeline(new GpuPipelineDescription
                {
                    BlendFactor = Vector4.Zero,
                    BlendAttachments = [GpuBlendAttachment.OverrideBlend],
                    DepthStencil = GpuDepthStencilState.Disabled,
                    Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid,
                        GpuFrontFace.Clockwise, depthClipEnabled: false, scissorTestEnabled: false),
                    Topology = GpuPrimitiveTopology.TriangleList,
                    ResourceLayouts = [layout],
                    ShaderSet = shaders,
                    VertexLayouts = new List<GpuVertexLayoutDescription>(),
                    Outputs = framebuffer.Outputs,
                });

                using (IGpuCommandList commands = f.CreateCommandList())
                {
                    commands.Begin();
                    commands.SetFramebuffer(framebuffer);
                    commands.ClearColorTarget(0, Color.Black);
                    commands.SetPipeline(pipeline);
                    commands.SetGraphicsResourceSet(0, set);
                    commands.Draw(3);
                    commands.End();
                    device.Submit(commands);
                    device.WaitForIdle();
                }
                Assert.Null(device.Diagnostics.DeviceLossReason);
                return GpuReadback.ToRgba(device, target, (int)size, (int)size);
            }

            public void Dispose()
            {
                _gpu.GpuDevice.WaitForIdle();
                _anisotropic.Dispose();
                _params.Dispose();
                _texture.Dispose();
                _gpu.Dispose();
            }
        }
    }
}
