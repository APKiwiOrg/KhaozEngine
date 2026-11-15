using System;

namespace KhaozEngine.Terrain;

/// <summary>Distance coverage shared by retained clusters and native typed recording.</summary>
internal static class PropClusterCoverage
{
    public static PropClusterDrawState Resolve(float distance, PropLayer layer,
        long individualGeneration, long mergedGeneration)
    {
        if (distance >= layer.DrawRadius) return new(false, 0, false, 0, false, individualGeneration, mergedGeneration);
        float t = PropHlod.CrossfadeAt(distance, layer.HlodDistance, layer.HlodCrossfadeWidth);
        bool individuals = PropHlod.DrawsHlodProps(t);
        float start = MathF.Max(0, layer.DrawRadius - layer.FadeBandWidth);
        if (layer.FadeBandWidth > 0 && distance >= start)
        {
            float exit = Math.Clamp((distance - start) / (layer.DrawRadius - start), 0, 1);
            float handoffStart = layer.HlodDistance - layer.HlodCrossfadeWidth * .5f;
            float individualSkip = handoffStart + layer.HlodCrossfadeWidth * PropHlod.HlodPropsSkipAt;
            if (layer.HlodCrossfadeWidth > 0 && start < individualSkip && distance >= handoffStart)
            {
                bool mergedVisible = PropHlod.DrawsHlodMerged(t);
                // The complement keeps [0, merged), and individuals keep [exit + merged, 1).
                // Reserve exactly one exit-sized gap while preserving each half's share of the survivors.
                // Keep the merged phase after individuals are skipped, through t = 1 and the draw radius.
                float merged = mergedVisible ? t * (1f - exit) : 0f;
                return new(individuals, exit + merged, mergedVisible, merged, mergedVisible,
                    individualGeneration, mergedGeneration);
            }
            // An exit starting after the individual gate follows the solid merge's ordinary phase.
            // The exit cannot enable a merged draw before the crossfade or hard swap admits it (#1807).
            return new(individuals, t, PropHlod.DrawsHlodMerged(t), exit, false,
                individualGeneration, mergedGeneration);
        }
        bool drawsMerged = PropHlod.DrawsHlodMerged(t);
        // Only dissolve while both halves are submitted. A lone representation stays solid.
        bool complement = individuals && drawsMerged;
        float handoffDissolve = complement ? t : 0f;
        return new(individuals, handoffDissolve, drawsMerged, handoffDissolve, complement,
            individualGeneration, mergedGeneration);
    }
}
