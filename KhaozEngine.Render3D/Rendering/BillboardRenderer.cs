using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// Immediate-mode triangle-list renderer for camera-facing soft-disc billboards. Draws coloured
    /// <see cref="BillboardVertex"/> triangles on top of an already-rendered target with depth disabled,
    /// transformed by a single mat4 view-projection. A thin config over <see cref="OverlayRenderer{TVertex}"/>
    /// with two blend pipelines: index 0 alpha, index 1 additive (SourceAlpha/One) for glowy accumulation.
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

        const int AlphaPipeline = 0;
        const int AdditivePipeline = 1;

        readonly OverlayRenderer<BillboardVertex> _overlay;

        public BillboardRenderer(IGpuDevice gd, GpuOutputDescription targetOutput)
        {
            var vertexLayout = new GpuVertexLayoutDescription(
                new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Uv", GpuVertexElementFormat.Float2),
                new GpuVertexElement("Color", GpuVertexElementFormat.Float4));

            // Two pipelines share the layout/UBO/shaders: [0] alpha, [1] additive
            // (out = src.rgb*src.a + dst*1) for glowy accumulation on sparks/flashes.
            _overlay = new OverlayRenderer<BillboardVertex>(gd, targetOutput,
                ShaderSources.BillboardVert, ShaderSources.BillboardFrag,
                vertexLayout, BillboardVertex.SizeInBytes, GpuPrimitiveTopology.TriangleList,
                GpuBlendAttachment.AlphaBlend, GpuBlendAttachment.Additive);
        }

        /// <summary>Draw <paramref name="verts"/> as a triangle list into <paramref name="target"/> (no clear;
        /// this is an overlay), transformed by <paramref name="viewProj"/>, using the additive pipeline when
        /// <paramref name="additive"/> else the alpha pipeline. No-op when empty.</summary>
        public void Draw(IGpuCommandList cl, Matrix4x4 viewProj, ReadOnlySpan<BillboardVertex> verts, IGpuFramebuffer target, bool additive) =>
            _overlay.Draw(cl, viewProj, verts, target, additive ? AdditivePipeline : AlphaPipeline);

        public void Dispose() => _overlay.Dispose();
    }
}
