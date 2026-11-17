using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Diagnostics;
using KhaozEngine.Render3D.Rendering;
using static KhaozEngine.Render3D.Internal.FragmentPassResources;

namespace KhaozEngine.Render3D.Internal;

/// <summary>Display-grid validity and resolve, then the fine-grid copy. Owned by the detail store, so all
/// sets and framebuffers retire before the history generation they borrow. No independent frame or pair index.</summary>
internal sealed class TemporalDetailPass
{
    readonly IGpuResourceSet?[] _fineSets = new IGpuResourceSet?[2];
    readonly IGpuResourceSet?[] _classifySets = new IGpuResourceSet?[2];
    readonly IGpuResourceSet?[] _updateSets = new IGpuResourceSet?[2];
    readonly IGpuFramebuffer?[] _resolveFrames = new IGpuFramebuffer?[2];
    readonly IGpuFramebuffer?[] _updateFrames = new IGpuFramebuffer?[2];
    IGpuTexture? _classification, _transport;
    readonly IGpuTexture?[] _children = new IGpuTexture?[4];
    IGpuFramebuffer? _classificationFrame;
    IGpuShaderSet? _classifyShaders, _resolveShaders, _updateShaders;
    IGpuResourceLayout? _classifyLayout, _updateLayout;
    readonly IGpuResourceLayout _fineLayout;
    IGpuPipeline? _classifyPipeline, _resolvePipeline, _updatePipeline;
    IGpuSampler? _sampler;

    internal TemporalDetailPass(IGpuDevice gd, TemporalHistory history, TemporalDetailStore store,
        IGpuResourceLayout resolveLayout, IGpuResourceLayout fineLayout, Func<string, string>? fragmentSourceForTests)
    {
        _fineLayout = fineLayout;
        try
        {
            IGpuResourceFactory f = gd.Factory;
            _classification = Target(f, history, TemporalFormats.DetailClassification);
            for (int child = 0; child < 4; child++) _children[child] = Target(f, history, TemporalFormats.HistoryColor);
            if (history.CaptureDetailPathForTests) _transport = Target(f, history, GpuPixelFormat.R32G32B32A32Float);
            _classificationFrame = f.CreateFramebuffer(null, _classification);
            _classifyLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(T("DetailColor")));
            _updateLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                T("Child0"), T("Child1"), T("Child2"), T("Child3"), S("LinearClamp")));
            _sampler = f.CreateSampler(GpuSamplerDescription.Linear);
            TemporalResolvePrecision precision = TemporalResolvePrecisionPolicy.For(gd);
            string Fragment(string source) => TemporalVisibilityGatherPolicy.ForBackend(
                fragmentSourceForTests?.Invoke(source) ?? source, gd.Backend);
            _classifyShaders = f.CreateShadersFromSpirv(ShaderSources.FullscreenVert,
                Fragment(ShaderSources.TemporalDetailClassifyFragment(precision)));
            // One checked resolve for every certificate state. A certified specialization rounds the shared
            // accumulation's float32 values differently on some compilers, and the stored detail amplifies that.
            _resolveShaders = f.CreateShadersFromSpirv(ShaderSources.FullscreenVert,
                Fragment(ShaderSources.TemporalDetailResolveFragment(precision, _transport is not null)));
            _updateShaders = f.CreateShadersFromSpirv(ShaderSources.FullscreenVert, Fragment(ShaderSources.TemporalDetailUpdateFrag));
            _classifyPipeline = TemporalResolveRenderer.Fullscreen(f, _classifyShaders,
                [resolveLayout, _classifyLayout], _classificationFrame.Outputs);
            // R, state and the four children, then the test-only transport after them.
            var outputs = new GpuPixelFormat[TemporalDetailMath.ResolveAttachments + (_transport is null ? 0 : 1)];
            Array.Fill(outputs, TemporalFormats.HistoryColor);
            outputs[1] = TemporalFormats.HistoryConfidence;
            if (_transport is not null) outputs[^1] = GpuPixelFormat.R32G32B32A32Float;
            _resolvePipeline = TemporalResolveRenderer.Fullscreen(f, _resolveShaders, [resolveLayout, _fineLayout],
                new GpuOutputDescription(null, outputs));
            _updatePipeline = TemporalResolveRenderer.Fullscreen(f, _updateShaders, _updateLayout,
                new GpuOutputDescription(null, TemporalFormats.HistoryColor));
            for (int write = 0; write < 2; write++)
            {
                _classifySets[write] = f.CreateResourceSet(new GpuResourceSetDescription(_classifyLayout, store.Color(1 - write)));
                _fineSets[write] = f.CreateResourceSet(new GpuResourceSetDescription(_fineLayout,
                    store.Color(1 - write), _classification));
                var targets = new IGpuTexture[outputs.Length];
                targets[0] = history.Color(write);
                targets[1] = history.Confidence(write);
                for (int child = 0; child < 4; child++) targets[2 + child] = _children[child]!;
                if (_transport is not null) targets[^1] = _transport;
                _resolveFrames[write] = f.CreateFramebuffer(null, targets);
                _updateFrames[write] = f.CreateFramebuffer(null, store.Color(write));
                _updateSets[write] = f.CreateResourceSet(new GpuResourceSetDescription(_updateLayout,
                    _children[0]!, _children[1]!, _children[2]!, _children[3]!, _sampler));
            }
        }
        catch { Release(null); throw; }
    }

    static IGpuTexture Target(IGpuResourceFactory f, TemporalHistory history, GpuPixelFormat format) =>
        f.CreateTexture(GpuTextureDescription.Texture2D((uint)history.DisplayWidth, (uint)history.DisplayHeight,
            format, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));

    internal void Resolve(IGpuCommandList cl, IGpuResourceSet inputs, int write, RenderPassProfiling? profiling)
    {
        using (profiling?.Pass(cl, "temporal.detail-classify") ?? default)
        {
            cl.SetFramebuffer(_classificationFrame!);
            cl.SetPipeline(_classifyPipeline!);
            cl.SetGraphicsResourceSet(0, inputs);
            cl.SetGraphicsResourceSet(1, _classifySets[write]!);
            cl.Draw(3);
        }
        using var resolve = profiling?.Pass(cl, "temporal.resolve") ?? default;
        cl.SetFramebuffer(_resolveFrames[write]!);
        cl.SetPipeline(_resolvePipeline!);
        cl.SetGraphicsResourceSet(0, inputs);
        cl.SetGraphicsResourceSet(1, _fineSets[write]!);
        cl.Draw(3);
    }

    internal void Update(IGpuCommandList cl, int write)
    {
        cl.SetFramebuffer(_updateFrames[write]!);
        cl.SetPipeline(_updatePipeline!);
        cl.SetGraphicsResourceSet(0, _updateSets[write]!);
        cl.Draw(3);
    }

    /// <summary>Borrowed transport export for test readback. Never dispose or write it.</summary>
    internal IGpuTexture TransportForTests => _transport!;

    internal IGpuResourceSet ReadSet(int write) => _fineSets[write]!;

    internal void Release(GpuRetireQueue? retired)
    {
        for (int i = 0; i < 2; i++)
        {
            Free(ref _classifySets[i], retired);
            Free(ref _fineSets[i], retired);
            Free(ref _updateSets[i], retired);
            Free(ref _resolveFrames[i], retired);
            Free(ref _updateFrames[i], retired);
        }
        Free(ref _classificationFrame, retired);
        Free(ref _classifyPipeline, retired);
        Free(ref _resolvePipeline, retired);
        Free(ref _updatePipeline, retired);
        Free(ref _classifyShaders, retired);
        Free(ref _resolveShaders, retired);
        Free(ref _updateShaders, retired);
        Free(ref _classifyLayout, retired);
        Free(ref _updateLayout, retired);
        Free(ref _sampler, retired);
        Free(ref _classification, retired);
        for (int i = 0; i < 4; i++) Free(ref _children[i], retired);
        Free(ref _transport, retired);
    }
}
