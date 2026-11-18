using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using static KhaozEngine.Render3D.Internal.FragmentPassResources;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Replays Fine into private targets. All programs see the shipped renderer's immutable inputs for this frame.</summary>
internal sealed class TemporalFineCostProbeReplay : IDisposable
{
    readonly TemporalFixture _fixture;
    readonly IGpuResourceFactory _factory;
    readonly List<IDisposable> _owned = [];
    readonly IGpuResourceLayout _classifyLayout, _resolveLayout, _planeLayout, _optionsLayout, _demuxLayout, _storeLayout;
    readonly IGpuFramebuffer _classifyFrame, _resolveFrame, _twoTargetFrame, _storeFrame, _demuxFrame;
    readonly IGpuBuffer _options;
    readonly IGpuResourceSet[] _classifySets, _resolveSets, _demuxSets;
    readonly IGpuResourceSet _planeClassifySet, _planeSet, _optionsSet, _storeSet;
    readonly IGpuPipeline _demux, _store;
    readonly IGpuTexture _color, _state, _classification, _stored;
    readonly IGpuTexture[] _children;
    readonly TemporalResolveRenderer _renderer;
    readonly TemporalResolvePrecision _precision;

    internal TemporalFineCostProbeReplay(TemporalFixture fixture)
    {
        _fixture = fixture;
        _factory = fixture.Device.Factory;
        _renderer = fixture.Scene.TemporalResolveRendererForTests!;
        _precision = TemporalResolvePrecisionPolicy.For(fixture.Device);
        try
        {
            _classifyLayout = Layout(T("DetailColor"));
            _resolveLayout = Layout(T("DetailColor"), T("DetailClassification"));
            _planeLayout = Layout(T("DetailColor"), T("DetailClassification"),
                T("ProbePlane1"), T("ProbePlane2"), T("ProbePlane3"));
            _optionsLayout = Layout(U("ProbeOptions"));
            _demuxLayout = Layout(T("Atlas"), S("PointClamp"));
            _storeLayout = Layout(T("Child0"), T("Child1"), T("Child2"), T("Child3"), S("LinearClamp"));
            _classification = Target(TemporalFormats.DetailClassification);
            _color = Target(TemporalFormats.HistoryColor);
            _state = Target(TemporalFormats.HistoryConfidence);
            _children = Planes();
            IGpuTexture[] inputPlanes = Planes();
            _stored = Target(TemporalFormats.HistoryColor, scale: 2);
            _classifyFrame = Own(_factory.CreateFramebuffer(null, _classification));
            _resolveFrame = Own(_factory.CreateFramebuffer(null, [_color, _state, .. _children]));
            _twoTargetFrame = Own(_factory.CreateFramebuffer(null, _color, _state));
            _storeFrame = Own(_factory.CreateFramebuffer(null, _stored));
            _demuxFrame = Own(_factory.CreateFramebuffer(null, inputPlanes));

            var sampler = Own(_factory.CreateSampler(GpuSamplerDescription.Linear));
            _options = Own(_factory.CreateBuffer(new GpuBufferDescription(16, GpuBufferUsage.UniformBuffer)));
            _optionsSet = Set(_optionsLayout, _options);
            _planeClassifySet = Set(_planeLayout, inputPlanes[0], inputPlanes[0], inputPlanes[1], inputPlanes[2], inputPlanes[3]);
            _planeSet = Set(_planeLayout, inputPlanes[0], _classification, inputPlanes[1], inputPlanes[2], inputPlanes[3]);
            _storeSet = Set(_storeLayout, [.. _children, sampler]);
            TemporalDetailStore history = fixture.Scene.TemporalHistory.DetailStore!;
            _classifySets = [Set(_classifyLayout, history.Color(0)), Set(_classifyLayout, history.Color(1))];
            _resolveSets = [Set(_resolveLayout, history.Color(0), _classification), Set(_resolveLayout, history.Color(1), _classification)];
            _demuxSets = [Set(_demuxLayout, history.Color(0), sampler), Set(_demuxLayout, history.Color(1), sampler)];
            _demux = Pipeline(_owned, TemporalFineCostProbePrograms.Demux, [_demuxLayout], _demuxFrame);
            _store = Pipeline(_owned, ShaderSources.TemporalDetailUpdateFrag, [_storeLayout], _storeFrame);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    // The fixture keeps the same light and certificate across warmed frames. The latched readiness describes
    // the bound read side, while VisibilityHistory.ReadFullyLit queried after Store describes the new write side.
    internal bool SkipVisibility => _renderer.LastCurrentVisibilityFullyLit
        && _renderer.LastVisibilityUniforms.PreviousJitter.Z > 0.5f;

    internal void PrepareOptions(IGpuCommandList cl, bool certified) =>
        cl.UpdateBuffer(_options, 0, new Vector4(certified ? 1.0f : 0.0f, 0.0f, 0.0f, 0.0f));

    IGpuTexture[] Planes() => Enumerable.Range(0, 4).Select(_ => Target(TemporalFormats.HistoryColor)).ToArray();

    IGpuTexture Target(GpuPixelFormat format, uint scale = 1) =>
        Own(_factory.CreateTexture(GpuTextureDescription.Texture2D(
            (uint)_fixture.DisplayWidth * scale, (uint)_fixture.DisplayHeight * scale, format,
            GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled)));

    IGpuResourceLayout Layout(params GpuResourceLayoutElement[] elements) =>
        Own(_factory.CreateResourceLayout(new GpuResourceLayoutDescription(elements)));

    IGpuResourceSet Set(IGpuResourceLayout layout, params IGpuBindableResource[] resources) =>
        Own(_factory.CreateResourceSet(new GpuResourceSetDescription(layout, resources)));

    IGpuPipeline Pipeline(List<IDisposable> owned, string source, IGpuResourceLayout[] layouts, IGpuFramebuffer target)
    {
        var shaders = _factory.CreateShadersFromSpirv(ShaderSources.FullscreenVert,
            TemporalVisibilityGatherPolicy.ForBackend(source, _fixture.Device.Backend));
        owned.Add(shaders);
        var pipeline = TemporalResolveRenderer.Fullscreen(_factory, shaders, layouts, target.Outputs);
        owned.Add(pipeline);
        return pipeline;
    }

    internal Program Compile(TemporalFineCostVariant variant) => new(this, variant);

    internal sealed class Program : IDisposable
    {
        readonly List<IDisposable> _owned = [];
        internal TemporalFineCostVariant Variant { get; }
        internal IGpuPipeline? Classify { get; }
        internal IGpuPipeline Resolve { get; }
        internal IGpuFramebuffer ResolveTarget { get; }

        internal Program(TemporalFineCostProbeReplay replay, TemporalFineCostVariant variant)
        {
            Variant = variant;
            try
            {
                bool planes = variant == TemporalFineCostVariant.DirectPlanes;
                var layout = replay._renderer.ResolveLayout;
                if (TemporalFineCostProbePrograms.Classifies(variant))
                    Classify = replay.Pipeline(_owned,
                        TemporalFineCostProbePrograms.Classify(replay._precision, variant),
                        [layout, planes ? replay._planeLayout : replay._classifyLayout], replay._classifyFrame);
                var fineLayout = planes ? replay._planeLayout : replay._resolveLayout;
                IGpuResourceLayout[] layouts = TemporalFineCostProbePrograms.UsesUniform(variant)
                    ? [layout, fineLayout, replay._optionsLayout]
                    : [layout, fineLayout];
                ResolveTarget = variant is TemporalFineCostVariant.TwoTargetsProxy or TemporalFineCostVariant.CoreRemainder
                    ? replay._twoTargetFrame : replay._resolveFrame;
                Resolve = replay.Pipeline(_owned, TemporalFineCostProbePrograms.Resolve(replay._precision, variant),
                    layouts, ResolveTarget);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose() => Release(_owned);
    }

    internal void Record(IGpuCommandList cl, Program program, IGpuTimestampPool? pool, uint first)
    {
        bool planes = program.Variant == TemporalFineCostVariant.DirectPlanes;
        int read = _fixture.Scene.TemporalHistory.ReadIndex;
        if (planes) Draw(cl, _demuxFrame, _demux, _demuxSets[read]);
        var inputs = _renderer.CurrentSet!;
        Mark(cl, pool, first);
        if (program.Classify is { } classifier)
            Draw(cl, _classifyFrame, classifier, inputs, planes ? _planeClassifySet : _classifySets[read]);
        Mark(cl, pool, first + 1);
        Mark(cl, pool, first + 2);
        Draw(cl, program.ResolveTarget, program.Resolve, inputs, planes ? _planeSet : _resolveSets[read],
            TemporalFineCostProbePrograms.UsesUniform(program.Variant) ? _optionsSet : null);
        Mark(cl, pool, first + 3);
        Mark(cl, pool, first + 4);
        if (TemporalFineCostProbePrograms.Stores(program.Variant)) Draw(cl, _storeFrame, _store, _storeSet);
        Mark(cl, pool, first + 5);
    }

    static void Draw(IGpuCommandList cl, IGpuFramebuffer frame, IGpuPipeline pipeline, IGpuResourceSet inputs,
        IGpuResourceSet? detail = null, IGpuResourceSet? options = null)
    {
        cl.SetFramebuffer(frame);
        cl.SetPipeline(pipeline);
        cl.SetGraphicsResourceSet(0, inputs);
        if (detail is not null) cl.SetGraphicsResourceSet(1, detail);
        if (options is not null) cl.SetGraphicsResourceSet(2, options);
        cl.Draw(3);
    }

    static void Mark(IGpuCommandList cl, IGpuTimestampPool? pool, uint index)
    {
        if (pool is not null) cl.WriteTimestamp(pool, index);
    }

    internal string CompareHistory(TemporalFineCostVariant variant)
    {
        TemporalHistory history = _fixture.Scene.TemporalHistory;
        int write = history.WriteIndex;
        byte[] shipped = ReadBytes(history.DetailStore!.Color(write), 8);
        byte[] replay = TemporalFineCostProbePrograms.Stores(variant) ? ReadBytes(_stored, 8) : PackChildren();
        int differingChildren = 0;
        for (int i = 0; i < shipped.Length; i += 8)
            if (!shipped.AsSpan(i, 8).SequenceEqual(replay.AsSpan(i, 8))) differingChildren++;
        bool color = ReadBytes(history.Color(write), 8).AsSpan().SequenceEqual(ReadBytes(_color, 8));
        bool state = ReadBytes(history.Confidence(write), 8).AsSpan().SequenceEqual(ReadBytes(_state, 8));
        return $"colorEqual={color}|stateEqual={state}|differentChildren={differingChildren}|children={shipped.Length / 8}"
            + $"|shippedSha256={Convert.ToHexString(SHA256.HashData(shipped))}|replaySha256={Convert.ToHexString(SHA256.HashData(replay))}";
    }

    byte[] PackChildren()
    {
        int width = _fixture.DisplayWidth, height = _fixture.DisplayHeight;
        var packed = new byte[width * height * 32];
        for (int child = 0; child < 4; child++)
        {
            byte[] plane = ReadBytes(_children[child], 8);
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int source = (y * width + x) * 8;
                    int target = ((y * 2 + child / 2) * width * 2 + x * 2 + child % 2) * 8;
                    plane.AsSpan(source, 8).CopyTo(packed.AsSpan(target, 8));
                }
        }
        return packed;
    }

    unsafe byte[] ReadBytes(IGpuTexture texture, int bytesPerPixel)
    {
        var device = _fixture.Device;
        using IGpuTexture staging = _factory.CreateTexture(GpuTextureDescription.Texture2D(
            texture.Width, texture.Height, texture.Format, GpuTextureUsage.Staging));
        using var cl = _factory.CreateCommandList();
        using (GpuRecording.Open(device, cl, "TemporalFineCostProbeReplay.ReadBytes")) cl.CopyTexture(texture, staging);
        device.Submit(cl);
        device.WaitForIdle();
        MappedData map = device.Map(staging, GpuMapMode.Read);
        try
        {
            int rowBytes = (int)texture.Width * bytesPerPixel;
            var bytes = new byte[rowBytes * texture.Height];
            for (int y = 0; y < texture.Height; y++)
                new ReadOnlySpan<byte>((byte*)map.Data + y * map.RowPitch, rowBytes).CopyTo(bytes.AsSpan(y * rowBytes));
            return bytes;
        }
        finally { device.Unmap(staging); }
    }

    TResource Own<TResource>(TResource resource) where TResource : IDisposable
    {
        _owned.Add(resource);
        return resource;
    }

    public void Dispose() => Release(_owned);

    static void Release(List<IDisposable> owned)
    {
        for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
    }
}
