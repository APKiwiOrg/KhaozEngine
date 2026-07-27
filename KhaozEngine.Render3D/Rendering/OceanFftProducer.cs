using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// Produces the FFT ocean's per-frame surface maps: one displacement and one derivative/foam texture ARRAY,
    /// a layer per cascade, consumed by <see cref="WaterRenderer"/>'s vertex and fragment stages. Owned by the
    /// water renderer and updated ONCE per frame regardless of how many <see cref="WaterPlane"/>s are queued -
    /// there is one ocean state, and every plane samples the same maps.
    /// <para>
    /// <b>The frame costs exactly one GPU stall.</b> The seam has no cross-dispatch barrier (#311), so a
    /// read-after-write between dispatches has to be paid for with <c>End + Submit + WaitForIdle</c>. Rather than
    /// pay it per FFT stage (14 per 2D transform at 128 points, per cascade), each axis is ONE dispatch that keeps
    /// its transform line in shared memory, and the surrounding work is fused in: the row pass carries the
    /// spectrum's time evolution, the column pass carries the map assembly and the foam step. Every cascade's row
    /// work is independent, so all of it shares one command list and one drain. The column pass is then recorded
    /// into the SCENE's command list, immediately before the water draw that samples its output - which is the
    /// seam's other guaranteed pattern (compute writes a <c>Storage | Sampled</c> texture, a graphics pass in the
    /// same list samples it).
    /// </para>
    /// <para>
    /// <b>Foam crosses the frame boundary on purpose.</b> The accumulator is a plain storage buffer, one float per
    /// texel, read and rewritten by the single invocation that owns that texel. It is the only state that
    /// survives a frame, and it survives across the frame's own submit boundary, so it needs no ordering of its
    /// own. Keeping it in a BUFFER rather than a ping-ponged texture also sidesteps typed UAV loads (restricted to
    /// 32-bit formats on Direct3D11) and any within-frame storage/sampled usage flip on the same texture.
    /// </para>
    /// </summary>
    internal sealed class OceanFftProducer : IDisposable
    {
        /// <summary>Mirrors the <c>Params</c> block in both kernels: five vec4s, 80 bytes, std140-clean.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct OceanUbo
        {
            public Vector4 Cascade0, Cascade1, Cascade2;   // x = tile metres
            public Vector4 Timing;                          // x = time, y = dt, z = choppiness, w = depth
            public Vector4 Foaming;                         // x = gain, y = jacobian bias, z = dissipation, w = count
        }

        /// <summary>Byte size of <see cref="OceanUbo"/>. A multiple of 16, which every backend's constant buffer
        /// requires.</summary>
        internal const uint UboBytes = 80;

        /// <summary>The sea-state fields that force a rebuild of the baked <c>h0</c> field (or of the pipelines).
        /// Compared by value every frame; everything NOT here is a per-frame uniform and costs nothing to change.
        /// </summary>
        readonly record struct Bake(float WindSpeed, float WindDirection, float Fetch, float Depth,
            float Spread, float Swell, float SwellDirection, float Cutoff, int Seed,
            int Cascades, float Tile, float Ratio, int Resolution, bool Mipped);

        readonly IGpuDevice _gd;
        readonly List<IDisposable> _owned = new();

        // Placeholder 1x1 arrays bound whenever the FFT is off, so the water pipeline's resource layout is the
        // same shape in both modes and needs no second pipeline. Never written, so they need no Storage usage and
        // exist happily on a device with no compute at all.
        IGpuTexture? _idleMap;
        IGpuSampler? _sampler;

        IGpuComputeShader? _rowShader, _colShader;
        IGpuComputePipeline? _rowPipe, _colPipe;
        IGpuResourceLayout? _rowLayout, _colLayout;
        IGpuResourceSet? _rowSet, _colSet;
        IGpuBuffer? _ubo, _h0, _work, _foam;
        IGpuTexture? _map;
        IGpuTexture? _mipMap;
        Bake _baked;
        bool _hasBake;
        float _lastTime;
        bool _hasLastTime;

        public OceanFftProducer(IGpuDevice gd) => _gd = gd;

        /// <summary>True once <see cref="Update"/> has produced maps this frame. False in
        /// <see cref="WaterWaveSource.Procedural"/> mode, and on any device without compute support.</summary>
        public bool Active { get; private set; }

        /// <summary>Cascade layers actually produced (0 when inactive).</summary>
        public int CascadeCount { get; private set; }

        /// <summary>FFT resolution per axis of the produced cascades (0 when inactive).</summary>
        public int Resolution { get; private set; }

        /// <summary>World-space tile size of each produced cascade, metres. Only the first
        /// <see cref="CascadeCount"/> entries are meaningful.</summary>
        public readonly float[] TileMetres = new float[OceanSpectrum.MaxCascades];

        /// <summary>Each produced cascade's expected slope variance, from the baked spectrum. The water fragment
        /// needs it for the Toksvig transfer when a cascade band-limits out of the normal.</summary>
        public readonly float[] SlopeVariance = new float[OceanSpectrum.MaxCascades];

        /// <summary>Each produced cascade's energy-weighted mean wave number, rad/m, from the baked spectrum. The
        /// <c>k</c> the shoaling taper uses, so the long swell feels the bottom before the chop does.</summary>
        public readonly float[] MeanWavenumber = new float[OceanSpectrum.MaxCascades];

        /// <summary>Significant wave height of the whole baked sea state, metres (<c>4 sqrt(m0)</c> over every
        /// cascade's height variance). What the breaking criterion measures the local depth against; 0 when
        /// inactive.</summary>
        public float SignificantHeight { get; private set; }

        /// <summary>
        /// The ocean map array, always bindable (a 1x1 placeholder when inactive). Layers
        /// <c>[0, CascadeCount)</c> are DISPLACEMENT (xyz = world displacement); layers
        /// <c>[CascadeCount, 2 * CascadeCount)</c> are DERIVATIVES (x/y = height slope, z = accumulated foam,
        /// w = displacement Jacobian).
        /// <para>
        /// One texture rather than two, because the water pipeline needs a single ocean texture bound AHEAD of the
        /// scene depth in its resource layout: the vertex stage uses the ocean map and nothing else, and the only
        /// arrangement whose per-stage Metal slot numbering agrees with the layout's own is one where each stage's
        /// resources are a prefix of the layout. See the layout note in <see cref="WaterRenderer"/>.
        /// </para>
        /// <para>
        /// When a mip chain was asked for this is the MIPPED twin rather than the compute target itself (same
        /// shape, same layer meanings, seeded from the target each frame). See <see cref="BuildMipChain"/> for why
        /// it has to be a second texture. <see cref="MaxMip"/> reports which one a caller is looking at.
        /// </para>
        /// </summary>
        public IGpuTexture Map => _mipMap ?? _map ?? _idleMap!;

        /// <summary>
        /// Top mip index of <see cref="Map"/>: 0 when the maps carry no chain (every consumer through 16.6.0, and
        /// still the default), <c>MipCount - 1</c> when the clipmap grid asked for one. The water shaders read it
        /// as the ceiling on their per-ring / per-footprint band limit, and a 0 there is what makes both stages
        /// sample a literal LOD 0 exactly as they always did.
        /// </summary>
        public float MaxMip { get; private set; }

        /// <summary>The wrapping bilinear sampler the maps are read through. Wrapping is load-bearing: each
        /// cascade tiles the world at its own period, so its edges must meet.</summary>
        public IGpuSampler Sampler => _sampler!;

        /// <summary>Wall-clock milliseconds the last <see cref="Update"/> spent blocked on the row pass's drain -
        /// the whole cost of #311's missing barrier, measured rather than assumed. 0 when inactive.</summary>
        public double LastStallMs { get; private set; }

        /// <summary>GPU stalls (<c>Submit</c> + <c>WaitForIdle</c> pairs) the last <see cref="Update"/> cost.
        /// 1 when active, 0 when not, independent of cascade count and resolution. <b>The mip chain adds none of
        /// these</b>, which is the point: the copy and the <c>GenerateMipmaps</c> go in the scene list beside the
        /// column dispatch, so they cost transfer bandwidth and no extra drain.</summary>
        public int LastStallCount { get; private set; }

        /// <summary>Array-layer copies the last <see cref="Update"/> recorded to seed the mip chain's base level
        /// (0 when no chain is wanted). One per layer, plus one <c>GenerateMipmaps</c>; all in the scene list, so
        /// this is a transfer count and not a stall count.</summary>
        public int LastMipCopies { get; private set; }

        /// <summary>
        /// Bring the ocean maps up to <paramref name="timeSeconds"/>. Records the column pass (and the graphics
        /// pass's dependency on it) into <paramref name="sceneList"/>, which MUST be the same command list the
        /// water draw is recorded into, and must still be open.
        /// </summary>
        /// <param name="sceneList">The command list the water draw is recorded into.</param>
        /// <param name="settings">The frame's water settings.</param>
        /// <param name="timeSeconds">The wave clock.</param>
        /// <param name="wantMips">Whether the maps need a mip chain this frame (i.e. whether anything sampling
        /// them will ask for a level above 0). Only <see cref="WaterGridMode.Clipmap"/> does. Off, the maps and the
        /// work are exactly what shipped through 16.6.0; on, a second SAMPLED texture is kept alongside the compute
        /// target and its chain regenerated per frame.</param>
        /// <returns>True when the maps are live and the water shader should read them.</returns>
        public bool Update(IGpuCommandList sceneList, WaterSettings settings, float timeSeconds, bool wantMips = false)
        {
            EnsureIdle();
            LastStallCount = 0;
            LastStallMs = 0d;
            LastMipCopies = 0;

            if (settings.WaveSource != WaterWaveSource.FftOcean || !_gd.Capabilities.SupportsCompute)
            {
                Active = false;
                CascadeCount = 0;
                MaxMip = 0f;
                return false;
            }

            WaterSeaState sea = settings.SeaState;
            int resolution = ClampResolution(sea.CascadeResolution);
            int cascades = Math.Clamp(sea.CascadeCount, 1, OceanSpectrum.MaxCascades);
            var want = new Bake(sea.WindSpeed, sea.WindDirectionDegrees, sea.FetchKilometres, sea.DepthMetres,
                sea.DirectionalSpread, sea.SwellAmount, sea.SwellDirectionDegrees, sea.SmallWaveCutoffMetres,
                sea.Seed, cascades, sea.CascadeTileMetres, sea.CascadeTileRatio, resolution, wantMips);
            if (!_hasBake || !_baked.Equals(want)) Rebake(sea, want);

            CascadeCount = cascades;
            Resolution = resolution;
            MaxMip = _mipMap != null ? _mipMap.MipLevels - 1 : 0f;

            // Frame delta, from the same clock the surface is evaluated on. Clamped so a paused frame, a first
            // frame, or a step backwards cannot inject a foam spike or run the dissipation backwards.
            float dt = _hasLastTime ? Math.Clamp(timeSeconds - _lastTime, 0f, 0.1f) : 0f;
            _lastTime = timeSeconds;
            _hasLastTime = true;

            var u = new OceanUbo
            {
                Cascade0 = new Vector4(TileMetres[0], 0f, 0f, 0f),
                Cascade1 = new Vector4(cascades > 1 ? TileMetres[1] : TileMetres[0], 0f, 0f, 0f),
                Cascade2 = new Vector4(cascades > 2 ? TileMetres[2] : TileMetres[0], 0f, 0f, 0f),
                Timing = new Vector4(timeSeconds, dt, MathF.Max(sea.Choppiness, 0f), sea.DepthMetres),
                Foaming = new Vector4(MathF.Max(sea.FoamGain, 0f), sea.FoamJacobianBias,
                    MathF.Max(sea.FoamDissipationPerSecond, 0f), cascades),
            };
            _gd.UpdateBuffer(_ubo!, 0, u);

            uint groups = (uint)resolution;
            DispatchRowPass(groups, (uint)cascades);

            // The column pass goes in the SCENE's list, so the storage-image writes and the water draw that
            // samples them share one command list. That is the seam's guaranteed compute-to-graphics ordering;
            // splitting them across two lists is silently wrong on Vulkan.
            sceneList.SetComputePipeline(_colPipe!);
            sceneList.SetComputeResourceSet(0, _colSet!);
            sceneList.Dispatch(groups, (uint)cascades, 1);

            BuildMipChain(sceneList, resolution, cascades);

            Active = true;
            return true;
        }

        /// <summary>
        /// Seed the mipped SAMPLED map from the freshly written compute target and regenerate its chain, into the
        /// same list, straight after the column dispatch and before the draw that reads it.
        /// <para>
        /// <b>Why two textures rather than one mipped storage texture.</b> A storage-image binding must cover
        /// exactly ONE mip level; a view spanning a whole chain is invalid as a storage image, and the seam binds
        /// whole textures rather than views. So the compute target stays single-mip and unchanged (which also
        /// keeps its bitwise determinism guarantees intact) and the chain lives on a second, sampled-only texture.
        /// </para>
        /// <para>
        /// <b>Why the copy is safe here.</b> Rule 2 of the seam's ordering contract is about a DISPATCH reading
        /// what a dispatch wrote. This is a transfer, and a transfer is where all three backends do emit the
        /// synchronisation: Vulkan transitions the image out of its storage layout (a barrier), Metal ends the
        /// compute encoder to open a blit encoder, and Direct3D11 serialises the resource itself. The acceptance
        /// test checks the result against a CPU box-downsample on every backend rather than trusting that
        /// reasoning.
        /// </para>
        /// </summary>
        void BuildMipChain(IGpuCommandList sceneList, int resolution, int cascades)
        {
            LastMipCopies = 0;
            if (_mipMap == null) return;
            uint n = (uint)resolution;
            uint layers = (uint)Math.Max(2 * cascades, 2);
            for (uint layer = 0; layer < layers; layer++)
                sceneList.CopyTextureSubresource(_map!, 0, layer, _mipMap, 0, layer, n, n);
            sceneList.GenerateMipmaps(_mipMap);
            LastMipCopies = (int)layers;
        }

        /// <summary>The row pass on its own list, drained before the caller's list is submitted. This drain IS the
        /// frame's single stall; it is timed so the cost of #311 stays a measured number rather than a belief.</summary>
        void DispatchRowPass(uint groups, uint cascades)
        {
            using IGpuCommandList cl = _gd.Factory.CreateCommandList();
            cl.Begin();
            cl.SetComputePipeline(_rowPipe!);
            cl.SetComputeResourceSet(0, _rowSet!);
            cl.Dispatch(groups, cascades, 1);
            cl.End();
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            _gd.Submit(cl);
            _gd.WaitForIdle();
            LastStallMs = (System.Diagnostics.Stopwatch.GetTimestamp() - start)
                * 1000d / System.Diagnostics.Stopwatch.Frequency;
            LastStallCount = 1;
        }

        /// <summary>Clamp a requested resolution to a supported power of two. A caller asking for 200 gets 128
        /// rather than an exception: this is a look knob on a settings bag, not a contract.</summary>
        internal static int ClampResolution(int requested)
        {
            int n = Math.Clamp(requested, OceanSpectrum.MinResolution, OceanSpectrum.MaxResolution);
            int p = OceanSpectrum.MinResolution;
            while (p * 2 <= n) p *= 2;
            return p;
        }

        /// <summary>Rebuild everything that depends on the baked spectrum. Runs on a sea-state change only, never
        /// per frame, so the CPU spectrum bake can afford to be the readable closed form it is.</summary>
        void Rebake(WaterSeaState sea, in Bake want)
        {
            bool shapeChanged = !_hasBake || _baked.Resolution != want.Resolution
                || _baked.Cascades != want.Cascades || _baked.Mipped != want.Mipped;
            if (shapeChanged) ReleaseFftResources();

            int n = want.Resolution;
            int cascades = want.Cascades;
            int texels = n * n;
            IGpuResourceFactory f = _gd.Factory;

            if (shapeChanged)
            {
                _ubo = Own(f.CreateBuffer(new GpuBufferDescription(UboBytes, GpuBufferUsage.UniformBuffer)));
                _h0 = Own(f.CreateBuffer(new GpuBufferDescription(
                    (uint)(texels * cascades * 16), GpuBufferUsage.StructuredBufferReadWrite, 16)));
                _work = Own(f.CreateBuffer(new GpuBufferDescription(
                    (uint)(texels * cascades * OceanComputeShaders.Fields * 8), GpuBufferUsage.StructuredBufferReadWrite, 8)));
                _foam = Own(f.CreateBuffer(new GpuBufferDescription(
                    (uint)(texels * cascades * 4), GpuBufferUsage.StructuredBufferReadWrite, 4)));
                _gd.UpdateBuffer(_foam, 0, new float[texels * cascades]);

                // At least TWO array layers, always, even for a single cascade. A one-layer texture is created as
                // a plain 2D texture one layer down, and binding that to a shader's texture2DArray slot writes
                // nothing and reads zero, silently: a single-cascade ocean produced a perfectly correct foam
                // BUFFER and an entirely blank map. The spare layer costs one texel grid and is never sampled,
                // because the shader only reads up to the cascade count.
                // 2 * cascades layers: displacement then derivatives. Never fewer than two, even for a single
                // cascade - a one-layer texture is created as a plain 2D texture one layer down, and binding that
                // to a shader's texture2DArray slot writes nothing and reads zero, silently. A single-cascade
                // ocean produced a perfectly correct foam BUFFER and an entirely blank map because of it.
                uint layers = (uint)Math.Max(2 * cascades, 2);
                _map = Own(f.CreateTexture(GpuTextureDescription.Texture2DArray((uint)n, (uint)n,
                    GpuPixelFormat.R16G16B16A16Float, GpuTextureUsage.Storage | GpuTextureUsage.Sampled,
                    layers, 1)));

                // The mipped SAMPLED twin, only when something is going to ask for a level above 0. Same shape and
                // format, a full chain, and no Storage flag - a storage-image binding must cover exactly one mip
                // level, so the compute target and the mipped read cannot be the same texture (see BuildMipChain).
                if (want.Mipped)
                    _mipMap = Own(f.CreateTexture(GpuTextureDescription.Texture2DArray((uint)n, (uint)n,
                        GpuPixelFormat.R16G16B16A16Float,
                        GpuTextureUsage.Sampled | GpuTextureUsage.GenerateMipmaps,
                        layers, (uint)WaterClipmap.MipCount(n))));

                BuildPipelines(f, n);
            }

            // Bake h0 for every cascade into one upload-shaped array.
            var h0 = new Vector4[texels * cascades];
            Array.Clear(TileMetres);
            Array.Clear(SlopeVariance);
            Array.Clear(MeanWavenumber);
            float heightVariance = 0f;
            for (int c = 0; c < cascades; c++)
            {
                TileMetres[c] = OceanSpectrum.TileMetres(c, sea.CascadeTileMetres, sea.CascadeTileRatio);
                OceanSpectrum.CascadeStatistics stats =
                    OceanSpectrum.BuildInitialSpectrum(sea, c, n, h0.AsSpan(c * texels, texels));
                SlopeVariance[c] = stats.SlopeVariance;
                MeanWavenumber[c] = stats.MeanWavenumber;
                // The cascades PARTITION wave-number space (OceanSpectrum.CascadeBand), so their height variances
                // add without double counting and the sum is m0 for the whole sea.
                heightVariance += stats.HeightVariance;
            }
            SignificantHeight = WaterShoaling.SignificantHeight(heightVariance);
            _gd.UpdateBuffer(_h0!, 0, h0);

            _baked = want;
            _hasBake = true;
        }

        void BuildPipelines(IGpuResourceFactory f, int n)
        {
            _rowShader = Own(f.CreateComputeShaderFromSpirv(OceanComputeShaders.RowPass(n)));
            _colShader = Own(f.CreateComputeShaderFromSpirv(OceanComputeShaders.ColumnPass(n)));

            _rowLayout = Own(f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Params", GpuResourceKind.UniformBuffer, GpuShaderStages.Compute),
                new GpuResourceLayoutElement("H0Buf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute),
                new GpuResourceLayoutElement("WorkBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute))));
            _colLayout = Own(f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Params", GpuResourceKind.UniformBuffer, GpuShaderStages.Compute),
                new GpuResourceLayoutElement("WorkBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute),
                new GpuResourceLayoutElement("FoamBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute),
                new GpuResourceLayoutElement("OceanMap", GpuResourceKind.TextureReadWrite, GpuShaderStages.Compute))));

            _rowPipe = Own(f.CreateComputePipeline(new GpuComputePipelineDescription(_rowShader, _rowLayout)));
            _colPipe = Own(f.CreateComputePipeline(new GpuComputePipelineDescription(_colShader, _colLayout)));

            _rowSet = Own(f.CreateResourceSet(new GpuResourceSetDescription(_rowLayout, _ubo!, _h0!, _work!)));
            _colSet = Own(f.CreateResourceSet(new GpuResourceSetDescription(_colLayout, _ubo!, _work!, _foam!, _map!)));
        }

        /// <summary>The always-present bindings: the wrapping sampler and the 1x1 placeholder maps. Created once,
        /// on the first update, and kept for the renderer's life.</summary>
        void EnsureIdle()
        {
            if (_sampler != null) return;
            IGpuResourceFactory f = _gd.Factory;
            _sampler = Own(f.CreateSampler(new GpuSamplerDescription(GpuSamplerFilter.MinLinearMagLinearMipLinear,
                GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap)));
            // Two layers for the same reason the live map has at least two (see Rebake): a one-layer array is not
            // an array texture on every backend, and this one is bound to a texture2DArray slot.
            _idleMap = Own(f.CreateTexture(GpuTextureDescription.Texture2DArray(1, 1,
                GpuPixelFormat.R16G16B16A16Float, GpuTextureUsage.Sampled, 2, 1)));
        }

        T Own<T>(T resource) where T : IDisposable
        {
            _owned.Add(resource);
            return resource;
        }

        /// <summary>Drop every resource whose SHAPE depends on the resolution or the cascade count, so a knob
        /// change rebuilds them. Drains first: the maps may still be referenced by the frame in flight, and
        /// disposing a live resource against a busy device is the teardown race the seam already learned about.
        /// The placeholder maps and the sampler are deliberately kept - they never change shape.</summary>
        void ReleaseFftResources()
        {
            if (!_hasBake) return;
            _gd.WaitForIdle();
            Drop(ref _colSet); Drop(ref _rowSet);
            Drop(ref _colPipe); Drop(ref _rowPipe);
            Drop(ref _colShader); Drop(ref _rowShader);
            Drop(ref _colLayout); Drop(ref _rowLayout);
            Drop(ref _mipMap); Drop(ref _map);
            Drop(ref _foam); Drop(ref _work); Drop(ref _h0); Drop(ref _ubo);
            _hasBake = false;
            Active = false;
            MaxMip = 0f;
        }

        void Drop<T>(ref T? resource) where T : class, IDisposable
        {
            if (resource is null) return;
            _owned.Remove(resource);
            resource.Dispose();
            resource = null;
        }

        public void Dispose()
        {
            _gd.WaitForIdle();
            for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
            _owned.Clear();
        }
    }
}
