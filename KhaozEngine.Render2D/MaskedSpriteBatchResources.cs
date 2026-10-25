using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render2D;

/// <summary>Owns the optional masked pipelines and descriptors, borrowing all input and VP handles.</summary>
internal sealed class MaskedSpriteBatchResources : IDisposable
{
    readonly IGpuResourceFactory _factory;
    readonly IGpuResourceLayout _layout;
    readonly IGpuShaderSet _shaders;
    readonly IGpuPipeline _alphaPipeline;
    readonly IGpuPipeline _additivePipeline;
    readonly Dictionary<BindingIdentity, BindingEntry> _entries = new();
    readonly List<BindingIdentity> _evictScratch = new();
    bool _disposed;

    internal MaskedSpriteBatchResources(IGpuDevice device, GpuOutputDescription output,
        IGpuResourceLayout vpLayout, GpuVertexLayoutDescription vertexLayout)
    {
        _factory = device.Factory;
        IGpuResourceLayout? layout = null;
        IGpuShaderSet? shaders = null;
        IGpuPipeline? alpha = null;
        IGpuPipeline? additive = null;
        try
        {
            layout = _factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Tex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Mask", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("MaskSamp", GpuResourceKind.Sampler, GpuShaderStages.Fragment)));
            shaders = _factory.CreateShadersFromSpirv(SpriteBatch.VertSrc, MaskedSpriteShader.FragmentSource);
            GpuPipelineDescription Describe(GpuBlendAttachment blend) => new()
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { blend },
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid,
                    GpuFrontFace.Clockwise, depthClipEnabled: false, scissorTestEnabled: true),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { layout, vpLayout },
                ShaderSet = shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout },
                Outputs = output,
            };
            alpha = _factory.CreateGraphicsPipeline(Describe(GpuBlendAttachment.AlphaBlend));
            additive = _factory.CreateGraphicsPipeline(Describe(GpuBlendAttachment.Additive));
        }
        catch (Exception original)
        {
            var failures = new List<Exception> { original };
            void Release(IDisposable? resource)
            {
                try { resource?.Dispose(); }
                catch (Exception cleanup) { failures.Add(cleanup); }
            }
            Release(additive);
            Release(alpha);
            Release(shaders);
            Release(layout);
            if (failures.Count > 1) throw new AggregateException(failures);
            throw;
        }
        _layout = layout;
        _shaders = shaders;
        _alphaPipeline = alpha;
        _additivePipeline = additive;
    }

    internal int CachedSetCount { get; private set; }

    internal RunKey KeyFor(IGpuTexture source, IGpuSampler sourceSampler, IGpuTexture mask,
        IGpuSampler maskSampler, BlendMode blend, long frame)
    {
        var identity = new BindingIdentity(source, sourceSampler, mask, maskSampler);
        if (!_entries.TryGetValue(identity, out var entry))
        {
            entry = new BindingEntry(identity, frame);
            _entries.Add(identity, entry);
        }
        entry.LastUsedFrame = frame;
        return blend == BlendMode.Alpha ? entry.Alpha : entry.Additive;
    }

    internal IGpuResourceSet SetFor(RunKey key, long frame)
    {
        var entry = key.Entry;
        if (entry.Set == null)
        {
            var binding = entry.Identity;
            entry.Set = _factory.CreateResourceSet(new GpuResourceSetDescription(_layout,
                binding.Source, binding.SourceSampler, binding.Mask, binding.MaskSampler));
            CachedSetCount++;
        }
        entry.LastUsedFrame = frame;
        return entry.Set;
    }

    internal IGpuPipeline PipelineFor(BlendMode blend) =>
        blend == BlendMode.Alpha ? _alphaPipeline : _additivePipeline;

    internal void Evict(long frame, int unusedFrames, Action<object> forgetKey, Action<IGpuResourceSet> retireSet)
    {
        long cutoff = frame - unusedFrames;
        foreach (var (identity, entry) in _entries)
            if (entry.LastUsedFrame <= cutoff) _evictScratch.Add(identity);
        foreach (var identity in _evictScratch)
        {
            var entry = _entries[identity];
            forgetKey(entry.Alpha);
            forgetKey(entry.Additive);
            if (entry.Set is { } set)
            {
                retireSet(set);
                entry.Set = null;
                CachedSetCount--;
            }
            _entries.Remove(identity);
        }
        // Identities borrow caller handles too, so eviction scratch must not retain them between frames.
        _evictScratch.Clear();
    }

    // Entries and their two immutable keys are allocated only when a handle tuple first appears.
    internal sealed class RunKey
    {
        internal BindingEntry Entry { get; }
        internal IGpuTexture Source => Entry.Identity.Source;
        internal BlendMode Blend { get; }
        internal RunKey(BindingEntry entry, BlendMode blend) { Entry = entry; Blend = blend; }
    }

    internal sealed class BindingEntry
    {
        internal BindingIdentity Identity { get; }
        internal RunKey Alpha { get; }
        internal RunKey Additive { get; }
        internal IGpuResourceSet? Set;
        internal long LastUsedFrame;

        internal BindingEntry(BindingIdentity identity, long frame)
        {
            Identity = identity;
            LastUsedFrame = frame;
            Alpha = new(this, BlendMode.Alpha);
            Additive = new(this, BlendMode.Additive);
        }
    }

    // Backend handles may implement value equality. Descriptor ownership always follows handle identity.
    internal readonly struct BindingIdentity(IGpuTexture source, IGpuSampler sourceSampler,
        IGpuTexture mask, IGpuSampler maskSampler) : IEquatable<BindingIdentity>
    {
        internal IGpuTexture Source { get; } = source;
        internal IGpuSampler SourceSampler { get; } = sourceSampler;
        internal IGpuTexture Mask { get; } = mask;
        internal IGpuSampler MaskSampler { get; } = maskSampler;

        public bool Equals(BindingIdentity other) =>
            ReferenceEquals(Source, other.Source) && ReferenceEquals(SourceSampler, other.SourceSampler) &&
            ReferenceEquals(Mask, other.Mask) && ReferenceEquals(MaskSampler, other.MaskSampler);
        public override bool Equals(object? obj) => obj is BindingIdentity other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(Source),
            RuntimeHelpers.GetHashCode(SourceSampler), RuntimeHelpers.GetHashCode(Mask), RuntimeHelpers.GetHashCode(MaskSampler));
    }

    /// <inheritdoc />
    public void Dispose() => Dispose(null);

    internal void Dispose(Action<object>? forgetKey)
    {
        if (_disposed) return;
        _disposed = true;
        var failures = ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
        int ordinal = 0;
        void Release(string kind, IDisposable? resource)
        {
            int index = ordinal++;
            try { resource?.Dispose(); }
            catch (Exception error)
            {
                if (error is GpuDiagnosticFailureException transport) failures.AddRange(transport.Failures);
                else failures.Add(new("renderer.session.dispose.ui.masked." + kind + "." + index, error));
            }
        }
        foreach (var entry in _entries.Values)
        {
            Release("set", entry.Set);
            entry.Set = null;
            forgetKey?.Invoke(entry.Alpha);
            forgetKey?.Invoke(entry.Additive);
        }
        _entries.Clear();
        _evictScratch.Clear();
        CachedSetCount = 0;
        Release("additivePipeline", _additivePipeline);
        Release("pipeline", _alphaPipeline);
        Release("shaders", _shaders);
        Release("layout", _layout);
        if (failures.Count != 0) throw new GpuDiagnosticFailureException(failures.ToImmutable());
    }
}
