using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>The tile-world ground pipeline: one albedo texture array (a layer per catalog material), four corner
    /// material slots per triangle, blended per fragment by the vertex weights. Part of the
    /// <see cref="ModelRenderer"/> partial, beside the splat-terrain pipeline it mirrors.</summary>
    internal sealed partial class ModelRenderer
    {
        // Tile-ground pipeline. Shares _ubo's frame-block SHAPE (each material carries its own combined buffer) and
        // the instance buffer + vertex/instance layouts with the model and splat passes, so a tile-ground mesh is a
        // plain GltfMesh and nothing in the upload path moves. Its own layout/shaders/pipeline live here.
        // Not readonly only because the constructor sets them through CreateTileGroundResources rather than inline.
        // Both are written exactly once, from that call, and never again.
        IGpuResourceLayout _tileGroundLayout = null!;   // U (frame + params, one UBO) + AlbedoArray + Sampler + shadow map + shadow sampler
        IGpuShaderSet _tileGroundShaders = null!;
        IGpuPipeline _tileGroundPipeline = null!;       // rebuilt by SetOutputs alongside _pipeline (set via BuildTileGroundPipeline)

        /// <summary>Create the tile-ground resource layout and shader set. Both are sample-count-independent, so
        /// this runs once from the constructor and only the pipeline is rebuilt when the MRT changes.</summary>
        void CreateTileGroundResources(IGpuResourceFactory factory)
        {
            // ONE descriptor set, ONE uniform buffer, for the same reason the splat pass has one: Veldrid/SPIRV-Cross
            // on Metal mis-binds a SECOND uniform buffer in a pipeline (the second reads the first buffer's bytes).
            // The per-material params therefore ride in the same combined buffer, appended after the frame block.
            // The textures are declared in the order the fragment samples them, with the SHADOW MAP LAST: Metal
            // wants the sample order to follow the binding order, which is what put the shadow map at the end of
            // the splat layout too.
            _tileGroundLayout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex | GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("AlbedoArray", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Sampler", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("ShadowMap", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("ShadowSamp", GpuResourceKind.Sampler, GpuShaderStages.Fragment)));

            _tileGroundShaders = factory.CreateShadersFromSpirv(ShaderSources.TileGroundVert, ShaderSources.TileGroundFrag);
        }

        /// <summary>Build the tile-ground pipeline from the MRT outputs and the shared vertex + instance layouts.
        /// Called by <see cref="BuildPipelines"/>, so <see cref="SetOutputs"/> rebuilds it with the rest when the
        /// sample count changes.</summary>
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
                ResourceLayouts = new[] { _tileGroundLayout },
                ShaderSet = _tileGroundShaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout, instanceLayout },
                Outputs = modelOutputs,
            });
        }

        /// <summary>Create a tile-ground material's combined UBO: <see cref="UboBytes"/> of frame uniforms (re-synced
        /// each frame via <see cref="WriteFrameUniformsTo(IGpuCommandList,TileGroundUniformBuffer)"/>) followed by
        /// <paramref name="paramsTail"/> at offset <see cref="UboBytes"/>. One uniform buffer holds both, so the
        /// pipeline binds a single UBO. The returned wrapper keeps the tail on the CPU so each frame's re-sync is a
        /// whole-buffer write rather than a partial one (#408). Owned by Scene3D and shared by every mesh using this
        /// material.</summary>
        public TileGroundUniformBuffer CreateTileGroundParamsUbo(Vector4[] paramsTail)
        {
            if (paramsTail.Length * 16 != TileGroundMaterialConfig.ParamsBytes)
                throw new ArgumentException(
                    $"a tile-ground params tail is {TileGroundMaterialConfig.ParamsBytes} bytes, got {paramsTail.Length * 16}.",
                    nameof(paramsTail));
            var ubo = _gd.Factory.CreateBuffer(new GpuBufferDescription(
                UboBytes + TileGroundMaterialConfig.ParamsBytes, GpuBufferUsage.UniformBuffer));
            _gd.UpdateBuffer(ubo, UboBytes, paramsTail);
            return new TileGroundUniformBuffer(ubo, paramsTail, UboBytes);
        }

        /// <summary>Build a tile-ground material resource set: the combined frame+params UBO + the albedo texture
        /// array + the shared terrain (wrap/anisotropic) sampler. Shared across every mesh using this material and
        /// owned by Scene3D, NOT per mesh.</summary>
        public IGpuResourceSet CreateTileGroundMaterialSet(IGpuBuffer combinedUbo, IGpuTexture albedoArray) =>
            CreateTileGroundMaterialSet(combinedUbo, albedoArray, _terrainSampler);

        /// <summary>As above, but binds an explicit <paramref name="sampler"/> instead of the shared default one
        /// (used by a material that overrides its <see cref="TerrainSamplerConfig"/>). The caller owns that sampler.</summary>
        public IGpuResourceSet CreateTileGroundMaterialSet(IGpuBuffer combinedUbo, IGpuTexture albedoArray, IGpuSampler sampler) =>
            _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(
                _tileGroundLayout, combinedUbo, albedoArray, sampler,
                _shadowMap.ShadowTexture, _shadowMap.ShadowSampler));

        /// <summary>Bind the tile-ground pipeline for the tile-ground pass (call once before its draw loop). Each
        /// material's combined UBO must already hold this frame's uniforms
        /// (<see cref="WriteFrameUniformsTo(IGpuCommandList,TileGroundUniformBuffer)"/>).</summary>
        public void BindTileGroundPass(IGpuCommandList cl) => cl.SetPipeline(_tileGroundPipeline);

        /// <summary>Draw one tile-ground mesh run through the tile-ground pipeline, reusing the shared instance
        /// buffer. <paramref name="groundSet"/> carries the material's combined UBO + albedo array + sampler.
        /// <see cref="BindTileGroundPass"/> must be bound.</summary>
        public void DrawTileGroundMeshInstanced(IGpuCommandList cl, IGpuBuffer vb, IGpuBuffer ib, int indexCount,
            GpuIndexFormat indexFormat, uint instanceStart, uint instanceCount, IGpuResourceSet groundSet)
        {
            cl.SetGraphicsResourceSet(0, groundSet);
            cl.SetVertexBuffer(0, vb);
            cl.SetVertexBuffer(1, _instanceBuffer!);
            cl.SetIndexBuffer(ib, indexFormat);
            cl.DrawIndexed((uint)indexCount, instanceCount, 0, 0, instanceStart);
        }

        /// <summary>Free the tile-ground pipeline, layout and shader set. Called from <see cref="Dispose"/>.</summary>
        void DisposeTileGroundResources()
        {
            _tileGroundPipeline.Dispose();
            _tileGroundLayout.Dispose();
            _tileGroundShaders.Dispose();
        }
    }
}
