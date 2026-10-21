using System;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain;

/// <summary>Stable model values shared by retained cover and native registration.</summary>
public readonly record struct GroundCoverStableModelPolicy(FoliageMotionProfile Motion, bool CastShadows);

public static class GroundCoverModelPolicy
{
    public static GroundCoverStableModelPolicy Resolve(GroundCoverRenderOptions options, string modelId)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        var motion = options.MotionProfiles.TryGetValue(modelId, out var profile) ? profile : FoliageMotionProfile.Blade;
        bool casts = options.ModelCastsShadows.TryGetValue(modelId, out bool value) ? value : options.CastsShadows;
        return new(motion, casts);
    }
}
