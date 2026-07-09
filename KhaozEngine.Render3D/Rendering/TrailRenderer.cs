using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// Draws motion-trail ribbons (weapon swings, thruster streaks, tracers) INTO the model MRT framebuffer
    /// alongside the lit meshes, so they interleave in depth: the depth test (less-or-equal, no write) reads the
    /// meshes' depth, so a nearer mesh occludes the trail and a trail in front draws over farther geometry.
    /// Attachment 0 blends (additive OR alpha, one pipeline each); attachments 1 &amp; 2 (normal, depth) preserve
    /// their destination so the edge post-pass ignores the trail. Sibling to <see cref="BeamRenderer"/> - a trail is
    /// a beam generalised to N points with per-sample width and alpha; the strip is built by <see cref="TrailGeometry"/>
    /// and the per-vertex colour/soft-edge is baked in, so all verts of a given blend draw in ONE call with no
    /// per-draw uniform rebinding.
    /// </summary>
    internal sealed class TrailRenderer : IDisposable
    {
        /// <summary>One trail vertex: world position, (across,along,softEdge) UV, and premultipliable RGBA (the tint
        /// with its alpha already folded with the per-sample alpha). 40 bytes.</summary>
        internal struct TrailVertex
        {
            public Vector3 Position;
            public Vector3 Uv;       // x=across [0,1], y=along [0,1], z=softEdge [0,1]
            public Vector4 Color;    // rgb tint; a = style.alpha * sample.alpha
            public TrailVertex(Vector3 position, Vector3 uv, Vector4 color)
            { Position = position; Uv = uv; Color = color; }
            public const uint SizeInBytes = 40;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct FrameUniforms { public Matrix4x4 ViewProj; }

        readonly IGpuDevice _gd;
        readonly IGpuBuffer _ubo;              // mat4 ViewProj (64 bytes)
        readonly IGpuResourceLayout _layout;   // UBO (vertex)
        readonly IGpuResourceSet _set;
        readonly IGpuShaderSet _shaders;
        IGpuPipeline _additive;                // rebuilt by SetOutputs when the MRT sample count (MSAA) changes
        IGpuPipeline _alpha;
        IGpuBuffer? _vb;
        uint _vbCapacity;                      // capacity in vertices

        public TrailRenderer(IGpuDevice gd, GpuOutputDescription modelOutputs)
        {
            _gd = gd;
            var factory = gd.Factory;

            _ubo = factory.CreateBuffer(new GpuBufferDescription(64, GpuBufferUsage.UniformBuffer)); // mat4
            _layout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex)));
            _set = factory.CreateResourceSet(new GpuResourceSetDescription(_layout, _ubo));
            _shaders = factory.CreateShadersFromSpirv(ShaderSources.TrailVert, ShaderSources.TrailFrag);

            _additive = BuildPipeline(factory, modelOutputs, GpuBlendAttachment.Additive);
            _alpha = BuildPipeline(factory, modelOutputs, GpuBlendAttachment.AlphaBlend);
        }

        /// <summary>Rebuild both pipelines for a new model-MRT output description (e.g. multisampled for MSAA - a
        /// pipeline's sample count must match its framebuffer). Layout/shaders/buffers are kept.</summary>
        public void SetOutputs(GpuOutputDescription modelOutputs)
        {
            _additive.Dispose();
            _alpha.Dispose();
            _additive = BuildPipeline(_gd.Factory, modelOutputs, GpuBlendAttachment.Additive);
            _alpha = BuildPipeline(_gd.Factory, modelOutputs, GpuBlendAttachment.AlphaBlend);
        }

        IGpuPipeline BuildPipeline(IGpuResourceFactory factory, GpuOutputDescription modelOutputs, GpuBlendAttachment color0)
        {
            var vertexLayout = new GpuVertexLayoutDescription(
                new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Uv", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Color", GpuVertexElementFormat.Float4));

            // Attachment 0 blends (additive or alpha); normal/depth preserved so the edge pass reads the meshes'
            // normal/depth, not the trail's (no outline traced around the strip).
            var blends = new[] { color0, GpuBlendAttachment.PreserveDestination, GpuBlendAttachment.PreserveDestination };

            return factory.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = blends,
                // Read the meshes' depth (interleave/occlude) but don't write it: a translucent trail must not occlude.
                DepthStencil = GpuDepthStencilState.DepthTestLessEqualNoWrite,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = _shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout },
                Outputs = modelOutputs,
            });
        }

        /// <summary>Upload this frame's view-projection (clip-Y corrected) once before the draws.</summary>
        public void SetFrameUniforms(IGpuCommandList cl, Matrix4x4 viewProj)
        {
            var u = new FrameUniforms { ViewProj = GpuClip.Correct(viewProj, _gd.Capabilities) };
            cl.UpdateBuffer(_ubo, 0, in u);
        }

        /// <summary>Draw <paramref name="verts"/> (all trails of one blend) into <paramref name="target"/> (the model
        /// FB, no clear) using the <paramref name="blend"/> pipeline. <see cref="SetFrameUniforms"/> must have run this
        /// frame. No-op when empty.</summary>
        public void Draw(IGpuCommandList cl, ReadOnlySpan<TrailVertex> verts, IGpuFramebuffer target, TrailBlend blend)
        {
            if (verts.Length == 0) return;

            EnsureCapacity((uint)verts.Length);
            cl.UpdateBuffer(_vb!, 0, verts);

            cl.SetFramebuffer(target);
            cl.SetPipeline(blend == TrailBlend.Alpha ? _alpha : _additive);
            cl.SetGraphicsResourceSet(0, _set);
            cl.SetVertexBuffer(0, _vb!);
            cl.Draw((uint)verts.Length, 1, 0, 0);
        }

        void EnsureCapacity(uint vertexCount)
        {
            if (_vb != null && _vbCapacity >= vertexCount) return;
            _vb?.Dispose();
            _vbCapacity = Math.Max(vertexCount, _vbCapacity == 0 ? 64u : _vbCapacity * 2);
            _vb = _gd.Factory.CreateBuffer(new GpuBufferDescription(_vbCapacity * TrailVertex.SizeInBytes, GpuBufferUsage.VertexBuffer));
        }

        public void Dispose()
        {
            _additive.Dispose();
            _alpha.Dispose();
            _shaders.Dispose();
            _set.Dispose();
            _layout.Dispose();
            _ubo.Dispose();
            _vb?.Dispose();
        }
    }
}
