using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>Previous internal-pixel jitter in XY, comparison readiness in Z and reserved W.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TemporalVisibilityUniforms
{
    internal const uint SizeInBytes = 16;
    internal const string GlslMembers = "vec4 PreviousJitter;";
    public Vector4 PreviousJitter;
}

/// <summary>The visibility resources and completed-frame jitter shared by both resolve entry points.</summary>
internal sealed partial class TemporalResolveRenderer
{
    readonly IGpuBuffer _visibilityBuffer;
    Vector2 _savedJitter;
    IGpuTexture? _visibilityInput;
    internal TemporalVisibilityHistory VisibilityHistory { get; } = new();
    internal TemporalVisibilityUniforms LastVisibilityUniforms { get; private set; }

    TemporalResolveInputs BindVisibility(in TemporalResolveInputs inputs, TemporalHistory history, GpuRetireQueue? retired)
    {
        bool matches = VisibilityHistory.Matches(_gd, history);
        if (_storeSet is not null && retired is null && (!matches || !SameInputs(inputs, _boundInputs)))
        {
            if (GpuRecording.OpenOwner(_gd) is { } owner)
                throw new InvalidOperationException($"Temporal bindings cannot drain while {owner} is recording. Pass the retire queue.");
            _gd.WaitForIdle();
        }
        if (!matches)
        {
            // Sets must go before the generation of textures they name.
            _split?.ReleaseSets(retired);
            FreeSets(retired);
            VisibilityHistory.Ensure(_gd, history, retired);
            _savedJitter = default;
            LastVisibilityUniforms = default;
        }
        _visibilityInput = inputs.KeyShadowVisibility ?? VisibilityHistory.FullyLit();
        return inputs with { KeyShadowVisibility = _visibilityInput };
    }

    void PrepareVisibilityUniforms(IGpuCommandList commands, in TemporalResolveUniforms resolve)
    {
        // Re-evaluation in the same frame must keep the original previous jitter, just as it keeps the read texture.
        if (!VisibilityHistory.StoredThisFrame)
            LastVisibilityUniforms = new TemporalVisibilityUniforms
            {
                PreviousJitter = new Vector4(_savedJitter,
                    VisibilityHistory.Ready && resolve.Jitter.W > 0f ? 1f : 0f, 0f),
            };
        TemporalVisibilityUniforms uniforms = LastVisibilityUniforms;
        commands.UpdateBuffer(_visibilityBuffer, 0, in uniforms);
    }

    void StoreVisibility(IGpuCommandList commands, TemporalHistory history)
    {
        if (VisibilityHistory.StoredThisFrame) return;
        VisibilityHistory.Store(commands, _visibilityInput!, history.WriteIndex);
        // Only publish jitter after the copy was successfully recorded for this frame.
        _savedJitter = new Vector2(LastUniforms.Jitter.X, LastUniforms.Jitter.Y);
    }

    void ReleaseVisibility(GpuRetireQueue? retired)
    {
        VisibilityHistory.Release(retired);
        _visibilityInput = null;
        _savedJitter = default;
        LastVisibilityUniforms = default;
    }
}
