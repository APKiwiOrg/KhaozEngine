using System;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    bool _coverageMaskThisRender;
    /// <summary>Runs once-only producers and reserves coverage temporal resources for this display size before
    /// opening the frame command list. Call again before recording after a size or temporal sizing change.</summary>
    public void PrepareFrame(int viewportWidth, int viewportHeight)
    {
        PrepareFrame();
        if (_coverageTransition is null || viewportWidth <= 0 || viewportHeight <= 0) return;
        var (width, height) = ComputeTemporalTargetSize(Post, viewportWidth, viewportHeight);
        _coverageTransition.EnsureSize(width, height, _retired);
        // The resolve's ordinary zero-mask binding also exists before a possible late AA activation.
        _temporalResolve ??= new Rendering.TemporalResolveRenderer(_gd);
    }

    // Temporal can be activated after preparation. Derive its prospective size without changing public settings.
    internal static (int W, int H) ComputeTemporalTargetSize(PixelPostProcessSettings post, int width, int height)
    {
        float scale = post.Temporal.ResolvedUpscaleRatio;
        int w = Math.Max(1, (int)MathF.Round(Math.Max(1, width) * scale));
        int h = Math.Max(1, (int)MathF.Round(Math.Max(1, height) * scale));
        int maxW = Math.Max(1, post.MaxRenderWidth), maxH = Math.Max(1, post.MaxRenderHeight);
        if (w <= maxW && h <= maxH) return (w, h);
        float cap = ViewportMath.Fit(w, h, maxW, maxH);
        return (Math.Max(1, (int)MathF.Round(w * cap)), Math.Max(1, (int)MathF.Round(h * cap)));
    }

    void RequireCoverageTemporalPreparation(int viewportWidth, int viewportHeight)
    {
        bool first = !_frameViewLatchedThisFrame;
        _coverageMaskThisRender = first && TemporalResolveActive && viewportWidth > 0 && viewportHeight > 0
            && _coverageArena.Preparations.Count + _coverageMotion.PreviousSurfaces.Length > 0;
        if (!_coverageMaskThisRender) return;
        var (width, height) = ComputeTemporalTargetSize(Post, viewportWidth, viewportHeight);
        if (_coverageTransition?.Matches(width, height) != true)
            throw new InvalidOperationException("Covered temporal rendering requires PrepareFrame(viewportWidth, viewportHeight) before opening the command list.");
    }

    internal CoverageTransitionMaskState CoverageTransitionForTests => new(_coverageTransition?.Texture,
        _coverageTransition is { } mask ? mask.Rectangles.ToArray() : []);
    internal readonly record struct CoverageTransitionMaskState(IGpuTexture? Texture,
        Rendering.CoverageTransitionMask.Rectangle[] Rectangles);
}
