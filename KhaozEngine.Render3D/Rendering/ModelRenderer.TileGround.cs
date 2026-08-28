using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// The tile-world ground pipeline: one albedo texture array (a layer per catalog material), four corner
    /// material slots per triangle, blended per fragment by the vertex weights. Part of the
    /// <see cref="ModelRenderer"/> partial, beside the splat-terrain pipeline it mirrors.
    /// <para>
    /// TWO DESCRIPTOR SETS, split per-frame from per-material, exactly as the splat sibling is. Set 0 is the
    /// SHARED frame UBO, the same buffer the model pass binds, read by both stages. Set 1 is the material: its own
    /// params buffer written once at load, its albedo array, its sampler and the shadow map. Until this split both
    /// halves rode in ONE buffer per material, with the frame block re-uploaded into every one of them each frame,
    /// because the retired Veldrid Metal backend numbered a pipeline's uniform buffers by per-kind DECLARATION
    /// ORDER and a stage referencing fewer of them than the declared array read an index nothing had written.
    /// Section 2.3a of <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c> measured that as a property of
    /// the incumbent's numbering rather than of Metal, and measured exactly this shape (set 0 in both stages, a
    /// fragment-only second buffer at set 1) binding correctly on every backend the engine ships.
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/604">#604</see> lifted the rule and unfolded the
    /// splat and skinned passes, and
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/727">#727</see> is this one, the last combined
    /// frame-plus-params buffer in the tree.
    /// </para>
    /// </summary>
    internal sealed partial class ModelRenderer
    {
        // Tile-ground pipeline. Shares the frame UBO (_ubo, set 0) and the instance buffer + vertex/instance
        // layouts with the model and splat passes, so a tile-ground mesh is a plain GltfMesh and nothing in the
        // upload path moves. Its own layouts/shaders/pipeline live here.
        // Not readonly only because the constructor sets them through CreateTileGroundResources rather than inline.
        // Each is written exactly once, from that call, and never again (the pipeline additionally by SetOutputs).
        IGpuResourceLayout _tileGroundFrameLayout = null!;     // set 0: U, the shared frame block, both stages
        IGpuResourceSet _tileGroundFrameSet = null!;           // set 0's one set: _ubo, the buffer the model pass reads too
        IGpuResourceLayout _tileGroundMaterialLayout = null!;  // set 1: TileGroundParams + AlbedoArray + Sampler + shadow map/sampler
        IGpuShaderSet _tileGroundShaders = null!;
        IGpuPipeline _tileGroundPipeline = null!;              // rebuilt by SetOutputs alongside _pipeline (set via BuildTileGroundPipeline)

        /// <summary>Create the tile-ground resource layouts, the shared frame set and the shader set. All three are
        /// sample-count-independent, so this runs once from the constructor (after <c>_ubo</c> and the shadow map
        /// exist) and only the pipeline is rebuilt when the MRT changes.</summary>
        void CreateTileGroundResources(IGpuResourceFactory factory)
        {
            _tileGroundFrameLayout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex | GpuShaderStages.Fragment)));
            _tileGroundFrameSet = factory.CreateResourceSet(new GpuResourceSetDescription(_tileGroundFrameLayout, _ubo));

            // The textures are declared in the order the fragment samples them, with the SHADOW MAP LAST. That was
            // a Metal requirement under the incumbent's numbering and is now the engine's own convention: the
            // native backend authors each index, so the emission and the binder agree by construction.
            _tileGroundMaterialLayout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("TileGroundParams", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("AlbedoArray", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Sampler", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("ShadowMap", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("ShadowSamp", GpuResourceKind.Sampler, GpuShaderStages.Fragment)));

            _tileGroundShaders = factory.CreateShadersFromSpirv(ShaderSources.TileGroundVert, ShaderSources.TileGroundFrag);
        }

        /// <summary>Build the tile-ground pipeline from the MRT outputs and the shared vertex + instance layouts.
        /// Called by <see cref="BuildPipelines"/>, so <see cref="SetOutputs"/> rebuilds it with the rest when the
        /// sample count changes. The layout array order IS the set numbering the backends flatten registers in, so
        /// set 0 (frame) comes before set 1 (material).</summary>
        void BuildTileGroundPipeline(IGpuResourceFactory factory, GpuOutputDescription modelOutputs,
            GpuVertexLayoutDescription vertexLayout, GpuVertexLayoutDescription instanceLayout)
        {
            _tileGroundPipeline = factory.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend, GpuBlendAttachment.OverrideBlend, GpuBlendAttachment.OverrideBlend },
                DepthStencil = GpuDepthStencilState.DepthOnlyLessEqual,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _tileGroundFrameLayout, _tileGroundMaterialLayout },
                ShaderSet = _tileGroundShaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout, instanceLayout },
                Outputs = modelOutputs,
            });
        }

        /// <summary>Create a tile-ground material's PARAMS UBO: <see cref="TileGroundMaterialConfig.ParamsBytes"/>
        /// holding <paramref name="paramsTail"/> alone, written once here and never re-uploaded. The frame block is
        /// not in it any more (#727), so this is pure load-time material data and there is no CPU mirror to keep:
        /// the write covers the whole buffer from offset 0, which is also the cheap Direct3D 11 route. (#408's
        /// blocking Map was caused by the PARTIAL per-frame write into the larger combined buffer this replaces,
        /// and that write is gone rather than optimised.) Owned by Scene3D and shared by every mesh using this
        /// material.</summary>
        public IGpuBuffer CreateTileGroundParamsUbo(Vector4[] paramsTail)
        {
            if (paramsTail.Length * 16 != TileGroundMaterialConfig.ParamsBytes)
                throw new ArgumentException(
                    $"a tile-ground params tail is {TileGroundMaterialConfig.ParamsBytes} bytes, got {paramsTail.Length * 16}.",
                    nameof(paramsTail));
            var ubo = _gd.Factory.CreateBuffer(new GpuBufferDescription(
                TileGroundMaterialConfig.ParamsBytes, GpuBufferUsage.UniformBuffer));
            _gd.UpdateBuffer(ubo, 0, paramsTail);
            return ubo;
        }

        /// <summary>Build a tile-ground material resource set (set 1): the material's params UBO + the albedo
        /// texture array + the shared terrain (wrap/anisotropic) sampler + the shadow map. Shared across every mesh
        /// using this material and owned by Scene3D, NOT per mesh.</summary>
        public IGpuResourceSet CreateTileGroundMaterialSet(IGpuBuffer paramsUbo, IGpuTexture albedoArray) =>
            CreateTileGroundMaterialSet(paramsUbo, albedoArray, _terrainSampler);

        /// <summary>As above, but binds an explicit <paramref name="sampler"/> instead of the shared default one
        /// (used by a material that overrides its <see cref="TerrainSamplerConfig"/>). The caller owns that sampler.</summary>
        public IGpuResourceSet CreateTileGroundMaterialSet(IGpuBuffer paramsUbo, IGpuTexture albedoArray, IGpuSampler sampler) =>
            _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(
                _tileGroundMaterialLayout, paramsUbo, albedoArray, sampler,
                _shadowMap.ShadowTexture, _shadowMap.ShadowSampler));

        /// <summary>Bind the tile-ground pipeline for the tile-ground pass (call once before its draw loop). The
        /// frame block it reads is the shared one <see cref="SetFrameUniforms"/> already uploaded this frame, so
        /// since #727 there is nothing per-material to re-sync first.</summary>
        public void BindTileGroundPass(IGpuCommandList cl) => cl.SetPipeline(_tileGroundPipeline);

        /// <summary>Draw one tile-ground mesh run through the tile-ground pipeline, reusing the shared instance
        /// buffer. Set 0 is the shared frame block and set 1 is <paramref name="groundSet"/>, the material's params
        /// UBO + albedo array + sampler. Both are bound per draw, the way the splat pass binds its own pair.
        /// <see cref="BindTileGroundPass"/> must be bound.</summary>
        public void DrawTileGroundMeshInstanced(IGpuCommandList cl, IGpuBuffer vb, IGpuBuffer ib, int indexCount,
            GpuIndexFormat indexFormat, uint instanceStart, uint instanceCount, IGpuResourceSet groundSet)
        {
            cl.SetGraphicsResourceSet(0, _tileGroundFrameSet);
            cl.SetGraphicsResourceSet(1, groundSet);
            cl.SetVertexBuffer(0, vb);
            cl.SetVertexBuffer(1, _instanceBuffer!);
            cl.SetIndexBuffer(ib, indexFormat);
            cl.DrawIndexed((uint)indexCount, instanceCount, 0, 0, instanceStart);
        }

        /// <summary>Free the tile-ground pipeline, both layouts, the shared frame set and the shader set. Called
        /// from <see cref="Dispose"/>.</summary>
        void DisposeTileGroundResources()
        {
            _tileGroundPipeline.Dispose();
            _tileGroundFrameSet.Dispose();
            _tileGroundFrameLayout.Dispose();
            _tileGroundMaterialLayout.Dispose();
            _tileGroundShaders.Dispose();
        }
    }
}
