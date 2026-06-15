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

            _ubo = factory.CreateBuffer(new BufferDescription(192, BufferUsage.UniformBuffer)); // 2 mat4 + 4 vec4

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

        public void Draw(CommandList cl, DeviceBuffer vb, DeviceBuffer ib, int indexCount,
            Matrix4x4 viewProj, Matrix4x4 model, RenderResources res, PixelPostProcessSettings s)
        {
            // Upload row-major directly: Veldrid's reinterpret into a column-major GLSL mat4 already
            // transposes, so `ViewProj * Model * pos` in the shader is the correct column-vector result.
            // Rendering into a texture on the Metal backend lands Y-flipped vs. the camera's standard
            // (OpenGL-style, unit-tested) clip space, so flip clip Y here so world-up maps to image-top.
            // Presentation-layer correction only; camera math is untouched. TODO: gate per-backend when a
            // non-Metal backend is added (Metal reports IsClipSpaceYInverted=false yet still needs this).
            var yFlip = Matrix4x4.Identity; yFlip.M22 = -1f;
            var ubo = new Ubo
            {
                ViewProj = viewProj * yFlip,
                Model = model,
                Dir = new Vector4(Vector3.Normalize(s.LightDirection), 0f),
                Color = s.LightColor,
                Ambient = s.AmbientColor,
                Params = new Vector4(s.CelBands, 0, 0, 0),
            };
            cl.UpdateBuffer(_ubo, 0, ref ubo);

            cl.SetFramebuffer(res.ModelFB);
            // Veldrid's Metal MRT clear collapses to a single value across all attachments, so clear all
            // three to the background. The background ends up uniform in the normal/depth targets too, which
            // keeps the depth-driven silhouette clean (background depth ~= bg.r << sphere depth) and avoids
            // spurious edges in empty space.
            // alpha 0 marks "background" for the starfield composite; the model writes alpha 1.
            var bg = new RgbaFloat(s.BackgroundColor.X, s.BackgroundColor.Y, s.BackgroundColor.Z, 0f);
            cl.ClearColorTarget(0, bg);
            cl.ClearColorTarget(1, bg);
            cl.ClearColorTarget(2, bg);
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
            _ubo.Dispose();
        }
    }
}
