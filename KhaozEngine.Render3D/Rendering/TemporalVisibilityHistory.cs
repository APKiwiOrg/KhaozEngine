using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using static KhaozEngine.Render3D.Internal.FragmentPassResources;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>Resolve-owned internal visibility history on the diagnostic archive. A copy after either entry point
/// stores exactly the value the lit MRT wrote. The read pair stays intact for the post-chain overlay.</summary>
internal sealed class TemporalVisibilityHistory : IDisposable
{
    readonly IGpuDevice _gd;
    readonly IGpuBuffer _jitterBuffer;
    readonly IGpuTexture?[] _history = new IGpuTexture?[2];
    IGpuTexture? _current, _fallback;
    TemporalHistory? _owner;
    int _generation = -1;
    Vector4 _previousJitter;

    internal TemporalVisibilityHistory(IGpuDevice gd)
    {
        _gd = gd;
        _jitterBuffer = gd.Factory.CreateBuffer(new GpuBufferDescription(16, GpuBufferUsage.UniformBuffer));
    }

    internal static GpuResourceLayoutElement[] Layout(params GpuResourceLayoutElement[] elements) =>
        TemporalVisibilityDiagnostics.Enabled
            ? [.. elements, T("KeyVisibility"), T("PrevVisibility"), U("VisibilityHistory")]
            : elements;

    internal static IGpuBindableResource[] Resources(TemporalVisibilityHistory? owner, int read,
        params IGpuBindableResource[] resources) => owner is null ? resources
            : [.. resources, owner._current!, owner._history[read]!, owner._jitterBuffer];

    internal void Ensure(in TemporalResolveInputs inputs, TemporalHistory history, GpuRetireQueue? retired)
    {
        if (!ReferenceEquals(_owner, history) || _generation != history.TargetGeneration)
        {
            Release(retired);
            uint w = (uint)history.InternalWidth, h = (uint)history.InternalHeight;
            for (int i = 0; i < 2; i++) _history[i] = Texture(w, h);
            _owner = history;
            _generation = history.TargetGeneration;
        }
        // Standalone resolve tests can supply inputs without a lit pass. Their visibility is fully lit.
        if (inputs.KeyVisibility is null && _fallback is null)
        {
            uint w = (uint)history.InternalWidth, h = (uint)history.InternalHeight;
            _fallback = Texture(w, h);
            var white = new byte[checked((int)(w * h * 4))];
            for (int i = 0; i < white.Length; i += 4) { white[i + 2] = 0x80; white[i + 3] = 0x3f; }
            _gd.UpdateTexture(_fallback, white, 0, 0, w, h);
        }
        _current = inputs.KeyVisibility ?? _fallback
            ?? throw new InvalidOperationException("The visibility diagnostic needs its lit-pass attachment.");
    }

    IGpuTexture Texture(uint w, uint h) => _gd.Factory.CreateTexture(GpuTextureDescription.Texture2D(w, h,
        TemporalVisibilityDiagnostics.Format, GpuTextureUsage.Sampled | GpuTextureUsage.RenderTarget));

    internal void Prepare(IGpuCommandList cl) => cl.UpdateBuffer(_jitterBuffer, 0, in _previousJitter);

    internal void Store(IGpuCommandList cl, int write, Vector4 jitter)
    {
        cl.CopyTexture(_current!, _history[write]!);
        _previousJitter = jitter;
    }

    internal void Release(GpuRetireQueue? retired)
    {
        if (retired is null && _history[0] is not null)
        {
            if (GpuRecording.OpenOwner(_gd) is { } owner)
                throw new InvalidOperationException($"Visibility history cannot drain while {owner} is recording. Pass the retire queue.");
            _gd.WaitForIdle();
        }
        for (int i = 0; i < 2; i++) Free(ref _history[i], retired);
        Free(ref _fallback, retired);
        _current = null;
        _owner = null;
        _generation = -1;
        _previousJitter = default;
    }

    public void Dispose()
    {
        Release(null);
        _jitterBuffer.Dispose();
    }
}
