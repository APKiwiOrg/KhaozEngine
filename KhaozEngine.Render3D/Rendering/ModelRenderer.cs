using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>Builds the model pipeline and draws the lit/cel glTF meshes into the low-res MRT via GPU
    /// instancing: one per-frame UBO upload (frame uniforms only) + one instance-buffer upload + one draw per
    /// UNIQUE mesh, each with the run's instanceCount.</summary>
    internal sealed class ModelRenderer : IDisposable
    {
        /// <summary>Maximum dynamic point lights consumed per frame. The host picks the N nearest (CPU-side
        /// budget); the renderer defensively clamps to this and zero-fills the unused tail. Must match the
        /// <c>[16]</c> array size declared in the std140 UBO block in BOTH ModelVert and ModelFrag.</summary>
        internal const int MaxPointLights = 16;

        // std140 UBO layout: a 176-byte header (the FrameUbo struct) followed by two vec4[MaxPointLights]
        // arrays (point light pos/radius, then colour/intensity). 176 + 2*256 = 688 bytes.
        const uint HeaderBytes = 176;
        const uint LightArrayBytes = MaxPointLights * 16;             // vec4 stride is 16 in std140
        const uint UboBytes = HeaderBytes + 2 * LightArrayBytes;      // 688

        /// <summary>Per-frame uniforms (binding 0) header. 1 mat4 + 7 vec4 = 64 + 112 = 176 bytes, uploaded at
        /// offset 0. Field order MUST exactly mirror the std140 UBO block in BOTH ModelVert and ModelFrag; the
        /// point-light arrays follow this header in the same buffer (see <see cref="MaxPointLights"/>).</summary>
        struct FrameUbo
        {
            public Matrix4x4 ViewProj;
            public Vector4 Dir; public Vector4 Color; public Vector4 Ambient; public Vector4 Params;
            public Vector4 FillDir; public Vector4 FillColor; public Vector4 CameraPos;
        }

        /// <summary>One dynamic point light, packed for the std140 UBO arrays: <see cref="PosRadius"/> is
        /// (worldX, worldY, worldZ, radius); <see cref="ColorIntensity"/> is (r, g, b, intensity).</summary>
        public struct PointLightData
        {
            public Vector4 PosRadius;
            public Vector4 ColorIntensity;
        }

        // Reused per-frame upload scratch (cleared/refilled, never realloc) for the two UBO light arrays.
        readonly Vector4[] _lightPosRadius = new Vector4[MaxPointLights];
        readonly Vector4[] _lightColorIntensity = new Vector4[MaxPointLights];

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
        readonly IGpuSampler _sampler;          // shared linear/wrap sampler (the device built-in, NOT owned here)
        readonly IGpuTexture _white;            // 1x1 white default; white*vColor*vTint == vColor*vTint (untextured invariant)
        readonly IGpuTexture _flatNormal;       // 1x1 (128,128,255): tangent-space (0,0,1); no-map normal default
        readonly IGpuTexture _defaultRough;     // 1x1 (0,0,0): roughness 0 (fully smooth); no-map spec default
        readonly IGpuResourceSet _defaultSet;   // UBO + white + flatNormal + defaultRough + sampler; bound for meshes with no material set
        readonly IGpuPipeline _pipeline;
        readonly IGpuShaderSet _shaders;

        // Splat-terrain pipeline (5-layer texture-array PBR, weights in vertex Color, triplanar). Shares _ubo
        // (frame uniforms) and the instance buffer; its own layout/sampler/shaders/pipeline.
        readonly IGpuResourceLayout _splatLayout;  // U (frame + params, one UBO) + AlbedoArray + NormalArray + Sampler
        readonly IGpuSampler _terrainSampler;   // wrap + anisotropic (trilinear fallback); OWNED here (dispose it)
        readonly IGpuShaderSet _splatShaders;
        readonly IGpuPipeline _splatPipeline;

        IGpuBuffer? _instanceBuffer;
        uint _instanceCapacity;          // capacity in instances
        // CPU-skinned path: skinned meshes are deformed on the CPU each frame and drawn through THIS no-bone model
        // pipeline (the bone-buffer GPU read corrupts past element 0 in the windowed Veldrid/Metal swapchain context;
        // CPU skinning + the proven-clean rigid path sidesteps it - see Scene3D's skinned block). One concatenated
        // transient vertex stream (all skinned draws' deformed verts) + a parallel per-draw instance stream, both
        // grown geometrically and retired like _instanceBuffer.
        IGpuBuffer? _skinnedVertexBuffer; uint _skinnedVertexCapacity;     // capacity in ModelVertex
        IGpuBuffer? _skinnedInstanceBuffer; uint _skinnedInstanceCapacity; // capacity in InstanceData
        // Instance buffers replaced by a grow are retired here (a prior in-flight frame may still read them);
        // disposed only in Dispose. Bounded by geometric growth.
        readonly List<IDisposable> _retired = new();

        public ModelRenderer(IGpuDevice gd, GpuOutputDescription modelOutputs)
        {
            _gd = gd;
            var factory = gd.Factory;

            _ubo = factory.CreateBuffer(new GpuBufferDescription(UboBytes, GpuBufferUsage.UniformBuffer)); // 176 header + 2 vec4[16] point-light arrays

            _layout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex | GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Albedo", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("NormalMap", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("RoughnessMap", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Sampler", GpuResourceKind.Sampler, GpuShaderStages.Fragment)));

            // Use the device's built-in linear sampler (wrap-addressed) - the SAME one Render2D samples its
            // textures (incl. a 1x1 white) through, which verifies correctly on D3D11/WARP. A custom
            // CreateSampler here (MinLinearMagLinearMipLinear, maxLod=uint.MaxValue) instead sampled the 1x1 white
            // default as < 1.0 on D3D11 - uniformly darkening untextured meshes (the golden net caught it on the
            // green box) while Metal clamped fine. Built-in sampler is non-owning, so it is NOT disposed here.
            _sampler = gd.LinearSampler;

            // 1x1 white default texture: an untextured mesh samples (1,1,1), so albedo == vColor*vTint and every
            // existing scene renders pixel-identical (the safety invariant).
            _white = factory.CreateTexture(GpuTextureDescription.Texture2D(
                1, 1, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            gd.UpdateTexture(_white, new byte[] { 255, 255, 255, 255 }, 0, 0, 1, 1);

            // No-map defaults. Flat normal (0,0,1 in tangent space) and zero roughness reproduce today's
            // geometric-normal lighting and per-instance specular exactly, so untextured meshes are unchanged.
            _flatNormal = factory.CreateTexture(GpuTextureDescription.Texture2D(
                1, 1, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            gd.UpdateTexture(_flatNormal, DefaultMaps.FlatNormalTexel(), 0, 0, 1, 1);
            _defaultRough = factory.CreateTexture(GpuTextureDescription.Texture2D(
                1, 1, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            gd.UpdateTexture(_defaultRough, DefaultMaps.ZeroRoughnessTexel(), 0, 0, 1, 1);

            _defaultSet = factory.CreateResourceSet(new GpuResourceSetDescription(_layout, _ubo, _white, _flatNormal, _defaultRough, _sampler));

            _shaders = factory.CreateShadersFromSpirv(ShaderSources.ModelVert, ShaderSources.ModelFrag);

            // Slot 0: per-vertex geometry (locations 0..4).
            var vertexLayout = new GpuVertexLayoutDescription(
                new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Normal", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Color", GpuVertexElementFormat.Float4),
                new GpuVertexElement("TexCoord", GpuVertexElementFormat.Float2),
                new GpuVertexElement("Tangent", GpuVertexElementFormat.Float4));

            // Slot 1: per-instance data (locations 5..11), one step per instance. SPIRV binds these by location
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

            // ONE descriptor set, ONE uniform buffer: the splat material's combined UBO carries the frame uniforms
            // (re-synced each frame, see WriteFrameUniformsTo) PLUS the per-material splat params appended at offset
            // UboBytes (see SplatVert/SplatFrag). Metal (via Veldrid/SPIRV-Cross) mis-binds a SECOND uniform buffer
            // in a pipeline (the second reads the first buffer's bytes), which zeroed the per-layer tint and blacked
            // out the terrain; folding the params into the one frame UBO matches the model pass's proven shape
            // (1 UBO + textures + sampler).
            _splatLayout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex | GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("AlbedoArray", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("NormalArray", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Sampler", GpuResourceKind.Sampler, GpuShaderStages.Fragment)));

            // Tileable detail textures REPEAT across the world, so wrap addressing; anisotropic for grazing ground
            // (CreateSampler falls back to trilinear when the backend lacks anisotropy).
            _terrainSampler = factory.CreateSampler(new GpuSamplerDescription(
                GpuSamplerFilter.Anisotropic, GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap, maximumAnisotropy: 8));

            _splatShaders = factory.CreateShadersFromSpirv(ShaderSources.SplatVert, ShaderSources.SplatFrag);

            _splatPipeline = factory.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend, GpuBlendAttachment.OverrideBlend, GpuBlendAttachment.OverrideBlend },
                DepthStencil = GpuDepthStencilState.DepthOnlyLessEqual,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _splatLayout },
                ShaderSet = _splatShaders,
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
            var bg = s.BackgroundColor.WithAlpha(0f);
            cl.ClearColorTarget(0, bg);
            cl.ClearColorTarget(1, bg);
            cl.ClearColorTarget(2, bg);
            cl.ClearDepthStencil(1f);
        }

        /// <summary>Upload the per-frame uniforms once per frame, before the instanced draws. <paramref name="lights"/>
        /// is the host's per-frame point-light list; it is clamped to <see cref="MaxPointLights"/> (the host is
        /// responsible for picking the N nearest) and the active count is written into <c>Params.y</c>. An empty
        /// span leaves the shader's point-light loop unentered, so the render is bit-identical to the key+fill path.</summary>
        public void SetFrameUniforms(IGpuCommandList cl, Matrix4x4 viewProj, Vector3 cameraPos,
            PixelPostProcessSettings s, ReadOnlySpan<PointLightData> lights)
        {
            int count = BuildLightArrays(lights, _lightPosRadius, _lightColorIntensity);

            // Clip-space-Y correction is derived from the live backend (GpuClip), not baked for Metal: it is the
            // identity on Metal/D3D (byte-identical render) and flips clip-Y on inverted-Y backends (Vulkan).
            // Applied only to the GPU-uploaded matrix; IsoCamera3D.ScreenToGround picking keeps the raw
            // Camera.ViewProjection, so render and picking stay consistent (an earlier unconditional flip broke both).
            _frame = new FrameUbo
            {
                ViewProj = GpuClip.Correct(viewProj, _gd.Capabilities),
                Dir = new Vector4(Vector3.Normalize(s.LightDirection), 0f),
                Color = s.LightColor,
                Ambient = s.AmbientColor,
                Params = new Vector4(s.CelBands, count, 0, 0),
                FillDir = new Vector4(Vector3.Normalize(s.FillLightDirection), 0f),
                FillColor = s.FillLightColor,
                CameraPos = new Vector4(cameraPos, 1f),
            };
            WriteFrameUniformsTo(cl, _ubo);
        }

        // Cached this-frame uniforms (set in SetFrameUniforms), re-uploaded into each splat material's combined UBO
        // by WriteFrameUniformsTo so the splat pipeline reads the current frame from its own single UBO.
        FrameUbo _frame;

        /// <summary>Upload the cached frame uniforms (header + the two point-light arrays) into <paramref name="dst"/>
        /// at offset 0. <paramref name="dst"/> must be at least <see cref="UboBytes"/> bytes; a splat material's
        /// combined UBO is larger (params follow at <see cref="UboBytes"/>) and that tail is left untouched.</summary>
        public void WriteFrameUniformsTo(IGpuCommandList cl, IGpuBuffer dst)
        {
            cl.UpdateBuffer(dst, 0, in _frame);
            // Point-light arrays follow the 176-byte header. Always upload the full fixed-size arrays (zero-filled
            // tail) so a previous frame's lights never leak past the active count.
            cl.UpdateBuffer(dst, HeaderBytes, (ReadOnlySpan<Vector4>)_lightPosRadius);
            cl.UpdateBuffer(dst, HeaderBytes + LightArrayBytes, (ReadOnlySpan<Vector4>)_lightColorIntensity);
        }

        /// <summary>Pure, headless-testable packing of the host light list into the two fixed-size UBO arrays:
        /// copies up to <see cref="MaxPointLights"/> lights (extras are dropped - the host selects the N nearest),
        /// zero-fills the remaining tail, and returns the active count. Both output arrays must be length
        /// <see cref="MaxPointLights"/>.</summary>
        internal static int BuildLightArrays(ReadOnlySpan<PointLightData> lights, Vector4[] posRadius, Vector4[] colorIntensity)
        {
            int count = Math.Min(lights.Length, MaxPointLights);
            for (int i = 0; i < count; i++)
            {
                posRadius[i] = lights[i].PosRadius;
                colorIntensity[i] = lights[i].ColorIntensity;
            }
            for (int i = count; i < MaxPointLights; i++)
            {
                posRadius[i] = Vector4.Zero;
                colorIntensity[i] = Vector4.Zero;
            }
            return count;
        }

        /// <summary>
        /// Bind the pipeline + resource set once for the model pass. Invariant across instances within a pass.
        /// Call after <see cref="BeginModelPass"/>/<see cref="SetFrameUniforms"/> and before the draw loop.
        /// </summary>
        public void BindPass(IGpuCommandList cl)
        {
            cl.SetPipeline(_pipeline);
            // The material resource set (UBO + albedo + normal + roughness + sampler) is bound per mesh in
            // DrawMeshInstanced, because the textures vary per mesh; the shared UBO is part of each set, so it
            // still sees fresh per-frame uniforms uploaded into _ubo.
        }

        /// <summary>Build a per-mesh material resource set binding <paramref name="albedo"/> (white default
        /// when null), <paramref name="normal"/> (flat-normal default when null), and
        /// <paramref name="roughness"/> (zero-roughness default when null), plus the shared frame UBO and
        /// sampler. Owned by the caller (Scene3D) and disposed when the mesh unloads. Passing only an albedo
        /// reproduces the pre-PBR single-texture material exactly.</summary>
        public IGpuResourceSet CreateMaterialSet(IGpuTexture? albedo = null, IGpuTexture? normal = null, IGpuTexture? roughness = null) =>
            _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(
                _layout, _ubo, albedo ?? _white, normal ?? _flatNormal, roughness ?? _defaultRough, _sampler));

        /// <summary>Create a splat material's combined UBO: <see cref="UboBytes"/> of frame uniforms (re-synced each
        /// frame via <see cref="WriteFrameUniformsTo"/>) followed by the per-material <paramref name="data"/> at
        /// offset <see cref="UboBytes"/>. One uniform buffer holds both, so the splat pipeline binds a single UBO
        /// (see SplatVert/SplatFrag). Owned by Scene3D; shared by every chunk using this material.</summary>
        public IGpuBuffer CreateSplatParamsUbo(in SplatParamsData data)
        {
            var ubo = _gd.Factory.CreateBuffer(new GpuBufferDescription(UboBytes + SplatParamsData.SizeInBytes, GpuBufferUsage.UniformBuffer));
            _gd.UpdateBuffer(ubo, UboBytes, in data);
            return ubo;
        }

        /// <summary>Build a splat-terrain material resource set: the combined frame+params UBO + the two 5-layer
        /// texture arrays (albedo, tangent-space normal) + the terrain (wrap/anisotropic) sampler. Shared across
        /// every chunk using this material; owned by Scene3D, NOT per mesh.</summary>
        public IGpuResourceSet CreateSplatMaterialSet(IGpuBuffer combinedUbo, IGpuTexture albedoArray, IGpuTexture normalArray) =>
            _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(
                _splatLayout, combinedUbo, albedoArray, normalArray, _terrainSampler));

        /// <summary>Bind the splat-terrain pipeline for the splat pass (call once before the splat draw loop). Each
        /// material's combined UBO must already hold this frame's uniforms (<see cref="WriteFrameUniformsTo"/>).</summary>
        public void BindSplatPass(IGpuCommandList cl) => cl.SetPipeline(_splatPipeline);

        /// <summary>Draw one splat-terrain mesh run through the splat pipeline, reusing the shared instance buffer
        /// (terrain instances are identity-transform, white-tint). <paramref name="splatSet"/> carries the material's
        /// combined UBO + texture arrays + sampler. <see cref="BindSplatPass"/> must be bound.</summary>
        public void DrawSplatMeshInstanced(IGpuCommandList cl, IGpuBuffer vb, IGpuBuffer ib, int indexCount,
            GpuIndexFormat indexFormat, uint instanceStart, uint instanceCount, IGpuResourceSet splatSet)
        {
            cl.SetGraphicsResourceSet(0, splatSet);
            cl.SetVertexBuffer(0, vb);
            cl.SetVertexBuffer(1, _instanceBuffer!);
            cl.SetIndexBuffer(ib, indexFormat);
            cl.DrawIndexed((uint)indexCount, instanceCount, 0, 0, instanceStart);
        }

        /// <summary>Ensure the persistent instance buffer holds at least <paramref name="instances"/>.Length
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
            // Retire (don't dispose inline): a prior frame's command list may still be reading the old buffer on
            // the GPU when this frame grows; disposing it then is a use-after-free. Geometric growth bounds the
            // retired count; freed in Dispose.
            if (_instanceBuffer != null) _retired.Add(_instanceBuffer);
            _instanceCapacity = Math.Max(instanceCount, _instanceCapacity == 0 ? 64u : _instanceCapacity * 2);
            _instanceBuffer = _gd.Factory.CreateBuffer(
                new GpuBufferDescription(_instanceCapacity * InstanceData.SizeInBytes, GpuBufferUsage.VertexBuffer));
        }

        /// <summary>Draw one mesh's run: <paramref name="instanceCount"/> instances starting at
        /// <paramref name="instanceStart"/> in the shared instance buffer. The pipeline + frame UBO must already be
        /// bound (<see cref="BindPass"/>/<see cref="SetFrameUniforms"/>). Binds <paramref name="materialSet"/> (or
        /// the white default when null) as resource set 0 before drawing, so each mesh can carry its own albedo.</summary>
        public void DrawMeshInstanced(IGpuCommandList cl, IGpuBuffer vb, IGpuBuffer ib, int indexCount,
            GpuIndexFormat indexFormat, uint instanceStart, uint instanceCount, IGpuResourceSet? materialSet = null)
        {
            cl.SetGraphicsResourceSet(0, materialSet ?? _defaultSet);
            cl.SetVertexBuffer(0, vb);
            cl.SetVertexBuffer(1, _instanceBuffer!);
            cl.SetIndexBuffer(ib, indexFormat);
            cl.DrawIndexed((uint)indexCount, instanceCount, 0, 0, instanceStart);
        }

        /// <summary>Upload this frame's CPU-skinned geometry: <paramref name="verts"/> is every skinned draw's
        /// deformed vertices concatenated; <paramref name="instances"/> is one <see cref="InstanceData"/> per draw
        /// (its world transform / tint / material), parallel to the draw order. Both buffers grow geometrically and
        /// retire (not dispose) the replaced buffer, matching the instance-buffer lifetime rule.</summary>
        public void UploadCpuSkinned(IGpuCommandList cl, ReadOnlySpan<ModelVertex> verts, ReadOnlySpan<InstanceData> instances)
        {
            if (verts.Length == 0 || instances.Length == 0) return;
            if (_skinnedVertexBuffer == null || _skinnedVertexCapacity < (uint)verts.Length)
            {
                if (_skinnedVertexBuffer != null) _retired.Add(_skinnedVertexBuffer);
                _skinnedVertexCapacity = Math.Max((uint)verts.Length, _skinnedVertexCapacity == 0 ? 4096u : _skinnedVertexCapacity * 2);
                _skinnedVertexBuffer = _gd.Factory.CreateBuffer(
                    new GpuBufferDescription(_skinnedVertexCapacity * ModelVertex.SizeInBytes, GpuBufferUsage.VertexBuffer));
            }
            if (_skinnedInstanceBuffer == null || _skinnedInstanceCapacity < (uint)instances.Length)
            {
                if (_skinnedInstanceBuffer != null) _retired.Add(_skinnedInstanceBuffer);
                _skinnedInstanceCapacity = Math.Max((uint)instances.Length, _skinnedInstanceCapacity == 0 ? 64u : _skinnedInstanceCapacity * 2);
                _skinnedInstanceBuffer = _gd.Factory.CreateBuffer(
                    new GpuBufferDescription(_skinnedInstanceCapacity * InstanceData.SizeInBytes, GpuBufferUsage.VertexBuffer));
            }
            cl.UpdateBuffer(_skinnedVertexBuffer!, 0, verts);
            cl.UpdateBuffer(_skinnedInstanceBuffer!, 0, instances);
        }

        /// <summary>Draw one CPU-skinned mesh through the model pipeline: its deformed vertices live at
        /// <paramref name="baseVertex"/>.. in the shared skinned vertex buffer (added per index via the draw's
        /// vertexOffset), and its instance data is element <paramref name="drawIndex"/> of the skinned instance
        /// buffer (selected by instanceStart). One <c>instanceCount=1</c> draw. <see cref="BindPass"/> +
        /// <see cref="SetFrameUniforms"/> must already be bound (the rigid pass shares the frame UBO).</summary>
        public void DrawCpuSkinned(IGpuCommandList cl, IGpuBuffer ib, int indexCount, GpuIndexFormat indexFormat, int baseVertex, uint drawIndex, IGpuResourceSet? materialSet)
        {
            cl.SetGraphicsResourceSet(0, materialSet ?? _defaultSet);
            cl.SetVertexBuffer(0, _skinnedVertexBuffer!);
            cl.SetVertexBuffer(1, _skinnedInstanceBuffer!);
            cl.SetIndexBuffer(ib, indexFormat);
            cl.DrawIndexed((uint)indexCount, 1, 0, baseVertex, drawIndex);
        }

        public void Dispose()
        {
            _pipeline.Dispose(); _defaultSet.Dispose(); _layout.Dispose();
            _white.Dispose(); _flatNormal.Dispose(); _defaultRough.Dispose(); // _sampler is the device built-in (non-owning); do not dispose it.
            _shaders.Dispose();
            _splatPipeline.Dispose(); _splatLayout.Dispose(); _splatShaders.Dispose(); _terrainSampler.Dispose();
            _ubo.Dispose();
            _instanceBuffer?.Dispose();
            _skinnedVertexBuffer?.Dispose();
            _skinnedInstanceBuffer?.Dispose();
            foreach (var r in _retired) r.Dispose();
            _retired.Clear();
        }
    }
}
