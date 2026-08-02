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
    /// <b>The steady-state frame costs no GPU stall at all.</b> The seam has no cross-dispatch barrier (#311), so
    /// a read-after-write between dispatches has to be paid for with <c>End + Submit + WaitForIdle</c>. Rather than
    /// pay it per FFT stage (14 per 2D transform at 128 points, per cascade), each axis is ONE dispatch that keeps
    /// its transform line in shared memory, and the surrounding work is fused in: the row pass carries the
    /// spectrum's time evolution, the column pass carries the map assembly and the foam step. That left ONE
    /// dependency and one drain per frame, which #398 then measured at 0.93 ms of blocked frame time, so the last
    /// one is gone too: the row intermediate is PING-PONGED, frame N's column pass consumes the rows frame N-1
    /// wrote, and the row pass is dispatched one frame ahead in time so the surface phase is unchanged (the
    /// compensation, and why it is exact, is <see cref="OceanFrameClock"/>). Both dispatches are recorded into the
    /// SCENE's command list, the column immediately before the water draw that samples its output - which is the
    /// seam's other guaranteed pattern (compute writes a <c>Storage | Sampled</c> texture, a graphics pass in the
    /// same list samples it). A drain is left only for PRIMING, on the first frame of an ocean and after a re-bake.
    /// </para>
    /// <para>
    /// <b>The row buffers cross the frame boundary, exactly as the foam accumulator already does.</b> That is not a
    /// new ordering assumption: the foam buffer has been read and rewritten across the frame's own submit boundary
    /// since 16.3.0, on the same three backends, and the ping-pong's cross-frame read-after-write is the same
    /// dependency between the same two submissions. It is also the weaker of the two, because the halves ALTERNATE:
    /// a frame reads the buffer it did not write, so the two dispatches recorded into one list touch disjoint
    /// storage and need no ordering with respect to each other at all.
    /// </para>
    /// <para>
    /// <b>Foam crosses the frame boundary on purpose.</b> The accumulator is a plain storage buffer, one float per
    /// texel, read and rewritten by the single invocation that owns that texel. It was the only state that
    /// survived a frame before the ping-pong above, and it survives across the frame's own submit boundary, so it
    /// needs no ordering of its own. Keeping it in a BUFFER rather than a ping-ponged texture also sidesteps typed
    /// UAV loads (restricted to 32-bit formats on Direct3D11) and any within-frame storage/sampled usage flip on
    /// the same texture.
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

        /// <summary>What <see cref="Prepare"/> decided for the frame and <see cref="Record"/> then records. Null
        /// means there is nothing to record (no plane wanted the ocean, or the device has no compute).</summary>
        readonly record struct Frame(WaterSeaState Sea, int Cascades, int Resolution, uint Groups,
            float RowTime, float Delta);

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

        // The ping-pong: two row intermediates and the two resource sets that bind each. The row pass writes
        // _work[1 - _pong] while the column pass reads _work[_pong], which is what the PREVIOUS frame's row pass
        // wrote, and the pair swaps at the end of the frame. Two buffers rather than one is the whole cost: at the
        // shipping defaults (3 cascades, 128 points, 4 complex fields) that is 1.5 MB more, and it buys back a full
        // device drain per frame.
        readonly IGpuResourceSet?[] _rowSets = new IGpuResourceSet?[2];
        readonly IGpuResourceSet?[] _colSets = new IGpuResourceSet?[2];
        readonly IGpuBuffer?[] _work = new IGpuBuffer?[2];
        int _pong;

        IGpuBuffer? _ubo, _h0, _foam;
        IGpuTexture? _map;
        IGpuTexture? _mipMap;
        Bake _baked;
        bool _hasBake;
        readonly OceanFrameClock _clock = new();

        // The frame split (#423). _prepared is the contract check - Record refuses to run on a frame nobody
        // prepared, rather than quietly preparing itself back inside the frame's recording. _pending is what
        // Prepare decided, and is null on a frame with no ocean to record.
        bool _prepared;
        Frame? _pending;

        public OceanFftProducer(IGpuDevice gd) => _gd = gd;

        /// <summary>True once <see cref="Record"/> has produced maps this frame. False when no plane asked for the
        /// ocean (every effective wave source is <see cref="WaterWaveSource.Procedural"/>), and on any device
        /// without compute support.</summary>
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

        /// <summary>Wall-clock milliseconds the last <see cref="Prepare"/> spent blocked on a priming drain - the
        /// residue of #311's missing barrier, measured rather than assumed. 0 on a steady-state frame, which since
        /// #398's ping-pong is every frame but the priming ones.</summary>
        public double LastStallMs { get; private set; }

        /// <summary>GPU stalls (<c>Submit</c> + <c>WaitForIdle</c> pairs) the last <see cref="Prepare"/> cost.
        /// <b>0 in the steady state</b> (#398): a frame's column pass consumes the row output of the frame before
        /// it, so nothing within the frame waits on anything. 1 on a PRIMING frame - the first frame of an ocean,
        /// the frame after a sea-state re-bake, or a frame whose wave clock jumped past
        /// <see cref="OceanFrameClock.MaxRowDrift"/> - where this frame's rows have to be produced and drained
        /// before they can be consumed. Independent of cascade count and resolution either way. <b>The mip chain
        /// adds none of these</b>, which is the point: the copy and the <c>GenerateMipmaps</c> go in the scene list
        /// beside the column dispatch, so they cost transfer bandwidth and no drain.</summary>
        public int LastStallCount { get; private set; }

        /// <summary>Array-layer copies the last <see cref="Record"/> recorded to seed the mip chain's base level
        /// (0 when no chain is wanted). One per layer, plus one <c>GenerateMipmaps</c>; all in the scene list, so
        /// this is a transfer count and not a stall count.</summary>
        public int LastMipCopies { get; private set; }

        /// <summary>
        /// PHASE 1 of the frame, and it runs with NO frame command list open (see
        /// <see cref="IFramePreparer"/>): bring the bake up to date with the sea state, advance the wave clock, and
        /// - only on a priming frame - produce this frame's rows on a command list of its own, submitted and
        /// drained here. Then <see cref="Record"/> records the frame's dispatches into the scene's list.
        /// <para>
        /// <b>Why the prime cannot live in phase 2.</b> It opens, submits and drains a SECOND command list. With
        /// Direct3D11 in immediate-context mode a command list is the device's immediate context and opening one
        /// resets it, so doing that while the frame's list is recording wipes the bindings the frame believes are
        /// live and the device faults a few draws later
        /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/423">#423</see>). Splitting the frame here
        /// changes only WHEN the CPU records the prime, never what the GPU executes: the prime is submitted and
        /// waited on before the frame's list is submitted either way, so the maps are bit-for-bit what they were.
        /// </para>
        /// </summary>
        /// <param name="settings">The frame's water settings. Read for the sea state and the wave clock only: the
        /// decision of whether to run at all is <paramref name="wantOcean"/>'s.</param>
        /// <param name="timeSeconds">The wave clock.</param>
        /// <param name="wantOcean">Whether anything drawn this frame actually reads the cascades, which the CALLER
        /// decides. It used to be read off <c>settings.WaveSource</c> here, and that is wrong once the wave source
        /// can be overridden per plane (<see cref="WaterLook.WaveSource"/>): a scene defaulting to
        /// <see cref="WaterWaveSource.Procedural"/> with one plane on the ocean would find the producer inactive
        /// and render that plane procedurally, silently. One ocean either way, driven by demand rather than by the
        /// scene default.</param>
        /// <param name="wantMips">Whether the maps need a mip chain this frame (i.e. whether anything sampling
        /// them will ask for a level above 0). Only <see cref="WaterGridMode.Clipmap"/> does. Off, the maps and the
        /// work are exactly what shipped through 16.6.0; on, a second SAMPLED texture is kept alongside the compute
        /// target and its chain regenerated per frame.</param>
        public void Prepare(WaterSettings settings, float timeSeconds, bool wantOcean, bool wantMips = false)
        {
            // A frame still pending here is a frame that was PLANNED and never RECORDED: the host prepared and then
            // did not render (a dropped frame, a host that bailed between the two phases). The wave clock counted
            // it as having produced rows, because Advance decides and publishes in one step, but the row dispatch
            // lives in Record and never happened. Left alone, the next frame would consume the frame-before-last's
            // rows as if they were current, silently and with no drain to notice. Dropping them re-primes instead,
            // which is what Rebake does for the same reason and is the clock's documented escape hatch. Costs one
            // drain on a frame that was already anomalous, and nothing at all on the paired path, where Record has
            // always cleared this by now.
            if (_pending is not null) _clock.Invalidate();
            _prepared = true;
            _pending = null;
            EnsureIdle();
            LastStallCount = 0;
            LastStallMs = 0d;
            LastMipCopies = 0;

            if (!wantOcean || !_gd.Capabilities.SupportsCompute)
            {
                Active = false;
                CascadeCount = 0;
                MaxMip = 0f;
                return;
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

            // The frame's clock: the delta the foam integrates over, the time the row pass runs AHEAD to, and
            // whether the pending row output describes this frame at all. See OceanFrameClock for the whole phase
            // argument - it is the fidelity claim of the ping-pong and it is tested headless.
            OceanFrameTick tick = _clock.Advance(timeSeconds);
            uint groups = (uint)resolution;

            // PRIME. The column pass recorded by Record consumes rows written by the PREVIOUS frame, so a frame
            // that has none (the first of an ocean, the one after a re-bake, one whose wave clock jumped) produces
            // its own the old way: one dispatch, one drain, this frame's own time. That frame then renders exactly
            // what the pre-ping-pong code rendered, and every frame after it costs no drain at all.
            if (tick.Prime) PrimeRowPass(sea, cascades, groups, timeSeconds, tick.Delta);

            _pending = new Frame(sea, cascades, resolution, groups, tick.RowTime, tick.Delta);
        }

        /// <summary>
        /// PHASE 2 of the frame: record both compute dispatches (this frame's column pass, and the row pass whose
        /// output the NEXT frame's column pass consumes) into <paramref name="sceneList"/>, which MUST be the same
        /// command list the water draw is recorded into, and must still be open. Records only - it opens no list,
        /// submits nothing and never blocks, so it is safe to call mid-recording on every backend.
        /// </summary>
        /// <param name="sceneList">The command list the water draw is recorded into.</param>
        /// <returns>True when the maps are live and the water shader should read them.</returns>
        /// <exception cref="InvalidOperationException"><see cref="Prepare"/> was not called for this frame. The
        /// producer cannot fall back to preparing itself here, because that is exactly the nested command list the
        /// split exists to remove.</exception>
        public bool Record(IGpuCommandList sceneList)
        {
            if (!_prepared)
                throw new InvalidOperationException(
                    "OceanFftProducer.Record was called without a Prepare for this frame. The host must call "
                    + "Scene3D.PrepareFrame() after queueing the frame's draws and before opening the frame's "
                    + "command list (see IFramePreparer).");
            _prepared = false;
            if (_pending is not { } frame) return false;
            _pending = null;

            // ONE parameter block serves both passes even though they belong to different frames: the row pass is
            // the only stage that reads Timing.x, and the column pass reads only the delta, the choppiness and the
            // foam knobs, all of which are this frame's.
            WriteUbo(sceneList, frame.Sea, frame.Cascades, frame.RowTime, frame.Delta);

            // Both dispatches go in the SCENE's list, so the column pass's storage-image writes and the water draw
            // that samples them share one command list. That is the seam's guaranteed compute-to-graphics ordering;
            // splitting them across two lists is silently wrong on Vulkan. The two dispatches touch DISJOINT work
            // buffers (the row pass writes the half the column pass is not reading), so they need no ordering with
            // respect to each other and the row pass is recorded first, giving its output the whole rest of the
            // frame to land before the next frame consumes it.
            int read = _pong, write = 1 - _pong;
            sceneList.SetComputePipeline(_rowPipe!);
            sceneList.SetComputeResourceSet(0, _rowSets[write]!);
            sceneList.Dispatch(frame.Groups, (uint)frame.Cascades, 1);

            sceneList.SetComputePipeline(_colPipe!);
            sceneList.SetComputeResourceSet(0, _colSets[read]!);
            sceneList.Dispatch(frame.Groups, (uint)frame.Cascades, 1);
            _pong = write;

            BuildMipChain(sceneList, frame.Resolution, frame.Cascades);

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

        /// <summary>
        /// Record the frame's parameter block INTO <paramref name="list"/>, immediately before the dispatches that
        /// read it. <paramref name="rowTime"/> is the wave-clock time the ROW pass evolves the spectrum to, which
        /// is NOT the frame's own time in the steady state: it is one predicted frame ahead, because the rows are
        /// consumed by the next frame's column pass (see <see cref="OceanFrameClock"/>). The column pass never
        /// reads it, so one block serves both.
        /// <para>
        /// <b>Recorded into the list, not written through the device, and that is load-bearing.</b>
        /// <c>IGpuDevice.UpdateBuffer</c> lands when the CPU calls it, while these dispatches run when the list is
        /// submitted, so with no drain between the two the next frame's block can overwrite this one before this
        /// frame's dispatches have read it. That is not theoretical: it is what the mid-frame drain used to hide.
        /// Removing the drain with the write still on the device path shifted the whole surface a frame ahead,
        /// because the CPU ran on and every queued dispatch read the LAST block written rather than its own. A
        /// list-recorded update is copied at RECORD time and applied in list order, so each frame's dispatches read
        /// the block recorded with them however far the CPU has run ahead. <c>WaterRenderer</c> already updates its
        /// own per-plane block this way.
        /// </para>
        /// </summary>
        void WriteUbo(IGpuCommandList list, WaterSeaState sea, int cascades, float rowTime, float dt)
        {
            var u = new OceanUbo
            {
                Cascade0 = new Vector4(TileMetres[0], 0f, 0f, 0f),
                Cascade1 = new Vector4(cascades > 1 ? TileMetres[1] : TileMetres[0], 0f, 0f, 0f),
                Cascade2 = new Vector4(cascades > 2 ? TileMetres[2] : TileMetres[0], 0f, 0f, 0f),
                Timing = new Vector4(rowTime, dt, MathF.Max(sea.Choppiness, 0f), sea.DepthMetres),
                Foaming = new Vector4(MathF.Max(sea.FoamGain, 0f), sea.FoamJacobianBias,
                    MathF.Max(sea.FoamDissipationPerSecond, 0f), cascades),
            };
            list.UpdateBuffer(_ubo!, 0, u);
        }

        /// <summary>The row pass on its own list, drained before the caller's list is submitted, into the half of
        /// the ping-pong this frame's column pass is about to read. The ONLY remaining drain (#311/#398): a frame
        /// that has no pending row output has to produce it within the frame, and the seam's only ordering for a
        /// dispatch that reads what a dispatch wrote is a submit and a device wait. It is timed so what is left of
        /// #311's cost stays a measured number rather than a belief.
        /// <para>
        /// It carries its own parameter block, recorded into its own list: this pass evolves the spectrum to THIS
        /// frame's time, while the scene list's block carries the next frame's, and each has to reach its own
        /// dispatch (see <see cref="WriteUbo"/>).
        /// </para></summary>
        void PrimeRowPass(WaterSeaState sea, int cascades, uint groups, float timeSeconds, float dt)
        {
            using IGpuCommandList cl = _gd.Factory.CreateCommandList();
            cl.Begin();
            WriteUbo(cl, sea, cascades, timeSeconds, dt);
            cl.SetComputePipeline(_rowPipe!);
            cl.SetComputeResourceSet(0, _rowSets[_pong]!);
            cl.Dispatch(groups, (uint)cascades, 1);
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
                // Two row intermediates, ping-ponged across the frame boundary (see the class note): the frame
                // writes one and reads the other, which is what removes the within-frame read-after-write and with
                // it the drain that used to pay for it.
                for (int i = 0; i < 2; i++)
                    _work[i] = Own(f.CreateBuffer(new GpuBufferDescription(
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

            // Whatever rows are pending were evolved from the spectrum that just went away (or live in buffers that
            // were just rebuilt), so the next frame primes rather than assembling a sea from the old sea state.
            // A re-bake is a sea-state change, not a per-frame event, so the drain it costs is not a per-frame one.
            _clock.Invalidate();
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

            // A resource set binds one concrete buffer, so the ping-pong needs a pair of each rather than one set
            // rebound per frame. They are otherwise identical, and nothing else about either pass changes.
            for (int i = 0; i < 2; i++)
            {
                _rowSets[i] = Own(f.CreateResourceSet(new GpuResourceSetDescription(_rowLayout, _ubo!, _h0!, _work[i]!)));
                _colSets[i] = Own(f.CreateResourceSet(new GpuResourceSetDescription(_colLayout, _ubo!, _work[i]!, _foam!, _map!)));
            }
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
            for (int i = 0; i < 2; i++) { Drop(ref _colSets[i]); Drop(ref _rowSets[i]); }
            Drop(ref _colPipe); Drop(ref _rowPipe);
            Drop(ref _colShader); Drop(ref _rowShader);
            Drop(ref _colLayout); Drop(ref _rowLayout);
            Drop(ref _mipMap); Drop(ref _map);
            Drop(ref _foam);
            for (int i = 0; i < 2; i++) Drop(ref _work[i]);
            Drop(ref _h0); Drop(ref _ubo);
            _pong = 0;
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
