using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// The splat-terrain pipeline: 5-layer texture-array PBR with the per-vertex weights in the mesh colour and
    /// world-space triplanar tiling. Part of the <see cref="ModelRenderer"/> partial, beside the tile-ground
    /// pipeline that mirrors it.
    /// <para>
    /// TWO DESCRIPTOR SETS, split per-frame from per-material, and the split is the whole reason this moved into a
    /// file of its own. Set 0 is the SHARED frame UBO, the same buffer the model pass binds, read by both stages.
    /// Set 1 is the material: its own 112-byte params buffer written once at load, its two texture arrays, its
    /// sampler and the shadow map. Until 18.0.0 both halves rode in ONE buffer per material, with the frame block
    /// re-uploaded into every one of them each frame, because the retired Veldrid Metal backend numbered a
    /// pipeline's uniform buffers by per-kind DECLARATION ORDER and a stage referencing fewer of them than the
    /// declared array put before it read an index nothing had written. Section 2.3a of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c> measured that as a property of the incumbent's
    /// numbering rather than of Metal, and measured exactly this shape (set 0 in both stages, a fragment-only
    /// second buffer at set 1) binding correctly on every backend the engine ships.
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/604">#604</see> is the unfold.
    /// </para>
    /// </summary>
    internal sealed partial class ModelRenderer
    {
        // Not readonly only because the constructor sets them through CreateSplatResources rather than inline. Each
        // is written exactly once, from that call, and never again (the pipeline additionally by SetOutputs).
        IGpuResourceLayout _splatFrameLayout = null!;     // set 0: U, the shared frame block, both stages
        IGpuResourceSet _splatFrameSet = null!;           // set 0's one set: _ubo, the buffer the model pass reads too
        IGpuResourceLayout _splatMaterialLayout = null!;  // set 1: SplatParams + AlbedoArray + NormalArray + Sampler + shadow map/sampler
        IGpuShaderSet _splatShaders = null!;
        IGpuPipeline _splatPipeline = null!;              // rebuilt by SetOutputs alongside _pipeline (set via BuildSplatPipeline)

        /// <summary>Create the splat resource layouts, the shared frame set and the shader set. All three are
        /// sample-count-independent, so this runs once from the constructor (after <c>_ubo</c> and the shadow map
        /// exist) and only the pipeline is rebuilt when the MRT changes.</summary>
        void CreateSplatResources(IGpuResourceFactory factory)
        {
            _splatFrameLayout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex | GpuShaderStages.Fragment)));
            _splatFrameSet = factory.CreateResourceSet(new GpuResourceSetDescription(_splatFrameLayout, _ubo));

            // The textures are declared in the order the fragment samples them, with the SHADOW MAP LAST. That was
            // a Metal requirement under the incumbent's numbering and is now the engine's own convention: the
            // native backend authors each index, so the emission and the binder agree by construction.
            _splatMaterialLayout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("SplatParams", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("AlbedoArray", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("NormalArray", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Sampler", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("ShadowMap", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("ShadowSamp", GpuResourceKind.Sampler, GpuShaderStages.Fragment)));

            _splatShaders = factory.CreateShadersFromSpirv(ShaderSources.SplatVert, ShaderSources.SplatFrag);
        }

        /// <summary>Build the splat pipeline from the MRT outputs and the shared vertex + instance layouts. Called by
        /// <see cref="BuildPipelines"/>, so <see cref="SetOutputs"/> rebuilds it with the rest when the sample count
        /// changes. The layout array order IS the set numbering the backends flatten registers in, so set 0 (frame)
        /// comes before set 1 (material).</summary>
        void BuildSplatPipeline(IGpuResourceFactory factory, GpuOutputDescription modelOutputs,
            GpuVertexLayoutDescription vertexLayout, GpuVertexLayoutDescription instanceLayout)
        {
            _splatPipeline = factory.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend, GpuBlendAttachment.OverrideBlend, GpuBlendAttachment.OverrideBlend },
                DepthStencil = GpuDepthStencilState.DepthOnlyLessEqual,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _splatFrameLayout, _splatMaterialLayout },
                ShaderSet = _splatShaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout, instanceLayout },
                Outputs = modelOutputs,
            });
        }

        /// <summary>Create a splat material's PARAMS UBO: <see cref="SplatParamsData.SizeInBytes"/> holding
        /// <paramref name="data"/> alone, written once here and never re-uploaded. The frame block is not in it any
        /// more (#604), so this is pure load-time material data and there is no CPU mirror to keep: the write covers
        /// the whole buffer from offset 0, which is also the cheap Direct3D 11 route. (#408's blocking Map was
        /// caused by the PARTIAL per-frame write into the larger combined buffer this replaces, and that write is
        /// gone rather than optimised.) Owned by Scene3D and shared by every chunk using this material.</summary>
        public IGpuBuffer CreateSplatParamsUbo(in SplatParamsData data)
        {
            var ubo = _gd.Factory.CreateBuffer(new GpuBufferDescription(SplatParamsData.SizeInBytes, GpuBufferUsage.UniformBuffer));
            _gd.UpdateBuffer(ubo, 0, in data);
            return ubo;
        }

        /// <summary>Build a splat-terrain material resource set (set 1): the material's params UBO + the two 5-layer
        /// texture arrays (albedo, tangent-space normal) + the shared terrain (wrap/anisotropic) sampler + the shadow
        /// map. Shared across every chunk using this material and owned by Scene3D, NOT per mesh.</summary>
        public IGpuResourceSet CreateSplatMaterialSet(IGpuBuffer paramsUbo, IGpuTexture albedoArray, IGpuTexture normalArray) =>
            CreateSplatMaterialSet(paramsUbo, albedoArray, normalArray, _terrainSampler);

        /// <summary>As above, but binds an explicit <paramref name="sampler"/> instead of the shared default one
        /// (used by a material that overrides its <see cref="TerrainSamplerConfig"/>). The caller owns that sampler.</summary>
        public IGpuResourceSet CreateSplatMaterialSet(IGpuBuffer paramsUbo, IGpuTexture albedoArray, IGpuTexture normalArray, IGpuSampler sampler) =>
            _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(
                _splatMaterialLayout, paramsUbo, albedoArray, normalArray, sampler,
                _shadowMap.ShadowTexture, _shadowMap.ShadowSampler));

        /// <summary>Bind the splat-terrain pipeline for the splat pass (call once before its draw loop). The frame
        /// block it reads is the shared one <see cref="SetFrameUniforms"/> already uploaded this frame, so unlike the
        /// tile-ground pass there is nothing per-material to re-sync first.</summary>
        public void BindSplatPass(IGpuCommandList cl) => cl.SetPipeline(_splatPipeline);

        /// <summary>Draw one splat-terrain mesh run through the splat pipeline, reusing the shared instance buffer
        /// (terrain instances are identity-transform, white-tint). Set 0 is the shared frame block and set 1 is
        /// <paramref name="splatSet"/>, the material's params UBO + texture arrays + sampler. Both are bound per
        /// draw, the way the GPU-skinning pass binds its own pair. <see cref="BindSplatPass"/> must be bound.</summary>
        public void DrawSplatMeshInstanced(IGpuCommandList cl, IGpuBuffer vb, IGpuBuffer ib, int indexCount,
            GpuIndexFormat indexFormat, uint instanceStart, uint instanceCount, IGpuResourceSet splatSet)
        {
            cl.SetGraphicsResourceSet(0, _splatFrameSet);
            cl.SetGraphicsResourceSet(1, splatSet);
            cl.SetVertexBuffer(0, vb);
            cl.SetVertexBuffer(1, _instanceBuffer!);
            cl.SetIndexBuffer(ib, indexFormat);
            cl.DrawIndexed((uint)indexCount, instanceCount, 0, 0, instanceStart);
        }

        /// <summary>Free the splat pipeline, both layouts, the shared frame set and the shader set. Called from
        /// <see cref="Dispose"/>.</summary>
        void DisposeSplatResources()
        {
            _splatPipeline.Dispose();
            _splatFrameSet.Dispose();
            _splatFrameLayout.Dispose();
            _splatMaterialLayout.Dispose();
            _splatShaders.Dispose();
        }
    }
}
