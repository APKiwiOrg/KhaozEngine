using System;
using System.Numerics;
using System.Text;
using Veldrid;
using Veldrid.SPIRV;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>Builds the model pipeline and draws the lit/cel glTF meshes into the low-res MRT via GPU
    /// instancing: one per-frame UBO upload (frame uniforms only) + one instance-buffer upload + one draw per
    /// UNIQUE mesh, each with the run's instanceCount.</summary>
    internal sealed class ModelRenderer : IDisposable
    {
        /// <summary>Per-frame uniforms (binding 0). 1 mat4 + 7 vec4 = 64 + 112 = 176 bytes. Field order MUST
        /// exactly mirror the std140 UBO block in BOTH ModelVert and ModelFrag.</summary>
        struct FrameUbo
        {
            public Matrix4x4 ViewProj;
            public Vector4 Dir; public Vector4 Color; public Vector4 Ambient; public Vector4 Params;
            public Vector4 FillDir; public Vector4 FillColor; public Vector4 CameraPos;
        }

        /// <summary>Per-instance vertex stream (buffer slot 1, instanceStepRate 1). 64 + 48 = 112 bytes. The
        /// Model matrix is a System.Numerics Matrix4x4 (row-major), read in the shader as four Float4 rows.</summary>
        public struct InstanceData
        {
            public Matrix4x4 Model;       // 64 bytes (4 rows -> 4 Float4 instance attributes)
            public Vector4 Tint;          // 16
            public Vector4 Emissive;      // 16
            public Vector4 SpecParams;    // 16 (x = strength, y = shininess)
            public const uint SizeInBytes = 112;
        }

        readonly GraphicsDevice _gd;
        readonly DeviceBuffer _ubo;
        readonly ResourceLayout _layout;
        readonly ResourceSet _set;
        readonly Pipeline _pipeline;
        readonly Shader[] _shaders;

        DeviceBuffer? _instanceBuffer;
        uint _instanceCapacity;          // capacity in instances

        public ModelRenderer(GraphicsDevice gd, OutputDescription modelOutputs)
        {
            _gd = gd;
            var factory = gd.ResourceFactory;

            _ubo = factory.CreateBuffer(new BufferDescription(176, BufferUsage.UniformBuffer)); // 1 mat4 + 7 vec4

            _layout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("U", ResourceKind.UniformBuffer, ShaderStages.Vertex | ShaderStages.Fragment)));
            _set = factory.CreateResourceSet(new ResourceSetDescription(_layout, _ubo));

            _shaders = factory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, Encoding.UTF8.GetBytes(ShaderSources.ModelVert), "main"),
                new ShaderDescription(ShaderStages.Fragment, Encoding.UTF8.GetBytes(ShaderSources.ModelFrag), "main"));

            // Slot 0: per-vertex geometry (locations 0..3).
            var vertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("Position", VertexElementSemantic.Position, VertexElementFormat.Float3),
                new VertexElementDescription("Normal", VertexElementSemantic.Normal, VertexElementFormat.Float3),
                new VertexElementDescription("Color", VertexElementSemantic.Color, VertexElementFormat.Float4),
                new VertexElementDescription("TexCoord", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2));

            // Slot 1: per-instance data (locations 4..10), one step per instance. Semantics are TextureCoordinate
            // throughout; SPIRV binds these by location order, so the names/semantics are placeholders.
            var instanceLayout = new VertexLayoutDescription(
                stride: InstanceData.SizeInBytes,
                instanceStepRate: 1,
                elements: new[]
                {
                    new VertexElementDescription("IModel0", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4),
                    new VertexElementDescription("IModel1", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4),
                    new VertexElementDescription("IModel2", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4),
                    new VertexElementDescription("IModel3", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4),
                    new VertexElementDescription("ITint", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4),
                    new VertexElementDescription("IEmissive", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4),
                    new VertexElementDescription("ISpecParams", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4),
                });

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
                ResourceLayouts = new[] { _layout },
                ShaderSet = new ShaderSetDescription(new[] { vertexLayout, instanceLayout }, _shaders),
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

        /// <summary>Upload the per-frame uniforms once per frame, before the instanced draws.</summary>
        public void SetFrameUniforms(CommandList cl, Matrix4x4 viewProj, Vector3 cameraPos, PixelPostProcessSettings s)
        {
            // Upload the camera's view-projection as-is. (An earlier clip-Y flip here rendered the scene
            // vertically inverted — invisible on symmetric content but obvious on an asymmetric board — and
            // it also disagreed with IsoCamera3D.ScreenToGround picking, which uses the unflipped matrix.
            // Using viewProj directly makes the render right-side up AND consistent with picking.)
            // TODO Phase 3c: when a non-Metal backend lands, derive any clip-Y/depth compensation from
            // GpuCapabilities (ClipSpaceYInverted/DepthRangeZeroToOne) rather than the baked Metal assumption.
            var ubo = new FrameUbo
            {
                ViewProj = viewProj,
                Dir = new Vector4(Vector3.Normalize(s.LightDirection), 0f),
                Color = s.LightColor,
                Ambient = s.AmbientColor,
                Params = new Vector4(s.CelBands, 0, 0, 0),
                FillDir = new Vector4(Vector3.Normalize(s.FillLightDirection), 0f),
                FillColor = s.FillLightColor,
                CameraPos = new Vector4(cameraPos, 1f),
            };
            cl.UpdateBuffer(_ubo, 0, ref ubo);
        }

        /// <summary>
        /// Bind the pipeline + resource set once for the model pass. Invariant across instances within a pass.
        /// Call after <see cref="BeginModelPass"/>/<see cref="SetFrameUniforms"/> and before the draw loop.
        /// </summary>
        public void BindPass(CommandList cl)
        {
            cl.SetPipeline(_pipeline);
            cl.SetGraphicsResourceSet(0, _set);
        }

        /// <summary>Ensure the persistent instance buffer holds at least <paramref name="instanceCount"/>
        /// instances, then upload <paramref name="instances"/> starting at offset 0. Geometric 2x growth.</summary>
        public void UploadInstances(CommandList cl, ReadOnlySpan<InstanceData> instances)
        {
            if (instances.Length == 0) return;
            EnsureInstanceCapacity((uint)instances.Length);
            cl.UpdateBuffer(_instanceBuffer, 0, instances);
        }

        void EnsureInstanceCapacity(uint instanceCount)
        {
            if (_instanceBuffer != null && _instanceCapacity >= instanceCount) return;
            _instanceBuffer?.Dispose();
            _instanceCapacity = Math.Max(instanceCount, _instanceCapacity == 0 ? 64u : _instanceCapacity * 2);
            _instanceBuffer = _gd.ResourceFactory.CreateBuffer(
                new BufferDescription(_instanceCapacity * InstanceData.SizeInBytes, BufferUsage.VertexBuffer));
        }

        /// <summary>Draw one mesh's run: <paramref name="instanceCount"/> instances starting at
        /// <paramref name="instanceStart"/> in the shared instance buffer. The pipeline + resource set + frame UBO
        /// must already be bound (<see cref="BindPass"/>/<see cref="SetFrameUniforms"/>).</summary>
        public void DrawMeshInstanced(CommandList cl, DeviceBuffer vb, DeviceBuffer ib, int indexCount,
            uint instanceStart, uint instanceCount)
        {
            cl.SetVertexBuffer(0, vb);
            cl.SetVertexBuffer(1, _instanceBuffer);
            cl.SetIndexBuffer(ib, IndexFormat.UInt16);
            cl.DrawIndexed((uint)indexCount, instanceCount, 0, 0, instanceStart);
        }

        public void Dispose()
        {
            _pipeline.Dispose(); _set.Dispose(); _layout.Dispose();
            foreach (var sh in _shaders) sh.Dispose();
            _ubo.Dispose();
            _instanceBuffer?.Dispose();
        }
    }
}
