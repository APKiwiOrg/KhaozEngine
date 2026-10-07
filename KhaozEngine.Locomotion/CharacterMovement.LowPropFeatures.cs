using System;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

public static partial class CharacterMovement
{
    // The represented query band is slightly inside the exact 0.1 mm limit. It never widens contact.
    private const float LowPropFeatureContactBand = 0.0001f;

    private static bool LowPropFeatureEligible(IPhysicsCapsuleFeatures capability, IPhysicsQueryLease lease,
        in CapsuleFeatureResult result, ReadOnlySpan<CapsuleIncidentFace> faces, float cosMaxSlope)
    {
        if (result.Status != CapsuleFeatureStatus.Complete) return false;
        capability.AssertFeatureCurrent(result, lease);
        if (faces.Length != result.Written || faces.IsEmpty || !float.IsFinite(cosMaxSlope) ||
            cosMaxSlope < 0 || cosMaxSlope > 1 ||
            !FeatureErrorWithin(result.PositionErrorMetres, 4000) ||
            !FeatureErrorWithin(result.NormalError, 100000) ||
            !double.IsFinite(result.SeparationLower) || !double.IsFinite(result.SeparationUpper) ||
            result.SeparationLower > result.SeparationUpper ||
            result.SeparationLower < -(double)LowPropFeatureContactBand ||
            result.SeparationUpper > LowPropFeatureContactBand ||
            Math.BitIncrement(result.SeparationUpper - result.SeparationLower) > LowPropFeatureContactBand)
            return false;
        if (result.Kind is not (CapsuleFeatureKind.FaceInterior or CapsuleFeatureKind.OpenBoundary or
            CapsuleFeatureKind.ConvexCrease)) return false;

        double upward = FeatureNormalLower(result.SeparationNormal.Y, result.NormalError);
        if (!(upward > 0) || upward < cosMaxSlope) return false;
        // For a proved closest upright segment/geometry pair, positive upward separation implies
        // the lower endpoint. A higher axis point could move downward and reduce that same distance.
        float supportingThreshold = MathF.Max(LipLandingFlatNormalY, cosMaxSlope);
        int supporting = 0;
        foreach (CapsuleIncidentFace face in faces)
        {
            if (face.Incidence != result.Kind || !FeatureErrorWithin(face.NormalError, 100000)) return false;
            double derivative = FeatureNormalLower(face.Normal.Y, face.NormalError);
            if (!(derivative >= 0)) return false;
            if (derivative >= supportingThreshold) supporting++;
        }
        // A complete convex crease supplies its actual incident planes and closest-pair normal
        // cone. Exactly one top may qualify. A face or open-boundary patch can retain several raw
        // coplanar triangles, which collectively remain one supporting patch.
        return result.Kind == CapsuleFeatureKind.ConvexCrease
            ? faces.Length == 2 && supporting == 1
            : supporting == faces.Length;
    }

    private static double FeatureNormalLower(float component, float error)
    {
        if (!float.IsFinite(component)) return double.NegativeInfinity;
        // Exact normals retain exact zero derivatives. Otherwise enclose subtraction downward,
        // rather than treating an uncertain vertical side as nonnegative with an epsilon.
        return error == 0 ? component : Math.BitDecrement((double)component - error);
    }

    private static bool FeatureErrorWithin(float error, double denominator) =>
        // Both denominators are small positive integers. Multiplication of a binary32 value by
        // either needs at most 41 significand bits, so the binary64 comparison is exact.
        float.IsFinite(error) && error >= 0 && (double)error * denominator <= 1;
}
