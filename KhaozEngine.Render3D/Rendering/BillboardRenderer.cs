using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// Immediate-mode triangle-list renderer for camera-facing soft-disc billboards. Draws coloured
    /// <see cref="BillboardVertex"/> triangles on top of an already-rendered target with depth disabled,
    /// transformed by a single mat4 view-projection. Two pipelines share the vertex layout + UBO: one alpha
    /// blend, one additive (SourceAlpha/One). The vertex buffer grows as needed and is uploaded per
    /// <see cref="Draw"/>. Mirrors <see cref="LineRenderer"/>.
    /// </summary>
    internal sealed class BillboardRenderer : IDisposable
    {
        /// <summary>One billboard vertex: world position + UV + RGBA colour (36 bytes).</summary>
        internal struct BillboardVertex
        {
            public Vector3 Position;
            public Vector2 Uv;
            public Vector4 Color;
            public BillboardVertex(Vector3 position, Vector2 uv, Vector4 color) { Position = position; Uv = uv; Color = color; }
            public const uint SizeInBytes = 36;
        }

        readonly IGpuDevice _gd;
        readonly IGpuBuffer _ubo;              // one mat4 ViewProj (64 bytes)
        readonly IGpuResourceLayout _layout;
        readonly IGpuResourceSet _set;
        readonly IGpuPipeline _alphaPipeline;
        readonly IGpuPipeline _additivePipeline;
        readonly IGpuShaderSet _shaders;

        IGpuBuffer? _vb;
        uint _vbCapacity;                      // capacity in vertices

        public BillboardRenderer(IGpuDevice gd, GpuOutputDescription targetOutput)
        {
            _gd = gd;
            var factory = gd.Factory;

            _ubo = factory.CreateBuffer(new GpuBufferDescription(64, GpuBufferUsage.UniformBuffer)); // mat4 ViewProj

            _layout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex)));
            _set = factory.CreateResourceSet(new GpuResourceSetDescription(_layout, _ubo));

            _shaders = factory.CreateShadersFromSpirv(ShaderSources.BillboardVert, ShaderSources.BillboardFrag);

            var vertexLayout = new GpuVertexLayoutDescription(
                new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Uv", GpuVertexElementFormat.Float2),
                new GpuVertexElement("Color", GpuVertexElementFormat.Float4));

            // Additive: out = src.rgb*src.a + dst*1, alpha src.a*1 + dst*1. Glowy accumulation for sparks/flashes.
            _alphaPipeline = CreatePipeline(factory, vertexLayout, targetOutput, GpuBlendAttachment.AlphaBlend);
            _additivePipeline = CreatePipeline(factory, vertexLayout, targetOutput, GpuBlendAttachment.Additive);
        }

        IGpuPipeline CreatePipeline(IGpuResourceFactory factory, GpuVertexLayoutDescription vertexLayout,
            GpuOutputDescription targetOutput, GpuBlendAttachment blend) =>
            factory.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { blend },
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: false, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = _shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout },
                Outputs = targetOutput,
            });

        /// <summary>Draw <paramref name="verts"/> as a triangle list into <paramref name="target"/> (no clear;
        /// this is an overlay), transformed by <paramref name="viewProj"/>, using the additive pipeline when
        /// <paramref name="additive"/> else the alpha pipeline. No-op when empty.</summary>
        public void Draw(IGpuCommandList cl, Matrix4x4 viewProj, ReadOnlySpan<BillboardVertex> verts, IGpuFramebuffer target, bool additive)
        {
            if (verts.Length == 0) return;

            EnsureCapacity((uint)verts.Length);
            cl.UpdateBuffer(_vb!, 0, verts);
            cl.UpdateBuffer(_ubo, 0, in viewProj);

            cl.SetFramebuffer(target);
            cl.SetPipeline(additive ? _additivePipeline : _alphaPipeline);
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
                new GpuBufferDescription(_vbCapacity * BillboardVertex.SizeInBytes, GpuBufferUsage.VertexBuffer));
        }

        public void Dispose()
        {
            _alphaPipeline.Dispose();
            _additivePipeline.Dispose();
            _set.Dispose();
            _layout.Dispose();
            _shaders.Dispose();
            _ubo.Dispose();
            _vb?.Dispose();
        }
    }
}
