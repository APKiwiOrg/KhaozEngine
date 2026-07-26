using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// Draws the queued <see cref="WaterPlane"/> as an animated, swell-displaced, alpha-blended surface into the
    /// lit color attachment + read-only scene depth (ColorDepthFB), sampling the resolved scene depth for the
    /// depth grading, the shore foam and the waterline alpha feather. Runs AFTER the sky and the ground-decal
    /// passes and BEFORE <see cref="RenderResources.ResolveColor"/>,
    /// so it is occluded by geometry above it (depth test ON) but never corrupts the normal/linear-depth MRT the
    /// outline pass reads (depth WRITE off - see the in-source note on <see cref="ShaderSources.WaterVert"/>). One
    /// draw per queued plane (its own dynamic-offset UBO slot, mirroring <see cref="GroundDecalRenderer"/>'s
    /// per-decal slot pattern so multiple planes never share/overwrite one slot regardless of backend buffer-write
    /// ordering).
    /// </summary>
    internal sealed class WaterRenderer : IDisposable
    {
        /// <summary>Packed water-plane UBO matching the <c>Water</c> block in <see cref="ShaderSources.WaterFrag"/>
        /// (2 mat4 + 24 vec4; every member 16-byte aligned, so std140 needs no extra padding).</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct WaterUbo
        {
            public Matrix4x4 ViewProj;
            public Matrix4x4 InvViewProj;
            public Vector4 LightDir;
            public Vector4 LightColor;
            public Vector4 CameraPos;
            public Vector4 DeepColor;
            public Vector4 ShallowColor;
            public Vector4 HorizonColor;
            public Vector4 WaveParams;    // x=waveScale, y=waveSpeed, z=normalStrength, w=time
            public Vector4 ShoreGlint;    // x=shoreFadeDistance, y=glintStrength, z=glintExponent, w=opacity
            public Vector4 DetailParams;  // x=warpStrength, y=detailFadeDistance, z=distantDetailScale, w=shallowDepth
            public Vector4 SkyHorizon;    // rgb, the reflected sky's horizon colour
            public Vector4 SkyZenith;     // rgb, the reflected sky's zenith colour
            public Vector4 SkySunColor;   // rgb, the reflected sun disc + halo colour
            public Vector4 SkyParams;     // x=sunEnabled, y=sunRadius, z=haloStrength, w=haloFalloff
            public Vector4 ReflectGlint;  // x=skyReflStrength, y=skyReflSunStrength, z=glintRoughness, w=glintDistantRoughness
            public Vector4 SwellParams;   // x=amplitude, y=wavelength, z=directionRadians, w=spreadRadians
            public Vector4 SwellShape;    // x=steepness, y=speedScale, z=componentCount, w=seed
            public Vector4 Absorption;    // rgb = per-metre coefficients (all-zero = legacy blend), w unused
            public Vector4 FoamColor;     // rgba
            public Vector4 FoamParams;    // x=strength, y=crestCoverage, z=shoreWidth, w=patternScale
            public Vector4 RippleSpectrum;   // x=componentCount, y=lacunarity, z=gain, w=seed
            public Vector4 FootprintParams;  // x=samplesPerWavelength, y=varianceToRoughness, z/w reserved
            public Vector4 FftParams;        // x=1 when the FFT maps are live, y=cascadeCount, z=resolution, w reserved
            public Vector4 FftTiles;          // xyz = per-cascade tile metres, w reserved
            public Vector4 FftVariance;       // xyz = per-cascade baked slope variance, w reserved
        }

        /// <summary>Byte size of <see cref="WaterUbo"/>, i.e. how much each slot actually uploads.
        /// 2*64 (mat4) + 24*16 (vec4) = 512.</summary>
        internal const uint PayloadBytes = 512;

        /// <summary>
        /// Per-plane stride in the shared UBO AND the size of the bound range. Each plane's params occupy their OWN
        /// slot, selected at draw time by a dynamic offset (i * SlotBytes), matching the GroundDecalRenderer
        /// precedent so a multi-plane frame never shares/overwrites a slot no matter how a backend orders buffer
        /// writes vs draws.
        /// <para>
        /// **It must be a multiple of 256, and so must the BOUND RANGE - which is why the range is SlotBytes and
        /// not PayloadBytes.** D3D11's <c>PSSetConstantBuffers1</c> requires both <c>FirstConstant</c> and
        /// <c>NumConstants</c> to be multiples of 16 constants, and Veldrid 4.9.0 computes them as
        /// <c>firstConstant = offset / 16</c> and <c>numConstants = max(size, 256) / 16</c> with no rounding
        /// (Veldrid.D3D11.D3D11CommandList). So a bound size UNDER 256 is padded up to 256 and is fine (that is why
        /// OverlayMeshRenderer's 128-byte range works), and any exact multiple of 256 is fine, but anything in
        /// between yields a non-multiple-of-16 count that D3D11 REJECTS: the whole cbuffer is then left unbound and
        /// the shader reads zeros. This bit: 14.22.0 grew the payload from 256 to 272 and binding that size made
        /// every water fragment read opacity 0 and discard, so D3D11 rendered no water at all while Metal and
        /// Vulkan were perfect. Round the payload UP to 256 here (ModelRenderer.Align256's convention), never bind
        /// the raw payload size. UboLayoutTests guards it.
        /// </para>
        /// </summary>
        internal const uint SlotBytes = 512;   // Align256(512)

        readonly IGpuDevice _gd;
        readonly IGpuShaderSet _shaders;
        readonly IGpuResourceLayout _layout;
        IGpuPipeline _pipe;   // rebuilt by SetOutputs when the MRT sample count (MSAA) changes
        IGpuBuffer? _ubo;     // grown geometrically to hold _capacity slots
        int _capacity;
        IGpuResourceSet? _set;
        RenderResources? _bound;
        int _boundGen;
        // The FFT ocean's compute producer. Owned here (rather than by Scene3D) because the cascade update must be
        // recorded into the SAME command list as the draw that samples its output, which is the seam's guaranteed
        // compute-to-graphics ordering. Updated ONCE per Draw, before the per-plane loop: one ocean state serves
        // every queued plane.
        readonly OceanFftProducer _ocean;
        // Which ocean map the current set was built against, so a rebaked (or first-activated) map rebinds.
        IGpuTexture? _boundMap;
        // Fixed-size grid buffers: every WaterPlane draws through the SAME GridResolution grid (only the CPU-side
        // vertex positions differ per plane, re-uploaded per draw), so these are allocated once and never regrown.
        IGpuBuffer? _vb;
        IGpuBuffer? _ib;
        // Heap-allocated once, not stackalloc'd per draw: at GridResolution 97 the position scratch is 113 KB and
        // the index scratch 216 KB, both far past what belongs on the stack.
        readonly Vector3[] _gridScratch = new Vector3[WaterMath.GridResolution * WaterMath.GridResolution];
        readonly float[] _axisScratch = new float[2 * WaterMath.GridResolution];

        public WaterRenderer(IGpuDevice gd, GpuOutputDescription colorOutput)
        {
            _gd = gd;
            var f = gd.Factory;
            _shaders = f.CreateShadersFromSpirv(ShaderSources.WaterVert, ShaderSources.WaterFrag);
            _layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                // ORDER IS LOAD-BEARING, and not for the usual reason. Veldrid numbers a backend's resource slots
                // with one counter PER KIND over this whole list, binding each element to the stages in its mask,
                // while the cross-compiler numbers each stage DENSELY over only the bindings that stage declares.
                // Those agree only when every stage's resources are a PREFIX of this list. The vertex stage uses
                // the ocean map and the shared UBO and nothing else, so the ocean map has to be the FIRST texture
                // and its sampler the FIRST sampler; put the scene depth first instead and the vertex samples an
                // unbound slot and reads zero, on Metal, silently. That is also why the ocean is ONE array texture
                // (displacement layers then derivative layers) rather than the two it reads as.
                new GpuResourceLayoutElement("OceanMap", GpuResourceKind.TextureReadOnly, GpuShaderStages.Vertex | GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("OceanSamp", GpuResourceKind.Sampler, GpuShaderStages.Vertex | GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("DepthTex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
                // Dynamic-offset UBO read by BOTH stages (the vertex shader only needs ViewProj, folded into the
                // same buffer per the one-UBO-per-set rule).
                new GpuResourceLayoutElement("Water", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex | GpuShaderStages.Fragment, dynamic: true)));
            _ocean = new OceanFftProducer(gd);
            _pipe = Pipe(f, colorOutput);
        }

        /// <summary>Rebuild the pipeline for a new colour-target output description (e.g. the MRT became
        /// multisampled for MSAA). Layout/shaders/buffers are kept.</summary>
        public void SetOutputs(GpuOutputDescription colorOutput)
        {
            _pipe.Dispose();
            _pipe = Pipe(_gd.Factory, colorOutput);
        }

        IGpuPipeline Pipe(IGpuResourceFactory f, GpuOutputDescription outputs)
        {
            var vertexLayout = new GpuVertexLayoutDescription(
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
                ShaderSet = _shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout },
                Outputs = outputs,
            });
        }

        void BindTargets(RenderResources res)
        {
            IGpuTexture map = _ocean.Map;
            if (_set != null && ReferenceEquals(_bound, res) && res.Generation == _boundGen
                && ReferenceEquals(_boundMap, map)) return;
            _set?.Dispose();
            _set = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_layout,
                map, _ocean.Sampler, res.DepthColorTex, _gd.PointSampler,
                new GpuBufferRange(_ubo!, 0, SlotBytes)));
            _bound = res; _boundGen = res.Generation;
            _boundMap = map;
        }

        /// <summary>Ensure the UBO holds at least <paramref name="planeCount"/> slots, growing geometrically. A
        /// regrown buffer drops the set so <see cref="BindTargets"/> rebuilds its range against the new buffer.</summary>
        void EnsureUboCapacity(int planeCount)
        {
            if (_ubo != null && _capacity >= planeCount) return;
            _capacity = Math.Max(planeCount, _capacity == 0 ? 4 : _capacity * 2);
            _ubo?.Dispose();
            _ubo = _gd.Factory.CreateBuffer(new GpuBufferDescription((uint)_capacity * SlotBytes, GpuBufferUsage.UniformBuffer));
            _set?.Dispose(); _set = null;
        }

        void EnsureGridBuffers()
        {
            if (_vb != null && _ib != null) return;
            const uint vcount = WaterMath.GridResolution * WaterMath.GridResolution;
            const uint icount = WaterMath.GridIndexCount;
            _vb = _gd.Factory.CreateBuffer(new GpuBufferDescription(vcount * 12u, GpuBufferUsage.VertexBuffer));   // Vector3 = 12 bytes
            _ib = _gd.Factory.CreateBuffer(new GpuBufferDescription(icount * sizeof(uint), GpuBufferUsage.IndexBuffer));
            uint[] indices = new uint[icount];   // built once, then thrown away: the index layout never changes
            WaterMath.BuildGridIndices(indices);
            _gd.UpdateBuffer(_ib, 0, indices);
        }

        /// <summary>Pure: pack one plane + the frame's light/camera/water/sky settings into the UBO.
        /// <paramref name="rawViewProj"/> is the RAW (not clip-corrected) view-projection so the fragment's depth
        /// reconstruction matches the ground-decal convention; <paramref name="clipViewProj"/> is the SEPARATE
        /// clip-corrected copy for <c>gl_Position</c> (mirrors every other pass in this file, e.g.
        /// <see cref="GpuClip.Correct"/> at the <see cref="Draw"/> call site).
        /// <para>
        /// <paramref name="sky"/> supplies the palette the surface REFLECTS, and it is read whether or not the sky
        /// PASS is enabled: a scene can legitimately want reflective water over a custom background, and forcing
        /// the game to hand-match a second copy of the sky colours is exactly the drift the shared settings bag
        /// exists to avoid. <see cref="SkySettings.Enabled"/> is deliberately NOT consulted here; only
        /// <see cref="SkySettings.SunEnabled"/> is, because a sky with no sun should not reflect one.
        /// </para>
        /// </summary>
        public static WaterUbo PackUbo(Matrix4x4 clipViewProj, Matrix4x4 rawViewProj, Vector3 lightDirection,
            Color lightColor, Vector3 cameraPos, WaterSettings settings, SkySettings sky, float timeSeconds)
            => PackUbo(clipViewProj, rawViewProj, lightDirection, lightColor, cameraPos, settings, sky, timeSeconds,
                default);

        /// <summary>As above, plus the FFT ocean's live map description. <paramref name="ocean"/> defaulted (i.e.
        /// inactive) packs <c>FftParams.x = 0</c>, which is what makes every FFT branch in both shader stages a
        /// not-taken uniform branch and the procedural surface byte-identical to 14.28.0.</summary>
        public static WaterUbo PackUbo(Matrix4x4 clipViewProj, Matrix4x4 rawViewProj, Vector3 lightDirection,
            Color lightColor, Vector3 cameraPos, WaterSettings settings, SkySettings sky, float timeSeconds,
            in OceanMaps ocean)
        {
            Matrix4x4.Invert(rawViewProj, out var inv);
            Vector4 deep = settings.DeepColor;
            Vector4 shallow = settings.ShallowColor;
            Vector4 horizon = settings.HorizonColor;
            Vector4 lightCol = lightColor;
            Vector4 skyHorizon = sky.HorizonColor;
            Vector4 skyZenith = sky.ZenithColor;
            Vector4 skySun = sky.SunColor;
            Vector4 absorption = settings.AbsorptionPerMetre;
            Vector4 foam = settings.FoamColor;
            return new WaterUbo
            {
                ViewProj = clipViewProj,
                InvViewProj = inv,
                LightDir = new Vector4(lightDirection, 0f),
                LightColor = lightCol,
                CameraPos = new Vector4(cameraPos, 0f),
                DeepColor = deep,
                ShallowColor = shallow,
                HorizonColor = horizon,
                WaveParams = new Vector4(settings.WaveScale, settings.WaveSpeed, settings.NormalStrength, timeSeconds),
                ShoreGlint = new Vector4(settings.ShoreFadeDistance, settings.GlintStrength, settings.GlintExponent, settings.Opacity),
                DetailParams = new Vector4(settings.WaveWarpStrength, settings.DetailFadeDistance,
                    settings.DistantDetailScale, settings.ShallowDepth),
                SkyHorizon = skyHorizon,
                SkyZenith = skyZenith,
                SkySunColor = skySun,
                SkyParams = new Vector4(sky.SunEnabled ? 1f : 0f, sky.SunRadius, sky.HaloStrength, sky.HaloFalloff),
                ReflectGlint = new Vector4(settings.SkyReflectionStrength, settings.SkyReflectionSunStrength,
                    settings.GlintRoughness, settings.GlintDistantRoughness),
                SwellParams = new Vector4(settings.SwellAmplitude, settings.SwellWavelength,
                    GerstnerWaves.DegreesToRadians(settings.SwellDirectionDegrees),
                    GerstnerWaves.DegreesToRadians(settings.SwellSpreadDegrees)),
                SwellShape = new Vector4(settings.SwellSteepness, settings.SwellSpeed,
                    Math.Clamp(settings.SwellComponents, 1, GerstnerWaves.MaxComponents), settings.SwellSeed),
                Absorption = absorption,
                FoamColor = foam,
                FoamParams = new Vector4(settings.FoamStrength, settings.FoamCrestCoverage,
                    settings.FoamShoreWidth, settings.FoamPatternScale),
                RippleSpectrum = new Vector4(
                    Math.Clamp(settings.RippleComponents, 1, Internal.RippleSpectrum.MaxComponents),
                    settings.RippleLacunarity, settings.RippleGain, settings.RippleSeed),
                FootprintParams = new Vector4(settings.FootprintSamples, settings.VarianceToRoughness, 0f, 0f),
                FftParams = new Vector4(ocean.Active ? 1f : 0f, ocean.CascadeCount, ocean.Resolution, 0f),
                FftTiles = new Vector4(ocean.Tiles, 0f),
                FftVariance = new Vector4(ocean.SlopeVariance, 0f),
            };
        }

        /// <summary>
        /// The live FFT ocean maps' shape, as the shaders need to read them: whether the maps are live at all, how
        /// many cascade layers they carry, their resolution, each layer's world tile size, and each layer's baked
        /// slope variance. A pure value so <see cref="PackUbo(Matrix4x4, Matrix4x4, Vector3, Color, Vector3,
        /// WaterSettings, SkySettings, float, in OceanMaps)"/> stays testable without a device.
        /// </summary>
        internal readonly struct OceanMaps
        {
            /// <summary>True when the compute producer wrote maps this frame.</summary>
            public bool Active { get; }
            /// <summary>Cascade layers in the map arrays.</summary>
            public int CascadeCount { get; }
            /// <summary>FFT resolution per axis.</summary>
            public int Resolution { get; }
            /// <summary>Per-cascade world tile size, metres.</summary>
            public Vector3 Tiles { get; }
            /// <summary>Per-cascade expected slope variance from the baked spectrum.</summary>
            public Vector3 SlopeVariance { get; }

            public OceanMaps(int cascadeCount, int resolution, Vector3 tiles, Vector3 slopeVariance)
            {
                Active = true;
                CascadeCount = cascadeCount;
                Resolution = resolution;
                Tiles = tiles;
                SlopeVariance = slopeVariance;
            }

            /// <summary>Snapshot a producer's current output. Returns the inactive default when it produced
            /// nothing this frame.</summary>
            public static OceanMaps From(OceanFftProducer producer)
                => producer.Active
                    ? new OceanMaps(producer.CascadeCount, producer.Resolution,
                        new Vector3(producer.TileMetres[0], producer.TileMetres[1], producer.TileMetres[2]),
                        new Vector3(producer.SlopeVariance[0], producer.SlopeVariance[1], producer.SlopeVariance[2]))
                    : default;
        }

        /// <summary>Draw all queued water planes into ColorDepthFB (lit colour + read-only scene depth). Caller
        /// guarantees the model + sky + decal passes are complete and the framebuffer is free to rebind. No-op when
        /// <paramref name="planes"/> is empty.</summary>
        public void Draw(IGpuCommandList cl, RenderResources res, ReadOnlySpan<WaterPlane> planes,
            Matrix4x4 viewProj, Vector3 lightDirection, Color lightColor, Vector3 cameraPos, WaterSettings settings,
            SkySettings sky, float timeSeconds)
        {
            if (planes.Length == 0) return;
            EnsureUboCapacity(planes.Length);
            EnsureGridBuffers();

            // ONE ocean update per frame, ahead of the per-plane loop and of BindTargets (which binds whatever maps
            // it produced). Every queued plane samples the same cascades: this release has one sea state, not one
            // per body of water. The producer records its final dispatch into THIS command list, so the storage
            // writes and the draws that sample them share a list - the seam's guaranteed ordering.
            _ocean.Update(cl, settings, timeSeconds);
            var oceanMaps = OceanMaps.From(_ocean);
            BindTargets(res);

            Matrix4x4 clipVp = GpuClip.Correct(viewProj, _gd.Capabilities);
            for (int i = 0; i < planes.Length; i++)
            {
                var u = PackUbo(clipVp, viewProj, lightDirection, lightColor, cameraPos, settings, sky, timeSeconds,
                    oceanMaps);
                cl.UpdateBuffer(_ubo!, (uint)i * SlotBytes, in u);
            }

            cl.SetFramebuffer(res.ColorDepthFB);
            cl.SetPipeline(_pipe);
            cl.SetIndexBuffer(_ib!, GpuIndexFormat.UInt32);
            for (int i = 0; i < planes.Length; i++)
            {
                // The grid concentrates its vertices around the camera's XZ (clamped inside the plane by
                // BuildGridPositions), so the fixed vertex budget lands where the displaced swell actually reads.
                int n = WaterMath.BuildGridPositions(planes[i], cameraPos.X, cameraPos.Z, settings.GridFocusBias,
                    _gridScratch, _axisScratch);
                cl.UpdateBuffer<Vector3>(_vb!, 0, _gridScratch.AsSpan(0, n));
                cl.SetGraphicsResourceSet(0, _set!, (uint)i * SlotBytes);
                cl.SetVertexBuffer(0, _vb!);
                cl.DrawIndexed((uint)WaterMath.GridIndexCount, 1, 0, 0, 0);
            }
        }

        /// <summary>The FFT producer's last-frame diagnostics: GPU stalls it cost and the wall-clock milliseconds
        /// they took. Internal, for the perf test that pins #311's cost as a measured number.</summary>
        internal (int Stalls, double StallMs) LastOceanCost => (_ocean.LastStallCount, _ocean.LastStallMs);

        public void Dispose()
        {
            _ocean.Dispose();
            _set?.Dispose();
            _pipe.Dispose();
            _layout.Dispose();
            _shaders.Dispose();
            _ubo?.Dispose();
            _vb?.Dispose();
            _ib?.Dispose();
        }
    }
}
