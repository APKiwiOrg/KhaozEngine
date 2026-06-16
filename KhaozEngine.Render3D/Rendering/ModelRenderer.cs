using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
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

        readonly IGpuDevice _gd;
        readonly IGpuBuffer _ubo;
        readonly IGpuResourceLayout _layout;
        readonly IGpuResourceSet _set;
        readonly IGpuPipeline _pipeline;
        readonly IGpuShaderSet _shaders;

        IGpuBuffer? _instanceBuffer;
        uint _instanceCapacity;          // capacity in instances

        public ModelRenderer(IGpuDevice gd, GpuOutputDescription modelOutputs)
        {
            _gd = gd;
            var factory = gd.Factory;

            _ubo = factory.CreateBuffer(new GpuBufferDescription(176, GpuBufferUsage.UniformBuffer)); // 1 mat4 + 7 vec4

            _layout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex | GpuShaderStages.Fragment)));
            _set = factory.CreateResourceSet(new GpuResourceSetDescription(_layout, _ubo));

            _shaders = factory.CreateShadersFromSpirv(ShaderSources.ModelVert, ShaderSources.ModelFrag);

            // Slot 0: per-vertex geometry (locations 0..3).
            var vertexLayout = new GpuVertexLayoutDescription(
                new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Normal", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Color", GpuVertexElementFormat.Float4),
                new GpuVertexElement("TexCoord", GpuVertexElementFormat.Float2));

            // Slot 1: per-instance data (locations 4..10), one step per instance. SPIRV binds these by location
            // order, so the names are placeholders.
            var instanceLayout = new GpuVertexLayoutDescription(
                stride: InstanceData.SizeInBytes,
                instanceStepRate: 1,
                elements: new[]
                {
                    new GpuVertexElement("IModel0", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("IModel1", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("IModel2", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("IModel3", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("ITint", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("IEmissive", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("ISpecParams", GpuVertexElementFormat.Float4),
                });

            _pipeline = factory.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[]
                {
                    GpuBlendAttachment.OverrideBlend,
                    GpuBlendAttachment.OverrideBlend,
                    GpuBlendAttachment.OverrideBlend,
                },
                DepthStencil = GpuDepthStencilState.DepthOnlyLessEqual,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = _shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout, instanceLayout },
                Outputs = modelOutputs,
            });
        }

        /// <summary>Bind + clear the model framebuffer once per frame, before drawing instances.</summary>
        public void BeginModelPass(IGpuCommandList cl, RenderResources res, PixelPostProcessSettings s)
        {
            cl.SetFramebuffer(res.ModelFB);
            // Metal MRT clear collapses to one value across attachments; clear all three to the background.
            // alpha 0 marks "background" for the starfield composite; the model writes alpha 1.
            var bg = new Vector4(s.BackgroundColor.X, s.BackgroundColor.Y, s.BackgroundColor.Z, 0f);
            cl.ClearColorTarget(0, bg);
            cl.ClearColorTarget(1, bg);
            cl.ClearColorTarget(2, bg);
            cl.ClearDepthStencil(1f);
        }

        /// <summary>Upload the per-frame uniforms once per frame, before the instanced draws.</summary>
        public void SetFrameUniforms(IGpuCommandList cl, Matrix4x4 viewProj, Vector3 cameraPos, PixelPostProcessSettings s)
        {
            // Upload the camera's view-projection as-is. (An earlier clip-Y flip here rendered the scene
            // vertically inverted — invisible on symmetric content but obvious on an asymmetric board — and
            // it also disagreed with IsoCamera3D.ScreenToGround picking, which uses the unflipped matrix.
            // Using viewProj directly makes the render right-side up AND consistent with picking.)
            // TODO cross-platform bring-up: when a non-Metal backend lands, derive any clip-Y/depth compensation
            // from GpuCapabilities (ClipSpaceYInverted/DepthRangeZeroToOne) rather than the baked Metal assumption.
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
            cl.UpdateBuffer(_ubo, 0, in ubo);
        }

        /// <summary>
        /// Bind the pipeline + resource set once for the model pass. Invariant across instances within a pass.
        /// Call after <see cref="BeginModelPass"/>/<see cref="SetFrameUniforms"/> and before the draw loop.
        /// </summary>
        public void BindPass(IGpuCommandList cl)
        {
            cl.SetPipeline(_pipeline);
            cl.SetGraphicsResourceSet(0, _set);
        }

        /// <summary>Ensure the persistent instance buffer holds at least <paramref name="instanceCount"/>
        /// instances, then upload <paramref name="instances"/> starting at offset 0. Geometric 2x growth.</summary>
        public void UploadInstances(IGpuCommandList cl, ReadOnlySpan<InstanceData> instances)
        {
            if (instances.Length == 0) return;
            EnsureInstanceCapacity((uint)instances.Length);
            cl.UpdateBuffer(_instanceBuffer!, 0, instances);
        }

        void EnsureInstanceCapacity(uint instanceCount)
        {
            if (_instanceBuffer != null && _instanceCapacity >= instanceCount) return;
            _instanceBuffer?.Dispose();
            _instanceCapacity = Math.Max(instanceCount, _instanceCapacity == 0 ? 64u : _instanceCapacity * 2);
            _instanceBuffer = _gd.Factory.CreateBuffer(
                new GpuBufferDescription(_instanceCapacity * InstanceData.SizeInBytes, GpuBufferUsage.VertexBuffer));
        }

        /// <summary>Draw one mesh's run: <paramref name="instanceCount"/> instances starting at
        /// <paramref name="instanceStart"/> in the shared instance buffer. The pipeline + resource set + frame UBO
        /// must already be bound (<see cref="BindPass"/>/<see cref="SetFrameUniforms"/>).</summary>
        public void DrawMeshInstanced(IGpuCommandList cl, IGpuBuffer vb, IGpuBuffer ib, int indexCount,
            uint instanceStart, uint instanceCount)
        {
            cl.SetVertexBuffer(0, vb);
            cl.SetVertexBuffer(1, _instanceBuffer!);
            cl.SetIndexBuffer(ib, GpuIndexFormat.UInt16);
            cl.DrawIndexed((uint)indexCount, instanceCount, 0, 0, instanceStart);
        }

        public void Dispose()
        {
            _pipeline.Dispose(); _set.Dispose(); _layout.Dispose();
            _shaders.Dispose();
            _ubo.Dispose();
            _instanceBuffer?.Dispose();
        }
    }
}
