using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    // The post chain's target binding: the resource sets built over one IPostChainTargets and the ping-output
    // pipelines rebuilt when its colour format flips.
    internal sealed partial class PixelPostProcess
    {
        // The ping framebuffers' colour format the ping pipelines were last built for (compared in BindTargets to
        // detect the HDR toggle). Seeded to the ctor's pingOutput so the first BindTargets sees no change.
        GpuOutputDescription _pingOutput;

        /// <summary>The most source slots a target set reports (<see cref="IPostChainTargets.SourceSlotCount"/>).</summary>
        internal const int MaxSourceSlots = 2;

        /// <summary>The nine sets that read the chain SOURCE, for one source slot. The internal chain builds one of these
        /// over ColorTex, the temporal display chain one per history target. The ping-reading sets below do not depend on
        /// the slot.</summary>
        sealed class SourceSets
        {
            public IGpuResourceSet Palette = null!, Edge = null!, Tone = null!, BlitPoint = null!, BlitLinear = null!, Fxaa = null!;
            public IGpuResourceSet? Bright, Composite, Apply;

            public void Dispose()
            {
                Palette.Dispose(); Edge.Dispose(); Tone.Dispose(); BlitPoint.Dispose(); BlitLinear.Dispose(); Fxaa.Dispose();
                Bright?.Dispose(); Composite?.Dispose(); Apply?.Dispose();
            }
        }

        readonly SourceSets?[] _fromSource = new SourceSets?[MaxSourceSlots];
        IGpuResourceSet _paletteFromPingA = null!, _paletteFromPingB = null!;
        IGpuResourceSet _edgeFromPingA = null!, _edgeFromPingB = null!;
        IGpuResourceSet _toneFromPingA = null!, _toneFromPingB = null!;   // tonemap reads (linear)
        IGpuResourceSet _blitPingAP = null!, _blitPingBP = null!;         // point sampler
        IGpuResourceSet _blitPingAL = null!, _blitPingBL = null!;         // linear sampler
        IGpuResourceSet _fxaaFromPingA = null!, _fxaaFromPingB = null!;   // FXAA reads (linear)
        // Bloom resource sets, only built while the targets report BloomAllocated (the half-res targets exist).
        IGpuResourceSet? _brightFromPingA, _brightFromPingB;              // bright-pass reads a full-res ping (linear)
        IGpuResourceSet? _blurHFromBloomA, _blurVFromBloomB;              // horizontal BloomA->BloomB (via _blurBufH), vertical BloomB->BloomA (via _blurBufV)
        IGpuResourceSet? _compositePingABloomA, _compositePingBBloomA;    // composite reads (full-res ping, BloomA)
        IPostChainTargets? _bound;

        /// <summary>Build the per-target resource sets. Call on construction and whenever the targets resize (incl.
        /// a bloom enable/disable toggle, which (re)allocates or frees the bloom pair), and whenever the chain switches
        /// between the internal targets and the temporal display targets.</summary>
        /// <exception cref="ArgumentException"><paramref name="res"/> reports fewer than one or more than
        /// <see cref="MaxSourceSlots"/> source slots. The chain stays bound to its previous targets.</exception>
        public void BindTargets(IPostChainTargets res)
        {
            // Refused before anything is disposed or rebuilt, so the bound targets and their sets stay usable.
            int slots = res.SourceSlotCount;
            if (slots < 1 || slots > MaxSourceSlots)
                throw new ArgumentException(
                    $"The post chain targets report {slots} source slots. The chain builds sets for 1 to {MaxSourceSlots}.",
                    nameof(res));
            // Rebuild the ping-output pipelines first if the ping colour format flipped (HDR float16 <-> legacy UNorm),
            // independent of the resource-set guard below (a pure format toggle keeps the same size/bloom state).
            RebuildPingPipelinesIfFormatChanged(res);
            // Generation-based guard: any recreate of the targets bumps it, including same-size recreates (MSAA
            // sample-count / bloom / HDR toggles), so the sets can never outlive the textures they reference.
            if (ReferenceEquals(_bound, res) && res.Generation == _boundGen) return;
            DisposeSets();
            _sharpen?.ReleaseSets();   // its cached sets reference the targets being replaced
            var f = _gd.Factory;
            var samp = _gd.PointSampler;
            var lin = _gd.LinearSampler;
            IGpuTexture pingA = res.PingA, pingB = res.PingB;

            // Palette quantize samples 1:1 with the point sampler (a colour-snap, not a filter). Legacy runs it first
            // from the source, the HDR chain runs it after tonemap from a ping.
            _paletteFromPingA = f.CreateResourceSet(new GpuResourceSetDescription(_palLayout, pingA, samp, _palBuf));
            _paletteFromPingB = f.CreateResourceSet(new GpuResourceSetDescription(_palLayout, pingB, samp, _palBuf));
            // Edge outline reads the colour source + the (format-fixed) normal/linear-depth MRT attachments, which stay
            // the internal ones on either chain.
            _edgeFromPingA = f.CreateResourceSet(new GpuResourceSetDescription(_edgeLayout, pingA, res.NormalTex, res.DepthColorTex, samp, _edgeBuf));
            _edgeFromPingB = f.CreateResourceSet(new GpuResourceSetDescription(_edgeLayout, pingB, res.NormalTex, res.DepthColorTex, samp, _edgeBuf));
            // Tonemap reads its input 1:1 with the linear sampler (matches the FXAA/blit-linear precedent for a 1:1 read).
            _toneFromPingA = f.CreateResourceSet(new GpuResourceSetDescription(_toneLayout, pingA, lin, _toneBuf));
            _toneFromPingB = f.CreateResourceSet(new GpuResourceSetDescription(_toneLayout, pingB, lin, _toneBuf));
            _blitPingAP = f.CreateResourceSet(new GpuResourceSetDescription(_blitLayout, pingA, samp, _finalBuf));
            _blitPingBP = f.CreateResourceSet(new GpuResourceSetDescription(_blitLayout, pingB, samp, _finalBuf));
            _blitPingAL = f.CreateResourceSet(new GpuResourceSetDescription(_blitLayout, pingA, lin, _finalBuf));
            _blitPingBL = f.CreateResourceSet(new GpuResourceSetDescription(_blitLayout, pingB, lin, _finalBuf));
            // FXAA samples its input bilinearly (the diagonal blend taps land between texels).
            _fxaaFromPingA = f.CreateResourceSet(new GpuResourceSetDescription(_fxaaLayout, pingA, lin, _fxaaBuf));
            _fxaaFromPingB = f.CreateResourceSet(new GpuResourceSetDescription(_fxaaLayout, pingB, lin, _fxaaBuf));

            if (res.BloomAllocated)
            {
                // Bright-pass reads whichever full-res target holds the current chain source, bilinearly (a soft
                // half-res downsample tap). The source's own set is in SourceSets.
                _brightFromPingA = f.CreateResourceSet(new GpuResourceSetDescription(_brightLayout, pingA, lin, _brightBuf));
                _brightFromPingB = f.CreateResourceSet(new GpuResourceSetDescription(_brightLayout, pingB, lin, _brightBuf));
                // Separable blur ping-pongs within the half-res pair: horizontal BloomA->BloomB (direction baked into
                // _blurBufH), vertical BloomB->BloomA (direction baked into _blurBufV), two buffers because both draws
                // happen inside Run's active render pass, where UBOs cannot be re-uploaded mid-pass.
                _blurHFromBloomA = f.CreateResourceSet(new GpuResourceSetDescription(_blurLayout, res.BloomA!, lin, _blurBufH));
                _blurVFromBloomB = f.CreateResourceSet(new GpuResourceSetDescription(_blurLayout, res.BloomB!, lin, _blurBufV));
                // Composite reads the full-res chain source (Src) + the blurred half-res bloom (BloomA) and writes a ping.
                _compositePingABloomA = f.CreateResourceSet(new GpuResourceSetDescription(_compositeLayout, pingA, res.BloomA!, lin, _compositeBuf));
                _compositePingBBloomA = f.CreateResourceSet(new GpuResourceSetDescription(_compositeLayout, pingB, res.BloomA!, lin, _compositeBuf));
            }

            // The sets that read the chain source, once per source slot.
            for (int slot = 0; slot < slots; slot++)
                _fromSource[slot] = BuildSourceSets(res, res.Source(slot), samp, lin);

            _bound = res; _boundGen = res.Generation;
        }
        int _boundGen;

        // The nine source-reading sets over one source texture. The distortion apply pass is always the chain's FIRST
        // pass, so it only ever reads the source, and it exists only while the offset field is allocated.
        SourceSets BuildSourceSets(IPostChainTargets res, IGpuTexture source, IGpuSampler samp, IGpuSampler lin)
        {
            var f = _gd.Factory;
            return new SourceSets
            {
                Palette = f.CreateResourceSet(new GpuResourceSetDescription(_palLayout, source, samp, _palBuf)),
                Edge = f.CreateResourceSet(new GpuResourceSetDescription(_edgeLayout, source, res.NormalTex, res.DepthColorTex, samp, _edgeBuf)),
                Tone = f.CreateResourceSet(new GpuResourceSetDescription(_toneLayout, source, lin, _toneBuf)),
                BlitPoint = f.CreateResourceSet(new GpuResourceSetDescription(_blitLayout, source, samp, _finalBuf)),
                BlitLinear = f.CreateResourceSet(new GpuResourceSetDescription(_blitLayout, source, lin, _finalBuf)),
                Fxaa = f.CreateResourceSet(new GpuResourceSetDescription(_fxaaLayout, source, lin, _fxaaBuf)),
                Bright = res.BloomAllocated
                    ? f.CreateResourceSet(new GpuResourceSetDescription(_brightLayout, source, lin, _brightBuf)) : null,
                Composite = res.BloomAllocated
                    ? f.CreateResourceSet(new GpuResourceSetDescription(_compositeLayout, source, res.BloomA!, lin, _compositeBuf)) : null,
                Apply = res.DistortAllocated
                    ? f.CreateResourceSet(new GpuResourceSetDescription(_applyLayout, source, res.DistortTex!, lin, _applyBuf)) : null,
            };
        }

        // Whether two ping output descriptions carry the same colour attachment format. The ping targets are always
        // one colour attachment, no depth, single-sample, so the first colour format is the only field that moves on
        // an HDR toggle.
        static bool SamePingFormat(in GpuOutputDescription a, in GpuOutputDescription b) =>
            a.Colour.Length == b.Colour.Length && a.Colour.Length > 0 && a.Colour[0] == b.Colour[0];

        // Rebuild every ping-output pipeline when the ping colour format flips (HDR float16 <-> legacy UNorm). A
        // pipeline bakes its target's colour format, so all seven ping writers must be recreated. The shaders,
        // layouts, and buffers are format-agnostic and survive. The blit pipeline targets the swapchain (format-fixed)
        // and is untouched. The caller idles the GPU before a format change (Scene3D.EnsureSize), so no pipeline is in
        // flight here.
        void RebuildPingPipelinesIfFormatChanged(IPostChainTargets res)
        {
            var pingOut = res.PingAFB.Outputs;
            if (SamePingFormat(pingOut, _pingOutput)) return;
            var f = _gd.Factory;
            _palPipe.Dispose(); _edgePipe.Dispose(); _fxaaPipe.Dispose(); _tonePipe.Dispose();
            _brightPipe.Dispose(); _blurPipe.Dispose(); _compositePipe.Dispose(); _applyPipe.Dispose();
            _palPipe = FullscreenPipeline(f, _palFrag, _palLayout, pingOut);
            _edgePipe = FullscreenPipeline(f, _edgeFrag, _edgeLayout, pingOut);
            _fxaaPipe = FullscreenPipeline(f, _fxaaFrag, _fxaaLayout, pingOut);
            _tonePipe = FullscreenPipeline(f, _toneFrag, _toneLayout, pingOut);
            _brightPipe = FullscreenPipeline(f, _brightFrag, _brightLayout, pingOut);
            _blurPipe = FullscreenPipeline(f, _blurFrag, _blurLayout, pingOut);
            _compositePipe = FullscreenPipeline(f, _compositeFrag, _compositeLayout, pingOut);
            _applyPipe = FullscreenPipeline(f, _applyFrag, _applyLayout, pingOut);
            _sharpen?.Rebuild(pingOut);
            _pingOutput = pingOut;
        }

        void DisposeSets()
        {
            for (int i = 0; i < MaxSourceSlots; i++)
            {
                _fromSource[i]?.Dispose();
                _fromSource[i] = null;
            }
            _paletteFromPingA?.Dispose(); _paletteFromPingB?.Dispose();
            _edgeFromPingA?.Dispose(); _edgeFromPingB?.Dispose();
            _toneFromPingA?.Dispose(); _toneFromPingB?.Dispose();
            _blitPingAP?.Dispose(); _blitPingBP?.Dispose();
            _blitPingAL?.Dispose(); _blitPingBL?.Dispose();
            _fxaaFromPingA?.Dispose(); _fxaaFromPingB?.Dispose();
            _brightFromPingA?.Dispose(); _brightFromPingB?.Dispose();
            _blurHFromBloomA?.Dispose(); _blurVFromBloomB?.Dispose();
            _compositePingABloomA?.Dispose(); _compositePingBBloomA?.Dispose();
            _brightFromPingA = _brightFromPingB = null;
            _blurHFromBloomA = _blurVFromBloomB = null;
            _compositePingABloomA = _compositePingBBloomA = null;
        }
    }
}
