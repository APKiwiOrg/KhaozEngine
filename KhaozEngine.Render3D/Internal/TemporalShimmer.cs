namespace KhaozEngine.Render3D.Internal;

/// <summary>
/// The C# mirror of the resolve's shimmer record (<see cref="ShaderSources.TemporalShimmerGlsl"/>), a per-pixel
/// exponential average of this frame's luma less its reprojected history (drift) and of its size (swing).
/// </summary>
internal static class TemporalShimmer
{
    /// <summary>The rate drift and swing average at for a display <paramref name="displayOverInternal"/> times the
    /// internal size per axis: one over the jitter's phase count (<see cref="TemporalJitter.PhaseCount"/>), so the
    /// record spans about one jitter cycle at every preset. The shader reads the ratio from the resolve uniforms'
    /// <c>Jitter.z</c>.</summary>
    public static float Rate(float displayOverInternal) => 1f / TemporalJitter.PhaseCount(displayOverInternal);
}
