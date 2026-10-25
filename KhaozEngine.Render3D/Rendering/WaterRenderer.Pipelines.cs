using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    internal sealed partial class WaterRenderer
    {
        // Geometry and optical capability are independent. Flat quads share the focused layout.
        readonly IGpuResourceLayout _layout;
        readonly WaterProgram _defaultProgram;
        WaterProgram? _clipProgram, _triangleProgram, _clipTriangleProgram;
        GpuOutputDescription _outputs;
        GpuOutputDescription? _pendingOutputs;

        sealed class WaterProgram(IGpuShaderSet shaders, IGpuPipeline pipeline) : IDisposable
        {
            internal readonly IGpuShaderSet Shaders = shaders;
            internal IGpuPipeline Pipeline = pipeline;
            public void Dispose() { Pipeline.Dispose(); Shaders.Dispose(); }
        }

        static IGpuResourceLayout CreateWaterLayout(IGpuResourceFactory f)
        {
            return f.CreateResourceLayout(new GpuResourceLayoutDescription(AppendOpticsLayout(AtmosphereBindings.AppendLayout(
                // ORDER WAS LOAD-BEARING, and not for the usual reason. Veldrid numbered a backend's resource slots
                // with one counter PER KIND over this whole list, binding each element to the stages in its mask,
                // while the cross-compiler numbered each stage DENSELY over only the bindings that stage declares.
                // Those agreed only when every stage's resources were a PREFIX of this list. The vertex stage uses
                // the bathymetry field, the ocean map and the shared UBO and nothing else, so both of those
                // textures had to precede the fragment-only scene depth and their samplers likewise. Put the
                // scene depth first instead and the vertex sampled an unbound slot and read zero, on Metal,
                // silently. That is also why the ocean is ONE array texture (displacement layers then derivative
                // layers) rather than the two it reads as.
                //
                // THAT BACKEND IS GONE (18.0.0) AND THE PREFIX RULE WITH IT (#604). The engine authors each Metal
                // argument index off this list and the native backend binds against the same scheme, so a
                // fragment-only element ahead of a vertex-read one is legal now. The order below is KEPT: moving it
                // moves the emission on all three backends and buys nothing. Bathymetry leads the ocean for a
                // SECOND reason anyway, one that never depended on the numbering: the vertex needs the depth before
                // it sums the cascades, because the shoaling taper is per cascade and applied inside that loop.
                // See ShaderSources.WaterShore.cs.
                new GpuResourceLayoutElement("BathyTex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Vertex | GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("BathySamp", GpuResourceKind.Sampler, GpuShaderStages.Vertex | GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("OceanMap", GpuResourceKind.TextureReadOnly, GpuShaderStages.Vertex | GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("OceanSamp", GpuResourceKind.Sampler, GpuShaderStages.Vertex | GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("DepthTex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
                // Dynamic-offset UBO read by BOTH stages (the vertex shader only needs ViewProj, folded into the
                // same buffer under the one-UBO-per-set rule #604 retired, and left folded).
                new GpuResourceLayoutElement("Water", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex | GpuShaderStages.Fragment, dynamic: true)))));
        }

        WaterProgram CreateWaterProgram(GpuOutputDescription outputs, bool clipmap, bool triangle)
        {
            IGpuShaderSet? shaders = null;
            IGpuPipeline? pipeline = null;
            try
            {
                shaders = _gd.Factory.CreateShadersFromSpirv(
                    clipmap ? ShaderSources.WaterClipmapVert : ShaderSources.WaterVert,
                    triangle ? ShaderSources.WaterTriangleFrag : ShaderSources.WaterFrag);
                pipeline = Pipe(_gd.Factory, outputs, shaders, clipmap);
                return new WaterProgram(shaders, pipeline);
            }
            catch
            {
                pipeline?.Dispose();
                shaders?.Dispose();
                throw;
            }
        }

        void PrepareWaterProgram(PlaneRoute route, bool triangle)
        {
            bool clipmap = route == PlaneRoute.Clipmap;
            if (triangle)
            {
                if (clipmap) _clipTriangleProgram ??= CreateWaterProgram(_outputs, true, true);
                else _triangleProgram ??= CreateWaterProgram(_outputs, false, true);
            }
            else if (clipmap) _clipProgram ??= CreateWaterProgram(_outputs, true, false);
        }

        // Triangle programs exist only after a bound preparation. Unbound routes are created before drawing,
        // at preparation or lazily by the legacy late binding in Draw.
        IGpuPipeline PreparedWaterPipeline(PlaneRoute route, bool triangle)
        {
            WaterProgram? program = triangle
                ? route == PlaneRoute.Clipmap ? _clipTriangleProgram : _triangleProgram
                : route == PlaneRoute.Clipmap ? _clipProgram : _defaultProgram;
            return program?.Pipeline ?? throw ChangedOpticsInputs();
        }

        /// <summary>Replace every existing variant as one transaction. Failed replacements leave all old
        /// pipelines usable for retry. Submitted old pipelines retire through the scene completion queue.</summary>
        public void SetOutputs(GpuOutputDescription colorOutput)
        {
            _pendingOutputs = colorOutput;
            _opticsPrepared = false;
            WaterProgram?[] programs = [_defaultProgram, _clipProgram, _triangleProgram, _clipTriangleProgram];
            IGpuPipeline?[] replacements = new IGpuPipeline?[4];
            try
            {
                for (int i = 0; i < programs.Length; i++)
                    if (programs[i] is { } program)
                        replacements[i] = Pipe(_gd.Factory, colorOutput, program.Shaders, i is 1 or 3);
            }
            catch
            {
                foreach (IGpuPipeline? replacement in replacements) replacement?.Dispose();
                throw;
            }
            for (int i = 0; i < programs.Length; i++)
                if (programs[i] is { } program)
                {
                    _retired.Retire(program.Pipeline);
                    program.Pipeline = replacements[i]!;
                }
            _outputs = colorOutput;
            _pendingOutputs = null;
        }

        void RetryWaterOutputReplacement()
        {
            if (_pendingOutputs is { } outputs) SetOutputs(outputs);
        }

        void DisposeWaterPrograms()
        {
            _clipTriangleProgram?.Dispose();
            _triangleProgram?.Dispose();
            _clipProgram?.Dispose();
            _defaultProgram.Dispose();
            _layout.Dispose();
        }
        IGpuPipeline Pipe(IGpuResourceFactory f, GpuOutputDescription outputs, IGpuShaderSet shaders, bool clipmap)
        {
            // Position only for the camera-focused grid; the clipmap adds the coarse-neighbour offset, the morphed
            // band-limit spacing and the morph weight (WaterClipmapVertex), which is why it cannot share this
            // pipeline.
            var vertexLayout = clipmap
                ? new GpuVertexLayoutDescription(
                    new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                    new GpuVertexElement("Coarse", GpuVertexElementFormat.Float2),
                    new GpuVertexElement("Cell", GpuVertexElementFormat.Float1),
                    new GpuVertexElement("Morph", GpuVertexElementFormat.Float1))
                : new GpuVertexLayoutDescription(
                    new GpuVertexElement("Position", GpuVertexElementFormat.Float3));
            return f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.AlphaBlend },
                // Depth test ON (Less) so terrain/props above the surface occlude the water; depth WRITE off so the
                // resolved normal/linear-depth MRT the outline pass reads stays exactly what the opaque geometry
                // wrote (see the WaterVert in-source note for the full reasoning).
                DepthStencil = new GpuDepthStencilState(depthTestEnabled: true, depthWriteEnabled: false, GpuComparison.Less),
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout },
                Outputs = outputs,
            });
        }

    }
}
