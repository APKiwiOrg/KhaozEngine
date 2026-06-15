using System;
using System.Numerics;
using System.Text;
using Veldrid;
using Veldrid.SPIRV;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>Builds the model pipeline and draws the lit/cel glTF mesh into the low-res MRT.</summary>
    internal sealed class ModelRenderer : IDisposable
    {
        struct CamUbo { public Matrix4x4 ViewProj; public Matrix4x4 Model; }
        struct LightUbo { public Vector4 Dir; public Vector4 Color; public Vector4 Ambient; public Vector4 Params; }

        readonly GraphicsDevice _gd;
        readonly DeviceBuffer _camBuf, _lightBuf;
        readonly ResourceSet _set;
        readonly Pipeline _pipeline;
        readonly Shader[] _shaders;

        public ModelRenderer(GraphicsDevice gd, OutputDescription modelOutputs)
        {
            _gd = gd;
            var factory = gd.ResourceFactory;

            _camBuf = factory.CreateBuffer(new BufferDescription(128, BufferUsage.UniformBuffer));
            _lightBuf = factory.CreateBuffer(new BufferDescription(64, BufferUsage.UniformBuffer));

            var layout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("Cam", ResourceKind.UniformBuffer, ShaderStages.Vertex),
                new ResourceLayoutElementDescription("Light", ResourceKind.UniformBuffer, ShaderStages.Fragment)));
            _set = factory.CreateResourceSet(new ResourceSetDescription(layout, _camBuf, _lightBuf));

            _shaders = factory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, Encoding.UTF8.GetBytes(ShaderSources.ModelVert), "main"),
                new ShaderDescription(ShaderStages.Fragment, Encoding.UTF8.GetBytes(ShaderSources.ModelFrag), "main"));

            var vertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("Position", VertexElementSemantic.Position, VertexElementFormat.Float3),
                new VertexElementDescription("Normal", VertexElementSemantic.Normal, VertexElementFormat.Float3),
                new VertexElementDescription("Color", VertexElementSemantic.Color, VertexElementFormat.Float4));

            var blend = new BlendStateDescription(RgbaFloat.Black,
                BlendAttachmentDescription.OverrideBlend,
                BlendAttachmentDescription.OverrideBlend,
                BlendAttachmentDescription.OverrideBlend);

            _pipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = blend,
                DepthStencilState = DepthStencilStateDescription.DepthOnlyLessEqual,
                RasterizerState = new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.Clockwise, true, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { layout },
                ShaderSet = new ShaderSetDescription(new[] { vertexLayout }, _shaders),
                Outputs = modelOutputs,
            });
        }

        public void Draw(CommandList cl, DeviceBuffer vb, DeviceBuffer ib, int indexCount,
            Matrix4x4 viewProj, Matrix4x4 model, RenderResources res, PixelPostProcessSettings s)
        {
            // Upload transposed so GLSL column-vector math is natural (correct positions AND normals).
            var cam = new CamUbo { ViewProj = Matrix4x4.Transpose(viewProj), Model = Matrix4x4.Transpose(model) };
            cl.UpdateBuffer(_camBuf, 0, ref cam);
            var light = new LightUbo
            {
                Dir = new Vector4(Vector3.Normalize(s.LightDirection), 0f),
                Color = s.LightColor,
                Ambient = s.AmbientColor,
                Params = new Vector4(s.CelBands, 0, 0, 0),
            };
            cl.UpdateBuffer(_lightBuf, 0, ref light);

            cl.SetFramebuffer(res.ModelFB);
            cl.ClearColorTarget(0, new RgbaFloat(s.AmbientColor.X, s.AmbientColor.Y, s.AmbientColor.Z, 1f));
            cl.ClearColorTarget(1, new RgbaFloat(0.5f, 0.5f, 0.5f, 1f)); // encoded ~zero normal
            cl.ClearColorTarget(2, new RgbaFloat(1f, 1f, 1f, 1f));       // far depth
            cl.ClearDepthStencil(1f);

            cl.SetPipeline(_pipeline);
            cl.SetGraphicsResourceSet(0, _set);
            cl.SetVertexBuffer(0, vb);
            cl.SetIndexBuffer(ib, IndexFormat.UInt16);
            cl.DrawIndexed((uint)indexCount, 1, 0, 0, 0);
        }

        public void Dispose()
        {
            _pipeline.Dispose(); _set.Dispose();
            foreach (var sh in _shaders) sh.Dispose();
            _camBuf.Dispose(); _lightBuf.Dispose();
        }
    }
}
