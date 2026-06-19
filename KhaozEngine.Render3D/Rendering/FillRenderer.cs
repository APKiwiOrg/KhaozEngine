using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// Immediate-mode triangle-list renderer for FILLED debug overlays (translucent ground tiles, zone/AoE
    /// highlights). Draws coloured <see cref="FillVertex"/> triangles on top of an already-rendered target with
    /// depth disabled and standard src-alpha blend, transformed by a single mat4 view-projection. Same vertex
    /// layout, UBO and shaders as <see cref="LineRenderer"/> (position + RGBA); the only difference is the
    /// triangle-list topology. The vertex buffer grows as needed and is uploaded per <see cref="Draw"/>.
    /// </summary>
    internal sealed class FillRenderer : IDisposable
    {
        /// <summary>One fill vertex: world position + RGBA colour (28 bytes). Layout matches
        /// <see cref="LineRenderer.LineVertex"/>, so the line shaders are reused.</summary>
        internal struct FillVertex
        {
            public Vector3 Position;
            public Vector4 Color;
            public FillVertex(Vector3 position, Vector4 color) { Position = position; Color = color; }
            public const uint SizeInBytes = 28;
        }

        readonly IGpuDevice _gd;
        readonly IGpuBuffer _ubo;              // one mat4 ViewProj (64 bytes)
        readonly IGpuResourceLayout _layout;
        readonly IGpuResourceSet _set;
        readonly IGpuPipeline _pipeline;
        readonly IGpuShaderSet _shaders;

        IGpuBuffer? _vb;
        uint _vbCapacity;                      // capacity in vertices

        public FillRenderer(IGpuDevice gd, GpuOutputDescription targetOutput)
        {
            _gd = gd;
            var factory = gd.Factory;

            _ubo = factory.CreateBuffer(new GpuBufferDescription(64, GpuBufferUsage.UniformBuffer)); // mat4 ViewProj

            _layout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex)));
            _set = factory.CreateResourceSet(new GpuResourceSetDescription(_layout, _ubo));

            // Same flat-colour shaders as the line pass (position + RGBA passthrough).
            _shaders = factory.CreateShadersFromSpirv(ShaderSources.LineVert, ShaderSources.LineFrag);

            var vertexLayout = new GpuVertexLayoutDescription(
                new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Color", GpuVertexElementFormat.Float4));

            _pipeline = factory.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.AlphaBlend },
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: false, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = _shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout },
                Outputs = targetOutput,
            });
        }

        /// <summary>Draw <paramref name="verts"/> as a triangle list into <paramref name="target"/> (no clear;
        /// this is an overlay), transformed by <paramref name="viewProj"/>. No-op when empty.</summary>
        public void Draw(IGpuCommandList cl, Matrix4x4 viewProj, ReadOnlySpan<FillVertex> verts, IGpuFramebuffer target)
        {
            if (verts.Length == 0) return;

            EnsureCapacity((uint)verts.Length);
            cl.UpdateBuffer(_vb!, 0, verts);
            cl.UpdateBuffer(_ubo, 0, in viewProj);

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
            // Grow with a little headroom so a slowly-growing overlay doesn't recreate every frame.
            _vbCapacity = Math.Max(vertexCount, _vbCapacity == 0 ? 256u : _vbCapacity * 2);
            _vb = _gd.Factory.CreateBuffer(
                new GpuBufferDescription(_vbCapacity * FillVertex.SizeInBytes, GpuBufferUsage.VertexBuffer));
        }

        public void Dispose()
        {
            _pipeline.Dispose();
            _set.Dispose();
            _layout.Dispose();
            _shaders.Dispose();
            _ubo.Dispose();
            _vb?.Dispose();
        }
    }
}
