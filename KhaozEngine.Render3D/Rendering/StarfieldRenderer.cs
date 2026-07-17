using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// Draws the procedural starfield as a single fullscreen BACKGROUND pass into the lit colour attachment +
    /// read-only scene depth (ColorDepthFB), exactly like <see cref="SkyRenderer"/>: the fullscreen triangle sits at
    /// the far plane (StarfieldVert, z=1) and a read-only Equal depth test passes ONLY where the stored depth still
    /// EQUALS the cleared far plane, i.e. background pixels where no geometry was drawn. Geometry (depth &lt; 1)
    /// rejects the stars, so the pass never overwrites the scene and never touches the MRT normal / linear-depth
    /// attachments the outline pass reads (ColorDepthFB binds only colour + depth). Runs after the model pass wrote
    /// depth and before the decals + post chain, so the stars flow through the pixel post like the rest of the
    /// scene. Zero cost when the background is not the starfield (the scene skips this pass entirely). One UBO, one
    /// draw.
    /// </summary>
    /// <remarks>
    /// The stars used to be generated at the END of the chain, in the final blit, which rebuilt the background from
    /// the clear colour wherever the colour target's alpha marker read &lt; 0.5. That discarded anything drawn at a
    /// background pixel, so translucent content over the void was either erased or punched a star-free hole. Painting
    /// the stars into the scene up front removes that whole class of bug (see the 11.8.0 CHANGELOG entry).
    /// </remarks>
    internal sealed class StarfieldRenderer : IDisposable
    {
        /// <summary>32-byte UBO matching the Starfield block in <see cref="ShaderSources.StarfieldFrag"/> (2 vec4,
        /// every member 16-byte aligned, so std140 needs no extra padding). Size-checked by the UboLayoutTests
        /// tripwire.</summary>
        public struct StarfieldUbo
        {
            public Vector4 BgColor;   // rgb = the scene clear colour the stars sit on
            public Vector4 Res;       // xy = 1/renderWidth, 1/renderHeight
        }

        /// <summary>Byte size of <see cref="StarfieldUbo"/> / the GPU uniform buffer. 2 * 16 (vec4) = 32.</summary>
        internal const uint UboBytes = 32;

        readonly IGpuDevice _gd;
        readonly IGpuShaderSet _shaders;
        readonly IGpuResourceLayout _layout;
        readonly IGpuBuffer _ubo;
        readonly IGpuResourceSet _set;
        IGpuPipeline _pipe;   // rebuilt by SetOutputs when the MRT sample count (MSAA) changes

        public StarfieldRenderer(IGpuDevice gd, GpuOutputDescription colorOutput)
        {
            _gd = gd;
            var f = gd.Factory;
            _shaders = f.CreateShadersFromSpirv(ShaderSources.StarfieldVert, ShaderSources.StarfieldFrag);
            _layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Starfield", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment)));
            _ubo = f.CreateBuffer(new GpuBufferDescription(UboBytes, GpuBufferUsage.UniformBuffer));
            _set = f.CreateResourceSet(new GpuResourceSetDescription(_layout, _ubo));
            _pipe = Pipe(f, colorOutput);
        }

        /// <summary>Rebuild the pipeline for a new colour-target output description (e.g. the MRT became multisampled
        /// for MSAA, and a pipeline's sample count must match its framebuffer). Layout / shaders / buffer are kept.</summary>
        public void SetOutputs(GpuOutputDescription colorOutput)
        {
            _pipe.Dispose();
            _pipe = Pipe(_gd.Factory, colorOutput);
        }

        IGpuPipeline Pipe(IGpuResourceFactory f, GpuOutputDescription outputs) =>
            f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend },
                // Read-only, Equal depth test: the far-plane triangle (StarfieldVert, z=1) passes only where the
                // stored depth still EQUALS the cleared far plane (fragZ==1==storedZ), i.e. true background where no
                // geometry was drawn. Geometry sits at depth < 1 (LessEqual model pass) so it fails Equal and
                // occludes the stars. GreaterEqual would be wrong here: 1 >= any storedZ, so the stars would paint
                // over ALL geometry. No depth write, so the scene depth is untouched for any later pass.
                DepthStencil = new GpuDepthStencilState(depthTestEnabled: true, depthWriteEnabled: false, GpuComparison.Equal),
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: false, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = _shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription>(),
                Outputs = outputs,
            });

        /// <summary>Pure: pack the clear colour + the inverse render size into the UBO. The fragment rebuilds its UV
        /// from gl_FragCoord (upper-left on every backend) times <see cref="StarfieldUbo.Res"/>, the same
        /// backend-independent convention SkyFrag and DecalFrag use.</summary>
        public static StarfieldUbo PackUbo(Color bgColor, int renderWidth, int renderHeight)
        {
            float invW = renderWidth > 0 ? 1f / renderWidth : 0f;
            float invH = renderHeight > 0 ? 1f / renderHeight : 0f;
            Vector4 bg = bgColor;
            return new StarfieldUbo
            {
                BgColor = new Vector4(bg.X, bg.Y, bg.Z, 0f),
                Res = new Vector4(invW, invH, 0f, 0f),
            };
        }

        /// <summary>Draw the starfield into ColorDepthFB (lit colour + read-only scene depth). Caller guarantees the
        /// model pass is complete (depth written) and the framebuffer is free to rebind.</summary>
        public void Draw(IGpuCommandList cl, RenderResources res, Color bgColor)
        {
            var u = PackUbo(bgColor, res.Width, res.Height);
            cl.UpdateBuffer(_ubo, 0, in u);
            cl.SetFramebuffer(res.ColorDepthFB);
            cl.SetPipeline(_pipe);
            cl.SetGraphicsResourceSet(0, _set);
            cl.Draw(3);
        }

        public void Dispose()
        {
            _set.Dispose();
            _pipe.Dispose();
            _layout.Dispose();
            _shaders.Dispose();
            _ubo.Dispose();
        }
    }
}
