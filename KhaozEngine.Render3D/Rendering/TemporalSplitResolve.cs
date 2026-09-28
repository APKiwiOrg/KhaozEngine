using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// The two-pass resolve's objects (ShaderSources.TemporalSplit.cs has the design): pass one over the internal
    /// texels into <see cref="TemporalSplitFormats"/>'s targets and the history's previous depth, then pass two over
    /// the display pixels into the history pair's write targets. <see cref="TemporalResolveRenderer"/> owns it, builds
    /// it the first time the two-pass resolve is selected, and shares its clamp sampler and uniform buffer with it.
    /// <para>The targets follow the history's internal size. The sets and framebuffers name the history targets, so
    /// they are rebuilt with each new history generation and let go with the renderer's own sets.</para>
    /// </summary>
    internal sealed class TemporalSplitResolve : IDisposable
    {
        readonly IGpuDevice _gd;
        readonly IGpuShaderSet _prepareShaders, _splitShaders;
        readonly IGpuResourceLayout _prepareLayout, _splitLayout;
        readonly IGpuPipeline _preparePipeline, _splitPipeline;
        readonly IGpuFramebuffer?[] _prepareFramebuffers = new IGpuFramebuffer?[2];
        readonly IGpuResourceSet?[] _splitSets = new IGpuResourceSet?[2];
        IGpuResourceSet? _prepareSet;
        IGpuTexture? _prepared, _reproject, _expected, _edge;
        int _targetWidth, _targetHeight;
        TemporalResolveInputs _boundInputs;
        int _boundTargets = int.MinValue;
        TemporalHistory? _boundHistory;

        public TemporalSplitResolve(IGpuDevice gd)
        {
            _gd = gd;
            IGpuResourceFactory f = gd.Factory;
            _prepareShaders = f.CreateShadersFromSpirv(ShaderSources.FullscreenVert, ShaderSources.TemporalPrepareFrag);
            _splitShaders = f.CreateShadersFromSpirv(ShaderSources.FullscreenVert, ShaderSources.TemporalSplitFrag);
            _prepareLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                T("SceneColor"), T("OpaqueColor"), T("SceneDepth"), T("MotionTex"), S("LinearClamp"), U("Resolve")));
            _splitLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                T("Prepared"), T("PreparedReproject"), T("PreparedExpected"), T("PreparedEdge"), T("CurrentViewDepth"),
                T("PrevDepth"), T("HistoryColor"), T("HistoryConfidence"), S("LinearClamp"), U("Resolve")));
            _preparePipeline = TemporalResolveRenderer.Fullscreen(f, _prepareShaders, _prepareLayout,
                new GpuOutputDescription(null, TemporalSplitFormats.Prepared, TemporalSplitFormats.Reproject,
                    TemporalSplitFormats.Expected, TemporalSplitFormats.Edge, TemporalFormats.PreviousDepth));
            _splitPipeline = TemporalResolveRenderer.Fullscreen(f, _splitShaders, _splitLayout,
                new GpuOutputDescription(null, TemporalFormats.HistoryColor, TemporalFormats.HistoryConfidence));
        }

        static GpuResourceLayoutElement T(string n) =>
            new(n, GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment);
        static GpuResourceLayoutElement S(string n) => new(n, GpuResourceKind.Sampler, GpuShaderStages.Fragment);
        static GpuResourceLayoutElement U(string n) => new(n, GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment);

        /// <summary>For the cost measurement alone: 1 records pass two without pass one, 2 pass one without pass two,
        /// so each pass can be timed on its own. The output is then not a resolve.</summary>
        internal int SkipPassForTests { get; set; }

        /// <summary>Whether the targets exist. For tests.</summary>
        internal bool TargetsAllocatedForTests => _prepared is not null;

        /// <summary>Build the targets at the history's internal size, and the sets and framebuffers over them, unless
        /// the inputs, the history owner and its generation are the ones already bound.</summary>
        public void Bind(in TemporalResolveInputs inputs, TemporalHistory history, IGpuSampler clamp,
            IGpuBuffer uniforms)
        {
            if (_prepareSet is not null && TemporalResolveRenderer.SameInputs(inputs, _boundInputs)
                && ReferenceEquals(history, _boundHistory) && history.TargetGeneration == _boundTargets)
                return;
            DisposeSets();
            IGpuResourceFactory f = _gd.Factory;
            if (_prepared is null || _targetWidth != history.InternalWidth || _targetHeight != history.InternalHeight)
            {
                if (_prepared is not null) _gd.WaitForIdle();   // a frame in flight may still read the old targets
                DisposeTargets();
                _targetWidth = history.InternalWidth;
                _targetHeight = history.InternalHeight;
                _prepared = Target(f, TemporalSplitFormats.Prepared);
                _reproject = Target(f, TemporalSplitFormats.Reproject);
                _expected = Target(f, TemporalSplitFormats.Expected);
                _edge = Target(f, TemporalSplitFormats.Edge);
            }
            _prepareSet = f.CreateResourceSet(new GpuResourceSetDescription(_prepareLayout,
                inputs.SceneColor, inputs.OpaqueColor, inputs.SceneDepth, inputs.Motion, clamp, uniforms));
            for (int i = 0; i < 2; i++)
            {
                _prepareFramebuffers[i] = f.CreateFramebuffer(null, _prepared, _reproject!, _expected!, _edge!,
                    history.PreviousDepth(i));
                // Read index i: pass one wrote this frame's depth into the other previous depth.
                _splitSets[i] = f.CreateResourceSet(new GpuResourceSetDescription(_splitLayout,
                    _prepared, _reproject!, _expected!, _edge!, history.PreviousDepth(1 - i), history.PreviousDepth(i),
                    history.Color(i), history.Confidence(i), clamp, uniforms));
            }
            _boundInputs = inputs;
            _boundTargets = history.TargetGeneration;
            _boundHistory = history;
        }

        IGpuTexture Target(IGpuResourceFactory f, GpuPixelFormat format) =>
            f.CreateTexture(GpuTextureDescription.Texture2D((uint)_targetWidth, (uint)_targetHeight, format,
                GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));

        /// <summary>Record pass one into the targets and the write index's previous depth, then pass two into the
        /// write index's colour and confidence, reading the read index's history and previous depth.</summary>
        public void Run(IGpuCommandList cl, TemporalHistory history)
        {
            if (SkipPassForTests != 1)
            {
                cl.SetFramebuffer(_prepareFramebuffers[history.WriteIndex]!);
                cl.SetPipeline(_preparePipeline);
                cl.SetGraphicsResourceSet(0, _prepareSet!);
                cl.Draw(3);
            }
            if (SkipPassForTests != 2)
            {
                cl.SetFramebuffer(history.ResolveFramebuffer(history.WriteIndex));
                cl.SetPipeline(_splitPipeline);
                cl.SetGraphicsResourceSet(0, _splitSets[history.ReadIndex]!);
                cl.Draw(3);
            }
        }

        /// <summary>Retire the sets and framebuffers, which name the history targets, into <paramref name="retired"/>.
        /// The targets and pipelines stay for the next time.</summary>
        public void ReleaseSets(GpuRetireQueue retired)
        {
            for (int i = 0; i < 2; i++)
            {
                retired.Retire(_splitSets[i]);
                retired.Retire(_prepareFramebuffers[i]);
                _splitSets[i] = null;
                _prepareFramebuffers[i] = null;
            }
            retired.Retire(_prepareSet);
            _prepareSet = null;
            ForgetBinding();
        }

        void DisposeSets()
        {
            for (int i = 0; i < 2; i++)
            {
                _splitSets[i]?.Dispose();
                _prepareFramebuffers[i]?.Dispose();
                _splitSets[i] = null;
                _prepareFramebuffers[i] = null;
            }
            _prepareSet?.Dispose();
            _prepareSet = null;
            ForgetBinding();
        }

        void ForgetBinding()
        {
            _boundInputs = default;
            _boundTargets = int.MinValue;
            _boundHistory = null;
        }

        void DisposeTargets()
        {
            _prepared?.Dispose();
            _reproject?.Dispose();
            _expected?.Dispose();
            _edge?.Dispose();
            _prepared = _reproject = _expected = _edge = null;
        }

        public void Dispose()
        {
            DisposeSets();
            DisposeTargets();
            _preparePipeline.Dispose();
            _splitPipeline.Dispose();
            _prepareLayout.Dispose();
            _splitLayout.Dispose();
            _prepareShaders.Dispose();
            _splitShaders.Dispose();
        }
    }
}
