using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>One mesh's immutable rank-sorted run in a spatial cell, with absolute world-space bounds.</summary>
internal readonly record struct FoliagePatch(MeshHandle Mesh, int Start, int Count, MeshBounds Bounds,
    Vector2 RootMin, Vector2 RootMax, float MaxHeight)
{
    /// <summary>Selects a conservative rank prefix in logarithmic time. Exact blade fading stays on the GPU.</summary>
    internal int CandidateCount(FoliageInstance[] ordered, Vector3 focus, in FoliageRenderSettings settings)
    {
        if (Count == 0 || settings.QualityDensity == 0f) return 0;
        float dx = MathF.Max(0f, MathF.Max(RootMin.X - focus.X, focus.X - RootMax.X));
        float dz = MathF.Max(0f, MathF.Max(RootMin.Y - focus.Z, focus.Z - RootMax.Y));
        float distance = MathF.Sqrt(dx * dx + dz * dz);
        if (distance > settings.DrawRadius) return 0;

        float densityRadius = MathF.Min(settings.DensityRadius ?? settings.DrawRadius, settings.DrawRadius);
        float band = MathF.Min(settings.FadeBandWidth, densityRadius);
        float inner = densityRadius - band;
        float distant = MathF.Min(settings.DistantDensity, settings.QualityDensity);
        int low = 0, high = Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            float rank = ordered[Start + middle].ThinningRank;
            bool keep = rank < settings.QualityDensity;
            if (keep && rank > distant && settings.QualityDensity > distant)
            {
                // Evaluate the same cutoff as the CPU ground-cover path. Inverting it into a rank threshold
                // can round an exact boundary the other way and incorrectly lose a visible placement.
                float progress = (settings.QualityDensity - rank) / (settings.QualityDensity - distant);
                keep = distance <= inner + progress * band;
            }
            // Keep equality with distant conservatively, including beyond DensityRadius.
            if (keep) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}
