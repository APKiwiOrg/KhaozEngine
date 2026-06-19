using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// Immediate-mode line-list renderer for the debug overlay. Draws coloured <see cref="LineVertex"/> pairs on
    /// top of an already-rendered target with depth disabled and alpha blend, transformed by a single mat4
    /// view-projection. A thin config over <see cref="OverlayRenderer{TVertex}"/> (line-list topology, flat-colour
    /// line shaders, alpha blend).
    /// </summary>
    internal sealed class LineRenderer : IDisposable
    {
        /// <summary>One line endpoint: world position + RGBA colour (28 bytes).</summary>
        internal struct LineVertex
        {
            public Vector3 Position;
            public Vector4 Color;
            public LineVertex(Vector3 position, Vector4 color) { Position = position; Color = color; }
            public const uint SizeInBytes = 28;
        }

        readonly OverlayRenderer<LineVertex> _overlay;

        public LineRenderer(IGpuDevice gd, GpuOutputDescription targetOutput)
        {
            var vertexLayout = new GpuVertexLayoutDescription(
                new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Color", GpuVertexElementFormat.Float4));

            _overlay = new OverlayRenderer<LineVertex>(gd, targetOutput,
                ShaderSources.LineVert, ShaderSources.LineFrag,
                vertexLayout, LineVertex.SizeInBytes, GpuPrimitiveTopology.LineList,
                GpuBlendAttachment.AlphaBlend);
        }

        /// <summary>Draw <paramref name="verts"/> as a line list into <paramref name="target"/> (no clear; this is
        /// an overlay), transformed by <paramref name="viewProj"/>. No-op when empty.</summary>
        public void Draw(IGpuCommandList cl, Matrix4x4 viewProj, ReadOnlySpan<LineVertex> verts, IGpuFramebuffer target) =>
            _overlay.Draw(cl, viewProj, verts, target);

        public void Dispose() => _overlay.Dispose();
    }
}
