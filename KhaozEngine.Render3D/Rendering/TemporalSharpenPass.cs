using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// The temporal sharpen: one RCAS fullscreen pass (<see cref="ShaderSources.TemporalSharpenFrag"/>) that
    /// <see cref="PixelPostProcess"/> runs after the tonemap while temporal anti-aliasing is on with a sharpness
    /// above zero. The post chain builds it lazily on the first frame that needs it, so a scene that never sharpens
    /// creates none of its shaders, pipeline, sampler or buffer.
    /// <para>
    /// Resource sets are cached per source texture rather than per named ping, so the pass follows whichever
    /// texture the chain hands it. <see cref="ReleaseSets"/> drops them whenever the chain's targets are rebuilt,
    /// and a steady frame only looks one up.
    /// </para>
    /// </summary>
    internal sealed class TemporalSharpenPass : IDisposable
    {
        internal struct SharpenUbo { public Vector4 Params; }   // .xy = 1/source size, .z = sharpness, .w reserved
        internal const uint SharpenBufferBytes = 16;

        readonly IGpuDevice _gd;
        readonly IGpuShaderSet _shaders;
        readonly IGpuResourceLayout _layout;
        readonly IGpuBuffer _ubo;
        readonly IGpuSampler _sampler;
        readonly Dictionary<IGpuTexture, IGpuResourceSet> _sets = new(ReferenceEqualityComparer.Instance);
        IGpuPipeline _pipeline;

        public TemporalSharpenPass(IGpuDevice gd, GpuOutputDescription output)
        {
            _gd = gd;
            IGpuResourceFactory f = gd.Factory;
            _shaders = f.CreateShadersFromSpirv(ShaderSources.FullscreenVert, ShaderSources.TemporalSharpenFrag);
            _layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Src", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Sharpen", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment)));
            _ubo = f.CreateBuffer(new GpuBufferDescription(SharpenBufferBytes, GpuBufferUsage.UniformBuffer));
            // Point sampling with clamp addressing (GpuSamplerDescription.Point clamps every axis): exact texels, and
            // the ring repeats the edge at the frame border instead of wrapping.
            _sampler = f.CreateSampler(GpuSamplerDescription.Point);
            _pipeline = BuildPipeline(output);
        }

        IGpuPipeline BuildPipeline(GpuOutputDescription output) =>
            _gd.Factory.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend },
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise,
                    depthClipEnabled: false, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = _shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription>(),
                Outputs = output,
            });

        /// <summary>Rebuild the pipeline for a new ping colour format (the HDR toggle). The caller has idled the
        /// GPU.</summary>
        public void Rebuild(GpuOutputDescription output)
        {
            _pipeline.Dispose();
            _pipeline = BuildPipeline(output);
        }

        /// <summary>Upload this frame's parameters. Call in the recording that draws, before its first draw or
        /// dispatch and before any render pass opens this frame. The sharpness maps to 0 to 1 as
        /// <see cref="TemporalSharpenMath"/> maps it, so a NaN sharpness is off.</summary>
        public void Prepare(IGpuCommandList cl, uint sourceWidth, uint sourceHeight, float sharpness)
        {
            var ubo = new SharpenUbo
            {
                Params = new Vector4(1f / Math.Max(1u, sourceWidth), 1f / Math.Max(1u, sourceHeight),
                    sharpness > 0f ? MathF.Min(sharpness, 1f) : 0f, 0f),
            };
            cl.UpdateBuffer(_ubo, 0, in ubo);
        }

        /// <summary>Sharpen <paramref name="src"/> into <paramref name="dst"/>.</summary>
        public void Draw(IGpuCommandList cl, IGpuTexture src, IGpuFramebuffer dst)
        {
            if (!_sets.TryGetValue(src, out IGpuResourceSet? set))
            {
                set = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_layout, src, _sampler, _ubo));
                _sets.Add(src, set);
            }
            cl.SetFramebuffer(dst);
            cl.SetPipeline(_pipeline);
            cl.SetGraphicsResourceSet(0, set);
            cl.Draw(3);
        }

        /// <summary>Dispose every cached set. Call when the textures they reference are rebuilt.</summary>
        public void ReleaseSets()
        {
            foreach (IGpuResourceSet set in _sets.Values) set.Dispose();
            _sets.Clear();
        }

        public void Dispose()
        {
            ReleaseSets();
            _pipeline.Dispose();
            _layout.Dispose();
            _shaders.Dispose();
            _sampler.Dispose();
            _ubo.Dispose();
        }
    }
}
