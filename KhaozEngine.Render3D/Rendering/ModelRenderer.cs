using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>Builds the model pipeline and draws the lit/cel glTF meshes into the low-res MRT via GPU
    /// instancing: one per-frame UBO upload (frame uniforms only) + one instance-buffer upload + one draw per
    /// UNIQUE mesh, each with the run's instanceCount.</summary>
    internal sealed partial class ModelRenderer : IDisposable
    {
        /// <summary>Maximum dynamic point lights consumed per frame. The host picks the N nearest (CPU-side
        /// budget); the renderer defensively clamps to this and zero-fills the unused tail. Must match the
        /// <c>[16]</c> array size declared in the std140 UBO block in BOTH ModelVert and ModelFrag.</summary>
        internal const int MaxPointLights = 16;

        /// <summary>Maximum cascaded shadow maps (matches <see cref="ShadowSettings.MaxCascades"/> and the
        /// fixed-size <c>ShadowMat[4]</c> arrays in the frame-UBO shadow tail). The atlas holds up to this many
        /// side-by-side cascade columns.</summary>
        internal const int MaxCascades = 4;

        // std140 UBO layout: a 176-byte header (the FrameUbo struct) followed by two vec4[MaxPointLights]
        // arrays (point light pos/radius, then colour/intensity) = 176 + 2*256 = 688, then the cascaded shadow tail
        // (MaxCascades light-clip matrices + params) = mat4[4] (256) + 3*vec4 (48) = 304, so 688 + 304 = 992, then
        // the render-origin vec4 = 1008 bytes.
        // (internal so UboLayoutTests can assert these against Marshal.SizeOf/OffsetOf and the GLSL block.)
        internal const uint HeaderBytes = 176;
        internal const uint LightArrayBytes = MaxPointLights * 16;    // vec4 stride is 16 in std140
        internal const uint LightArraysBytes = 2 * LightArrayBytes;   // both point-light arrays = 512
        // The cascaded shadow tail (mat4 ShadowMat[4] + vec4 ShadowParams + vec4 ShadowParams2 + vec4 ShadowNormalOffsets)
        // rides in the SAME frame UBO after the light arrays, so every pass that binds this block reads the cascade
        // atlas out of it. It went there because a second uniform buffer in a set mis-bound on the retired Veldrid
        // Metal backend, and it STAYS there because it is genuinely per-frame data the shadow-receiving passes all
        // want, which is the half #604 does not change.
        // ShadowParams = (cascadeCount, strength[0=inactive], constBias, slopeBias). ShadowParams2 = (texelStep =
        // 1/perCascadeResolution, maxDistance, borderFrac, cascadeBlendFrac), ShadowNormalOffsets = per-cascade
        // normal-offset world size (texel-world-size_i x ShadowNormalOffset, CPU-baked so it is extent-aware per cascade).
        internal const uint ShadowTailBytes = (uint)MaxCascades * 64 + 48;     // mat4[4] + 3*vec4 = 304
        internal const uint ShadowTailOffset = HeaderBytes + LightArraysBytes;  // 688
        // Camera-relative rendering (design doc 2026-07-27, section 9): the render origin every GPU-bound world
        // position this frame was reduced by, so a fragment can reconstruct the ABSOLUTE position for world-anchored
        // texturing and noise (terrain triplanar UVs, the model dissolve pattern). Lighting, eye vectors and depth
        // stay render-relative: those are differences and the origin cancels. Per-frame like the shadow tail, so it
        // rides at the END of the same block. It is the LAST member of the frame block now that the splat params
        // have their own buffer (#604), which is why nothing follows it here.
        internal const uint RenderOriginBytes = 16;                                            // one vec4, w unused
        internal const uint RenderOriginOffset = ShadowTailOffset + ShadowTailBytes;           // 992
        internal const uint UboBytes = RenderOriginOffset + RenderOriginBytes;                 // 1008

        // ---- GPU skinning (opt-in) PER-DRAW block geometry. The skinned pipeline's set 0 binding 1 is a
        // dynamic-offset UBO laid out as { mat4 Model; mat4 P; mat4 bones[128] } (see
        // ShaderSources.SkinnedModelVert): 2 header mats, then up to 128 bones. Each draw occupies a
        // 256-byte-aligned slot selected by a per-draw dynamic offset (the SpriteBatch view-proj slot pattern), so
        // a whole crowd shares one grow-with-retire buffer.
        //
        // It used to carry a COPY of the whole frame block between the header and the palette, plus a CPU-folded
        // Mvp ahead of them, so the pipeline read exactly one uniform buffer. That was the retired Veldrid Metal
        // backend's numbering rather than anything Metal needs, and #604 split the frame block back out into the
        // shared _ubo at set 0 binding 0. The slot lost 1008 bytes of duplicated frame block and the 64-byte Mvp
        // with it, and a frame no longer re-packs the frame uniforms once per skinned draw.
        internal const uint SkinnedHeaderMats = 2;                        // Model + P(Tint/Emissive/Spec)
        internal const uint SkinnedBonesOffset = SkinnedHeaderMats * 64;  // 128: the palette starts here
        internal static readonly uint SkinnedMainSlotBytes =
            Align256(SkinnedBonesOffset + (uint)SkinningMath.MaxBonesPerDraw * 64);  // 128 + 128*64 = 8320 -> 8448
        static uint Align256(uint n) => (n + 255u) & ~255u;

        /// <summary>Per-frame uniforms (binding 0) header. 1 mat4 + 7 vec4 = 64 + 112 = 176 bytes, uploaded at
        /// offset 0. Field order MUST exactly mirror the std140 UBO block in BOTH ModelVert and ModelFrag; the
        /// point-light arrays follow this header in the same buffer (see <see cref="MaxPointLights"/>).</summary>
        internal struct FrameUbo
        {
            public Matrix4x4 ViewProj;
            public Vector4 Dir; public Vector4 Color; public Vector4 Ambient; public Vector4 Params;
            public Vector4 FillDir; public Vector4 FillColor; public Vector4 CameraPos;
        }

        /// <summary>The cascaded shadow tail of the frame UBO (appended after the point-light arrays at
        /// <see cref="ShadowTailOffset"/>) - the per-cascade world-&gt;light-clip matrices the receivers sample the
        /// cascade atlas with, plus the PCF/bias/strength/fade params. <see cref="Cascade0"/>..<see cref="Cascade3"/>
        /// are the up-to-<see cref="MaxCascades"/> RECEIVER matrices (only the first <c>cascadeCount</c> are read, and the
        /// receiver breaks the loop past it). <see cref="Params"/> is (cascadeCount, strength, constantBias,
        /// slopeBias). Strength 0 means the atlas is inactive this frame, so the shader leaves the key light unshadowed
        /// (byte-stable with ShadowMode.Off). <see cref="Params2"/> is (texelStep = 1/perCascadeResolution,
        /// maxDistance, borderFrac, cascadeBlendFrac - the inner-cascade cross-fade band width, in cascade-local
        /// UV, that hides the texel-density step at a cascade hand-off). <see cref="NormalOffsets"/> holds the
        /// per-cascade normal-offset world size (x = cascade 0 .. w = cascade 3). 304 bytes = mat4[4] + 3*vec4.</summary>
        internal struct ShadowUbo
        {
            public Matrix4x4 Cascade0;        // 0
            public Matrix4x4 Cascade1;        // 64
            public Matrix4x4 Cascade2;        // 128
            public Matrix4x4 Cascade3;        // 192
            public Vector4 Params;            // 256: x = cascadeCount, y = strength, z = const bias, w = slope bias
            public Vector4 Params2;           // 272: x = texelStep (1/perCascadeRes), y = maxDistance, z = borderFrac, w = cascadeBlendFrac
            public Vector4 NormalOffsets;     // 288: per-cascade normal-offset world size (x=c0..w=c3)
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

        /// <summary>Per-instance vertex stream (buffer slot 1, instanceStepRate 1). 64 + 48 + 4 + 8 = 124 bytes. The
        /// Model matrix is a System.Numerics Matrix4x4 (row-major), read in the shader as four Float4 rows.</summary>
        public struct InstanceData
        {
            public Matrix4x4 Model;       // 64 bytes (4 rows -> 4 Float4 instance attributes)
            public Vector4 Tint;          // 16
            public Vector4 Emissive;      // 16
            // x = strength, y = shininess, z = alpha-cutout threshold (0 = OPAQUE/no clip, and ModelFrag discards
            // texels with albedo alpha below it). The CharDissolve pipeline instead overloads z = dissolve
            // threshold + w = edge width (a different fragment shader), so the two never collide within one draw.
            public Vector4 SpecParams;    // 16
            // Dynamic-geometry decal mask (issue #235): 0 = static world (the default; ModelFrag writes normal-target
            // alpha 1), 1 = dynamic/skinned geometry (ModelFrag writes alpha 0, so the main ground-decal pass rejects
            // it and a decal never paints onto a character standing in its Y-band). Left 0 for rigid instances, so a
            // scene with no skinned geometry is byte-identical. A single Float1 attribute (location 12); the splat
            // pipeline shares this layout and simply ignores it (terrain is always static).
            public float IsDynamic;       // 4
            // Per-instance rigid dissolve (issue #253): x = threshold (0 = fully drawn, 1 = fully dissolved,
            // matching DrawSkinned's dissolve parameter), y = edge width (a fraction of the noise range). Appended
            // AFTER IsDynamic on purpose: a trailing field leaves every existing element's byte offset unchanged
            // (Model 0, Tint 64, Emissive 80, SpecParams 96, IsDynamic 112), so a scene that queues no dissolve is
            // byte-identical and every GPU golden holds. ModelFrag gates the noise discard + edge on x > 0, so the
            // zero default (both InstanceData construction sites zero-fill via Add(default)) is inert. A single
            // Float2 attribute (location 13). The splat pipeline shares this layout and ignores it (terrain never
            // dissolves), and the CharDissolve pipeline (ModelDissolveFrag) also ignores it (it reads SpecParams.z/w).
            public Vector2 Dissolve;      // 8
            public const uint SizeInBytes = 124;
        }

        readonly IGpuDevice _gd;
        readonly IGpuBuffer _ubo;
        readonly IGpuResourceLayout _layout;
        readonly IGpuSampler _sampler;          // shared linear/wrap sampler (the device built-in, NOT owned here)
        readonly IGpuTexture _white;            // 1x1 white default; white*vColor*vTint == vColor*vTint (untextured invariant)
        readonly IGpuTexture _flatNormal;       // 1x1 (128,128,255): tangent-space (0,0,1); no-map normal default
        readonly IGpuTexture _defaultRough;     // 1x1 (0,0,0): roughness 0 (fully smooth); no-map spec default
        readonly IGpuResourceSet _defaultSet;   // UBO + white + flatNormal + defaultRough + sampler; bound for meshes with no material set
        IGpuPipeline _pipeline = null!;         // rebuilt by SetOutputs when the MRT sample count (MSAA) changes (set via BuildPipelines)
        readonly IGpuShaderSet _shaders;
        // Teleport CharDissolve variant: the SAME layout + vertex/instance layouts + outputs as _pipeline, only the
        // fragment shader differs (noise alpha-clip + emissive edge). A separate pipeline so the normal skinned/rigid
        // path keeps _pipeline byte-identical (the golden images are unaffected); selected per-draw by BindDissolvePass.
        IGpuPipeline _dissolvePipeline = null!;
        readonly IGpuShaderSet _dissolveShaders;

        // Shared by the splat (ModelRenderer.Splat.cs) and tile-ground (ModelRenderer.TileGround.cs) ground passes:
        // wrap + anisotropic (trilinear fallback), OWNED here, so it is disposed once for both.
        readonly IGpuSampler _terrainSampler;

        // The key-light shadow map. Owned here so its stable texture handle can be bound into every material set
        // (the model fragment samples it at set 0 bindings 5/6, the splat and skinned fragments at set 1's last
        // two). Allocated at a fixed resolution for the scene's lifetime (see the ctor), so material sets never
        // need rebuilding on a resolution change.
        readonly ShadowMapRenderer _shadowMap;
        /// <summary>The key-light shadow map (depth-only pass over instanced casters + the R32F depth target the
        /// receivers sample). Scene3D drives its per-frame depth pass and hands the light matrix / params in.</summary>
        public ShadowMapRenderer ShadowMap => _shadowMap;

        IGpuBuffer? _instanceBuffer;
        uint _instanceCapacity;          // capacity in instances
        // CPU-skinned path: skinned meshes are deformed on the CPU each frame and drawn through THIS no-bone model
        // pipeline (the bone-buffer GPU read corrupted past element 0 in the windowed Veldrid/Metal swapchain context;
        // CPU skinning + the proven-clean rigid path sidesteps it - see Scene3D's skinned block). One concatenated
        // transient vertex stream (all skinned draws' deformed verts) + a parallel per-draw instance stream, both
        // grown geometrically and retired like _instanceBuffer.
        IGpuBuffer? _skinnedVertexBuffer; uint _skinnedVertexCapacity;     // capacity in ModelVertex
        IGpuBuffer? _skinnedInstanceBuffer; uint _skinnedInstanceCapacity; // capacity in InstanceData

        // GPU-skinning path (Scene3D.UseGpuSkinning). Set 0 holds TWO uniform buffers since #604 unfolded the
        // combined per-draw block: binding 0 is the SHARED frame UBO (_ubo, the same buffer the model pass binds),
        // read by both stages, and binding 1 is the per-draw {Model,P,bones[128]} at that draw's dynamic offset,
        // read by the vertex alone. Set 1 is the per-mesh material maps + shadow map, fragment only. The frame
        // block used to be folded into every per-draw slot because the retired Veldrid Metal backend mis-bound a
        // second uniform buffer in a pipeline (GpuSkinningReproGpuTests variant 3 is the offscreen record of that
        // failure). The rest-pose SkinnedVertex buffer is the mesh's own vertex buffer, uploaded ONCE at load - no
        // per-frame vertex deform. Palette + per-draw matrices are all that upload each frame (the GPU skins).
        readonly IGpuResourceLayout _skinnedMainLayout;     // set 0: shared frame U (both stages) + per-draw VBlock (dynamic UBO, VERTEX)
        readonly IGpuResourceLayout _skinnedFragLayout;     // set 1: material maps + shadow map, FRAGMENT only
        readonly IGpuShaderSet _skinnedShaders;
        readonly IGpuShaderSet _skinnedDissolveShaders;
        IGpuPipeline _skinnedPipeline = null!;              // rebuilt by SetOutputs alongside _pipeline
        IGpuPipeline _skinnedDissolvePipeline = null!;
        readonly IGpuResourceSet _skinnedDefaultFragSet;   // white/flat/rough defaults + shadow map (untextured skinned mesh)
        IGpuBuffer? _skinnedMainUbo; uint _skinnedMainSlots; IGpuResourceSet? _skinnedMainSet; // grow-with-retire per-draw UBO + single-slot window set
        // Persistent CPU image of the complete skinned per-draw UBO. D3D11 takes its cheap UpdateSubresource route
        // only when a uniform-buffer upload covers the entire destination from offset 0, so all slots are packed
        // here first and uploaded together once per frame.
        byte[] _skinnedMainImage = Array.Empty<byte>();
        readonly Matrix4x4[] _skinnedHeaderScratch = new Matrix4x4[SkinnedHeaderMats]; // Model/P
        // Instance buffers replaced by a grow are retired here (a prior in-flight frame may still read them);
        // disposed only in Dispose. Bounded by geometric growth.
        readonly List<IDisposable> _retired = new();

        public ModelRenderer(IGpuDevice gd, GpuOutputDescription modelOutputs, int shadowMapResolution, int shadowCascadeCount)
        {
            _gd = gd;
            var factory = gd.Factory;

            // The cascade atlas is allocated up front at a fixed per-cascade resolution x cascade count so its texture
            // handle stays stable and can be bound into every material set below. The shader gates on ShadowParams.y
            // (strength), so an inactive frame never taps it (byte-stable with ShadowMode.Off).
            _shadowMap = new ShadowMapRenderer(gd, shadowMapResolution, shadowCascadeCount);

            _ubo = factory.CreateBuffer(new GpuBufferDescription(UboBytes, GpuBufferUsage.UniformBuffer)); // header + 2 vec4[16] point-light arrays + shadow tail

            _layout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex | GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Albedo", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("NormalMap", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("RoughnessMap", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Sampler", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("ShadowMap", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("ShadowSamp", GpuResourceKind.Sampler, GpuShaderStages.Fragment)));

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

            _defaultSet = factory.CreateResourceSet(new GpuResourceSetDescription(_layout, _ubo, _white, _flatNormal, _defaultRough, _sampler,
                _shadowMap.ShadowTexture, _shadowMap.ShadowSampler));

            _shaders = factory.CreateShadersFromSpirv(ShaderSources.ModelVert, ShaderSources.ModelFrag);
            _dissolveShaders = factory.CreateShadersFromSpirv(ShaderSources.ModelVert, ShaderSources.ModelDissolveFrag);

            // GPU-skinning layouts/shaders/default set. Set 0 declares the shared frame block FIRST, read by both
            // stages, then the per-draw VBlock the vertex alone reads at its dynamic offset. That order was
            // REQUIRED when this split landed: MslBindingOrder.CheckPrefix wanted every stage's buffer usage to be
            // a prefix of the layout, so a buffer only ONE stage reads could only come after one both stages read.
            // #604 deleted that check with the rest of the one-uniform-buffer rule, so the order is now a shape
            // kept rather than a constraint. Set 1 is the per-mesh material maps + shadow map, fragment only. The default frag
            // (set 1) set uses white/flat/rough defaults + the shadow map, so an untextured skinned mesh is lit
            // exactly like the CPU path's _defaultSet.
            _skinnedMainLayout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex | GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("VBlock", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex, dynamic: true)));
            _skinnedFragLayout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Albedo", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("NormalMap", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("RoughnessMap", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Sampler", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("ShadowMap", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("ShadowSamp", GpuResourceKind.Sampler, GpuShaderStages.Fragment)));
            _skinnedShaders = factory.CreateShadersFromSpirv(ShaderSources.SkinnedModelVert, ShaderSources.SkinnedModelFrag);
            _skinnedDissolveShaders = factory.CreateShadersFromSpirv(ShaderSources.SkinnedModelVert, ShaderSources.SkinnedModelDissolveFrag);
            _skinnedDefaultFragSet = factory.CreateResourceSet(new GpuResourceSetDescription(
                _skinnedFragLayout, _white, _flatNormal, _defaultRough, _sampler,
                _shadowMap.ShadowTexture, _shadowMap.ShadowSampler));

            // Tileable detail textures REPEAT across the world, so wrap addressing; anisotropic for grazing ground
            // (CreateSampler falls back to trilinear when the backend lacks anisotropy). 16x anisotropy + a +1 mip
            // LOD bias tame the shimmer/"fuzz" a high-frequency noisy albedo (e.g. grass) throws off at distance and
            // grazing angles: aniso covers the directional grazing case, the bias nudges distant ground to a blurrier
            // mip so the noise stops aliasing frame-to-frame. (Bias is a D3D11/Vulkan feature; Metal ignores it.)
            _terrainSampler = factory.CreateSampler(new GpuSamplerDescription(
                GpuSamplerFilter.Anisotropic, GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap, maximumAnisotropy: 16, mipLodBias: 1));

            // The two ground passes' layouts + shaders, each in its own partial and each splitting its uniforms
            // across two sets the same way: the shared frame block at set 0, the material's own params beside its
            // textures at set 1 (ModelRenderer.Splat.cs for #604, ModelRenderer.TileGround.cs for #727).
            CreateSplatResources(factory);
            CreateTileGroundResources(factory);

            // Build the model + splat + tile-ground pipelines from the MRT outputs (rebuilt by SetOutputs when MSAA changes).
            BuildPipelines(factory, modelOutputs);
        }

        /// <summary>Rebuild the model + splat pipelines for a new MRT output description (e.g. it became multisampled
        /// for MSAA - a pipeline's sample count must match its framebuffer). Layouts / shaders / sampler / material
        /// sets are ALL kept (material sets bind to <see cref="_layout"/>, not the pipeline), so loaded meshes'
        /// materials survive the rebuild.</summary>
        public void SetOutputs(GpuOutputDescription modelOutputs)
        {
            _pipeline.Dispose(); _splatPipeline.Dispose(); _dissolvePipeline.Dispose(); _tileGroundPipeline.Dispose();
            _skinnedPipeline.Dispose(); _skinnedDissolvePipeline.Dispose();
            BuildPipelines(_gd.Factory, modelOutputs);
        }

        void BuildPipelines(IGpuResourceFactory factory, GpuOutputDescription modelOutputs)
        {
            // Slot 0: per-vertex geometry (locations 0..4).
            var vertexLayout = new GpuVertexLayoutDescription(
                new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Normal", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Color", GpuVertexElementFormat.Float4),
                new GpuVertexElement("TexCoord", GpuVertexElementFormat.Float2),
                new GpuVertexElement("Tangent", GpuVertexElementFormat.Float4));

            // Slot 1: per-instance data (locations 5..13), one step per instance. SPIRV binds these by location
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
                    // Location 11: per-instance material params. Consumed by ModelVert and SplatVert. The
                    // TILE-GROUND pipeline shares this layout and declares nothing from 11 up (it derives its
                    // specular from the material UBO), so 11, 12 and 13 are all trailing inputs there. The rule the
                    // three of them rely on is the same one: a TRAILING input the shader does not declare is valid
                    // on all three backends, while a hole below a declared one is not.
                    new GpuVertexElement("ISpecParams", GpuVertexElementFormat.Float4),
                    // Location 12: dynamic-geometry decal mask (0 static / 1 skinned). Consumed by ModelVert. The
                    // splat and tile-ground pipelines share this instance layout and ignore it.
                    new GpuVertexElement("IDynamic", GpuVertexElementFormat.Float1),
                    // Location 13: per-instance rigid dissolve (issue #253) - x = threshold, y = edge width. Consumed
                    // by ModelVert (passed to ModelFrag, which gates on x > 0). The splat, tile-ground and
                    // CharDissolve pipelines all share this layout and ignore this trailing input.
                    new GpuVertexElement("IDissolve", GpuVertexElementFormat.Float2),
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

            // CharDissolve variant: identical to _pipeline except the fragment shader (noise alpha-clip + edge).
            _dissolvePipeline = factory.CreateGraphicsPipeline(new GpuPipelineDescription
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
                ShaderSet = _dissolveShaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout, instanceLayout },
                Outputs = modelOutputs,
            });

            // The two ground pipelines: the same two vertex layouts, each with its own shaders + resource layouts
            // (ModelRenderer.Splat.cs and ModelRenderer.TileGround.cs).
            BuildSplatPipeline(factory, modelOutputs, vertexLayout, instanceLayout);
            BuildTileGroundPipeline(factory, modelOutputs, vertexLayout, instanceLayout);

            // GPU-skinning pipelines. ONE vertex buffer slot: the rest-pose SkinnedVertex stream (Position/Normal/
            // Color/TexCoord/BoneIndices/BoneWeights/Tangent = locations 0..6), no per-instance stream (the per-draw
            // data lives in the combined UBO). Set 0 = combined VBlock (vertex), set 1 = frame+material (fragment).
            var skinnedVertexLayout = new GpuVertexLayoutDescription(
                new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Normal", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Color", GpuVertexElementFormat.Float4),
                new GpuVertexElement("TexCoord", GpuVertexElementFormat.Float2),
                new GpuVertexElement("BoneIndices", GpuVertexElementFormat.Float4),
                new GpuVertexElement("BoneWeights", GpuVertexElementFormat.Float4),
                new GpuVertexElement("Tangent", GpuVertexElementFormat.Float4));
            _skinnedPipeline = factory.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend, GpuBlendAttachment.OverrideBlend, GpuBlendAttachment.OverrideBlend },
                DepthStencil = GpuDepthStencilState.DepthOnlyLessEqual,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _skinnedMainLayout, _skinnedFragLayout },
                ShaderSet = _skinnedShaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { skinnedVertexLayout },
                Outputs = modelOutputs,
            });
            _skinnedDissolvePipeline = factory.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend, GpuBlendAttachment.OverrideBlend, GpuBlendAttachment.OverrideBlend },
                DepthStencil = GpuDepthStencilState.DepthOnlyLessEqual,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _skinnedMainLayout, _skinnedFragLayout },
                ShaderSet = _skinnedDissolveShaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { skinnedVertexLayout },
                Outputs = modelOutputs,
            });
        }

        /// <summary>Bind + clear the model framebuffer once per frame, before drawing instances.</summary>
        public void BeginModelPass(IGpuCommandList cl, RenderResources res, PixelPostProcessSettings s)
        {
            cl.SetFramebuffer(res.ModelFB);
            // All three colour targets are cleared to the background, and alpha 0 marks "background" for the
            // starfield composite while the model writes alpha 1. That is what this pass wants on every backend.
            //
            // THE SECOND REASON THE THREE VALUES ARE EQUAL NAMES ONE IMPLEMENTATION: the VELDRID Metal backend
            // wrote every clear into colorAttachments[0], so attachments 1 and 2 were never cleared there at all
            // and they LOADED a freshly created private texture nothing had written. Equal values do not fix that,
            // they only make the one attachment that does get cleared carry the value the other two should have
            // had. The engine's own native Metal backend (GpuBackendKind.MetalNative) folds each clear into its
            // own attachment's loadAction, so all three really are cleared there, which is a deliberate rendering
            // change on the fleet's reference golden family rather than an invisible correction (decision M-A2 of
            // docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md). The values were kept equal while the Veldrid
            // Metal leg was still selectable, because this code could not tell which Metal it was on and had to
            // be correct on both. That leg went in 18.0.0 and the constraint went with it.
            var bg = s.BackgroundColor.WithAlpha(0f);
            cl.ClearColorTarget(0, bg);
            cl.ClearColorTarget(1, bg);
            cl.ClearColorTarget(2, bg);
            cl.ClearDepthStencil(1f);
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
                _layout, _ubo, albedo ?? _white, normal ?? _flatNormal, roughness ?? _defaultRough, _sampler,
                _shadowMap.ShadowTexture, _shadowMap.ShadowSampler));

        /// <summary>Create a wrap-addressed terrain sampler from <paramref name="cfg"/> (anisotropy/trilinear/point +
        /// mip LOD bias). The caller owns and disposes it. Mirrors the shared default sampler this renderer builds at
        /// construction, so <see cref="TerrainSamplerConfig.Default"/> reproduces it exactly.</summary>
        public IGpuSampler CreateTerrainSampler(in TerrainSamplerConfig cfg) =>
            _gd.Factory.CreateSampler(new GpuSamplerDescription(
                cfg.Filter, GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap,
                maximumAnisotropy: cfg.MaximumAnisotropy, mipLodBias: cfg.MipLodBias));

        /// <summary>Bind the CharDissolve pipeline variant for the skinned draws that carry a dissolve threshold (the
        /// SpecParams.z/.w channels drive the noise alpha-clip + emissive edge). Same material sets + frame UBO as
        /// <see cref="BindPass"/>; switch back with <see cref="BindPass"/> for non-dissolving draws.</summary>
        public void BindDissolvePass(IGpuCommandList cl) => cl.SetPipeline(_dissolvePipeline);

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

        /// <summary>Begin the cascaded shadow depth pass (bind + clear the whole atlas, upload every cascade's DEPTH
        /// matrix into its dynamic slot, bind the rigid depth pipeline). <paramref name="depthMats"/> are the
        /// GPU-clip-corrected AND column-transformed per-cascade matrices (first <paramref name="cascadeCount"/> read).
        /// Follow with <see cref="BeginShadowCascadeRigid"/> per cascade, then <see cref="EndShadowPass"/>. Call after
        /// <see cref="UploadInstances"/> (the depth pass reuses that instance buffer).
        /// <paramref name="noiseScales"/> is the per-cascade dissolve noise scale (issue #391).</summary>
        public void BeginShadowPass(IGpuCommandList cl, ReadOnlySpan<Matrix4x4> depthMats, int cascadeCount,
            Vector3 renderOrigin, ReadOnlySpan<float> noiseScales) =>
            _shadowMap.BeginDepthPass(cl, depthMats, cascadeCount, renderOrigin, noiseScales);

        /// <summary>Bind cascade <paramref name="cascade"/> (scissor its atlas column + rebase the light matrix) for
        /// the rigid + CPU-skinned caster draws that follow. <see cref="BeginShadowPass"/> must be bound.</summary>
        public void BeginShadowCascadeRigid(IGpuCommandList cl, int cascade) => _shadowMap.BeginCascadeRigid(cl, cascade);

        /// <summary>Bind cascade <paramref name="cascade"/> on the DISSOLVE-AWARE depth pipeline (issue #287), for the
        /// caster spans carrying a per-instance dissolve. Switch back with <see cref="BeginShadowCascadeRigid"/>.</summary>
        public void BeginShadowCascadeRigidDissolve(IGpuCommandList cl, int cascade) => _shadowMap.BeginCascadeRigidDissolve(cl, cascade);

        /// <summary>Bind cascade <paramref name="cascade"/> on the INVERTED dissolve depth pipeline (issue #391), for
        /// the merged half of an HLOD crossfade, whose dither must complement the fading props' rather than nest
        /// inside it. Switch back with <see cref="BeginShadowCascadeRigid"/>.</summary>
        public void BeginShadowCascadeRigidDissolveInverted(IGpuCommandList cl, int cascade) =>
            _shadowMap.BeginCascadeRigidDissolveInverted(cl, cascade);

        /// <summary>Reset the scissor to full after the cascaded depth pass. Call once after all cascades are drawn.</summary>
        public void EndShadowPass(IGpuCommandList cl) => _shadowMap.EndDepthPass(cl);

        /// <summary>Draw one rigid caster run into the CURRENTLY-BOUND cascade, reusing the shared instance buffer the
        /// model pass uploaded (no second upload). <see cref="BeginShadowCascadeRigid"/> must be bound.</summary>
        public void DrawShadowCasterRun(IGpuCommandList cl, IGpuBuffer vb, IGpuBuffer ib, int indexCount,
            GpuIndexFormat indexFormat, uint instanceStart, uint instanceCount) =>
            _shadowMap.DrawCasterRun(cl, vb, ib, indexCount, indexFormat, _instanceBuffer!, instanceStart, instanceCount);

        /// <summary>Draw one CPU-skinned caster into the shadow map, reusing the shared skinned vertex + instance
        /// buffers (<see cref="UploadCpuSkinned"/> must have run this frame). <see cref="BeginShadowPass"/> bound.</summary>
        public void DrawShadowSkinnedCaster(IGpuCommandList cl, IGpuBuffer ib, int indexCount, GpuIndexFormat indexFormat,
            int baseVertex, uint drawIndex) =>
            _shadowMap.DrawSkinnedCaster(cl, _skinnedVertexBuffer!, _skinnedInstanceBuffer!, ib, indexCount, indexFormat, baseVertex, drawIndex);

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

        // ---- GPU skinning (opt-in). See the field block + ShaderSources.SkinnedModelVert for the two-buffer design. ----

        /// <summary>Build a skinned mesh's set-1 material set (albedo/normal/roughness + shared sampler + shadow map),
        /// bound to the FRAGMENT-only skinned material layout. The frame UBO is NOT here - the skinned fragment reads
        /// it from set 0 binding 0, the shared block the model pass binds (see <see cref="EnsureSkinnedMainCapacity"/>),
        /// so this set stays pure per-mesh material data and never has to be rebuilt when a frame changes. Defaults to
        /// white/flat/zero so an untextured skinned mesh matches the CPU path. Owned by the caller (Scene3D), disposed
        /// when the mesh unloads.</summary>
        public IGpuResourceSet CreateSkinnedMaterialSet(IGpuTexture? albedo = null, IGpuTexture? normal = null, IGpuTexture? roughness = null) =>
            _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(
                _skinnedFragLayout, albedo ?? _white, normal ?? _flatNormal, roughness ?? _defaultRough, _sampler,
                _shadowMap.ShadowTexture, _shadowMap.ShadowSampler));

        /// <summary>Ensure the per-draw main UBO holds at least <paramref name="slotCount"/> slots (each
        /// <see cref="SkinnedMainSlotBytes"/>), growing geometrically and retiring the old buffer + its set. Rebuilds
        /// the set-0 resource set, which carries both of the pipeline's uniform buffers: the shared frame block at
        /// binding 0 (whole, read by both stages) and a single-slot window over the per-draw buffer at binding 1, the
        /// one the dynamic offset indexes. One shared set, cheap to rebuild on the rare geometric grow. Call once
        /// before packing this frame's skinned main slots.</summary>
        public void EnsureSkinnedMainCapacity(uint slotCount)
        {
            if (_skinnedMainUbo != null && _skinnedMainSlots >= slotCount) return;
            if (_skinnedMainUbo != null) _retired.Add(_skinnedMainUbo);
            if (_skinnedMainSet != null) _retired.Add(_skinnedMainSet);
            _skinnedMainSlots = Math.Max(slotCount, _skinnedMainSlots == 0 ? 8u : _skinnedMainSlots * 2);
            var image = new byte[checked((int)(_skinnedMainSlots * SkinnedMainSlotBytes))];
            _skinnedMainImage.AsSpan().CopyTo(image);
            _skinnedMainImage = image;
            _skinnedMainUbo = _gd.Factory.CreateBuffer(
                new GpuBufferDescription(_skinnedMainSlots * SkinnedMainSlotBytes, GpuBufferUsage.UniformBuffer));
            _skinnedMainSet = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(
                _skinnedMainLayout, _ubo, new GpuBufferRange(_skinnedMainUbo, 0, SkinnedMainSlotBytes)));
        }

        /// <summary>Pack one skinned draw's per-draw slot: the two-matrix header (<c>Model</c> for world
        /// pos/normal/tangent, <c>P</c> packing tint/emissive/specParams into its three columns), then the composed
        /// <paramref name="bones"/> at <see cref="SkinnedBonesOffset"/> (uploaded raw, read column-major = their
        /// transpose, so the shader blend equals <see cref="SkinningMath.SkinVertex"/>). Uploads only the mesh's
        /// bones (indices load-validated &lt; boneCount).
        /// <para>
        /// NOTHING PER-FRAME IS WRITTEN HERE ANY MORE. The slot used to open with a CPU-folded <c>Mvp</c> and carry a
        /// whole copy of the frame block, re-packed into every draw each frame, because the pipeline was allowed
        /// exactly one uniform buffer. Since #604 the vertex reads <c>ViewProj</c> straight out of the shared frame
        /// block at set 0 binding 0, so <see cref="SetFrameUniforms"/> uploading that block once is all this pass
        /// needs from it and this method is pure per-draw data.
        /// </para></summary>
        public void PackSkinnedMainSlot(uint slot, in Matrix4x4 model,
            Vector4 tint, Vector4 emissive, Vector4 specParams, ReadOnlySpan<Matrix4x4> bones, float isDynamic = 1f)
        {
            uint baseOff = slot * SkinnedMainSlotBytes;
            _skinnedHeaderScratch[0] = model;
            // Row 3 is the P matrix's 4th column in the shader (GLSL reads the raw bytes column-major). Its .x carries
            // the dynamic-geometry decal mask (SkinnedModelVert -> vDynamic): every GPU-skinned draw is a skinned
            // character, so it defaults to 1 (dynamic), and the skinned fragment writes normal-target alpha 0 to keep
            // the main ground-decal pass off it. The rest of the row stays 0.
            _skinnedHeaderScratch[1] = new Matrix4x4(
                tint.X, tint.Y, tint.Z, tint.W,
                emissive.X, emissive.Y, emissive.Z, emissive.W,
                specParams.X, specParams.Y, specParams.Z, specParams.W,
                isDynamic, 0f, 0f, 0f);
            // Header then palette, straight into the persistent full-buffer image (they are adjacent now that no
            // frame block sits between them). UploadSkinnedMainSlots sends that image once every slot is ready.
            Span<byte> destination = _skinnedMainImage.AsSpan(checked((int)baseOff), checked((int)SkinnedMainSlotBytes));
            MemoryMarshal.AsBytes<Matrix4x4>(_skinnedHeaderScratch).CopyTo(destination);
            if (bones.Length > 0)
                MemoryMarshal.AsBytes(bones).CopyTo(destination.Slice((int)SkinnedBonesOffset));
        }

        /// <summary>Upload every packed GPU-skinned main slot in one whole-buffer write. Slots without a visible-main
        /// draw may retain old bytes because no draw binds them this frame.</summary>
        public void UploadSkinnedMainSlots(IGpuCommandList cl)
            => cl.UpdateBuffer(_skinnedMainUbo!, 0, (ReadOnlySpan<byte>)_skinnedMainImage);

        /// <summary>Bind the GPU-skinning model pipeline. Call after <see cref="BeginModelPass"/>/
        /// <see cref="SetFrameUniforms"/>, before the skinned draw loop.</summary>
        public void BindSkinnedPass(IGpuCommandList cl) => cl.SetPipeline(_skinnedPipeline);

        /// <summary>Bind the GPU-skinning CharDissolve pipeline variant (same layouts, dissolve fragment).</summary>
        public void BindSkinnedDissolvePass(IGpuCommandList cl) => cl.SetPipeline(_skinnedDissolvePipeline);

        /// <summary>Draw one GPU-skinned mesh: its rest-pose <paramref name="restVb"/> (uploaded once at load) at
        /// vertex slot 0, set 0 carrying the shared frame block plus this draw's per-draw window (selected by the
        /// dynamic offset <paramref name="slot"/> * <see cref="SkinnedMainSlotBytes"/>, which applies to binding 1
        /// alone because it is the only element the layout declares dynamic), and <paramref name="skinnedFragSet"/>
        /// (or the white default when null) at set 1. One <c>instanceCount=1</c> indexed draw. The GPU skins in the
        /// vertex shader. A pipeline (<see cref="BindSkinnedPass"/>/<see cref="BindSkinnedDissolvePass"/>) must be
        /// bound.</summary>
        public void DrawGpuSkinned(IGpuCommandList cl, IGpuBuffer restVb, IGpuBuffer ib, int indexCount,
            GpuIndexFormat indexFormat, uint slot, IGpuResourceSet? skinnedFragSet)
        {
            cl.SetGraphicsResourceSet(0, _skinnedMainSet!, slot * SkinnedMainSlotBytes);
            cl.SetGraphicsResourceSet(1, skinnedFragSet ?? _skinnedDefaultFragSet);
            cl.SetVertexBuffer(0, restVb);
            cl.SetIndexBuffer(ib, indexFormat);
            cl.DrawIndexed((uint)indexCount, 1, 0, 0, 0);
        }

        /// <summary>Ensure the shadow map's combined skinned-depth UBO holds <paramref name="slotCount"/> slots (grows
        /// + retires like the main one). Forwards to <see cref="ShadowMapRenderer"/>.</summary>
        public void EnsureSkinnedShadowCapacity(uint slotCount) => _shadowMap.EnsureSkinnedShadowCapacity(slotCount);

        /// <summary>Pack one GPU-skinned caster's shadow-depth slot for one cascade: <c>LightMvp = model *
        /// cascadeDepthMat</c> folded per draw + the composed bones. <paramref name="cascadeDepthMat"/> is that
        /// cascade's GPU-clip-corrected AND column-transformed matrix. Forwards to <see cref="ShadowMapRenderer"/>.</summary>
        public void PackSkinnedShadowSlot(uint slot, in Matrix4x4 model, in Matrix4x4 cascadeDepthMat, ReadOnlySpan<Matrix4x4> bones) =>
            _shadowMap.PackSkinnedShadowSlot(slot, model, cascadeDepthMat, bones);

        /// <summary>Upload every packed GPU-skinned shadow slot in one whole-buffer write.</summary>
        public void UploadSkinnedShadowSlots(IGpuCommandList cl) => _shadowMap.UploadSkinnedShadowSlots(cl);

        /// <summary>Bind cascade <paramref name="cascade"/> for the GPU-skinning depth draws: scissor its atlas column
        /// and switch to the skinned depth pipeline. Call per cascade after the rigid runs. Forwards to <see cref="ShadowMapRenderer"/>.</summary>
        public void BindShadowCascadeSkinned(IGpuCommandList cl, int cascade) => _shadowMap.BindCascadeSkinned(cl, cascade);

        /// <summary>Draw one GPU-skinned caster into the CURRENTLY-BOUND cascade (rest-pose vertex buffer + per-draw
        /// dynamic offset). <see cref="BindShadowCascadeSkinned"/> must be bound. Forwards to <see cref="ShadowMapRenderer"/>.</summary>
        public void DrawGpuSkinnedShadowCaster(IGpuCommandList cl, IGpuBuffer restVb, IGpuBuffer ib, int indexCount, GpuIndexFormat indexFormat, uint slot) =>
            _shadowMap.DrawGpuSkinnedCaster(cl, restVb, ib, indexCount, indexFormat, slot);

        public void Dispose()
        {
            _shadowMap.Dispose();
            _pipeline.Dispose(); _defaultSet.Dispose(); _layout.Dispose();
            _white.Dispose(); _flatNormal.Dispose(); _defaultRough.Dispose(); // _sampler is the device built-in (non-owning); do not dispose it.
            _shaders.Dispose();
            _dissolvePipeline.Dispose(); _dissolveShaders.Dispose();
            DisposeSplatResources();
            DisposeTileGroundResources();
            _terrainSampler.Dispose();  // shared by both ground passes' sets, so it is freed after them
            _skinnedPipeline.Dispose(); _skinnedDissolvePipeline.Dispose();
            _skinnedShaders.Dispose(); _skinnedDissolveShaders.Dispose();
            _skinnedDefaultFragSet.Dispose(); _skinnedMainLayout.Dispose(); _skinnedFragLayout.Dispose();
            _skinnedMainUbo?.Dispose(); _skinnedMainSet?.Dispose();
            _ubo.Dispose();
            _instanceBuffer?.Dispose();
            _skinnedVertexBuffer?.Dispose();
            _skinnedInstanceBuffer?.Dispose();
            foreach (var r in _retired) r.Dispose();
            _retired.Clear();
        }
    }
}
