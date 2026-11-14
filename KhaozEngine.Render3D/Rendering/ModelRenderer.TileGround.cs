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

        // The RELIEF variants (TileGroundRelief): the same vertex shader and frame set, their own fragment and a set 1
        // with the SurfaceArray beside the albedo array, in the splat pass's binding order. Two of them, the second
        // with hex-tiling compiled in, and a mesh draws with the cheapest program its slots need
        // (TileGroundProgram). Each is built by the first relief material load that can need it
        // (EnsureTileGroundRelief), so a renderer that never loads one compiles nothing more than it always did.
        IGpuResourceLayout _tileGroundReliefMaterialLayout = null!;  // set 1: params + AlbedoArray + SurfaceArray + Sampler + shadow maps
        IGpuShaderSet? _tileGroundReliefShaders, _tileGroundReliefHexShaders;
        IGpuPipeline? _tileGroundReliefPipeline, _tileGroundReliefHexPipeline;
        // What the last BuildTileGroundPipeline was given, so a relief pipeline built later matches the plain one.
        GpuOutputDescription _tileGroundOutputs;
        GpuVertexLayoutDescription _tileGroundVertexLayout, _tileGroundInstanceLayout;

        /// <summary>Create the tile-ground resource layouts, the shared frame set and the shader set. All three are
        /// sample-count-independent, so this runs once from the constructor (after <c>_ubo</c> and the shadow map
        /// exist) and only the pipeline is rebuilt when the MRT changes.</summary>
        void CreateTileGroundResources(IGpuResourceFactory factory)
        {
            _tileGroundFrameLayout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(AppendWaterVolumeLayout(AtmosphereBindings.AppendLayout(
                new GpuResourceLayoutElement("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex | GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("PointLights", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("PointLightClusters", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Fragment)))));
            _tileGroundFrameSet = factory.CreateResourceSet(new GpuResourceSetDescription(
                _tileGroundFrameLayout, AppendWaterVolumeResources(_atmosphere.AppendResources(_ubo, _pointLightBuffer, _pointLightClusterBuffer))));

            // The textures are declared in the order the fragment samples them, with the SHADOW MAP LAST. That was
            // a Metal requirement under the incumbent's numbering and is now the engine's own convention: the
            // native backend authors each index, so the emission and the binder agree by construction.
            _tileGroundMaterialLayout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("TileGroundParams", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("AlbedoArray", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Sampler", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("ShadowMap", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("ShadowSamp", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("PointShadowMap", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("PointShadowTransientMap", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment)));

            _tileGroundShaders = factory.CreateShadersFromSpirv(ShaderSources.TileGroundVert, ShaderSources.TileGroundFrag);

            _tileGroundReliefMaterialLayout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("TileGroundParams", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("AlbedoArray", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("SurfaceArray", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Sampler", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("ShadowMap", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("ShadowSamp", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("PointShadowMap", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("PointShadowTransientMap", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment)));
        }

        /// <summary>Build the tile-ground pipelines, plain and relief, from the MRT outputs and the shared vertex +
        /// instance layouts. Called by <see cref="BuildPipelines"/>, so <see cref="SetOutputs"/> rebuilds them with
        /// the rest when the sample count changes. The layout array order IS the set numbering the backends flatten
        /// registers in, so set 0 (frame) comes before set 1 (material), and against a temporal target set 2 is the
        /// motion block.</summary>
        void BuildTileGroundPipeline(IGpuResourceFactory factory, GpuOutputDescription modelOutputs,
            GpuVertexLayoutDescription vertexLayout, GpuVertexLayoutDescription instanceLayout)
        {
            _tileGroundOutputs = modelOutputs;
            _tileGroundVertexLayout = vertexLayout;
            _tileGroundInstanceLayout = instanceLayout;
            _tileGroundPipeline = BuildTileGroundVariant(factory, modelOutputs, vertexLayout, instanceLayout,
                _tileGroundMaterialLayout, ShaderSources.TileGroundFrag, ShaderSources.TileGroundMotionFrag,
                _tileGroundShaders, motion => motion.TileGroundShaders);
            // Rebuilt only once some relief material has asked for them.
            if (_tileGroundReliefShaders is not null) BuildTileGroundReliefPipeline(factory, hex: false);
            if (_tileGroundReliefHexShaders is not null) BuildTileGroundReliefPipeline(factory, hex: true);
        }

        /// <summary>Build a relief variant's shaders and pipeline if no relief material has yet, against the outputs
        /// the plain pipeline was last built for: the hex variant when <paramref name="hex"/>, the other otherwise.
        /// Called by relief material loads, outside frame recording.</summary>
        internal void EnsureTileGroundRelief(bool hex = false)
        {
            if ((hex ? _tileGroundReliefHexPipeline : _tileGroundReliefPipeline) is not null) return;
            if (hex)
                _tileGroundReliefHexShaders ??= _gd.Factory.CreateShadersFromSpirv(ShaderSources.TileGroundVert,
                    ShaderSources.TileGroundReliefHexFrag);
            else
                _tileGroundReliefShaders ??= _gd.Factory.CreateShadersFromSpirv(ShaderSources.TileGroundVert,
                    ShaderSources.TileGroundReliefFrag);
            BuildTileGroundReliefPipeline(_gd.Factory, hex);
        }

        void BuildTileGroundReliefPipeline(IGpuResourceFactory factory, bool hex)
        {
            if (hex)
                _tileGroundReliefHexPipeline = BuildTileGroundVariant(factory, _tileGroundOutputs, _tileGroundVertexLayout,
                    _tileGroundInstanceLayout, _tileGroundReliefMaterialLayout, ShaderSources.TileGroundReliefHexFrag,
                    ShaderSources.TileGroundReliefHexMotionFrag, _tileGroundReliefHexShaders!,
                    motion => motion.TileGroundReliefHexShaders);
            else
                _tileGroundReliefPipeline = BuildTileGroundVariant(factory, _tileGroundOutputs, _tileGroundVertexLayout,
                    _tileGroundInstanceLayout, _tileGroundReliefMaterialLayout, ShaderSources.TileGroundReliefFrag,
                    ShaderSources.TileGroundReliefMotionFrag, _tileGroundReliefShaders!, motion => motion.TileGroundReliefShaders);
        }

        IGpuPipeline BuildTileGroundVariant(IGpuResourceFactory factory, GpuOutputDescription modelOutputs,
            GpuVertexLayoutDescription vertexLayout, GpuVertexLayoutDescription instanceLayout,
            IGpuResourceLayout materialLayout, string fragment, string motionFragment, IGpuShaderSet shaders,
            Func<ModelMotionResources, IGpuShaderSet> motionShaders)
        {
            if (_motion is { } motion && MotionMath.IsTemporal(modelOutputs))
            {
                // The temporal variant: the ground's two sets, then the motion block at set 2.
                return OpaqueMotionPipeline(factory, modelOutputs, _visibilityShaders.TrySelect(factory, modelOutputs, ShaderSources.TileGroundMotionVert, motionFragment, preserve: false, out IGpuShaderSet motionVisibilityShaders)
                    ? motionVisibilityShaders : motionShaders(motion),
                    new[] { _tileGroundFrameLayout, materialLayout, motion.FrameLayout },
                    new List<GpuVertexLayoutDescription> { vertexLayout, instanceLayout });
            }
            return factory.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = ModelTargetBlends.Opaque(modelOutputs),
                DepthStencil = GpuDepthStencilState.DepthOnlyLessEqual,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _tileGroundFrameLayout, materialLayout },
                ShaderSet = _visibilityShaders.TrySelect(factory, modelOutputs, ShaderSources.TileGroundVert, fragment, preserve: false, out IGpuShaderSet visibilityShaders)
                    ? visibilityShaders : shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout, instanceLayout },
                Outputs = modelOutputs,
            });
        }

        // SetOutputs retires both pipelines with the rest before BuildPipelines builds their replacements.
        void RetireTileGroundPipelines()
        {
            _retired.Retire(_tileGroundPipeline);
            if (_tileGroundReliefPipeline is not null) _retired.Retire(_tileGroundReliefPipeline);
            if (_tileGroundReliefHexPipeline is not null) _retired.Retire(_tileGroundReliefHexPipeline);
            _tileGroundReliefPipeline = _tileGroundReliefHexPipeline = null;
        }

        /// <summary>Create a tile-ground material's PARAMS UBO holding <paramref name="paramsTail"/> alone, written
        /// once here and never re-uploaded: <see cref="TileGroundMaterialConfig.ParamsBytes"/> for a plain material,
        /// <see cref="TileGroundMaterialConfig.ReliefParamsBytes"/> for a relief one. The frame block is not in it any
        /// more (#727), so this is pure load-time material data and there is no CPU mirror to keep: the write covers
        /// the whole buffer from offset 0, which is also the cheap Direct3D 11 route. (#408's blocking Map was caused
        /// by the PARTIAL per-frame write into the larger combined buffer this replaces, and that write is gone
        /// rather than optimised.) Owned by Scene3D and shared by every mesh using this material.</summary>
        public IGpuBuffer CreateTileGroundParamsUbo(Vector4[] paramsTail)
        {
            uint bytes = (uint)paramsTail.Length * 16;
            if (bytes != TileGroundMaterialConfig.ParamsBytes && bytes != TileGroundMaterialConfig.ReliefParamsBytes)
                throw new ArgumentException(
                    $"a tile-ground params tail is {TileGroundMaterialConfig.ParamsBytes} bytes, or " +
                    $"{TileGroundMaterialConfig.ReliefParamsBytes} for a relief material, got {bytes}.",
                    nameof(paramsTail));
            var ubo = _gd.Factory.CreateBuffer(new GpuBufferDescription(bytes, GpuBufferUsage.UniformBuffer));
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
            CreateShadowSamplingSet(_tileGroundMaterialLayout, _shadowMap.ShadowTexture,
                paramsUbo, albedoArray, sampler);

        /// <summary>Build a RELIEF tile-ground material resource set (set 1): the material's relief params UBO, the
        /// albedo array, the surface array, the sampler (the shared terrain one unless <paramref name="sampler"/>
        /// overrides it, which the caller then owns) and the shadow maps. Tracked like the plain set, so a shadow
        /// layout change rebuilds it.</summary>
        public IGpuResourceSet CreateTileGroundReliefMaterialSet(IGpuBuffer paramsUbo, IGpuTexture albedoArray,
            IGpuTexture surfaceArray, IGpuSampler? sampler = null) =>
            CreateShadowSamplingSet(_tileGroundReliefMaterialLayout, _shadowMap.ShadowTexture,
                paramsUbo, albedoArray, surfaceArray, sampler ?? _terrainSampler);

        /// <summary>Bind the tile-ground pipeline of <paramref name="program"/> for the tile-ground pass. The pass
        /// binds again only when a run's mesh needs another program. The frame block it reads is the shared one
        /// <see cref="SetFrameUniforms"/> already uploaded this frame, so since #727 there is nothing per-material to
        /// re-sync first.</summary>
        public void BindTileGroundPass(IGpuCommandList cl, TileGroundProgram program = TileGroundProgram.Plain) =>
            cl.SetPipeline(program switch
            {
                TileGroundProgram.Plain => _tileGroundPipeline,
                TileGroundProgram.Relief => _tileGroundReliefPipeline,
                _ => _tileGroundReliefHexPipeline,
            } ?? throw new InvalidOperationException(
                $"a tile-ground mesh needs the {program} program, but no relief material load built its pipeline."));

        /// <summary>Draw one tile-ground mesh run through the tile-ground pipeline, reusing the shared instance
        /// buffer. Set 0 is the shared frame block and set 1 is <paramref name="groundSet"/>, the material's params
        /// UBO + albedo array + sampler. Both are bound per draw, the way the splat pass binds its own pair, and
        /// against a temporal target so is the motion block at set 2. <see cref="BindTileGroundPass"/> must be
        /// bound.</summary>
        public void DrawTileGroundMeshInstanced(IGpuCommandList cl, IGpuBuffer vb, IGpuBuffer ib, int indexCount,
            GpuIndexFormat indexFormat, uint instanceStart, uint instanceCount, IGpuResourceSet groundSet)
        {
            cl.SetGraphicsResourceSet(0, _tileGroundFrameSet);
            cl.SetGraphicsResourceSet(1, groundSet);
            if (_motion is { } motion) cl.SetGraphicsResourceSet(2, motion.FrameSet);   // the ground variant's motion block
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
            _tileGroundReliefPipeline?.Dispose();
            _tileGroundReliefHexPipeline?.Dispose();
            _tileGroundFrameSet.Dispose();
            _tileGroundFrameLayout.Dispose();
            _tileGroundMaterialLayout.Dispose();
            _tileGroundReliefMaterialLayout.Dispose();
            _tileGroundShaders.Dispose();
            _tileGroundReliefShaders?.Dispose();
            _tileGroundReliefHexShaders?.Dispose();
        }
    }
}
