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
    /// sky is off (the scene skips this pass entirely). Physical atmosphere borrows the scene's LUT and uniforms.
    /// </summary>
    internal sealed class SkyRenderer : IDisposable
    {
        /// <summary>608-byte UBO matching the Sky block in <see cref="ShaderSources.SkyFrag"/> (6 vec4 and four
        /// <c>vec4[8]</c> disc arrays, every member
        /// 16-byte aligned, so std140 needs no extra padding). Size-checked by the UboLayoutTests tripwire.</summary>
        public struct SkyUbo
        {
            public Vector4 Horizon;   // rgb gradient at the horizon (bottom), w = atmosphere enabled this frame
            public Vector4 Zenith;    // rgb gradient at the zenith (top)
            public Vector4 Res;       // xy = 1/renderWidth, 1/renderHeight, z = aspect (width/height), w = disc count
            public Vector4 Ground;      // rgb ground band below the world horizon, a = blend depth (sin-elevation)
            public Vector4 HorizonRay;  // view ray through NDC (x,y) = (x * .x + .z, y * .y + .w, -1)
            public Vector4 HorizonUp;   // xyz = world-Y of the camera right/up/back axes, w = 1 when live
            public SkyDiscVec4s DiscColor;  // per on-screen disc: rgb colour, a = opacity
            public SkyDiscVec4s DiscPlace;  // xy = screen NDC, z = radius, w = halo strength
            public SkyDiscVec4s DiscHalo;   // x = halo falloff, y = physical primary marker
            public SkyDiscVec4s DiscDirection; // xyz = world direction, w = angular radius (physical sky only)
        }

        /// <summary>Byte size of <see cref="SkyUbo"/> / the GPU uniform buffer. (6 + 4 * 8) * 16 = 608.</summary>
        internal const uint UboBytes = 608;

        readonly IGpuDevice _gd;
        readonly IGpuShaderSet _shaders;
        readonly IGpuResourceLayout _layout;
        readonly IGpuBuffer _ubo;
        readonly IGpuResourceSet _set;
        readonly AtmosphereBindings? _ownedAtmosphere;
        IGpuPipeline _pipe;   // rebuilt by SetOutputs when the MRT sample count (MSAA) changes

        public SkyRenderer(IGpuDevice gd, GpuOutputDescription colorOutput)
            : this(gd, colorOutput, new AtmosphereBindings(gd), ownsAtmosphere: true) { }

        public SkyRenderer(IGpuDevice gd, GpuOutputDescription colorOutput, AtmosphereBindings atmosphere)
            : this(gd, colorOutput, atmosphere, ownsAtmosphere: false) { }

        SkyRenderer(IGpuDevice gd, GpuOutputDescription colorOutput, AtmosphereBindings atmosphere, bool ownsAtmosphere)
        {
            _gd = gd;
            _ownedAtmosphere = ownsAtmosphere ? atmosphere : null;
            var f = gd.Factory;
            _shaders = f.CreateShadersFromSpirv(ShaderSources.SkyVert, ShaderSources.SkyFrag);
            _layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(AtmosphereBindings.AppendLayout(
                new GpuResourceLayoutElement("Sky", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment))));
            _ubo = f.CreateBuffer(new GpuBufferDescription(UboBytes, GpuBufferUsage.UniformBuffer));
            _set = f.CreateResourceSet(new GpuResourceSetDescription(_layout, atmosphere.AppendResources(_ubo)));
            _pipe = Pipe(f, colorOutput);
        }

        /// <summary>Rebuild the pipeline for a new colour-target output description (e.g. the MRT became multisampled
        /// for MSAA - a pipeline's sample count must match its framebuffer). Layout / shaders / buffer are kept.</summary>
        public void SetOutputs(GpuOutputDescription colorOutput, GpuRetireQueue? retired = null)
        {
            ResourceRetirement.Free(_pipe, retired);
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

        /// <summary>Pure: pack the sky settings + the CPU-projected screen position of every disc into the UBO. Each
        /// disc (a DIRECTIONAL body) is placed at a screen NDC point per <see cref="SkySettings.Anchor"/> (see
        /// <see cref="SkyMath.ProjectSunToNdc"/>): the world-anchored point-at-infinity projection (default, needs
        /// <paramref name="projection"/>) or the legacy stylized backdrop. The projection is done ENTIRELY on the CPU,
        /// and <see cref="SkyDiscs.Resolve"/> decides which discs there are. The render size derives the aspect
        /// (keeps the discs round) and the 1/size the shader uses to rebuild NDC from gl_FragCoord. Physical
        /// packing carries angular world directions independently of the legacy anchor and horizon controls.</summary>
        public static SkyUbo PackUbo(SkySettings sky, Matrix4x4 view, Matrix4x4 projection, Vector3 lightDirection,
            int renderWidth, int renderHeight, bool atmosphereEnabled = false)
        {
            float aspect = renderHeight > 0 ? (float)renderWidth / renderHeight : 1f;
            float invW = renderWidth > 0 ? 1f / renderWidth : 0f;
            float invH = renderHeight > 0 ? 1f / renderHeight : 0f;
            Vector4 horizon = sky.HorizonColor;
            Vector4 zenith = sky.ZenithColor;
            SkyHorizonFrame frame = SkyHorizonMath.ResolveFrame(sky, view, projection);

            // Legacy discs must project on screen. Physical discs keep their directions for per-pixel angle tests.
            Span<SkyDisc> discs = stackalloc SkyDisc[SkySettings.MaxDiscs];
            int resolved = SkyDiscs.Resolve(sky, lightDirection, discs);
            SkyDiscVec4s color = default, place = default, halo = default, direction = default;
            int onScreen = 0;
            for (int i = 0; i < resolved; i++)
            {
                SkyDisc disc = discs[i];
                if (atmosphereEnabled)
                {
                    // Parallel orthographic rays have no directional disc location. Perspective discs are
                    // evaluated by world angle, including limbs whose centres lie outside the viewport.
                    if (!SkyHorizonMath.IsPerspective(projection)) continue;
                    color[onScreen] = disc.Color;
                    direction[onScreen] = new Vector4(disc.Direction, disc.Radius);
                    halo[onScreen] = new Vector4(0f, sky.SunEnabled && i == 0 ? 1f : 0f, 0f, 0f);
                    onScreen++;
                    continue;
                }
                if (!SkyMath.ProjectSunToNdc(sky.Anchor, view, projection, disc.Direction, out Vector2 ndc)) continue;
                color[onScreen] = disc.Color;
                place[onScreen] = new Vector4(ndc.X, ndc.Y, disc.Radius, disc.HaloStrength);
                halo[onScreen] = new Vector4(disc.HaloFalloff, 0f, 0f, 0f);
                onScreen++;
            }

            return new SkyUbo
            {
                Horizon = new Vector4(horizon.X, horizon.Y, horizon.Z, atmosphereEnabled ? 1f : 0f),
                Zenith = new Vector4(zenith.X, zenith.Y, zenith.Z, 0f),
                Res = new Vector4(invW, invH, aspect, onScreen),
                Ground = new Vector4(frame.Ground.Color, frame.Ground.Softness),
                HorizonRay = frame.Ray,
                HorizonUp = new Vector4(frame.Up, frame.Live ? 1f : 0f),
                DiscColor = color,
                DiscPlace = place,
                DiscHalo = halo,
                DiscDirection = direction,
            };
        }

        /// <summary>Draw the sky into ColorDepthFB (lit colour + read-only scene depth). Caller guarantees the model
        /// pass is complete (depth written) and the framebuffer is free to rebind. The sun screen position is
        /// CPU-projected from the RAW <paramref name="view"/> + <paramref name="projection"/> per
        /// <see cref="SkySettings.Anchor"/> (the fragment rebuilds NDC from gl_FragCoord, the backend-independent decal
        /// convention), so the disc lands consistently across backends. Physical atmosphere reconstructs rays
        /// through its shared camera basis instead of using projected disc centres.</summary>
        public void Draw(IGpuCommandList cl, RenderResources res, Matrix4x4 view, Matrix4x4 projection, Vector3 lightDirection, SkySettings sky,
            bool atmosphereEnabled = false)
        {
            var u = PackUbo(sky, view, projection, lightDirection, res.Width, res.Height, atmosphereEnabled);
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
            _ownedAtmosphere?.Dispose();
        }
    }
}
