using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// Immediate-mode triangle-list renderer for FILLED debug overlays (translucent ground tiles, zone/AoE
    /// highlights). Draws coloured <see cref="FillVertex"/> triangles on top of an already-rendered target with
    /// depth disabled and standard src-alpha blend, transformed by a single mat4 view-projection. A thin config
    /// over <see cref="OverlayRenderer{TVertex}"/>; identical to <see cref="LineRenderer"/> except for triangle-list
    /// topology (same vertex layout, line shaders, alpha blend).
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

        readonly OverlayRenderer<FillVertex> _overlay;

        public FillRenderer(IGpuDevice gd, GpuOutputDescription targetOutput)
        {
            var vertexLayout = new GpuVertexLayoutDescription(
                new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Color", GpuVertexElementFormat.Float4));

            // Same flat-colour shaders as the line pass (position + RGBA passthrough); triangle-list topology.
            _overlay = new OverlayRenderer<FillVertex>(gd, targetOutput,
                ShaderSources.LineVert, ShaderSources.LineFrag,
                vertexLayout, FillVertex.SizeInBytes, GpuPrimitiveTopology.TriangleList,
                GpuBlendAttachment.AlphaBlend);
        }

        /// <summary>Draw <paramref name="verts"/> as a triangle list into <paramref name="target"/> (no clear;
        /// this is an overlay), transformed by <paramref name="viewProj"/>. No-op when empty.</summary>
        public void Draw(IGpuCommandList cl, Matrix4x4 viewProj, ReadOnlySpan<FillVertex> verts, IGpuFramebuffer target) =>
            _overlay.Draw(cl, viewProj, verts, target);

        public void Dispose() => _overlay.Dispose();
    }
}
