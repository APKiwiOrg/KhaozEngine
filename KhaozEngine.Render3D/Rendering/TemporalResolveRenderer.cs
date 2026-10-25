using KhaozEngine.Render3D.Diagnostics;
using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using static KhaozEngine.Render3D.Internal.FragmentPassResources;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>The internal-resolution inputs of one resolve. The renderer rebuilds its resource sets when any of the
    /// textures is a different one from those it last bound.</summary>
    internal readonly record struct TemporalResolveInputs(IGpuTexture SceneColor, IGpuTexture OpaqueColor,
        IGpuTexture SceneDepth, IGpuTexture Motion, IGpuTexture? KeyShadowVisibility = null);

    /// <summary>
    /// THE TEMPORAL RESOLVE (TEMPORAL-RESOLVE-UPSCALING-DESIGN section 3): one fullscreen fragment pass at the display
    /// resolution that reconstructs the jittered internal frame, reprojects and clips the history and accumulates into
    /// <see cref="TemporalHistory"/>'s write pair, then one internal-resolution pass that stores this frame's linear view
    /// depth for the next frame's disocclusion test. Fragment rather than compute, because the GPU seam has no compute
    /// barrier and the handoff to graphics would have to stay inside one command list.
    /// <para><b>ITS OWN CLAMP SAMPLER.</b> The device's shared samplers wrap (<see cref="IGpuDevice.PointSampler"/>), and
    /// a history tap past the edge must not read the other side of the screen. <see cref="GpuSamplerDescription.Linear"/>
    /// clamps on every axis.</para>
    /// <para><b>TWO RESOURCE SETS, ONE PER READ INDEX.</b> The history pair alternates every frame, so both sets are built
    /// once per set of input textures and history target generation, and <see cref="Run"/> picks one, which keeps a
    /// steady frame from building or allocating anything. They are keyed on the textures themselves, so a change
    /// elsewhere in the scene's targets, such as the distortion field coming or going, rebuilds nothing.</para>
    /// <para>The scene creates it on the first frame the resolve runs. On the first frame the resolve does not run it lets
    /// go of the sets (<see cref="ReleaseSets"/>), which name the released history targets, and keeps the pipelines for the
    /// next time. A scene that never selects temporal anti-aliasing owns none of its objects.
    /// <see cref="ResolveLayout"/> and <see cref="CurrentSet"/> are exposed for the passes that re-evaluate the
    /// resolve through <c>ShaderSources.TemporalResolveCoreGlsl</c> over the set it bound, such as the debug views
    /// (TemporalResolveRenderer.Finish.cs).</para>
    /// </summary>
    internal sealed partial class TemporalResolveRenderer : IDisposable
    {
        internal RenderPassProfiling? Profiling { get; set; }

        /// <summary>The draws <see cref="Run"/> records: the fused resolve and the depth store, or the split's two
        /// passes.</summary>
        internal const int DrawCallsPerFrame = 2;

        readonly IGpuDevice _gd;
        readonly IGpuResourceLayout _resolveLayout, _fullyLitResolveLayout, _storeLayout;
        IGpuShaderSet? _storeShaders;
        IGpuPipeline? _storePipeline;
        TemporalVisibilityPipelinePair? _resolvePrograms;
        readonly IGpuBuffer _resolveBuffer, _storeBuffer;
        readonly IGpuSampler _clampSampler;
        readonly IGpuResourceSet?[] _resolveSets = new IGpuResourceSet?[2];
        readonly IGpuResourceSet?[] _fullyLitResolveSets = new IGpuResourceSet?[2];
        IGpuResourceSet? _storeSet;
        TemporalResolveInputs _boundInputs;
        int _boundTargets = int.MinValue;
        TemporalHistory? _boundHistory;
        readonly Func<string, string>? _fragmentSourceForTests;

        /// <summary>A renderer on <paramref name="gd"/>. <paramref name="fragmentSourceForTests"/> is for tests alone:
        /// it rewrites the text of every fragment program this renderer and its split compile, so an oracle can run a
        /// frozen copy of the rules through the same objects. Null everywhere else.</summary>
        public TemporalResolveRenderer(IGpuDevice gd, Func<string, string>? fragmentSourceForTests = null)
        {
            _gd = gd;
            _fragmentSourceForTests = fragmentSourceForTests;
            IGpuResourceFactory f = gd.Factory;
            _resolveLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                T("SceneColor"), T("OpaqueColor"), T("SceneDepth"), T("MotionTex"), T("PrevDepth"),
                T("HistoryColor"), T("HistoryConfidence"), S("LinearClamp"), U("Resolve"),
                T("KeyShadowVisibility"), T("PreviousKeyShadowVisibility"), U("VisibilityHistory")));
            _fullyLitResolveLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                T("SceneColor"), T("OpaqueColor"), T("SceneDepth"), T("MotionTex"), T("PrevDepth"),
                T("HistoryColor"), T("HistoryConfidence"), S("LinearClamp"), U("Resolve")));
            _storeLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                T("SceneDepth"), T("MotionTex"), S("Samp"), U("DepthStore")));
            _resolveBuffer = f.CreateBuffer(new GpuBufferDescription(TemporalResolveUniforms.SizeInBytes, GpuBufferUsage.UniformBuffer));
            _storeBuffer = f.CreateBuffer(new GpuBufferDescription(TemporalDepthStoreUniforms.SizeInBytes, GpuBufferUsage.UniformBuffer));
            _visibilityBuffer = f.CreateBuffer(new GpuBufferDescription(TemporalVisibilityUniforms.SizeInBytes, GpuBufferUsage.UniformBuffer));
            _clampSampler = f.CreateSampler(GpuSamplerDescription.Linear);
        }

        // The fused entry point's programs and pipelines, built on the first frame that records it, so a device that
        // records the split never compiles them.
        void EnsureFused()
        {
            if (_resolvePrograms is not null) return;
            IGpuResourceFactory f = _gd.Factory;
            _resolvePrograms = new TemporalVisibilityPipelinePair(_gd,
                ShaderSources.TemporalResolveFragment(TemporalResolvePrecisionPolicy.For(_gd)), [_resolveLayout], [_fullyLitResolveLayout],
                new GpuOutputDescription(null, TemporalFormats.HistoryColor, TemporalFormats.HistoryConfidence),
                _fragmentSourceForTests);
            _storeShaders = f.CreateShadersFromSpirv(ShaderSources.FullscreenVert,
                FragmentSource(ShaderSources.TemporalDepthStoreFrag));
            _storePipeline = Fullscreen(f, _storeShaders, _storeLayout,
                new GpuOutputDescription(null, TemporalFormats.PreviousDepth));
        }

        // Apply fixture rewrites before selecting this backend's current visibility reads.
        string FragmentSource(string source) => TemporalVisibilityGatherPolicy.ForBackend(
            _fragmentSourceForTests?.Invoke(source) ?? source, _gd.Backend);

        /// <summary>The resolve's resource layout, for a pass that binds the resolve's own set to re-evaluate
        /// it.</summary>
        internal IGpuResourceLayout ResolveLayout => _resolveLayout;
        internal IGpuResourceLayout FullyLitResolveLayout => _fullyLitResolveLayout;

        /// <summary>The resolve set <see cref="Run"/> bound this frame, over the history pair it read. Null before the
        /// first run.</summary>
        internal IGpuResourceSet? CurrentSet { get; private set; }

        /// <summary>The uniforms uploaded for this frame. The split reads them to pick its second pass, the one compiled
        /// for an upscale or the one for the display's own size, and the tests read them too.</summary>
        internal TemporalResolveUniforms LastUniforms { get; private set; }

        internal static IGpuPipeline Fullscreen(IGpuResourceFactory f, IGpuShaderSet shaders, IGpuResourceLayout layout,
            GpuOutputDescription outputs) => Fullscreen(f, shaders, [layout], outputs);

        internal static IGpuPipeline Fullscreen(IGpuResourceFactory f, IGpuShaderSet shaders,
            IGpuResourceLayout[] layouts, GpuOutputDescription outputs)
        {
            var blends = new GpuBlendAttachment[outputs.Colour.Length];
            Array.Fill(blends, GpuBlendAttachment.OverrideBlend);
            return f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = blends,
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise,
                    depthClipEnabled: false, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = layouts,
                ShaderSet = shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription>(),
                Outputs = outputs,
            });
        }

        /// <summary>Whether the resolve and depth store sets exist. For tests.</summary>
        internal bool HoldsSetsForTests => _storeSet is not null;

        /// <summary>Build the resolve sets for both read indices and the depth store set, unless the input textures
        /// are the very ones already bound and the history targets are the same generation of the same owner. The
        /// fused sets are built on either entry point, since the debug views and counts re-evaluate through them, and
        /// the chosen entry point's own objects first (Entry partial). What either replaces goes to
        /// <paramref name="retired"/>, since a frame the device has not finished may still bind it. Without one the
        /// split drains and the sets go at once.</summary>
        public void BindInputs(in TemporalResolveInputs inputs, TemporalHistory history, GpuRetireQueue? retired = null)
        {
            TemporalResolveInputs effective = BindVisibility(inputs, history, retired);
            BindEntry(effective, history, retired);
            if (_storeSet is not null && SameInputs(inputs, _boundInputs) && ReferenceEquals(history, _boundHistory)
                && history.TargetGeneration == _boundTargets)
                return;
            FreeSets(retired);
            IGpuResourceFactory f = _gd.Factory;
            for (int read = 0; read < 2; read++)
            {
                _resolveSets[read] = f.CreateResourceSet(new GpuResourceSetDescription(_resolveLayout,
                    inputs.SceneColor, inputs.OpaqueColor, inputs.SceneDepth, inputs.Motion, history.PreviousDepth(read),
                    history.Color(read), history.Confidence(read), _clampSampler, _resolveBuffer, effective.KeyShadowVisibility!,
                    VisibilityHistory.Texture(read), _visibilityBuffer));
                _fullyLitResolveSets[read] = f.CreateResourceSet(new GpuResourceSetDescription(_fullyLitResolveLayout,
                    inputs.SceneColor, inputs.OpaqueColor, inputs.SceneDepth, inputs.Motion, history.PreviousDepth(read),
                    history.Color(read), history.Confidence(read), _clampSampler, _resolveBuffer));
            }
            _storeSet = f.CreateResourceSet(new GpuResourceSetDescription(_storeLayout,
                inputs.SceneDepth, inputs.Motion, _clampSampler, _storeBuffer));
            _boundInputs = inputs;
            _boundTargets = history.TargetGeneration;
            _boundHistory = history;
        }

        // By reference: the sets name these objects, and holding them keeps a replacement from ever being the same one.
        internal static bool SameInputs(in TemporalResolveInputs a, in TemporalResolveInputs b)
            => ReferenceEquals(a.SceneColor, b.SceneColor) && ReferenceEquals(a.OpaqueColor, b.OpaqueColor)
                && ReferenceEquals(a.SceneDepth, b.SceneDepth) && ReferenceEquals(a.Motion, b.Motion)
                && ReferenceEquals(a.KeyShadowVisibility, b.KeyShadowVisibility);

        /// <summary>Let go of the sets, which name the history targets and the scene's inputs, and keep the pipelines,
        /// layouts, buffers and sampler. The sets go to <paramref name="retired"/>, since a frame the device has not
        /// finished may still bind them. The next <see cref="BindInputs"/> builds them again.</summary>
        public void ReleaseSets(GpuRetireQueue retired)
        {
            _split?.ReleaseTargets(retired);   // a frame without the resolve holds none of the split's targets either
            FreeSets(retired);
            ReleaseVisibility(retired);
        }

        /// <summary>Upload this frame's uniforms. Call before any framebuffer is bound this frame, as the post chain's
        /// <c>PrepareUniforms</c> is.</summary>
        public void PrepareUniforms(IGpuCommandList cl, in TemporalResolveUniforms resolve, in TemporalDepthStoreUniforms store)
        {
            using var profile = Profiling?.Pass(cl, "temporal.uniforms") ?? default;
            LastUniforms = resolve;
            cl.UpdateBuffer(_resolveBuffer, 0, in resolve);
            cl.UpdateBuffer(_storeBuffer, 0, in store);
            PrepareVisibilityUniforms(cl, resolve);
        }

        /// <summary>Record the resolve into <see cref="TemporalHistory.WriteIndex"/>'s colour and confidence, reading
        /// <see cref="TemporalHistory.ReadIndex"/>'s history and previous depth, then store this frame's depth into the
        /// write index's previous depth. The read targets are left untouched, so a later pass can re-evaluate the resolve
        /// through <see cref="CurrentSet"/> and see exactly what it saw. The split entry point records its two passes
        /// in their place, into the same targets (Entry partial). <paramref name="currentVisibilityFullyLit"/> is supplied
        /// only by the scene after the shared receiver upload. Arbitrary supplied R8 inputs default to unknown.</summary>
        public void Run(IGpuCommandList cl, TemporalHistory history, bool currentVisibilityFullyLit = false)
        {
            if (_storeSet is null)
                throw new InvalidOperationException("TemporalResolveRenderer.Run was called before BindInputs.");
            LastCurrentVisibilityFullyLit = _fullyLitFallback || currentVisibilityFullyLit;
            LastVisibilityDecisionSkipped = LastCurrentVisibilityFullyLit && _readVisibilityFullyLit;
            bool usesVisibility = Entry == TemporalResolveEntry.Split
                ? _split!.UsesVisibility(TemporalSplitResolve.Upscales(LastUniforms), LastVisibilityDecisionSkipped)
                : _resolvePrograms!.UsesVisibility(LastVisibilityDecisionSkipped);
            IGpuResourceSet set = (usesVisibility ? _resolveSets : _fullyLitResolveSets)[history.ReadIndex]
                ?? throw new InvalidOperationException("TemporalResolveRenderer.Run was called before BindInputs.");
            CurrentSet = set;
            LastEntry = Entry;
            if (!RunSplit(cl, history))
            {
                using (Profiling?.Pass(cl, "temporal.resolve", 0) ?? default)
                {
                    cl.SetFramebuffer(history.ResolveFramebuffer(history.WriteIndex));
                    cl.SetPipeline(_resolvePrograms!.Select(LastVisibilityDecisionSkipped));
                    cl.SetGraphicsResourceSet(0, set);
                    cl.Draw(3);
                }
                using (Profiling?.Pass(cl, "temporal.depth-store", 0) ?? default)
                {
                    cl.SetFramebuffer(history.PreviousDepthFramebuffer(history.WriteIndex));
                    cl.SetPipeline(_storePipeline!);
                    cl.SetGraphicsResourceSet(0, _storeSet!);
                    cl.Draw(3);
                }
            }
            StoreVisibility(cl, history);
            RecordFinishProbe(cl);   // an armed temporal count request, over the set this run bound (Finish partial)
        }

        /// <summary>Let go of the resolve and depth store sets into <paramref name="retired"/>, or at once without
        /// one.</summary>
        void FreeSets(GpuRetireQueue? retired)
        {
            for (int i = 0; i < 2; i++)
            {
                Free(ref _resolveSets[i], retired);
                Free(ref _fullyLitResolveSets[i], retired);
            }
            Free(ref _storeSet, retired);
            ForgetBinding();
        }

        void ForgetBinding()
        {
            CurrentSet = null;
            _boundInputs = default;
            _boundTargets = int.MinValue;
            _boundHistory = null;
        }

        public void Dispose()
        {
            DisposeFinish();
            _split?.Dispose();
            FreeSets(null);
            ReleaseVisibility(null);
            _visibilityBuffer.Dispose();
            _resolvePrograms?.Dispose();
            _storePipeline?.Dispose();
            _resolveLayout.Dispose();
            _fullyLitResolveLayout.Dispose();
            _storeLayout.Dispose();
            _storeShaders?.Dispose();
            _resolveBuffer.Dispose();
            _storeBuffer.Dispose();
            _clampSampler.Dispose();
        }
    }
}
