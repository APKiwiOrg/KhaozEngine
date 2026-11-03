using System;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

/// <summary>The private probe's switches and the record of what a frame actually uploaded.</summary>
public sealed partial class Scene3D
{
    const int NoProbeGatesApplied = -1, MixedProbeGatesApplied = -2;
    int _appliedProbeGates = NoProbeGatesApplied;

    /// <summary>The gates the next frame uploads. Only the recorder sets this, between callbacks, so one callback sees
    /// one state. Off by default.</summary>
    public ShaderGateProbe ProbeShaderGates { get; set; }

    /// <summary>What was uploaded since the last call as a mask, -1 when no frame rendered, or -2 when renders
    /// disagreed.</summary>
    public int ConsumeAppliedProbeShaderGates()
    {
        int applied = _appliedProbeGates;
        _appliedProbeGates = NoProbeGatesApplied;
        return applied;
    }

    int _probeExactSize = NoProbeGatesApplied;

    /// <summary>Whether rendered frames had active TAA and exactly equal internal and display sizes, 0 or 1.
    /// Negative sentinels have the same meaning as the applied mask.</summary>
    public int ConsumeProbeExactSizeCondition()
    {
        int result = _probeExactSize;
        _probeExactSize = NoProbeGatesApplied;
        return result;
    }

    bool RigidWorldReuseOn => (ProbeShaderGates & ShaderGateProbe.RigidWorldReuse) != 0;

    ShaderGateProbe ApplyProbeShaderGates(int displayWidth, int displayHeight)
    {
        int exact = _resolveThisRender && _currentFrameView.Width == displayWidth && _currentFrameView.Height == displayHeight ? 1 : 0;
        _probeExactSize = _probeExactSize == NoProbeGatesApplied || _probeExactSize == exact ? exact : MixedProbeGatesApplied;
        int gates = (int)ProbeShaderGates;
        _appliedProbeGates = _appliedProbeGates == NoProbeGatesApplied || _appliedProbeGates == gates ? gates : MixedProbeGatesApplied;
        return ProbeShaderGates;
    }

    // What UploadInstancesRelative sent this frame, which runs before PrepareMotionFrame.
    ReadOnlySpan<ModelRenderer.InstanceData> UploadedInstances =>
        CollectionsMarshal.AsSpan(_frameOriginActive ? _instanceDataRelative : _instanceData);

    /// <summary>This frame's motion slots. For tests.</summary>
    internal ReadOnlySpan<float> MotionSlotsForTests => _motionSlots.AsSpan(0, _instanceData.Count);

    /// <summary>The hardware depth and stencil attachment for the probe's native pixel identity test.</summary>
    internal IGpuTexture ProbeDepthStencilForTests => _res.DepthStencil;

    /// <summary>The motion attachment, or null with temporal rendering off. For tests.</summary>
    internal IGpuTexture? MotionTargetForTests => _res.MotionTex;
}
