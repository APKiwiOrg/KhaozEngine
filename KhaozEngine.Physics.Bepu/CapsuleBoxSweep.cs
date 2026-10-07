using System;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Composes one live identity-box leaf's closed-path proof and distance encoding.
/// The caller retains the owner's read gate. This does not certify other world candidates.</summary>
internal static class CapsuleBoxSweep
{
    internal static CapsuleSweepResult Sweep(Shapes shapes, TypedIndex shape, RigidPose pose,
        Vector3 centre, float radius, float halfCylinderLength, Vector3 displacement,
        float maximumErrorMetres, int maximumCells)
    {
        if (!float.IsFinite(maximumErrorMetres) || maximumErrorMetres < 0) return default;
        double estimatedLength = Math.Sqrt((double)displacement.X * displacement.X +
            (double)displacement.Y * displacement.Y + (double)displacement.Z * displacement.Z);
        float representedLength = (float)estimatedLength;
        if (!float.IsFinite(representedLength)) return default;
        // This estimate only chooses refinement work. The final Hit encoder independently proves
        // its mathematical norm bounds, error budget and request extent before any result is returned.
        double fractionWidth = estimatedLength > 0 && maximumErrorMetres > 0
            ? Math.Min(1d, maximumErrorMetres / (4d * estimatedLength)) : 1d;
        if (!CapsuleBoxFractionSweep.TrySweep(shapes, shape, pose, centre, radius, halfCylinderLength,
            displacement, fractionWidth, maximumCells, out double lower, out double? upper)) return default;
        if (upper is double impact)
            return CapsuleSweepDistanceEncoding.Hit(displacement, lower, impact, maximumErrorMetres);
        // Clear describes the whole original vector. The float field is its required representation,
        // not a different endpoint or a shorter displacement that a movement consumer may substitute.
        return new(CapsuleSweepStatus.Clear, representedLength, null, 0f);
    }
}
