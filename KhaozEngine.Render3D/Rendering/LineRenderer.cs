using System;
using System.Numerics;
using System.Text;
using Veldrid;
using Veldrid.SPIRV;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// Immediate-mode line-list renderer for the debug overlay. Draws coloured <see cref="LineVertex"/> pairs on
    /// top of an already-rendered target with depth disabled and alpha blend, transformed by a single mat4
    /// view-projection. The vertex buffer grows as needed and is uploaded per <see cref="Draw"/>.
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

        readonly GraphicsDevice _gd;
        readonly DeviceBuffer _ubo;            // one mat4 ViewProj (64 bytes)
        readonly ResourceLayout _layout;
        readonly ResourceSet _set;
        readonly Pipeline _pipeline;
        readonly Shader[] _shaders;

        DeviceBuffer? _vb;
        uint _vbCapacity;                      // capacity in vertices

        public LineRenderer(GraphicsDevice gd, OutputDescription targetOutput)
        {
            _gd = gd;
            var factory = gd.ResourceFactory;

            _ubo = factory.CreateBuffer(new BufferDescription(64, BufferUsage.UniformBuffer)); // mat4 ViewProj

            _layout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("U", ResourceKind.UniformBuffer, ShaderStages.Vertex)));
            _set = factory.CreateResourceSet(new ResourceSetDescription(_layout, _ubo));

            _shaders = factory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, Encoding.UTF8.GetBytes(ShaderSources.LineVert), "main"),
                new ShaderDescription(ShaderStages.Fragment, Encoding.UTF8.GetBytes(ShaderSources.LineFrag), "main"));

            var vertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("Position", VertexElementSemantic.Position, VertexElementFormat.Float3),
                new VertexElementDescription("Color", VertexElementSemantic.Color, VertexElementFormat.Float4));

            var blend = new BlendStateDescription(RgbaFloat.Black, BlendAttachmentDescription.AlphaBlend);

            _pipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = blend,
                DepthStencilState = DepthStencilStateDescription.Disabled,
                RasterizerState = new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.Clockwise, false, false),
                PrimitiveTopology = PrimitiveTopology.LineList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = new ShaderSetDescription(new[] { vertexLayout }, _shaders),
                Outputs = targetOutput,
            });
        }

        /// <summary>Draw <paramref name="verts"/> as a line list into <paramref name="target"/> (no clear; this is
        /// an overlay), transformed by <paramref name="viewProj"/>. No-op when empty.</summary>
        public void Draw(CommandList cl, Matrix4x4 viewProj, ReadOnlySpan<LineVertex> verts, Framebuffer target)
        {
            if (verts.Length == 0) return;

            EnsureCapacity((uint)verts.Length);
            cl.UpdateBuffer(_vb, 0, verts);
            cl.UpdateBuffer(_ubo, 0, ref viewProj);

            cl.SetFramebuffer(target);
            cl.SetPipeline(_pipeline);
            cl.SetGraphicsResourceSet(0, _set);
            cl.SetVertexBuffer(0, _vb);
            cl.Draw((uint)verts.Length, 1, 0, 0);
        }

        void EnsureCapacity(uint vertexCount)
        {
            if (_vb != null && _vbCapacity >= vertexCount) return;
            _vb?.Dispose();
            // Grow with a little headroom so a slowly-growing overlay doesn't recreate every frame.
            _vbCapacity = Math.Max(vertexCount, _vbCapacity == 0 ? 256u : _vbCapacity * 2);
            _vb = _gd.ResourceFactory.CreateBuffer(
                new BufferDescription(_vbCapacity * LineVertex.SizeInBytes, BufferUsage.VertexBuffer));
        }

        public void Dispose()
        {
            _pipeline.Dispose();
            _set.Dispose();
            _layout.Dispose();
            foreach (var sh in _shaders) sh.Dispose();
            _ubo.Dispose();
            _vb?.Dispose();
        }
    }
}
