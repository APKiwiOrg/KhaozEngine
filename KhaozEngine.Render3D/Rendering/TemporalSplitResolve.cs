using System;
using KhaozEngine.Render3D.Diagnostics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using static KhaozEngine.Render3D.Internal.FragmentPassResources;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>One of the split's two passes, for the cost measurement's <c>SkipPassForTests</c>.</summary>
    internal enum TemporalSplitPass
    {
        /// <summary>Neither: both passes run.</summary>
        None,

        /// <summary>The first pass, over the internal texels.</summary>
        Prepare,

        /// <summary>The second pass, over the display pixels.</summary>
        Accumulate,
    }

    /// <summary>
    /// The split entry point's objects (<see cref="ShaderSources.TemporalPrepareFrag"/> and
    /// <see cref="ShaderSources.TemporalAccumulateFrag"/>): the first pass over the internal texels into
    /// <see cref="TemporalSplitFormats"/>'s targets and the history's previous depth, then the second pass over the
    /// display pixels into the history pair's write targets. <see cref="TemporalResolveRenderer"/> owns it, builds it
    /// the first time the split is chosen, and shares its clamp sampler and uniform buffer with it.
    /// <para><b>ITS TARGETS EXIST ONLY WHILE THE SPLIT RUNS.</b> They follow the history's internal size and are made
    /// on the first frame that records the split. A frame that records the fused entry point, or no resolve, retires
    /// them (<see cref="ReleaseTargets"/>), so a device that falls back to the fused entry point, or a process forced
    /// to it, holds none of them. The pipelines stay for the next time.</para>
    /// <para>The sets and framebuffers name the scene's inputs and the history targets, so they are rebuilt with a new
    /// input texture or history generation and let go with the renderer's own sets.</para>
    /// </summary>
    internal sealed partial class TemporalSplitResolve : IDisposable
    {
        readonly IGpuDevice _gd;
        readonly IGpuShaderSet _prepareShaders;
        readonly TemporalVisibilityPipelinePair _accumulatePrograms;
        readonly IGpuResourceLayout _prepareLayout, _accumulateLayout, _fullyLitAccumulateLayout;
        readonly IGpuPipeline _preparePipeline;
        readonly IGpuTexture?[] _targets = new IGpuTexture?[TemporalSplitFormats.Targets.Length];
        readonly IGpuFramebuffer?[] _prepareFramebuffers = new IGpuFramebuffer?[2];
        readonly IGpuResourceSet?[] _accumulateSets = new IGpuResourceSet?[2];
        readonly IGpuResourceSet?[] _fullyLitAccumulateSets = new IGpuResourceSet?[2];
        IGpuResourceSet? _prepareSet;
        int _targetWidth, _targetHeight;
        TemporalResolveInputs _boundInputs;
        int _boundTargets = int.MinValue;
        TemporalHistory? _boundHistory;
        readonly Func<string, string>? _fragmentSourceForTests;

        /// <summary>The split's objects on <paramref name="gd"/>. <paramref name="fragmentSourceForTests"/> is the
        /// owning renderer's test rewrite of each fragment program's text, null outside tests.</summary>
        public TemporalSplitResolve(IGpuDevice gd, Func<string, string>? fragmentSourceForTests = null)
        {
            _gd = gd;
            _fragmentSourceForTests = fragmentSourceForTests;
            IGpuResourceFactory f = gd.Factory;
            _prepareShaders = f.CreateShadersFromSpirv(ShaderSources.FullscreenVert,
                FragmentSource(ShaderSources.TemporalPrepareFrag));
            _prepareLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                T("SceneColor"), T("OpaqueColor"), T("SceneDepth"), T("MotionTex"), S("LinearClamp"), U("Resolve")));
            _accumulateLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                T("SceneColor"), T("SceneDepth"), T("MotionTex"), T("PrevDepth"), T("HistoryColor"),
                T("HistoryConfidence"), T("PreparedColour"), T("PreparedSurface"), T("PreparedExpected"),
                T("PreparedEdge"), S("LinearClamp"), U("Resolve"),
                T("KeyShadowVisibility"), T("PreviousKeyShadowVisibility"), U("VisibilityHistory")));
            _fullyLitAccumulateLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                T("SceneColor"), T("SceneDepth"), T("MotionTex"), T("PrevDepth"), T("HistoryColor"),
                T("HistoryConfidence"), T("PreparedColour"), T("PreparedSurface"), T("PreparedExpected"),
                T("PreparedEdge"), S("LinearClamp"), U("Resolve")));
            var prepareOutputs = new GpuPixelFormat[TemporalSplitFormats.FirstPassAttachments];
            TemporalSplitFormats.Targets.CopyTo(prepareOutputs, 0);
            prepareOutputs[^1] = TemporalFormats.PreviousDepth;
            _preparePipeline = TemporalResolveRenderer.Fullscreen(f, _prepareShaders, _prepareLayout,
                new GpuOutputDescription(null, prepareOutputs));
            _accumulatePrograms = new TemporalVisibilityPipelinePair(gd,
                ShaderSources.TemporalAccumulateFragment(TemporalResolvePrecisionPolicy.For(gd)), [_accumulateLayout], [_fullyLitAccumulateLayout],
                new GpuOutputDescription(null, TemporalFormats.HistoryColor, TemporalFormats.HistoryConfidence),
                _fragmentSourceForTests);
        }

        // Apply fixture rewrites before selecting this backend's current visibility reads.
        string FragmentSource(string source) => TemporalVisibilityGatherPolicy.ForBackend(
            _fragmentSourceForTests?.Invoke(source) ?? source, _gd.Backend);

        /// <summary>For the cost measurement alone: which pass <see cref="Run"/> leaves out, so each pass can be timed
        /// on its own. The output is then not a resolve.</summary>
        internal TemporalSplitPass SkipPassForTests { get; set; }

        /// <summary>Whether the split's targets exist.</summary>
        internal bool TargetsAllocated => _targets[0] is not null;

        /// <summary>The first pass's target at <paramref name="index"/> in <see cref="TemporalSplitFormats.Targets"/>
        /// order, null before the first bind, for tests that read what the first pass stored.</summary>
        internal IGpuTexture? TargetForTests(int index) => _targets[index];

        /// <summary>The first pass's layout and the second pass's, for tests.</summary>
        internal (IGpuResourceLayout Prepare, IGpuResourceLayout Accumulate) LayoutsForTests =>
            (_prepareLayout, _accumulateLayout);

        /// <summary>Make the targets at the history's internal size, retiring any of another size into
        /// <paramref name="retired"/> (or draining without one), and build the sets and framebuffers over them, unless
        /// the inputs, the history owner and its generation are the ones already bound.</summary>
        public void Bind(in TemporalResolveInputs inputs, TemporalHistory history, IGpuSampler clamp,
            IGpuBuffer uniforms, TemporalVisibilityHistory visibility, IGpuBuffer visibilityUniforms, GpuRetireQueue? retired)
        {
            if (TargetsAllocated && (_targetWidth != history.InternalWidth || _targetHeight != history.InternalHeight))
                ReleaseTargets(retired);
            if (_prepareSet is not null && TemporalResolveRenderer.SameInputs(inputs, _boundInputs)
                && ReferenceEquals(history, _boundHistory) && history.TargetGeneration == _boundTargets)
                return;
            ReleaseSets(retired);
            IGpuResourceFactory f = _gd.Factory;
            if (!TargetsAllocated)
            {
                _targetWidth = history.InternalWidth;
                _targetHeight = history.InternalHeight;
                for (int t = 0; t < _targets.Length; t++)
                    _targets[t] = f.CreateTexture(GpuTextureDescription.Texture2D((uint)_targetWidth,
                        (uint)_targetHeight, TemporalSplitFormats.Targets[t],
                        GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            }
            IGpuTexture[] targets = Array.ConvertAll(_targets, t => t!);
            _prepareSet = f.CreateResourceSet(new GpuResourceSetDescription(_prepareLayout,
                inputs.SceneColor, inputs.OpaqueColor, inputs.SceneDepth, inputs.Motion, clamp, uniforms));
            for (int i = 0; i < 2; i++)
            {
                // Write index i: the first pass writes this frame's depth into that previous depth.
                var colour = new IGpuTexture[targets.Length + 1];
                targets.CopyTo(colour, 0);
                colour[^1] = history.PreviousDepth(i);
                _prepareFramebuffers[i] = f.CreateFramebuffer(null, colour);
                // Read index i: last frame's history and depths.
                _accumulateSets[i] = f.CreateResourceSet(new GpuResourceSetDescription(_accumulateLayout,
                    inputs.SceneColor, inputs.SceneDepth, inputs.Motion, history.PreviousDepth(i), history.Color(i),
                    history.Confidence(i), targets[0], targets[1], targets[2], targets[3], clamp, uniforms,
                    inputs.KeyShadowVisibility!, visibility.Texture(i), visibilityUniforms));
                _fullyLitAccumulateSets[i] = f.CreateResourceSet(new GpuResourceSetDescription(_fullyLitAccumulateLayout,
                    inputs.SceneColor, inputs.SceneDepth, inputs.Motion, history.PreviousDepth(i), history.Color(i),
                    history.Confidence(i), targets[0], targets[1], targets[2], targets[3], clamp, uniforms));
            }
            _boundInputs = inputs;
            _boundTargets = history.TargetGeneration;
            _boundHistory = history;
        }

        /// <summary>Record the first pass into the targets and the write index's previous depth, then the second pass
        /// into the write index's colour and confidence, reading the read index's history and previous depth. A frame
        /// that does not upscale (<see cref="Upscales"/>) records the second pass compiled for the display's own
        /// size.</summary>
        internal RenderPassProfiling? Profiling { get; set; }

        public void Run(IGpuCommandList cl, TemporalHistory history, bool upscales, bool withoutVisibility = false)
        {
            using (Profiling?.Pass(cl, "temporal.prepare", executed: SkipPassForTests != TemporalSplitPass.Prepare) ?? default)
                if (SkipPassForTests != TemporalSplitPass.Prepare)
                {
                    cl.SetFramebuffer(_prepareFramebuffers[history.WriteIndex]
                        ?? throw new InvalidOperationException("TemporalSplitResolve.Run was called before Bind."));
                    cl.SetPipeline(_preparePipeline);
                    cl.SetGraphicsResourceSet(0, _prepareSet!);
                    cl.Draw(3);
                }
            using (Profiling?.Pass(cl, "temporal.accumulate", executed: SkipPassForTests != TemporalSplitPass.Accumulate) ?? default)
                if (SkipPassForTests != TemporalSplitPass.Accumulate)
                {
                    cl.SetFramebuffer(history.ResolveFramebuffer(history.WriteIndex));
                    cl.SetPipeline(AccumulatePrograms(upscales).Select(withoutVisibility));
                    cl.SetGraphicsResourceSet(0, (UsesVisibility(upscales, withoutVisibility) ? _accumulateSets
                        : _fullyLitAccumulateSets)[history.ReadIndex]
                        ?? throw new InvalidOperationException("TemporalSplitResolve.Run was called before Bind."));
                    cl.Draw(3);
                }
        }

        /// <summary>Let go of the sets and framebuffers, which name the scene's inputs and the history targets, into
        /// <paramref name="retired"/>, since a frame the device has not finished may still bind them, or at once
        /// without one. The targets and pipelines stay.</summary>
        public void ReleaseSets(GpuRetireQueue? retired)
        {
            for (int i = 0; i < 2; i++)
            {
                Free(ref _accumulateSets[i], retired);
                Free(ref _fullyLitAccumulateSets[i], retired);
                Free(ref _prepareFramebuffers[i], retired);
            }
            Free(ref _prepareSet, retired);
            _boundInputs = default;
            _boundTargets = int.MinValue;
            _boundHistory = null;
        }

        /// <summary>Let go of the sets and the targets into <paramref name="retired"/>, or through a drain without one,
        /// which throws <see cref="InvalidOperationException"/> while the device is recording. The pipelines stay for
        /// the next frame that records the split.</summary>
        public void ReleaseTargets(GpuRetireQueue? retired)
        {
            // A drain waits out submitted work only, so with no queue an open recording could still read what this
            // frees. Refuse then, as the history does, and change nothing.
            if (retired is null && TargetsAllocated && GpuRecording.OpenOwner(_gd) is { } owner)
                throw new InvalidOperationException("The split's targets were asked to go with no retire queue while "
                    + $"{owner} is recording on this device. Pass the frame's GpuRetireQueue, or release outside the "
                    + "recording. Nothing was drained or freed.");
            if (retired is null && TargetsAllocated) _gd.WaitForIdle();   // a frame in flight may still read them
            ReleaseSets(retired);
            for (int t = 0; t < _targets.Length; t++) Free(ref _targets[t], retired);
            _targetWidth = _targetHeight = 0;
        }

        public void Dispose()
        {
            ReleaseTargets(null);
            _preparePipeline.Dispose();
            _accumulatePrograms.Dispose();
            DisposeAtDisplaySize();
            _prepareLayout.Dispose();
            _accumulateLayout.Dispose();
            _fullyLitAccumulateLayout.Dispose();
            _prepareShaders.Dispose();
        }
    }
}
