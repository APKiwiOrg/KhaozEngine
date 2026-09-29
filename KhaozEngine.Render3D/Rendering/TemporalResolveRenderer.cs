using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>The internal-resolution inputs of one resolve. The renderer rebuilds its resource sets when any of the
    /// four textures is a different one from those it last bound.</summary>
    internal readonly record struct TemporalResolveInputs(IGpuTexture SceneColor, IGpuTexture OpaqueColor,
        IGpuTexture SceneDepth, IGpuTexture Motion);

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
        /// <summary>The draws <see cref="Run"/> records: the fused resolve and the depth store, or the split's two
        /// passes.</summary>
        internal const int DrawCallsPerFrame = 2;

        readonly IGpuDevice _gd;
        readonly IGpuResourceLayout _resolveLayout, _storeLayout;
        IGpuShaderSet? _resolveShaders, _storeShaders;
        IGpuPipeline? _resolvePipeline, _storePipeline;
        readonly IGpuBuffer _resolveBuffer, _storeBuffer;
        readonly IGpuSampler _clampSampler;
        readonly IGpuResourceSet?[] _resolveSets = new IGpuResourceSet?[2];
        IGpuResourceSet? _storeSet;
        TemporalResolveInputs _boundInputs;
        int _boundTargets = int.MinValue;
        TemporalHistory? _boundHistory;

        public TemporalResolveRenderer(IGpuDevice gd)
        {
            _gd = gd;
            IGpuResourceFactory f = gd.Factory;
            _resolveLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                T("SceneColor"), T("OpaqueColor"), T("SceneDepth"), T("MotionTex"), T("PrevDepth"),
                T("HistoryColor"), T("HistoryConfidence"), S("LinearClamp"), U("Resolve")));
            _storeLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                T("SceneDepth"), T("MotionTex"), S("Samp"), U("DepthStore")));
            _resolveBuffer = f.CreateBuffer(new GpuBufferDescription(TemporalResolveUniforms.SizeInBytes, GpuBufferUsage.UniformBuffer));
            _storeBuffer = f.CreateBuffer(new GpuBufferDescription(TemporalDepthStoreUniforms.SizeInBytes, GpuBufferUsage.UniformBuffer));
            _clampSampler = f.CreateSampler(GpuSamplerDescription.Linear);
        }

        // The fused entry point's programs and pipelines, built on the first frame that records it, so a backend and
        // preset that picks the split never compiles them.
        void EnsureFused()
        {
            if (_resolvePipeline is not null) return;
            IGpuResourceFactory f = _gd.Factory;
            _resolveShaders = f.CreateShadersFromSpirv(ShaderSources.FullscreenVert, ShaderSources.TemporalResolveFrag);
            _storeShaders = f.CreateShadersFromSpirv(ShaderSources.FullscreenVert,
                ShaderSources.TemporalDepthStoreFrag);
            _resolvePipeline = Fullscreen(f, _resolveShaders, _resolveLayout,
                new GpuOutputDescription(null, TemporalFormats.HistoryColor, TemporalFormats.HistoryConfidence));
            _storePipeline = Fullscreen(f, _storeShaders, _storeLayout,
                new GpuOutputDescription(null, TemporalFormats.PreviousDepth));
        }

        /// <summary>The resolve's resource layout, for a pass that binds the resolve's own set to re-evaluate
        /// it.</summary>
        internal IGpuResourceLayout ResolveLayout => _resolveLayout;

        /// <summary>The resolve set <see cref="Run"/> bound this frame, over the history pair it read. Null before the
        /// first run.</summary>
        internal IGpuResourceSet? CurrentSet { get; private set; }

        /// <summary>The uniforms uploaded for this frame. Internal, for the tests and planned temporal diagnostics.</summary>
        internal TemporalResolveUniforms LastUniforms { get; private set; }

        static GpuResourceLayoutElement T(string n) => new(n, GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment);
        static GpuResourceLayoutElement S(string n) => new(n, GpuResourceKind.Sampler, GpuShaderStages.Fragment);
        static GpuResourceLayoutElement U(string n) => new(n, GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment);

        internal static IGpuPipeline Fullscreen(IGpuResourceFactory f, IGpuShaderSet shaders, IGpuResourceLayout layout,
            GpuOutputDescription outputs)
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
                ResourceLayouts = new[] { layout },
                ShaderSet = shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription>(),
                Outputs = outputs,
            });
        }

        /// <summary>Whether the resolve and depth store sets exist. For tests.</summary>
        internal bool HoldsSetsForTests => _storeSet is not null;

        /// <summary>Build the resolve sets for both read indices and the depth store set, unless the four input textures
        /// are the very ones already bound and the history targets are the same generation of the same owner. The
        /// fused sets are built on either entry point, since the debug views and counts re-evaluate through them, and
        /// the chosen entry point's own objects first (Entry partial). What the split replaces goes to
        /// <paramref name="retired"/>, or through a drain without one.</summary>
        public void BindInputs(in TemporalResolveInputs inputs, TemporalHistory history, GpuRetireQueue? retired = null)
        {
            BindEntry(inputs, history, retired);
            if (_storeSet is not null && SameInputs(inputs, _boundInputs) && ReferenceEquals(history, _boundHistory)
                && history.TargetGeneration == _boundTargets)
                return;
            DisposeSets();
            IGpuResourceFactory f = _gd.Factory;
            for (int read = 0; read < 2; read++)
                _resolveSets[read] = f.CreateResourceSet(new GpuResourceSetDescription(_resolveLayout,
                    inputs.SceneColor, inputs.OpaqueColor, inputs.SceneDepth, inputs.Motion, history.PreviousDepth(read),
                    history.Color(read), history.Confidence(read), _clampSampler, _resolveBuffer));
            _storeSet = f.CreateResourceSet(new GpuResourceSetDescription(_storeLayout,
                inputs.SceneDepth, inputs.Motion, _clampSampler, _storeBuffer));
            _boundInputs = inputs;
            _boundTargets = history.TargetGeneration;
            _boundHistory = history;
        }

        // By reference: the sets name these objects, and holding them keeps a replacement from ever being the same one.
        internal static bool SameInputs(in TemporalResolveInputs a, in TemporalResolveInputs b)
            => ReferenceEquals(a.SceneColor, b.SceneColor) && ReferenceEquals(a.OpaqueColor, b.OpaqueColor)
                && ReferenceEquals(a.SceneDepth, b.SceneDepth) && ReferenceEquals(a.Motion, b.Motion);

        /// <summary>Let go of the sets, which name the history targets and the scene's inputs, and keep the pipelines,
        /// layouts, buffers and sampler. The sets go to <paramref name="retired"/>, since a frame the device has not
        /// finished may still bind them. The next <see cref="BindInputs"/> builds them again.</summary>
        public void ReleaseSets(GpuRetireQueue retired)
        {
            _split?.ReleaseTargets(retired);   // a frame without the resolve holds none of the split's targets either
            if (_storeSet is null) return;
            for (int i = 0; i < 2; i++)
            {
                retired.Retire(_resolveSets[i]);
                _resolveSets[i] = null;
            }
            retired.Retire(_storeSet);
            _storeSet = null;
            ForgetBinding();
        }

        /// <summary>Upload this frame's uniforms. Call before any framebuffer is bound this frame, as the post chain's
        /// <c>PrepareUniforms</c> is.</summary>
        public void PrepareUniforms(IGpuCommandList cl, in TemporalResolveUniforms resolve, in TemporalDepthStoreUniforms store)
        {
            LastUniforms = resolve;
            cl.UpdateBuffer(_resolveBuffer, 0, in resolve);
            cl.UpdateBuffer(_storeBuffer, 0, in store);
        }

        /// <summary>Record the resolve into <see cref="TemporalHistory.WriteIndex"/>'s colour and confidence, reading
        /// <see cref="TemporalHistory.ReadIndex"/>'s history and previous depth, then store this frame's depth into the
        /// write index's previous depth. The read targets are left untouched, so a later pass can re-evaluate the resolve
        /// through <see cref="CurrentSet"/> and see exactly what it saw. The split entry point records its two passes
        /// in their place, into the same targets (Entry partial).</summary>
        public void Run(IGpuCommandList cl, TemporalHistory history)
        {
            IGpuResourceSet set = _resolveSets[history.ReadIndex]
                ?? throw new InvalidOperationException("TemporalResolveRenderer.Run was called before BindInputs.");
            CurrentSet = set;
            LastEntry = Entry;
            if (RunSplit(cl, history))
            {
                RecordFinishProbe(cl);
                return;
            }
            cl.SetFramebuffer(history.ResolveFramebuffer(history.WriteIndex));
            cl.SetPipeline(_resolvePipeline!);
            cl.SetGraphicsResourceSet(0, set);
            cl.Draw(3);

            cl.SetFramebuffer(history.PreviousDepthFramebuffer(history.WriteIndex));
            cl.SetPipeline(_storePipeline!);
            cl.SetGraphicsResourceSet(0, _storeSet!);
            cl.Draw(3);
            RecordFinishProbe(cl);   // an armed temporal count request, over the set this run bound (Finish partial)
        }

        void DisposeSets()
        {
            for (int i = 0; i < 2; i++)
            {
                _resolveSets[i]?.Dispose();
                _resolveSets[i] = null;
            }
            _storeSet?.Dispose();
            _storeSet = null;
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
            DisposeSets();
            _resolvePipeline?.Dispose();
            _storePipeline?.Dispose();
            _resolveLayout.Dispose();
            _storeLayout.Dispose();
            _resolveShaders?.Dispose();
            _storeShaders?.Dispose();
            _resolveBuffer.Dispose();
            _storeBuffer.Dispose();
            _clampSampler.Dispose();
        }
    }
}
