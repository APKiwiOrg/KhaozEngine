using System;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    ShadowLayoutRequest? _pendingShadowLayout;
    bool _shadowReconfigureDisposed;
    readonly ILogger _shadowReconfigureLogger;

    /// <summary>Request one of the standard shadow-map layouts. The latest request is applied when the next
    /// <see cref="Begin"/> call starts a scene frame.</summary>
    public void RequestShadowMapDetail(ShadowMapDetail detail)
    {
        ThrowIfShadowReconfigureDisposed();
        if (detail is < ShadowMapDetail.Low or > ShadowMapDetail.High)
            throw new ArgumentOutOfRangeException(nameof(detail), detail, "Unsupported shadow-map detail.");

        ShadowSettings requested = ShadowSettings.ForDetail(detail);
        RequestShadowMapLayout(requested.ShadowMapResolution, requested.ShadowCascadeCount);
    }

    /// <summary>Request a shadow-map resolution and cascade count. The latest valid request is applied when the
    /// next <see cref="Begin"/> call starts a scene frame.</summary>
    public void RequestShadowMapLayout(int resolution, int cascadeCount)
    {
        ThrowIfShadowReconfigureDisposed();
        ShadowSettings.ValidateShadowMapResolution(resolution);
        ShadowSettings.ValidateShadowCascadeCount(cascadeCount);
        _pendingShadowLayout = new ShadowLayoutRequest(resolution, cascadeCount);
    }

    void ApplyPendingShadowLayout()
    {
        ShadowLayoutRequest? captured = _pendingShadowLayout;
        if (captured is null) return;

        _pendingShadowLayout = null;
        ShadowLayoutReplacementResult result = ReplaceShadowLayoutWithResult(
            captured.Value.Resolution, captured.Value.CascadeCount);
        if (result == ShadowLayoutReplacementResult.Failed)
        {
            ShadowSettings retained = Post.Quality.Shadows;
            _shadowReconfigureLogger.Error(
                $"Shadow-map layout replacement to {captured.Value.Resolution} with {captured.Value.CascadeCount} " +
                $"cascades failed. Retaining {retained.ShadowMapResolution} with {retained.ShadowCascadeCount} cascades.");
        }
    }

    void DisposeShadowReconfiguration()
    {
        _pendingShadowLayout = null;
        _shadowReconfigureDisposed = true;
    }

    void ThrowIfShadowReconfigureDisposed() =>
        ObjectDisposedException.ThrowIf(_shadowReconfigureDisposed, this);

    readonly struct ShadowLayoutRequest
    {
        internal ShadowLayoutRequest(int resolution, int cascadeCount)
        {
            Resolution = resolution;
            CascadeCount = cascadeCount;
        }

        internal int Resolution { get; }
        internal int CascadeCount { get; }
    }
}
