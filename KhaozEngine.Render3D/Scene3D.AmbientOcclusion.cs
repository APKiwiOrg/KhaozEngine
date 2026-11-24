using System;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    bool _ambientOcclusionPrepared;
    (int Width, int Height, int ColorAttachments)? _failedAoLayout;

    /// <summary>The effective AO path last prepared by this scene.</summary>
    public AmbientOcclusionStatus EffectiveAmbientOcclusion { get; private set; }

    /// <summary>The additional texture payload for the effective AO path, excluding driver overhead.</summary>
    public long AmbientOcclusionTextureBytes => _res.AmbientOcclusion?.TextureBytes ?? 0;

    /// <summary>The error from the last refused AO resource request, if any.</summary>
    public Exception? AmbientOcclusionFailure { get; private set; }

    /// <summary>The error from the last render's refused colour grade, if any.</summary>
    public Exception? ColorGradeFailure =>
        (_frameResolves && !_resolveThisRender ? _laterRenderPost : _post)?.ColorGradeFailure;

    AmbientOcclusionStatus RequestedAmbientOcclusion()
    {
        if (!Post.AmbientOcclusion.Enabled || Post.AmbientOcclusion.Strength == 0f)
            return AmbientOcclusionStatus.Disabled;
        if (!_res.HdrColor) return AmbientOcclusionStatus.RequiresHdr;
        if (_res.Msaa) return AmbientOcclusionStatus.RequiresSingleSample;
        if (_res.ModelFB.Outputs.Colour.Length >= _gd.Capabilities.MaxColorAttachments)
            return AmbientOcclusionStatus.InsufficientColorAttachments;
        return AmbientOcclusionStatus.Active;
    }

    void ConfigureAmbientOcclusion(bool modelAlreadyExtended)
    {
        EffectiveAmbientOcclusion = RequestedAmbientOcclusion();
        var layout = (_res.Width, _res.Height, _res.ModelFB.Outputs.Colour.Length);
        if (EffectiveAmbientOcclusion != AmbientOcclusionStatus.Active || _failedAoLayout != layout)
        {
            _failedAoLayout = null;
            AmbientOcclusionFailure = null;
        }
        if (EffectiveAmbientOcclusion != AmbientOcclusionStatus.Active)
        {
            _res.ReleaseAmbientOcclusion(_retired);
            return;
        }
        if (_res.AmbientOcclusion is not null) return;
        if (_failedAoLayout == layout)
        {
            EffectiveAmbientOcclusion = AmbientOcclusionStatus.ResourceUnavailable;
            return;
        }

        OpaqueAmbientOcclusion? candidate = null;
        try
        {
            candidate = new OpaqueAmbientOcclusion(_gd, _res);
            _decalRenderer.EnsureAmbientBlobPipeline(candidate.BlobFramebuffer.Outputs);
            if (!modelAlreadyExtended) _model.EnableAmbientOutput(candidate.ModelFramebuffer.Outputs);
            _res.AmbientOcclusion = candidate;
        }
        catch (Exception failure)
        {
            candidate?.Dispose();
            _failedAoLayout = layout;
            AmbientOcclusionFailure = failure;
            EffectiveAmbientOcclusion = AmbientOcclusionStatus.ResourceUnavailable;
            Log.For<Scene3D>().Error($"Could not prepare ambient occlusion. {failure.Message}");
        }
    }

    void PrepareAmbientOcclusion(IGpuCommandList cl) =>
        _ambientOcclusionPrepared = _res.AmbientOcclusion?.Prepare(cl, _currentFrameView,
            Post.AmbientOcclusion, Post.BackgroundColor.R) == true;

    void ApplyAmbientOcclusion(IGpuCommandList cl)
    {
        if (_res.AmbientOcclusion is not { } ao) return;
        if (_ambientOcclusionPrepared)
        {
            using (ProfilePass(cl, "opaque.ao-horizon")) ao.DrawHorizon(cl);
            using (ProfilePass(cl, "opaque.ao-composite")) ao.DrawComposite(cl, _res.ColorDepthFB);
            _frameStats.DrawCalls += 2;
        }
        // All later model writers use the unextended framebuffer and keep their original pipelines.
        cl.SetFramebuffer(_res.ModelFB);
    }
}
