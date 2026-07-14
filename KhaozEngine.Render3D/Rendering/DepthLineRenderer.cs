using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// DEPTH-TESTED immediate-mode line-list renderer for the debug wire volumes. Unlike the always-on-top
    /// <see cref="LineRenderer"/> (which draws after the post chain into the single-attachment present target with
    /// depth disabled), this draws INTO the lit colour + read-only scene depth framebuffer (<c>ColorDepthFB</c>)
    /// BEFORE the post chain, so the depth test (less-or-equal, no write) reads the opaque meshes' depth and terrain,
    /// props, and other geometry occlude the buried parts of a volume. Depth WRITE is off so the lines never disturb
    /// the depth the outline/edge post-pass reads, and the single colour attachment means there is no MRT
    /// normal/linear-depth to preserve. Alpha-blended. Reuses <see cref="LineRenderer.LineVertex"/> and the flat
    /// line shaders. Rebuild the pipeline via <see cref="SetOutputs"/> when the target sample count (MSAA) changes.
    /// </summary>
    internal sealed class DepthLineRenderer : IDisposable
    {
        readonly IGpuDevice _gd;
        readonly IGpuBuffer _ubo;              // one mat4 ViewProj (64 bytes)
        readonly IGpuResourceLayout _layout;
        readonly IGpuResourceSet _set;
        readonly IGpuShaderSet _shaders;
        IGpuPipeline _pipeline;                // rebuilt by SetOutputs when the target sample count (MSAA) changes
        IGpuBuffer? _vb;
        uint _vbCapacity;                      // capacity in vertices

        public DepthLineRenderer(IGpuDevice gd, GpuOutputDescription targetOutput)
        {
            _gd = gd;
            var factory = gd.Factory;

            _ubo = factory.CreateBuffer(new GpuBufferDescription(64, GpuBufferUsage.UniformBuffer)); // mat4 ViewProj
            _layout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex)));
            _set = factory.CreateResourceSet(new GpuResourceSetDescription(_layout, _ubo));
            _shaders = factory.CreateShadersFromSpirv(ShaderSources.LineVert, ShaderSources.LineFrag);

            _pipeline = BuildPipeline(factory, targetOutput);
        }

        /// <summary>Rebuild the pipeline for a new target output description (e.g. multisampled for MSAA - a
        /// pipeline's sample count must match its framebuffer). Layout/shaders/buffers are kept.</summary>
        public void SetOutputs(GpuOutputDescription targetOutput)
        {
            _pipeline.Dispose();
            _pipeline = BuildPipeline(_gd.Factory, targetOutput);
        }

        IGpuPipeline BuildPipeline(IGpuResourceFactory factory, GpuOutputDescription targetOutput)
        {
            var vertexLayout = new GpuVertexLayoutDescription(
                new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Color", GpuVertexElementFormat.Float4));

            return factory.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.AlphaBlend },
                // Read the meshes' depth (occlude the buried parts) but don't write it: a wire overlay must not
                // punch the depth the post edge pass and later overlays read.
                DepthStencil = GpuDepthStencilState.DepthTestLessEqualNoWrite,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.LineList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = _shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout },
                Outputs = targetOutput,
            });
        }

        /// <summary>Draw <paramref name="verts"/> as a line list into <paramref name="target"/> (no clear, this is an
        /// in-world overlay), transformed by <paramref name="viewProj"/> (clip-Y corrected for the live backend).
        /// No-op when empty.</summary>
        public void Draw(IGpuCommandList cl, Matrix4x4 viewProj, ReadOnlySpan<LineRenderer.LineVertex> verts, IGpuFramebuffer target)
        {
            if (verts.Length == 0) return;

            EnsureCapacity((uint)verts.Length);
            cl.UpdateBuffer(_vb!, 0, verts);
            var clipVp = GpuClip.Correct(viewProj, _gd.Capabilities);
            cl.UpdateBuffer(_ubo, 0, in clipVp);

            cl.SetFramebuffer(target);
            cl.SetPipeline(_pipeline);
            cl.SetGraphicsResourceSet(0, _set);
            cl.SetVertexBuffer(0, _vb!);
            cl.Draw((uint)verts.Length, 1, 0, 0);
        }

        void EnsureCapacity(uint vertexCount)
        {
            if (_vb != null && _vbCapacity >= vertexCount) return;
            _vb?.Dispose();
            // Grow with headroom so a slowly-growing overlay doesn't recreate the buffer every frame.
            _vbCapacity = Math.Max(vertexCount, _vbCapacity == 0 ? 256u : _vbCapacity * 2);
            _vb = _gd.Factory.CreateBuffer(new GpuBufferDescription(_vbCapacity * LineRenderer.LineVertex.SizeInBytes, GpuBufferUsage.VertexBuffer));
        }

        public void Dispose()
        {
            _pipeline.Dispose();
            _shaders.Dispose();
            _set.Dispose();
            _layout.Dispose();
            _ubo.Dispose();
            _vb?.Dispose();
        }
    }
}
