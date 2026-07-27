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
        /// (2 mat4 + 34 vec4; every member 16-byte aligned, so std140 needs no extra padding).</summary>
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
            public Vector4 FftFocus;          // xy = onshore focus point (world XZ), z = strength, w = wind radians
            public Vector4 FftRotCos;         // xyz = cos(per-cascade rotation offset), w = domain-warp metres
            public Vector4 FftRotSin;         // xyz = sin(per-cascade rotation offset), w = domain-warp wavelength
            public Vector4 FftSector;         // x = focus sector count, yz = (cos, sin) of one sector, w reserved
            public Vector4 FftWave;           // xyz = per-cascade energy-weighted mean wave number (rad/m), w reserved
            public Vector4 BathyRect;         // xy = depth field world min corner (XZ), zw = 1 / world size
            public Vector4 BathyParams;       // x = field live, y = shoaling strength, z = depth scale, w = significant wave height
            public Vector4 SurfParams;        // x = surf strength, y = break depth (m), z = band width, w = crest bias
            public Vector4 SurfShape;         // x = trail width, y = amplitude collapse, z = plane surface Y, w = bathymetry texel metres
            public Vector4 RenderOrigin;      // xyz = the render origin the plane, the grid and the eye were reduced by
        }

        /// <summary>Byte size of <see cref="WaterUbo"/>, i.e. how much each slot actually uploads.
        /// 2*64 (mat4) + 34*16 (vec4) = 672.</summary>
        internal const uint PayloadBytes = 672;

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
        internal const uint SlotBytes = 768;   // Align256(672)

        readonly IGpuDevice _gd;
        readonly IGpuShaderSet _shaders;
        readonly IGpuResourceLayout _layout;
        IGpuPipeline _pipe;   // rebuilt by SetOutputs when the MRT sample count (MSAA) changes
        GpuOutputDescription _outputs;
        // The clipmap grid's own shader set + pipeline, built on FIRST USE and kept. It needs its own because the
        // two grids do not share a vertex layout (12 bytes against 28); the resource LAYOUT and the fragment source
        // are the same, so nothing else doubles. A scene that never selects WaterGridMode.Clipmap never creates it.
        IGpuShaderSet? _clipShaders;
        IGpuPipeline? _clipPipe;
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
        // The consumer's depth field on the GPU. Owned here for the same reason the ocean is: it is bound into
        // this pass's resource set, and nothing else in the engine reads it.
        readonly WaterBathymetryMap _bathymetry;
        // Which ocean map the current set was built against, so a rebaked (or first-activated) map rebinds.
        IGpuTexture? _boundMap;
        // Same, for the depth field: a resolution change replaces the texture and the set has to follow it.
        IGpuTexture? _boundBathy;
        // Fixed-size grid buffers: every WaterPlane draws through the SAME GridResolution grid (only the CPU-side
        // vertex positions differ per plane, re-uploaded per draw), so these are allocated once and never regrown.
        IGpuBuffer? _vb;
        IGpuBuffer? _ib;
        // Heap-allocated once, not stackalloc'd per draw: at GridResolution 97 the position scratch is 113 KB and
        // the index scratch 216 KB, both far past what belongs on the stack.
        readonly Vector3[] _gridScratch = new Vector3[WaterMath.GridResolution * WaterMath.GridResolution];
        readonly float[] _axisScratch = new float[2 * WaterMath.GridResolution];

        // ---- Clipmap grid state ------------------------------------------------------------------------------
        // Its own buffers, because their SIZE depends on the ring settings rather than being the one fixed budget
        // the camera-focused grid has. Allocated on first clipmap use and regrown only when those settings move.
        //
        // ONE buffer pair holding a per-plane SLICE each, rather than a shared scratch every plane overwrites.
        // That is what makes the cache below work at all with more than one plane: a cached "nothing moved" is a
        // claim about what the buffer still HOLDS, and a shared buffer makes that claim false the moment the next
        // plane uploads over it. Slices are addressed by the draw's own indexStart/vertexOffset, so the bindings
        // are set once for the whole pass.
        IGpuBuffer? _clipVb;
        IGpuBuffer? _clipIb;
        WaterClipmapVertex[] _clipVerts = Array.Empty<WaterClipmapVertex>();   // scratch for ONE plane
        uint[] _clipIndices = Array.Empty<uint>();                            // scratch for ONE plane
        int _clipSliceVerts, _clipSliceIndices;                               // per-plane slice capacity
        ClipSlot[] _clipSlots = Array.Empty<ClipSlot>();
        readonly long[] _clipSnapScratchX = new long[WaterClipmap.MaxLevels];
        readonly long[] _clipSnapScratchZ = new long[WaterClipmap.MaxLevels];

        /// <summary>Clipmap grids rebuilt AND re-uploaded by the last <see cref="Draw"/>, across every queued
        /// plane. The headline behaviour of a world-locked grid is that this is 0 on most frames: a ring only
        /// moves when the camera crosses one of its snap boundaries. Internal, for the test that pins that as a
        /// measured number rather than a claim (same role as <see cref="LastOceanCost"/> for #311).</summary>
        internal int LastClipmapRebuilds { get; private set; }

        /// <summary>One plane's cached grid: what its slice of the buffers currently holds. Per PLANE, not
        /// per renderer - a single shared entry made every plane compare against the previous plane's key, so a
        /// two-plane scene missed on every plane on every frame and quietly rebuilt both, which is the whole
        /// saving gone. Identity is the plane's INDEX in the frame's queue, so a consumer that reorders its
        /// DrawWater calls between frames pays one rebuild and then settles.</summary>
        sealed class ClipSlot
        {
            public ClipKey Key;
            public readonly long[] SnapX = new long[WaterClipmap.MaxLevels];
            public readonly long[] SnapZ = new long[WaterClipmap.MaxLevels];
            public bool Valid;
            public int IndexCount;
        }

        /// <summary>Everything the built clipmap geometry depends on EXCEPT the per-level snap indices, which are
        /// compared separately (they are an array). The plane fields are ABSOLUTE world coordinates and the render
        /// origin is carried separately: the lattice is a function of the world alone, but the vertex POSITIONS are
        /// render-relative, so a rebase leaves the lattice put and still needs a re-upload.</summary>
        readonly record struct ClipKey(float Cell, int RingCells, int Levels, float GeomorphBand,
            float CenterX, float CenterZ, float SurfaceY, float HalfX, float HalfZ,
            float OriginX, float OriginY, float OriginZ);

        public WaterRenderer(IGpuDevice gd, GpuOutputDescription colorOutput)
        {
            _gd = gd;
            _outputs = colorOutput;
            var f = gd.Factory;
            _shaders = f.CreateShadersFromSpirv(ShaderSources.WaterVert, ShaderSources.WaterFrag);
            _layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                // ORDER IS LOAD-BEARING, and not for the usual reason. Veldrid numbers a backend's resource slots
                // with one counter PER KIND over this whole list, binding each element to the stages in its mask,
                // while the cross-compiler numbers each stage DENSELY over only the bindings that stage declares.
                // Those agree only when every stage's resources are a PREFIX of this list. The vertex stage uses
                // the bathymetry field, the ocean map and the shared UBO and nothing else, so both of those
                // textures have to precede the fragment-only scene depth and their samplers likewise; put the
                // scene depth first instead and the vertex samples an unbound slot and reads zero, on Metal,
                // silently. That is also why the ocean is ONE array texture (displacement layers then derivative
                // layers) rather than the two it reads as.
                //
                // Bathymetry leads the ocean for a SECOND reason on top of that: within a stage the numbering
                // follows FIRST REFERENCE, and the vertex needs the depth before it sums the cascades because the
                // shoaling taper is per cascade and applied inside that loop. See ShaderSources.WaterShore.cs.
                new GpuResourceLayoutElement("BathyTex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Vertex | GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("BathySamp", GpuResourceKind.Sampler, GpuShaderStages.Vertex | GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("OceanMap", GpuResourceKind.TextureReadOnly, GpuShaderStages.Vertex | GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("OceanSamp", GpuResourceKind.Sampler, GpuShaderStages.Vertex | GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("DepthTex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
                // Dynamic-offset UBO read by BOTH stages (the vertex shader only needs ViewProj, folded into the
                // same buffer per the one-UBO-per-set rule).
                new GpuResourceLayoutElement("Water", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex | GpuShaderStages.Fragment, dynamic: true)));
            _ocean = new OceanFftProducer(gd);
            _bathymetry = new WaterBathymetryMap(gd);
            _pipe = Pipe(f, colorOutput, _shaders, false);
        }

        /// <summary>Rebuild the pipeline for a new colour-target output description (e.g. the MRT became
        /// multisampled for MSAA). Layout/shaders/buffers are kept.</summary>
        public void SetOutputs(GpuOutputDescription colorOutput)
        {
            _outputs = colorOutput;
            _pipe.Dispose();
            _pipe = Pipe(_gd.Factory, colorOutput, _shaders, false);
            if (_clipPipe == null) return;
            _clipPipe.Dispose();
            _clipPipe = Pipe(_gd.Factory, colorOutput, _clipShaders!, true);
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

        /// <summary>Build the clipmap shader set + pipeline on first use, so a scene that never asks for
        /// <see cref="WaterGridMode.Clipmap"/> pays nothing for it (not even a shader compile).</summary>
        void EnsureClipPipeline()
        {
            if (_clipPipe != null) return;
            _clipShaders = _gd.Factory.CreateShadersFromSpirv(ShaderSources.WaterClipmapVert, ShaderSources.WaterFrag);
            _clipPipe = Pipe(_gd.Factory, _outputs, _clipShaders, true);
        }

        void BindTargets(RenderResources res)
        {
            IGpuTexture map = _ocean.Map;
            IGpuTexture bathy = _bathymetry.Texture;
            if (_set != null && ReferenceEquals(_bound, res) && res.Generation == _boundGen
                && ReferenceEquals(_boundMap, map) && ReferenceEquals(_boundBathy, bathy)) return;
            _set?.Dispose();
            _set = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_layout,
                bathy, _bathymetry.Sampler, map, _ocean.Sampler, res.DepthColorTex, _gd.PointSampler,
                new GpuBufferRange(_ubo!, 0, SlotBytes)));
            _bound = res; _boundGen = res.Generation;
            _boundMap = map;
            _boundBathy = bathy;
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
                default, default);

        /// <summary>As above, plus the FFT ocean's live map description. <paramref name="ocean"/> defaulted (i.e.
        /// inactive) packs <c>FftParams.x = 0</c>, which is what makes every FFT branch in both shader stages a
        /// not-taken uniform branch and the procedural surface byte-identical to 14.28.0.</summary>
        public static WaterUbo PackUbo(Matrix4x4 clipViewProj, Matrix4x4 rawViewProj, Vector3 lightDirection,
            Color lightColor, Vector3 cameraPos, WaterSettings settings, SkySettings sky, float timeSeconds,
            in OceanMaps ocean, Vector3 renderOrigin = default, ShoreMaps shore = default,
            float planeSurfaceY = 0f)
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
            // The sampling-frame group. Read whether or not the ocean is live: the shader gates every one of them
            // behind FftParams.x, so a procedural surface never looks at them, and packing them unconditionally
            // keeps this a pure function of the settings bag.
            WaterSeaState sea = settings.SeaState;
            // The depth-driven group. It needs BOTH a field and live cascades: the shoaling taper is per cascade
            // against that cascade's own mean wave number, and the procedural swell has none, so the whole group
            // is gated to the FFT source here rather than half-gated in two shader stages.
            bool shoreLive = shore.Active && ocean.Active;
            float breakDepth = shoreLive
                ? WaterShoaling.BreakDepth(ocean.SignificantHeight, settings.SurfBreakerIndex)
                : 0f;
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
                FootprintParams = new Vector4(settings.FootprintSamples, settings.VarianceToRoughness,
                    MathF.Max(settings.ClipmapBandLimitSamples, 1f), 0f),
                // FftParams.w is the top mip index of the live maps, and it is the switch that turns the whole
                // band-limit on: 0 (which is what an ocean with no mip chain reports, i.e. every consumer that has
                // not opted into the clipmap) makes oceanMip early-return and both stages sample a literal LOD 0.
                FftParams = new Vector4(ocean.Active ? 1f : 0f, ocean.CascadeCount, ocean.Resolution, ocean.MaxMip),
                FftTiles = new Vector4(ocean.Tiles, 0f),
                FftVariance = new Vector4(ocean.SlopeVariance, 0f),
                FftFocus = new Vector4(sea.OnshoreFocusPoint, Math.Clamp(sea.OnshoreFocusStrength, 0f, 1f),
                    GerstnerWaves.DegreesToRadians(sea.WindDirectionDegrees)),
                FftRotCos = new Vector4(Cos(sea.CascadeRotationDegrees), MathF.Max(sea.DomainWarpMetres, 0f)),
                FftRotSin = new Vector4(Sin(sea.CascadeRotationDegrees), sea.DomainWarpWavelengthMetres),
                FftSector = SectorParams(sea.OnshoreFocusSectors),
                FftWave = new Vector4(ocean.MeanWavenumber, 0f),
                BathyRect = shore.Rect,
                // BathyParams.x is the switch for the whole group: 0 makes every shore helper in both stages
                // early-return its identity, so an ocean with no depth field is what it was before 16.13.0.
                BathyParams = new Vector4(shoreLive ? 1f : 0f, Math.Clamp(settings.ShoalingStrength, 0f, 1f),
                    MathF.Max(settings.ShoalingDepthScale, 1e-4f), ocean.SignificantHeight),
                SurfParams = new Vector4(Math.Clamp(settings.SurfStrength, 0f, 1f), breakDepth,
                    MathF.Max(settings.SurfBandWidth, 1e-3f), settings.SurfCrestBias),
                SurfShape = new Vector4(MathF.Max(settings.SurfTrailWidth, 0f),
                    Math.Clamp(settings.SurfAmplitudeCollapse, 0f, 1f), planeSurfaceY, shore.TexelMetres),
                // The plane, the grid and the eye all arrive already reduced by this. The surface's world-ANCHORED
                // patterns (the swell phase, the ocean sampling frame, the ripple and foam lattices, the onshore
                // focus point) add it back so they stay pinned to the world across an origin step.
                RenderOrigin = new Vector4(renderOrigin, 0f),
            };
        }

        /// <summary>The focus blend's sector ring: how many fixed lattice rotations the wanted heading is
        /// quantized to, and the <c>(cos, sin)</c> of ONE sector so the shader reaches the upper tap by composing
        /// rather than by a second <c>cos</c>/<c>sin</c> pair. Only two taps are ever non-zero, so the count is
        /// free at any value.</summary>
        static Vector4 SectorParams(int sectors)
        {
            int n = Math.Clamp(sectors, OceanFocus.MinSectors, OceanFocus.MaxSectors);
            float step = 2f * MathF.PI / n;
            return new Vector4(n, MathF.Cos(step), MathF.Sin(step), 0f);
        }

        /// <summary>Per-cascade <c>cos</c>/<c>sin</c> of <see cref="WaterSeaState.CascadeRotationDegrees"/>, taken
        /// here rather than in the shader so the sampling frame costs no transcendentals per vertex or per
        /// fragment. An all-zero rotation gives exactly <c>(1, 1, 1)</c> and <c>(0, 0, 0)</c> (both are
        /// <c>MathF.Cos(0f)</c> / <c>MathF.Sin(0f)</c>, which .NET pins), so the unrotated frame reaching the
        /// shader is the bit-exact identity rather than something a per-backend <c>cos</c> rounded.</summary>
        static Vector3 Cos(Vector3 degrees) => new(
            MathF.Cos(GerstnerWaves.DegreesToRadians(degrees.X)),
            MathF.Cos(GerstnerWaves.DegreesToRadians(degrees.Y)),
            MathF.Cos(GerstnerWaves.DegreesToRadians(degrees.Z)));

        /// <summary>Companion to <see cref="Cos"/>.</summary>
        static Vector3 Sin(Vector3 degrees) => new(
            MathF.Sin(GerstnerWaves.DegreesToRadians(degrees.X)),
            MathF.Sin(GerstnerWaves.DegreesToRadians(degrees.Y)),
            MathF.Sin(GerstnerWaves.DegreesToRadians(degrees.Z)));

        /// <summary>
        /// The live FFT ocean maps' shape, as the shaders need to read them: whether the maps are live at all, how
        /// many cascade layers they carry, their resolution, each layer's world tile size, and each layer's baked
        /// slope variance, plus the two spectrum scalars the shoaling and the breaker criterion need. A pure value
        /// so <c>PackUbo</c> stays testable without a device.
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

            /// <summary>Top mip index the maps carry; 0 when they have no chain, which makes both shader stages
            /// sample LOD 0 exactly as they did before mips existed.</summary>
            public float MaxMip { get; }

            /// <summary>Per-cascade energy-weighted mean wave number, rad/m. The shoaling taper's <c>k</c>.</summary>
            public Vector3 MeanWavenumber { get; }

            /// <summary>Significant wave height of the whole sea state, metres: what the breaker criterion
            /// measures the local depth against.</summary>
            public float SignificantHeight { get; }

            public OceanMaps(int cascadeCount, int resolution, Vector3 tiles, Vector3 slopeVariance, float maxMip,
                Vector3 meanWavenumber = default, float significantHeight = 0f)
            {
                Active = true;
                CascadeCount = cascadeCount;
                Resolution = resolution;
                Tiles = tiles;
                SlopeVariance = slopeVariance;
                MaxMip = maxMip;
                MeanWavenumber = meanWavenumber;
                SignificantHeight = significantHeight;
            }

            /// <summary>Snapshot a producer's current output. Returns the inactive default when it produced
            /// nothing this frame.</summary>
            public static OceanMaps From(OceanFftProducer producer)
                => producer.Active
                    ? new OceanMaps(producer.CascadeCount, producer.Resolution,
                        new Vector3(producer.TileMetres[0], producer.TileMetres[1], producer.TileMetres[2]),
                        new Vector3(producer.SlopeVariance[0], producer.SlopeVariance[1], producer.SlopeVariance[2]),
                        producer.MaxMip,
                        new Vector3(producer.MeanWavenumber[0], producer.MeanWavenumber[1], producer.MeanWavenumber[2]),
                        producer.SignificantHeight)
                    : default;
        }

        /// <summary>Draw all queued water planes into ColorDepthFB (lit colour + read-only scene depth). Caller
        /// guarantees the model + sky + decal passes are complete and the framebuffer is free to rebind. No-op when
        /// <paramref name="planes"/> is empty.</summary>
        public void Draw(IGpuCommandList cl, RenderResources res, ReadOnlySpan<WaterPlane> planes,
            Matrix4x4 viewProj, Vector3 lightDirection, Color lightColor, Vector3 cameraPos, WaterSettings settings,
            SkySettings sky, float timeSeconds, Vector3 renderOrigin = default)
        {
            if (planes.Length == 0) return;
            bool clipmap = settings.GridMode == WaterGridMode.Clipmap;
            LastClipmapRebuilds = 0;
            EnsureUboCapacity(planes.Length);
            if (clipmap)
            {
                EnsureClipPipeline();
                EnsureClipBuffers(planes, settings, renderOrigin);
            }
            else
            {
                EnsureGridBuffers();
            }

            // ONE ocean update per frame, ahead of the per-plane loop and of BindTargets (which binds whatever maps
            // it produced). Every queued plane samples the same cascades: this release has one sea state, not one
            // per body of water. The producer records its final dispatch into THIS command list, so the storage
            // writes and the draws that sample them share a list - the seam's guaranteed ordering.
            _ocean.Update(cl, settings, timeSeconds, wantMips: clipmap);
            var oceanMaps = OceanMaps.From(_ocean);
            // The depth field, likewise once per frame and ahead of BindTargets. Uploads only on a revision
            // change, so the steady state is a compare and nothing else.
            _bathymetry.Update(settings.Bathymetry);
            ShoreMaps shore = _bathymetry.Snapshot();
            BindTargets(res);

            Matrix4x4 clipVp = GpuClip.Correct(viewProj, _gd.Capabilities);
            for (int i = 0; i < planes.Length; i++)
            {
                // Per plane now, not once: the surf band measures the crest's height above THIS plane's still
                // water, and a scene may queue several planes at different levels.
                var u = PackUbo(clipVp, viewProj, lightDirection, lightColor, cameraPos, settings, sky, timeSeconds,
                    oceanMaps, renderOrigin, shore, planes[i].SurfaceY);
                cl.UpdateBuffer(_ubo!, (uint)i * SlotBytes, in u);
            }

            // Clipmap: every upload happens HERE, before a single draw is recorded, so no plane's geometry can be
            // written over another's mid-pass and the draw loop below touches no buffer contents at all.
            if (clipmap)
                for (int i = 0; i < planes.Length; i++)
                    RefreshClipmapPlane(cl, i, planes[i], cameraPos, settings, renderOrigin);

            cl.SetFramebuffer(res.ColorDepthFB);
            cl.SetPipeline(clipmap ? _clipPipe! : _pipe);
            cl.SetIndexBuffer(clipmap ? _clipIb! : _ib!, GpuIndexFormat.UInt32);
            if (clipmap) cl.SetVertexBuffer(0, _clipVb!);
            for (int i = 0; i < planes.Length; i++)
            {
                cl.SetGraphicsResourceSet(0, _set!, (uint)i * SlotBytes);
                if (clipmap)
                {
                    // Each plane reads its own slice: indexStart walks the index buffer, vertexOffset rebases the
                    // plane-local indices onto its own vertex block, so one binding serves the whole pass.
                    cl.DrawIndexed((uint)_clipSlots[i].IndexCount, 1,
                        (uint)(i * _clipSliceIndices), i * _clipSliceVerts, 0);
                    continue;
                }
                // The grid concentrates its vertices around the camera's XZ (clamped inside the plane by
                // BuildGridPositions), so the fixed vertex budget lands where the displaced swell actually reads.
                int n = WaterMath.BuildGridPositions(planes[i], cameraPos.X, cameraPos.Z, settings.GridFocusBias,
                    _gridScratch, _axisScratch);
                cl.UpdateBuffer<Vector3>(_vb!, 0, _gridScratch.AsSpan(0, n));
                cl.SetVertexBuffer(0, _vb!);
                cl.DrawIndexed((uint)WaterMath.GridIndexCount, 1, 0, 0, 0);
            }
        }

        /// <summary>
        /// Bring ONE plane's slice of the clipmap buffers up to date, and only if its rings actually moved.
        /// <para>
        /// The early-out is not an optimisation detail, it is the mode's headline behaviour. A ring only moves when
        /// the camera crosses one of its snap boundaries, so at walking pace most frames upload NOTHING, against
        /// the camera-focused grid's unconditional 113 KB of vertices every single frame. It also means the
        /// steadiness and the cost improve together rather than trading off, which is why
        /// <see cref="LastClipmapRebuilds"/> exists to hold it to that.
        /// </para>
        /// <para>
        /// <b>The lattice is decided in ABSOLUTE world space</b> - the plane and the focus are lifted back out of
        /// the render frame before anything is snapped - so a render-origin rebase moves no ring. It does change
        /// every vertex POSITION (those are render-relative by construction), which is why the origin is part of
        /// the key and a rebase costs one rebuild.
        /// </para>
        /// </summary>
        void RefreshClipmapPlane(IGpuCommandList cl, int index, in WaterPlane relativePlane, Vector3 cameraPos,
            WaterSettings settings, Vector3 renderOrigin)
        {
            float cell = MathF.Max(settings.ClipmapCellSize, 1e-4f);
            int ringCells = WaterClipmap.ClampRingCells(settings.ClipmapRingCells);

            // Back to absolute, for every decision that defines the lattice.
            var plane = new WaterPlane(relativePlane.CenterX + renderOrigin.X, relativePlane.SurfaceY + renderOrigin.Y,
                relativePlane.CenterZ + renderOrigin.Z, relativePlane.HalfExtentX, relativePlane.HalfExtentZ);
            int levels = LevelsFor(plane, settings, cell, ringCells);
            Vector2 focus = WaterClipmap.ClampFocus(plane, cameraPos.X + renderOrigin.X, cameraPos.Z + renderOrigin.Z);

            ClipSlot slot = _clipSlots[index];
            var key = new ClipKey(cell, ringCells, levels, settings.ClipmapGeomorphBand,
                plane.CenterX, plane.CenterZ, plane.SurfaceY,
                plane.HalfExtentX, plane.HalfExtentZ, renderOrigin.X, renderOrigin.Y, renderOrigin.Z);
            WaterClipmap.SnapIndices(focus.X, focus.Y, cell, levels, _clipSnapScratchX, _clipSnapScratchZ);

            bool same = slot.Valid && slot.Key == key;
            for (int l = 0; same && l < levels; l++)
                same = slot.SnapX[l] == _clipSnapScratchX[l] && slot.SnapZ[l] == _clipSnapScratchZ[l];
            if (same) return;

            int vcount = WaterClipmap.Build(plane, focus.X, focus.Y, cell, ringCells, levels,
                settings.ClipmapGeomorphBand, _clipVerts, _clipIndices, out int icount, renderOrigin);
            cl.UpdateBuffer<WaterClipmapVertex>(_clipVb!,
                (uint)(index * _clipSliceVerts) * ClipVertexBytes, _clipVerts.AsSpan(0, vcount));
            cl.UpdateBuffer<uint>(_clipIb!,
                (uint)(index * _clipSliceIndices) * sizeof(uint), _clipIndices.AsSpan(0, icount));

            slot.Key = key;
            slot.IndexCount = icount;
            Array.Copy(_clipSnapScratchX, slot.SnapX, levels);
            Array.Copy(_clipSnapScratchZ, slot.SnapZ, levels);
            slot.Valid = true;
            LastClipmapRebuilds++;
        }

        /// <summary>The ring count for one plane: the explicit setting when it is positive, otherwise derived so
        /// the outermost ring covers the plane from any camera position on it.</summary>
        static int LevelsFor(in WaterPlane plane, WaterSettings settings, float cell, int ringCells)
            => settings.ClipmapLevels > 0
                ? Math.Clamp(settings.ClipmapLevels, 1, WaterClipmap.MaxLevels)
                : WaterClipmap.LevelsFor(plane, cell, ringCells);

        /// <summary>
        /// Size the clipmap's buffers, CPU scratch and per-plane cache slots for the frame: one SLICE per plane,
        /// each big enough for the largest plane's grid. Growing only, and called once before any draw is recorded.
        /// <para>
        /// Both halves of that are load-bearing. A per-plane slice is what lets the cache mean anything with more
        /// than one plane (a shared buffer would have each plane overwrite the last, so a cache hit would draw
        /// someone else's geometry). And sizing up front is what stops a second, bigger plane reallocating
        /// mid-loop and freeing the buffer an already-recorded draw still points at - with
        /// <see cref="WaterSettings.ClipmapLevels"/> at 0 the ring count is derived per plane, so different-sized
        /// planes genuinely want different grids.
        /// </para>
        /// </summary>
        void EnsureClipBuffers(ReadOnlySpan<WaterPlane> planes, WaterSettings settings, Vector3 renderOrigin)
        {
            float cell = MathF.Max(settings.ClipmapCellSize, 1e-4f);
            int ringCells = WaterClipmap.ClampRingCells(settings.ClipmapRingCells);
            int vcount = 0, icount = 0;
            foreach (WaterPlane relative in planes)
            {
                var plane = new WaterPlane(relative.CenterX + renderOrigin.X, relative.SurfaceY + renderOrigin.Y,
                    relative.CenterZ + renderOrigin.Z, relative.HalfExtentX, relative.HalfExtentZ);
                int levels = LevelsFor(plane, settings, cell, ringCells);
                vcount = Math.Max(vcount, WaterClipmap.VertexCount(levels, ringCells));
                icount = Math.Max(icount, WaterClipmap.IndexCount(levels, ringCells));
            }

            if (_clipSlots.Length < planes.Length)
            {
                var grown = new ClipSlot[planes.Length];
                Array.Copy(_clipSlots, grown, _clipSlots.Length);
                for (int i = _clipSlots.Length; i < grown.Length; i++) grown[i] = new ClipSlot();
                _clipSlots = grown;
            }

            if (_clipVb != null && _clipSliceVerts >= vcount && _clipSliceIndices >= icount
                && (long)_clipSliceVerts * planes.Length * ClipVertexBytes <= _clipVb.SizeInBytes) return;

            _clipSliceVerts = Math.Max(_clipSliceVerts, vcount);
            _clipSliceIndices = Math.Max(_clipSliceIndices, icount);
            _clipVb?.Dispose();
            _clipIb?.Dispose();
            _clipVerts = new WaterClipmapVertex[_clipSliceVerts];
            _clipIndices = new uint[_clipSliceIndices];
            _clipVb = _gd.Factory.CreateBuffer(new GpuBufferDescription(
                (uint)(_clipSliceVerts * planes.Length) * ClipVertexBytes, GpuBufferUsage.VertexBuffer));
            _clipIb = _gd.Factory.CreateBuffer(new GpuBufferDescription(
                (uint)(_clipSliceIndices * planes.Length) * sizeof(uint), GpuBufferUsage.IndexBuffer));
            // Fresh buffers hold nothing, so every slot's "what my slice contains" claim is void.
            foreach (ClipSlot slot in _clipSlots) slot.Valid = false;
        }

        /// <summary>Byte size of one <see cref="WaterClipmapVertex"/>: Float3 position + Float2 coarse-neighbour
        /// offset + Float1 cell + Float1 morph. Must match the clipmap pipeline's vertex layout, which
        /// <c>WaterClipmapVertexTests</c> pins.</summary>
        internal const uint ClipVertexBytes = 28;

        /// <summary>The FFT producer's last-frame diagnostics: GPU stalls it cost and the wall-clock milliseconds
        /// they took. Internal, for the perf test that pins #311's cost as a measured number.</summary>
        internal (int Stalls, double StallMs) LastOceanCost => (_ocean.LastStallCount, _ocean.LastStallMs);

        public void Dispose()
        {
            _ocean.Dispose();
            _bathymetry.Dispose();
            _set?.Dispose();
            _clipPipe?.Dispose();
            _pipe.Dispose();
            _layout.Dispose();
            _clipShaders?.Dispose();
            _shaders.Dispose();
            _ubo?.Dispose();
            _vb?.Dispose();
            _ib?.Dispose();
            _clipVb?.Dispose();
            _clipIb?.Dispose();
        }
    }
}
