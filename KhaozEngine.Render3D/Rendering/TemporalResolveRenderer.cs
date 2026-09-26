using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>The internal-resolution inputs of one resolve. <paramref name="Generation"/> changes whenever any of the
    /// four textures is replaced, which is when the renderer rebuilds its resource sets.</summary>
    internal readonly record struct TemporalResolveInputs(IGpuTexture SceneColor, IGpuTexture OpaqueColor,
        IGpuTexture SceneDepth, IGpuTexture Motion, int Generation);

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
    /// once per target generation and <see cref="Run"/> picks one, which keeps a steady frame from building or
    /// allocating anything.</para>
    /// <para>Created on the first frame the resolve runs, so a scene that never selects temporal anti-aliasing owns
    /// none of its objects. The temporal debug views and the sampled temporal counts re-evaluate the resolve over
    /// <see cref="ResolveLayout"/> and <see cref="CurrentSet"/>, through <c>ShaderSources.TemporalResolveCoreGlsl</c>.</para>
    /// </summary>
    internal sealed partial class TemporalResolveRenderer : IDisposable
    {
        /// <summary>The draws <see cref="Run"/> records: the resolve and the depth store.</summary>
        internal const int DrawCallsPerFrame = 2;

        readonly IGpuDevice _gd;
        readonly IGpuShaderSet _resolveShaders, _storeShaders;
        readonly IGpuResourceLayout _resolveLayout, _storeLayout;
        readonly IGpuPipeline _resolvePipeline, _storePipeline;
        readonly IGpuBuffer _resolveBuffer, _storeBuffer;
        readonly IGpuSampler _clampSampler;
        readonly IGpuResourceSet?[] _resolveSets = new IGpuResourceSet?[2];
        IGpuResourceSet? _storeSet;
        int _boundInputs = int.MinValue, _boundTargets = int.MinValue;
        TemporalHistory? _boundHistory;

        public TemporalResolveRenderer(IGpuDevice gd)
        {
            _gd = gd;
            IGpuResourceFactory f = gd.Factory;
            _resolveShaders = f.CreateShadersFromSpirv(ShaderSources.FullscreenVert, ShaderSources.TemporalResolveFrag);
            _storeShaders = f.CreateShadersFromSpirv(ShaderSources.FullscreenVert, ShaderSources.TemporalDepthStoreFrag);
            _resolveLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                T("SceneColor"), T("OpaqueColor"), T("SceneDepth"), T("MotionTex"), T("PrevDepth"),
                T("HistoryColor"), T("HistoryConfidence"), S("LinearClamp"), U("Resolve")));
            _storeLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                T("SceneDepth"), T("MotionTex"), S("Samp"), U("DepthStore")));
            _resolveBuffer = f.CreateBuffer(new GpuBufferDescription(TemporalResolveUniforms.SizeInBytes, GpuBufferUsage.UniformBuffer));
            _storeBuffer = f.CreateBuffer(new GpuBufferDescription(TemporalDepthStoreUniforms.SizeInBytes, GpuBufferUsage.UniformBuffer));
            _clampSampler = f.CreateSampler(GpuSamplerDescription.Linear);
            _resolvePipeline = Fullscreen(f, _resolveShaders, _resolveLayout,
                new GpuOutputDescription(null, TemporalFormats.HistoryColor, TemporalFormats.HistoryConfidence));
            _storePipeline = Fullscreen(f, _storeShaders, _storeLayout,
                new GpuOutputDescription(null, TemporalFormats.PreviousDepth));
        }

        /// <summary>The resolve's resource layout, which the temporal debug views and the count probe bind the resolve's
        /// own set through.</summary>
        internal IGpuResourceLayout ResolveLayout => _resolveLayout;

        /// <summary>The resolve set <see cref="Run"/> bound this frame, over the history pair it read. Null before the
        /// first run.</summary>
        internal IGpuResourceSet? CurrentSet { get; private set; }

        /// <summary>The uniforms uploaded for this frame. Internal, for the tests and the temporal diagnostics.</summary>
        internal TemporalResolveUniforms LastUniforms { get; private set; }

        static GpuResourceLayoutElement T(string n) => new(n, GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment);
        static GpuResourceLayoutElement S(string n) => new(n, GpuResourceKind.Sampler, GpuShaderStages.Fragment);
        static GpuResourceLayoutElement U(string n) => new(n, GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment);

        static IGpuPipeline Fullscreen(IGpuResourceFactory f, IGpuShaderSet shaders, IGpuResourceLayout layout,
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

        /// <summary>Build the resolve sets for both read indices and the depth store set, unless the inputs and the
        /// history targets are the ones already bound.</summary>
        public void BindInputs(in TemporalResolveInputs inputs, TemporalHistory history)
        {
            if (inputs.Generation == _boundInputs && ReferenceEquals(history, _boundHistory)
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
            _boundInputs = inputs.Generation;
            _boundTargets = history.TargetGeneration;
            _boundHistory = history;
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
        /// through <see cref="CurrentSet"/> and see exactly what it saw.</summary>
        public void Run(IGpuCommandList cl, TemporalHistory history)
        {
            IGpuResourceSet set = _resolveSets[history.ReadIndex]
                ?? throw new InvalidOperationException("TemporalResolveRenderer.Run was called before BindInputs.");
            CurrentSet = set;
            cl.SetFramebuffer(history.ResolveFramebuffer(history.WriteIndex));
            cl.SetPipeline(_resolvePipeline);
            cl.SetGraphicsResourceSet(0, set);
            cl.Draw(3);

            cl.SetFramebuffer(history.PreviousDepthFramebuffer(history.WriteIndex));
            cl.SetPipeline(_storePipeline);
            cl.SetGraphicsResourceSet(0, _storeSet!);
            cl.Draw(3);
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
            CurrentSet = null;
            _boundInputs = _boundTargets = int.MinValue;
            _boundHistory = null;
        }

        public void Dispose()
        {
            DisposeSets();
            _resolvePipeline.Dispose();
            _storePipeline.Dispose();
            _resolveLayout.Dispose();
            _storeLayout.Dispose();
            _resolveShaders.Dispose();
            _storeShaders.Dispose();
            _resolveBuffer.Dispose();
            _storeBuffer.Dispose();
            _clampSampler.Dispose();
        }
    }
}
