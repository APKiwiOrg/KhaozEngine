using System;
using System.Numerics;
using System.Text;
using Veldrid;
using Veldrid.SPIRV;
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

        readonly GraphicsDevice _gd;
        readonly DeviceBuffer _ubo;            // one mat4 ViewProj (64 bytes)
        readonly ResourceLayout _layout;
        readonly ResourceSet _set;
        readonly Pipeline _alphaPipeline;
        readonly Pipeline _additivePipeline;
        readonly Shader[] _shaders;

        DeviceBuffer? _vb;
        uint _vbCapacity;                      // capacity in vertices

        public BillboardRenderer(GraphicsDevice gd, OutputDescription targetOutput)
        {
            _gd = gd;
            var factory = gd.ResourceFactory;

            _ubo = factory.CreateBuffer(new BufferDescription(64, BufferUsage.UniformBuffer)); // mat4 ViewProj

            _layout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("U", ResourceKind.UniformBuffer, ShaderStages.Vertex)));
            _set = factory.CreateResourceSet(new ResourceSetDescription(_layout, _ubo));

            _shaders = factory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, Encoding.UTF8.GetBytes(ShaderSources.BillboardVert), "main"),
                new ShaderDescription(ShaderStages.Fragment, Encoding.UTF8.GetBytes(ShaderSources.BillboardFrag), "main"));

            var vertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("Position", VertexElementSemantic.Position, VertexElementFormat.Float3),
                new VertexElementDescription("Uv", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
                new VertexElementDescription("Color", VertexElementSemantic.Color, VertexElementFormat.Float4));

            // Additive: out = src.rgb*src.a + dst*1, alpha src.a*1 + dst*1. Glowy accumulation for sparks/flashes.
            var additiveAttachment = new BlendAttachmentDescription(
                blendEnabled: true,
                sourceColorFactor: BlendFactor.SourceAlpha,
                destinationColorFactor: BlendFactor.One,
                colorFunction: BlendFunction.Add,
                sourceAlphaFactor: BlendFactor.SourceAlpha,
                destinationAlphaFactor: BlendFactor.One,
                alphaFunction: BlendFunction.Add);

            _alphaPipeline = CreatePipeline(factory, vertexLayout, targetOutput,
                new BlendStateDescription(RgbaFloat.Black, BlendAttachmentDescription.AlphaBlend));
            _additivePipeline = CreatePipeline(factory, vertexLayout, targetOutput,
                new BlendStateDescription(RgbaFloat.Black, additiveAttachment));
        }

        Pipeline CreatePipeline(ResourceFactory factory, VertexLayoutDescription vertexLayout,
            OutputDescription targetOutput, BlendStateDescription blend) =>
            factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = blend,
                DepthStencilState = DepthStencilStateDescription.Disabled,
                RasterizerState = new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.Clockwise, false, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = new ShaderSetDescription(new[] { vertexLayout }, _shaders),
                Outputs = targetOutput,
            });

        /// <summary>Draw <paramref name="verts"/> as a triangle list into <paramref name="target"/> (no clear;
        /// this is an overlay), transformed by <paramref name="viewProj"/>, using the additive pipeline when
        /// <paramref name="additive"/> else the alpha pipeline. No-op when empty.</summary>
        public void Draw(CommandList cl, Matrix4x4 viewProj, ReadOnlySpan<BillboardVertex> verts, Framebuffer target, bool additive)
        {
            if (verts.Length == 0) return;

            EnsureCapacity((uint)verts.Length);
            cl.UpdateBuffer(_vb, 0, verts);
            cl.UpdateBuffer(_ubo, 0, ref viewProj);

            cl.SetFramebuffer(target);
            cl.SetPipeline(additive ? _additivePipeline : _alphaPipeline);
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
                new BufferDescription(_vbCapacity * BillboardVertex.SizeInBytes, BufferUsage.VertexBuffer));
        }

        public void Dispose()
        {
            _alphaPipeline.Dispose();
            _additivePipeline.Dispose();
            _set.Dispose();
            _layout.Dispose();
            foreach (var sh in _shaders) sh.Dispose();
            _ubo.Dispose();
            _vb?.Dispose();
        }
    }
}
