using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// Draws the procedural sky (gradient + optional sun disc/halo) as a single fullscreen BACKGROUND pass into the
    /// lit colour attachment + read-only scene depth (ColorDepthFB): the fullscreen triangle sits at the far plane
    /// (SkyVert, z=1) and a read-only Equal depth test passes ONLY where the stored depth still EQUALS the cleared
    /// far plane, i.e. background pixels where no geometry was drawn. Geometry (depth &lt; 1) rejects the sky, so it
    /// never overwrites the scene and never touches the MRT normal / linear-depth attachments the outline pass reads
    /// (ColorDepthFB binds only colour + depth). This is the inverse selection to the decal pass's Greater test,
    /// which under the [0,1]/LessEqual convention passes on GEOMETRY only. Runs after the model pass wrote depth and before the
    /// decals + post chain, so the sky flows through the pixel post like the rest of the scene. Zero cost when the
    /// sky is off (the scene skips this pass entirely). One UBO, one draw.
    /// </summary>
    internal sealed class SkyRenderer : IDisposable
    {
        /// <summary>96-byte UBO matching the Sky block in <see cref="ShaderSources.SkyFrag"/> (6 vec4; every member
        /// 16-byte aligned, so std140 needs no extra padding). Size-checked by the UboLayoutTests tripwire.</summary>
        public struct SkyUbo
        {
            public Vector4 Horizon;   // rgb gradient at the horizon (bottom)
            public Vector4 Zenith;    // rgb gradient at the zenith (top)
            public Vector4 SunColor;  // rgb sun disc + halo colour
            public Vector4 SunNdc;    // xy = sun screen NDC, z = sunVisible (1/0), w = aspect (width/height)
            public Vector4 Params;    // x=sunEnabled, y=sunRadius, z=haloStrength, w=haloFalloff
            public Vector4 Res;       // xy = 1/renderWidth, 1/renderHeight
        }

        /// <summary>Byte size of <see cref="SkyUbo"/> / the GPU uniform buffer. 6 * 16 (vec4) = 96.</summary>
        internal const uint UboBytes = 96;

        readonly IGpuDevice _gd;
        readonly IGpuShaderSet _shaders;
        readonly IGpuResourceLayout _layout;
        readonly IGpuBuffer _ubo;
        readonly IGpuResourceSet _set;
        IGpuPipeline _pipe;   // rebuilt by SetOutputs when the MRT sample count (MSAA) changes

        public SkyRenderer(IGpuDevice gd, GpuOutputDescription colorOutput)
        {
            _gd = gd;
            var f = gd.Factory;
            _shaders = f.CreateShadersFromSpirv(ShaderSources.SkyVert, ShaderSources.SkyFrag);
            _layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Sky", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment)));
            _ubo = f.CreateBuffer(new GpuBufferDescription(UboBytes, GpuBufferUsage.UniformBuffer));
            _set = f.CreateResourceSet(new GpuResourceSetDescription(_layout, _ubo));
            _pipe = Pipe(f, colorOutput);
        }

        /// <summary>Rebuild the pipeline for a new colour-target output description (e.g. the MRT became multisampled
        /// for MSAA - a pipeline's sample count must match its framebuffer). Layout / shaders / buffer are kept.</summary>
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
                // Read-only, Equal depth test: the far-plane triangle (SkyVert, z=1) passes only where the stored
                // depth still EQUALS the cleared far plane (fragZ==1==storedZ), i.e. true background where no geometry
                // was drawn. Geometry sits at depth < 1 (LessEqual model pass) so it fails Equal and occludes the sky.
                // GreaterEqual would be wrong here: 1 >= any storedZ, so the sky would paint over ALL geometry. No
                // depth write, so the scene depth is untouched for any later pass (decals, resolve).
                DepthStencil = new GpuDepthStencilState(depthTestEnabled: true, depthWriteEnabled: false, GpuComparison.Equal),
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: false, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = _shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription>(),
                Outputs = outputs,
            });

        /// <summary>Pure: pack the sky settings + the CPU-projected sun screen position into the UBO. The sun (a
        /// DIRECTIONAL light) is placed at a screen NDC point from its direction rotated into view space via
        /// <paramref name="view"/> (see <see cref="SkyMath.ProjectSunToNdc"/>), so it lands consistently for both the
        /// ortho iso camera and the perspective follow camera. The render size derives the aspect (keeps the disc
        /// round) and the 1/size the shader uses to rebuild NDC from gl_FragCoord.</summary>
        public static SkyUbo PackUbo(SkySettings sky, Matrix4x4 view, Vector3 lightDirection,
            int renderWidth, int renderHeight)
        {
            Vector3 sun = sky.ResolveSunDirection(lightDirection);
            bool visible = SkyMath.ProjectSunToNdc(view, sun, out Vector2 sunNdc);
            float aspect = renderHeight > 0 ? (float)renderWidth / renderHeight : 1f;
            float invW = renderWidth > 0 ? 1f / renderWidth : 0f;
            float invH = renderHeight > 0 ? 1f / renderHeight : 0f;
            Vector4 horizon = sky.HorizonColor;
            Vector4 zenith = sky.ZenithColor;
            Vector4 sunCol = sky.SunColor;
            return new SkyUbo
            {
                Horizon = new Vector4(horizon.X, horizon.Y, horizon.Z, 0f),
                Zenith = new Vector4(zenith.X, zenith.Y, zenith.Z, 0f),
                SunColor = new Vector4(sunCol.X, sunCol.Y, sunCol.Z, 0f),
                SunNdc = new Vector4(sunNdc.X, sunNdc.Y, visible ? 1f : 0f, aspect),
                Params = new Vector4(sky.SunEnabled ? 1f : 0f, sky.SunRadius, sky.HaloStrength, sky.HaloFalloff),
                Res = new Vector4(invW, invH, 0f, 0f),
            };
        }

        /// <summary>Draw the sky into ColorDepthFB (lit colour + read-only scene depth). Caller guarantees the model
        /// pass is complete (depth written) and the framebuffer is free to rebind. The sun screen position comes from
        /// the RAW view matrix (the fragment rebuilds NDC from gl_FragCoord, the backend-independent decal
        /// convention), so the disc lands consistently across backends.</summary>
        public void Draw(IGpuCommandList cl, RenderResources res, Matrix4x4 view, Vector3 lightDirection, SkySettings sky)
        {
            var u = PackUbo(sky, view, lightDirection, res.Width, res.Height);
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
