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
        struct Ubo
        {
            public Matrix4x4 ViewProj; public Matrix4x4 Model;
            public Vector4 Dir; public Vector4 Color; public Vector4 Ambient; public Vector4 Params;
            public Vector4 Tint;
        }

        readonly GraphicsDevice _gd;
        readonly DeviceBuffer _ubo;
        readonly ResourceSet _set;
        readonly Pipeline _pipeline;
        readonly Shader[] _shaders;

        public ModelRenderer(GraphicsDevice gd, OutputDescription modelOutputs)
        {
            _gd = gd;
            var factory = gd.ResourceFactory;

            _ubo = factory.CreateBuffer(new BufferDescription(208, BufferUsage.UniformBuffer)); // 2 mat4 + 5 vec4

            var layout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("U", ResourceKind.UniformBuffer, ShaderStages.Vertex | ShaderStages.Fragment)));
            _set = factory.CreateResourceSet(new ResourceSetDescription(layout, _ubo));

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

        /// <summary>Bind + clear the model framebuffer once per frame, before drawing instances.</summary>
        public void BeginModelPass(CommandList cl, RenderResources res, PixelPostProcessSettings s)
        {
            cl.SetFramebuffer(res.ModelFB);
            // Metal MRT clear collapses to one value across attachments; clear all three to the background.
            // alpha 0 marks "background" for the starfield composite; the model writes alpha 1.
            var bg = new RgbaFloat(s.BackgroundColor.X, s.BackgroundColor.Y, s.BackgroundColor.Z, 0f);
            cl.ClearColorTarget(0, bg);
            cl.ClearColorTarget(1, bg);
            cl.ClearColorTarget(2, bg);
            cl.ClearDepthStencil(1f);
        }

        /// <summary>Draw one instance into the (already-bound, already-cleared) model pass.</summary>
        public void DrawInstance(CommandList cl, DeviceBuffer vb, DeviceBuffer ib, int indexCount,
            Matrix4x4 viewProj, Matrix4x4 model, PixelPostProcessSettings s, Vector4 tint)
        {
            // Upload the camera's view-projection as-is. (An earlier clip-Y flip here rendered the scene
            // vertically inverted — invisible on symmetric content but obvious on an asymmetric board — and
            // it also disagreed with IsoCamera3D.ScreenToGround picking, which uses the unflipped matrix.
            // Using viewProj directly makes the render right-side up AND consistent with picking.)
            var ubo = new Ubo
            {
                ViewProj = viewProj,
                Model = model,
                Dir = new Vector4(Vector3.Normalize(s.LightDirection), 0f),
                Color = s.LightColor,
                Ambient = s.AmbientColor,
                Params = new Vector4(s.CelBands, 0, 0, 0),
                Tint = tint,
            };
            cl.UpdateBuffer(_ubo, 0, ref ubo);
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
            _ubo.Dispose();
        }
    }
}
