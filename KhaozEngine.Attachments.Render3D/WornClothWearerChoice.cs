using System;
using System.Numerics;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>A visible candidate for the single cloth reservation, with an ID stable across frames.</summary>
public readonly record struct WornClothCandidate(ulong WearerId, Vector3 Position, bool Visible, bool Preferred = false);

/// <summary>Distance selection for a caller sharing a single local cloth slot.</summary>
public static class WornClothWearerChoice
{
    /// <summary>Choose a visible preferred wearer, then retain a visible incumbent within the exit radius,
    /// or choose the nearest new wearer within the enter radius. Equal distances prefer the lower ID.</summary>
    public static ulong? Select(ReadOnlySpan<WornClothCandidate> candidates, Vector3 viewer,
        float enterRadius, float exitRadius, ulong? incumbent)
    {
        if (!ChainMath.IsFinite(viewer))
            throw new ArgumentException("Viewer position must be finite.", nameof(viewer));
        if (!float.IsFinite(enterRadius) || enterRadius < 0)
            throw new ArgumentOutOfRangeException(nameof(enterRadius));
        if (!float.IsFinite(exitRadius) || exitRadius < enterRadius)
            throw new ArgumentOutOfRangeException(nameof(exitRadius));

        ulong? preferred = null, nearest = null, retained = null;
        double enterSquared = (double)enterRadius * enterRadius;
        double exitSquared = (double)exitRadius * exitRadius;
        double preferredDistance = enterSquared, nearestDistance = enterSquared;
        foreach (ref readonly var candidate in candidates)
        {
            if (!candidate.Visible) continue;
            double distance = ChainMath.DistanceSquared(candidate.Position, viewer);
            if (distance <= exitSquared && candidate.WearerId == incumbent)
                retained = incumbent;
            if (candidate.Preferred && distance <= enterSquared)
                Pick(candidate.WearerId, distance, ref preferred, ref preferredDistance);
            if (distance <= enterSquared)
                Pick(candidate.WearerId, distance, ref nearest, ref nearestDistance);
        }
        return preferred ?? retained ?? nearest;
    }

    private static void Pick(ulong candidate, double distance, ref ulong? best, ref double bestDistance)
    {
        if (distance < bestDistance || distance == bestDistance && (best is null || candidate < best))
            (best, bestDistance) = (candidate, distance);
    }
}
