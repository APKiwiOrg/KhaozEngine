using System;

namespace KhaozEngine.Locomotion;

/// <summary>Answers the water boundary question from the capture's reach certificate when the answer cannot depend on
/// the water geometry. A true result means every Known full solve of the same question gives Known, no normals and a
/// default hit, so returning that answer changes no output bit wherever the full solve would not refuse. False runs
/// the full solve unchanged.
/// <list type="bullet">
/// <item>An empty reach holds for every policy. The trace can report no contact, so no constraint can arise.</item>
/// <item>One free level holds for a SurfaceSwimmer. Every contact is free surface and skipped, and every span carries
/// that one converted level, so the level rule cannot refuse.</item>
/// <item>One free level holds for a WadeOnly body whose lowest path centre is above the threshold at that level with
/// the largest error a Known trace admits. Each span start or end is no lower than that centre and each contact's
/// threshold no higher than that bound, in every rounding and flush mode, so every contact is skipped. The level is
/// the single counted level, never a maximum over different levels, so no monotonicity across levels is needed.</item>
/// </list></summary>
internal static class MovementWaterBoundaryCertificate
{
    internal static bool Answers(in MovementMediumSweepQuery query, float bodyHalfHeight, float enterFraction,
        WaterTraversalMode mode, MovementQueryLease queries, float proximity)
    {
        MovementWaterReach reach = queries.CertifyWaterBoundary(query);
        if (reach.Kind == MovementWaterReachKind.Empty) return true;
        if (reach.Kind != MovementWaterReachKind.OneFreeSurface) return false;
        return mode == WaterTraversalMode.SurfaceSwimmer || mode == WaterTraversalMode.WadeOnly &&
            WadeClear(reach.SurfaceY, bodyHalfHeight, enterFraction, proximity, query.Body.Centre.Y, query.Delta.Y);
    }

    /// <summary>Whether a WadeOnly path stays above its enter line over a free surface at <paramref name="surfaceY"/>.
    /// The comparison is strict and exact.</summary>
    internal static bool WadeClear(float surfaceY, float bodyHalfHeight, float enterFraction, float proximity,
        float centreY, float deltaY) => LowestCentreY(centreY, deltaY) >
        MovementWaterBoundary.WadeThreshold(surfaceY, bodyHalfHeight, enterFraction, MovementQueryLease.CoverageSkinMetres, proximity);

    /// <summary>A lower bound on the path centre's height at every fraction in [0, 1].</summary>
    internal static double LowestCentreY(float centreY, float deltaY) => centreY + Math.Min(0d, (double)deltaY);
}
