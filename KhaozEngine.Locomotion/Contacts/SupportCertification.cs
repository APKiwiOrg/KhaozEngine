using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>How one certified finite feature may support a body.</summary>
internal enum CertifiedSupportKind : byte { Refused, Walkable, Steep }

/// <summary>One feature's support at the body axis. <see cref="Lower"/> and <see cref="Upper"/> enclose the
/// contribution height. For <see cref="CertifiedSupportKind.Walkable"/>, <see cref="Normal"/> is the walkable
/// face that wins the minimum. For <see cref="CertifiedSupportKind.Steep"/>, the bounds enclose the witness
/// height and <see cref="Normal"/> is the least steep incident face. A refusal carries no usable values.</summary>
internal readonly record struct SupportContribution(CertifiedSupportKind Kind, double Lower, double Upper,
    Vector3 Normal, Vector3 Witness, int FeatureId);

/// <summary>Turns a complete capsule-feature result into a certified support contribution at an axis.</summary>
internal static class SupportCertification
{
    // The represented query band is slightly inside the exact 0.1 mm limit. It never widens contact.
    internal const float ContactBand = 0.0001f;

    static readonly SupportContribution Refusal = default;

    /// <summary>Certifies <paramref name="result"/> under its original <paramref name="lease"/> and receiver.
    /// An expired, foreign or wrong-receiver result throws rather than refusing.</summary>
    internal static SupportContribution Certify(IPhysicsCapsuleFeatures capability, IPhysicsQueryLease lease,
        in CapsuleFeatureResult result, ReadOnlySpan<CapsuleIncidentFace> faces, Vector2 axis, float cosMaxSlope)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(lease);
        if (result.Status != CapsuleFeatureStatus.Complete) return Refusal;
        capability.AssertFeatureCurrent(result, lease);
        if (!Admissible(result, faces, axis, cosMaxSlope)) return Refusal;

        int walkable = 0, winner = -1, leastSteep = -1;
        double lower = double.PositiveInfinity, upper = double.PositiveInfinity, leastSteepY = double.NegativeInfinity;
        double winnerLower = double.PositiveInfinity, winnerUpper = double.PositiveInfinity;
        for (int i = 0; i < faces.Length; i++)
        {
            CapsuleIncidentFace face = faces[i];
            if (face.Incidence != result.Kind || !ErrorWithin(face.NormalError, 100000)) return Refusal;
            double upward = LowerBound(face.Normal.Y, face.NormalError);
            // A face that may lean back over the witness cannot be part of an upward support neighborhood.
            if (!(upward >= 0)) return Refusal;
            if (upward > leastSteepY) { leastSteepY = upward; leastSteep = i; }
            if (upward < cosMaxSlope) continue;
            walkable++;
            if (!PlaneAtAxis(result, face, upward, axis, out double faceLower, out double faceUpper))
                return Refusal;
            // The face with the lowest enclosure wins. Equal lower bounds prefer the lower upper bound.
            if (faceLower < winnerLower || (faceLower == winnerLower && faceUpper < winnerUpper))
                (winner, winnerLower, winnerUpper) = (i, faceLower, faceUpper);
            lower = Math.Min(lower, faceLower);
            upper = Math.Min(upper, faceUpper);
        }

        double witnessLower = LowerBound(result.GeometryPoint.Y, result.PositionErrorMetres);
        double witnessUpper = UpperBound(result.GeometryPoint.Y, result.PositionErrorMetres);
        if (walkable == 0)
            return new(CertifiedSupportKind.Steep, witnessLower, witnessUpper, faces[leastSteep].Normal,
                result.GeometryPoint, result.FeatureId);
        // Raw coplanar triangles of one face or open-boundary patch must agree. A convex crease is
        // supported by any walkable side because its other sides fall away below the shared edge.
        if (result.Kind != CapsuleFeatureKind.ConvexCrease && walkable != faces.Length) return Refusal;
        lower = Math.Min(lower, witnessLower);
        upper = Math.Min(upper, witnessUpper);
        if (!double.IsFinite(lower) || !double.IsFinite(upper) || lower > upper) return Refusal;
        return new(CertifiedSupportKind.Walkable, lower, upper, faces[winner].Normal, result.GeometryPoint,
            result.FeatureId);
    }

    static bool Admissible(in CapsuleFeatureResult result, ReadOnlySpan<CapsuleIncidentFace> faces, Vector2 axis,
        float cosMaxSlope)
    {
        if (faces.Length != result.Written || faces.IsEmpty || !float.IsFinite(cosMaxSlope) ||
            cosMaxSlope < 0 || cosMaxSlope > 1 || !float.IsFinite(axis.X) || !float.IsFinite(axis.Y) ||
            !ErrorWithin(result.PositionErrorMetres, 4000) ||
            !ErrorWithin(result.NormalError, 100000) ||
            !double.IsFinite(result.SeparationLower) || !double.IsFinite(result.SeparationUpper) ||
            result.SeparationLower > result.SeparationUpper ||
            result.SeparationLower < -(double)ContactBand ||
            result.SeparationUpper > ContactBand ||
            Math.BitIncrement(result.SeparationUpper - result.SeparationLower) > ContactBand)
            return false;
        if (result.Kind is not (CapsuleFeatureKind.FaceInterior or CapsuleFeatureKind.OpenBoundary or
            CapsuleFeatureKind.ConvexCrease)) return false;
        // For a proved closest upright segment/geometry pair, positive upward separation implies
        // the lower endpoint. A higher axis point could move downward and reduce that same distance.
        return LowerBound(result.SeparationNormal.Y, result.NormalError) > 0;
    }

    // Encloses w.y + (n.x (w.x - axis.X) + n.z (w.z - axis.Y)) / n.y over the witness box and the face
    // normal box. Every binary64 operation is rounded outward by one step.
    static bool PlaneAtAxis(in CapsuleFeatureResult result, in CapsuleIncidentFace face, double upwardLower,
        Vector2 axis, out double lower, out double upper)
    {
        lower = upper = 0;
        // The quotient needs a strictly positive divisor to stay bounded.
        if (!(upwardLower > 0)) return false;
        float position = result.PositionErrorMetres, normal = face.NormalError;
        (double Lo, double Hi) dx = Difference(result.GeometryPoint.X, position, axis.X);
        (double Lo, double Hi) dz = Difference(result.GeometryPoint.Z, position, axis.Y);
        (double Lo, double Hi) nx = (LowerBound(face.Normal.X, normal), UpperBound(face.Normal.X, normal));
        (double Lo, double Hi) nz = (LowerBound(face.Normal.Z, normal), UpperBound(face.Normal.Z, normal));
        (double Lo, double Hi) ny = (upwardLower, UpperBound(face.Normal.Y, normal));
        (double Lo, double Hi) px = Product(nx, dx), pz = Product(nz, dz);
        (double Lo, double Hi) rise = (Math.BitDecrement(px.Lo + pz.Lo), Math.BitIncrement(px.Hi + pz.Hi));
        (double Lo, double Hi) offset = Quotient(rise, ny);
        lower = Math.BitDecrement(LowerBound(result.GeometryPoint.Y, position) + offset.Lo);
        upper = Math.BitIncrement(UpperBound(result.GeometryPoint.Y, position) + offset.Hi);
        return double.IsFinite(lower) && double.IsFinite(upper) && lower <= upper;
    }

    static (double Lo, double Hi) Difference(float value, float error, float origin) =>
        (Math.BitDecrement(LowerBound(value, error) - origin), Math.BitIncrement(UpperBound(value, error) - origin));

    static (double Lo, double Hi) Product((double Lo, double Hi) a, (double Lo, double Hi) b)
    {
        double ll = a.Lo * b.Lo, lh = a.Lo * b.Hi, hl = a.Hi * b.Lo, hh = a.Hi * b.Hi;
        return (Math.BitDecrement(Math.Min(Math.Min(ll, lh), Math.Min(hl, hh))),
            Math.BitIncrement(Math.Max(Math.Max(ll, lh), Math.Max(hl, hh))));
    }

    // The divisor interval is strictly positive, so its endpoints bound the quotient.
    static (double Lo, double Hi) Quotient((double Lo, double Hi) a, (double Lo, double Hi) b)
    {
        double ll = a.Lo / b.Lo, lh = a.Lo / b.Hi, hl = a.Hi / b.Lo, hh = a.Hi / b.Hi;
        return (Math.BitDecrement(Math.Min(Math.Min(ll, lh), Math.Min(hl, hh))),
            Math.BitIncrement(Math.Max(Math.Max(ll, lh), Math.Max(hl, hh))));
    }

    static double LowerBound(float component, float error)
    {
        if (!float.IsFinite(component)) return double.NegativeInfinity;
        // Exact values retain exact zeros. Otherwise enclose subtraction downward, rather than
        // treating an uncertain bound as settled with an epsilon.
        return error == 0 ? component : Math.BitDecrement((double)component - error);
    }

    static double UpperBound(float component, float error)
    {
        if (!float.IsFinite(component)) return double.PositiveInfinity;
        return error == 0 ? component : Math.BitIncrement((double)component + error);
    }

    static bool ErrorWithin(float error, double denominator) =>
        // Both denominators are small positive integers. Multiplication of a binary32 value by
        // either needs at most 41 significand bits, so the binary64 comparison is exact.
        float.IsFinite(error) && error >= 0 && (double)error * denominator <= 1;
}
