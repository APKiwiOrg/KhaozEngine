using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    /// <summary>Start a frame: apply pending scene changes, latch <see cref="RenderOrigin"/>, take the next frame index, and clear the per-frame draw queues.</summary>
    public void Begin()
    {
        _profiling?.Reset();
        using var phase = ProfileCpu("device.scene.begin");
        BeginCore();
    }

    /// <summary>Start a scene frame in the host's prepare phase and share its completion fence for retirement.
    /// Submit the host before the next scene boundary. This avoids the standalone queue's empty submission.</summary>
    public void BeginFrame(GpuFrameSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        _profiling?.Reset();
        using var phase = ProfileCpu("device.scene.begin");
        BeginCore(submission);
    }

    /// <summary>Start a frame: apply pending scene changes, latch <see cref="RenderOrigin"/>, take the next frame index, and clear the per-frame draw queues.</summary>
    void BeginCore(GpuFrameSubmission? submission = null)
    {
        if (submission is not null) _retired.BeginFrame(submission);
        ApplyPendingShadowLayout();
        ApplyPendingPointShadowLayout();
        if (submission is null) _retired.BeginFrame();
        LatchRenderOrigin();
        BeginFrameView();   // one frame index per Begin (Scene3D.FrameView.cs)
        BeginRigidInstances();
        BeginFoliageFrame();
        _skinnedInstances.Begin();
        _boneMatrices.Clear();
        BeginMotionFrame();                // swap or forget per-key motion history (Scene3D.MotionKeys.cs)
        _lights.Clear();
        _lineVerts.Clear();
        _depthLineVerts.Clear();
        _fillVerts.Clear();
        _decals.Clear();
        _shadowBlobs.Clear();
        _waterPlanes.Clear();
        _overlayMeshDraws.Clear();
        _silhouetteDraws.Clear();
        BeginMeshOutlineFrame();
        _billboardAlphaItems.Clear();
        _billboardAlpha.Clear();
        _billboardAdditive.Clear();
        _particleSprites.Clear();
        _particleSorted.Clear();
        _distortionSprites.Clear();
        _texBillboardItems.Clear();
        _beamItems.Clear();
        _trailItems.Clear();
        _trailSamples.Clear();
        _billboardBasisValid = false; _framePrepared = false;   // per-frame latches (see PrepareFrame)
    }
}
