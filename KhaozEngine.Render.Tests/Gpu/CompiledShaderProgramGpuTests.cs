using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>A compiled Slang program loads and draws through the backend KE_GRAPHICS_BACKEND selects.</summary>
    public sealed class CompiledShaderProgramGpuTests
    {
        const int Size = 32;

        [StructLayout(LayoutKind.Sequential)]
        struct SpriteVertex
        {
            public Vector2 Pos;
            public Vector2 Uv;
            public Vector4 Color;
            public Vector2 Local;
            public Vector4 Shape;
            public Vector2 Mode;
        }

        static GpuShaderProgram SpriteProgram(string id = "sprite")
            => GpuShaderBundle.FromAssembly(typeof(CompiledShaderProgramGpuTests).Assembly).Get(id);

        [GpuTheory]
        [InlineData("sprite")]
        [InlineData("sprite-precise")]
        public void A_compiled_sprite_program_draws_the_same_pixels_as_the_glsl_pair(string id)
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice dev = gpu.GpuDevice;
            IGpuResourceFactory f = dev.Factory;
            GpuShaderProgram program = SpriteProgram(id);

            if (program.VertexPreciseMath && dev.Backend == GpuBackendKind.VulkanNative && !dev.Capabilities.SupportsShaderFloatControls)
            {
                Assert.Equal(GpuShaderPolicyRefusal.FloatControlsUnavailable,
                    Assert.Throws<GpuShaderPolicyUnsupportedException>(() => f.CreateShaders(program)).Reason);
                return;
            }
            using IGpuShaderSet glsl = f.CreateShadersFromSpirv(
                SpriteBatch.VertSrc, SpriteBatch.FragSrc, new GpuShaderCompileOptions(program.VertexPreciseMath));
            using IGpuShaderSet compiled = f.CreateShaders(program);

            using var scene = new SpriteScene(dev);
            byte[] expected = scene.Draw(glsl);
            byte[] actual = scene.Draw(compiled);

            Assert.Contains(expected, channel => channel != 0);
            int differing = 0, largest = 0;
            for (int i = 0; i < expected.Length; i++)
            {
                int delta = Math.Abs(expected[i] - actual[i]);
                if (delta == 0) continue;
                differing++;
                largest = Math.Max(largest, delta);
            }
            Assert.True(differing == 0,
                $"{dev.Backend}: {differing} of {expected.Length} channels differ, largest delta {largest}.");
        }

        [GpuFact]
        public void A_program_whose_bindings_disagree_with_the_layouts_is_refused_at_pipeline_creation()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuResourceFactory f = gpu.GpuDevice.Factory;

            using IGpuShaderSet compiled = f.CreateShaders(SpriteProgram());
            using IGpuResourceLayout swapped = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Tex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment)));
            using IGpuResourceLayout vp = SpriteScene.ViewProjectionLayout(f);
            using IGpuTexture colour = f.CreateTexture(GpuTextureDescription.Texture2D(
                Size, Size, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = f.CreateFramebuffer(null, colour);

            var refusal = Assert.Throws<ShaderValidationException>(
                () => f.CreateGraphicsPipeline(SpriteScene.Pipeline(compiled, swapped, vp, fb.Outputs)));

            Assert.Equal(ShaderValidationRefusal.BundleLayoutMismatch, refusal.Reason);
            Assert.Equal("sprite", refusal.Label);
            Assert.Equal(0, refusal.Set);
            Assert.Equal(0, refusal.Binding);
        }

        sealed class SpriteScene : IDisposable
        {
            readonly IGpuDevice _dev;
            readonly IGpuTexture _source;
            readonly IGpuResourceLayout _textureLayout;
            readonly IGpuResourceLayout _vpLayout;
            readonly IGpuResourceSet _textureSet;
            readonly IGpuBuffer _vp;
            readonly IGpuResourceSet _vpSet;
            readonly IGpuBuffer _vertices;

            internal SpriteScene(IGpuDevice dev)
            {
                _dev = dev;
                IGpuResourceFactory f = dev.Factory;
                _source = f.CreateTexture(GpuTextureDescription.Texture2D(
                    4, 4, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
                var texels = new byte[4 * 4 * 4];
                for (int i = 0; i < 16; i++)
                {
                    texels[i * 4] = (byte)(i * 16);
                    texels[i * 4 + 1] = (byte)(255 - i * 13);
                    texels[i * 4 + 2] = (byte)(i % 3 * 120);
                    texels[i * 4 + 3] = 255;
                }
                dev.UpdateTexture(_source, texels, 0, 0, 4, 4);

                _textureLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                    new GpuResourceLayoutElement("Tex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                    new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Fragment)));
                _vpLayout = ViewProjectionLayout(f);
                _textureSet = f.CreateResourceSet(new GpuResourceSetDescription(_textureLayout, _source, dev.LinearSampler));
                _vp = f.CreateBuffer(new GpuBufferDescription(256, GpuBufferUsage.UniformBuffer));
                dev.UpdateBuffer(_vp, 0, Matrix4x4.CreateOrthographicOffCenter(0, Size, Size, 0, -1, 1));
                _vpSet = f.CreateResourceSet(new GpuResourceSetDescription(_vpLayout, new GpuBufferRange(_vp, 0, 64)));

                SpriteVertex Corner(float x, float y, float u, float v) => new()
                {
                    Pos = new Vector2(x, y),
                    Uv = new Vector2(u, v),
                    Color = new Vector4(1f, 0.75f, 0.5f, 1f),
                    Local = new Vector2(x - 16f, y - 15.5f),
                    Shape = new Vector4(11f, 9.5f, 4f, 0f),
                    Mode = new Vector2(0f, 1f),
                };
                SpriteVertex a = Corner(3, 4, 0, 0), b = Corner(29, 4, 1, 0), c = Corner(29, 27, 1, 1), d = Corner(3, 27, 0, 1);
                SpriteVertex[] quad = [a, b, c, a, c, d];
                _vertices = f.CreateBuffer(new GpuBufferDescription(
                    (uint)(quad.Length * Marshal.SizeOf<SpriteVertex>()), GpuBufferUsage.VertexBuffer));
                dev.UpdateBuffer(_vertices, 0, quad);
            }

            internal static IGpuResourceLayout ViewProjectionLayout(IGpuResourceFactory f)
                => f.CreateResourceLayout(new GpuResourceLayoutDescription(
                    new GpuResourceLayoutElement("Vp", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex, dynamic: true)));

            internal static GpuPipelineDescription Pipeline(IGpuShaderSet shaders, IGpuResourceLayout textures,
                IGpuResourceLayout vp, GpuOutputDescription outputs) => new()
                {
                    BlendFactor = Vector4.Zero,
                    BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend },
                    DepthStencil = GpuDepthStencilState.Disabled,
                    Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, false, false),
                    Topology = GpuPrimitiveTopology.TriangleList,
                    ResourceLayouts = new[] { textures, vp },
                    ShaderSet = shaders,
                    VertexLayouts = new List<GpuVertexLayoutDescription>
                    {
                        new(new GpuVertexElement("LocalPos", GpuVertexElementFormat.Float2),
                            new GpuVertexElement("Uv", GpuVertexElementFormat.Float2),
                            new GpuVertexElement("Color", GpuVertexElementFormat.Float4),
                            new GpuVertexElement("Local", GpuVertexElementFormat.Float2),
                            new GpuVertexElement("Shape", GpuVertexElementFormat.Float4),
                            new GpuVertexElement("Mode", GpuVertexElementFormat.Float2)),
                    },
                    Outputs = outputs,
                };

            internal byte[] Draw(IGpuShaderSet shaders)
            {
                IGpuResourceFactory f = _dev.Factory;
                using IGpuTexture colour = f.CreateTexture(GpuTextureDescription.Texture2D(
                    Size, Size, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
                using IGpuFramebuffer fb = f.CreateFramebuffer(null, colour);
                using IGpuPipeline pipeline = f.CreateGraphicsPipeline(Pipeline(shaders, _textureLayout, _vpLayout, fb.Outputs));
                using (IGpuCommandList cl = f.CreateCommandList())
                {
                    cl.Begin();
                    cl.SetFramebuffer(fb);
                    cl.ClearColorTarget(0, Color.Black);
                    cl.SetPipeline(pipeline);
                    cl.SetGraphicsResourceSet(0, _textureSet);
                    cl.SetGraphicsResourceSet(1, _vpSet, 0);
                    cl.SetVertexBuffer(0, _vertices);
                    cl.Draw(6);
                    cl.End();
                    _dev.Submit(cl);
                    _dev.WaitForIdle();
                }
                return GpuReadback.ToRgba(_dev, colour, Size, Size);
            }

            public void Dispose()
            {
                _vertices.Dispose();
                _vpSet.Dispose();
                _vp.Dispose();
                _textureSet.Dispose();
                _vpLayout.Dispose();
                _textureLayout.Dispose();
                _source.Dispose();
            }
        }
    }
}
