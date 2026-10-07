using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

public static partial class CharacterMovement
{
    // The represented query band is slightly inside the exact 0.1 mm limit. It never widens contact.
    private const float LowPropFeatureContactBand = 0.0001f;

    private static bool TryLowPropFeatureSupport(IPhysicsWorld world, in CapsuleShape capsule,
        in Vector3 pos, in Vector3 expectedOrigin, float probeStart, float maxProbe, StaticHandle target,
        float cosMaxSlope, out float centreY)
    {
        centreY = 0;
        if (world is not IPhysicsCapsuleFeatures features || world is not IPhysicsQueryLeaseSource source)
            return false;
        IPhysicsQueryLease lease;
        try { lease = source.AcquireQueryReadLease(); }
        catch (InvalidOperationException error) when (error is not ObjectDisposedException)
        {
            // A caller may already own a non-nestable read interval. This optional path cannot
            // borrow it, and declines without masking query, authentication or disposal failures.
            return false;
        }
        using (lease)
        {
            lease.AssertCurrent();
            if (lease.Origin != expectedOrigin || world.Origin != expectedOrigin) return false;
            // The first sweep is a hint. Repeat it inside this interval so candidate generation
            // and finite-feature consumption share the same selected receiver and physical state.
            if (!world.SweepCapsule(capsule, Pose.At(new Vector3(pos.X, probeStart, pos.Z)),
                    -Vector3.UnitY, maxProbe, out SweepHit current) || current.Body != target ||
                !(current.Normal.Y >= cosMaxSlope) || !UnderFootprint(current.Point, pos, capsule.Radius))
                return false;
            float candidateY = probeStart - current.Distance;
            Pose candidate = Pose.At(new Vector3(pos.X, candidateY, pos.Z));
            Span<CapsuleIncidentFace> faces = stackalloc CapsuleIncidentFace[256];
            CapsuleFeatureResult result = features.QueryCapsuleFeature(lease, target, capsule, candidate,
                LowPropFeatureContactBand, faces, QueryFilter.StaticsOnly);
            if (result.Status != CapsuleFeatureStatus.Complete || result.Written < 1 || result.Written > faces.Length ||
                !LowPropFeatureEligible(features, lease, result, faces[..result.Written], cosMaxSlope) ||
                !FeatureFootprintFits(result, pos, capsule.Radius)) return false;
            centreY = candidateY;
            return true;
        }
    }

    private static bool FeatureFootprintFits(in CapsuleFeatureResult result, in Vector3 center, float radius)
    {
        // A Euclidean position error bounds both horizontal components. Bound the complete
        // witness rectangle outward instead of accepting only its rounded representative.
        double x = Math.BitIncrement(Math.Abs((double)result.GeometryPoint.X - center.X));
        double z = Math.BitIncrement(Math.Abs((double)result.GeometryPoint.Z - center.Z));
        x = Math.BitIncrement(x + result.PositionErrorMetres);
        z = Math.BitIncrement(z + result.PositionErrorMetres);
        double squared = Math.BitIncrement(Math.BitIncrement(x * x) + Math.BitIncrement(z * z));
        // Squaring a finite binary32 radius is exact in binary64.
        return double.IsFinite(squared) && squared <= (double)radius * radius;
    }

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
