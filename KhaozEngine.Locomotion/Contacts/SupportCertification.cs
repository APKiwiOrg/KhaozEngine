using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>How one certified finite feature may support a body.</summary>
internal enum CertifiedSupportKind : byte { Refused, Walkable, Steep }

/// <summary>One feature's support at the body axis. <see cref="Lower"/> and <see cref="Upper"/> enclose the
/// contribution height, the minimum of the faces' planes at the axis and the witness height. A
/// <see cref="CertifiedSupportKind.Walkable"/> contribution takes that minimum over its walkable faces. A
/// <see cref="CertifiedSupportKind.Steep"/> one has no walkable face and takes it over every face with a
/// positive upward bound. <see cref="Normal"/> is the face that wins the minimum. A refusal carries no usable
/// values.</summary>
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

        int walkable = 0;
        PlaneMinimum walkablePlanes = PlaneMinimum.Empty, risingPlanes = PlaneMinimum.Empty;
        for (int i = 0; i < faces.Length; i++)
        {
            CapsuleIncidentFace face = faces[i];
            if (face.Incidence != result.Kind || !ErrorWithin(face.NormalError, 100000)) return Refusal;
            double upward = LowerBound(face.Normal.Y, face.NormalError);
            // A face that may lean back over the witness cannot be part of an upward support neighborhood.
            if (!(upward >= 0)) return Refusal;
            bool isWalkable = upward >= cosMaxSlope;
            if (isWalkable) walkable++;
            // A face whose upward bound is zero has no bounded plane at the axis. Only a walkable one refuses.
            if (!(upward > 0))
            {
                if (isWalkable) return Refusal;
                continue;
            }
            if (!PlaneAtAxis(result, face, upward, axis, out double faceLower, out double faceUpper))
                return Refusal;
            risingPlanes.Offer(i, faceLower, faceUpper);
            if (isWalkable) walkablePlanes.Offer(i, faceLower, faceUpper);
        }

        CertifiedSupportKind kind;
        PlaneMinimum planes;
        if (walkable > 0)
        {
            // Raw coplanar triangles of one face or open-boundary patch must agree. A convex crease is
            // supported by any walkable side because its other sides fall away below the shared edge.
            if (result.Kind != CapsuleFeatureKind.ConvexCrease && walkable != faces.Length) return Refusal;
            (kind, planes) = (CertifiedSupportKind.Walkable, walkablePlanes);
        }
        else if (risingPlanes.Winner >= 0)
            (kind, planes) = (CertifiedSupportKind.Steep, risingPlanes);
        else
            return Refusal;
        double lower = Math.Min(planes.Lower, LowerBound(result.GeometryPoint.Y, result.PositionErrorMetres));
        double upper = Math.Min(planes.Upper, UpperBound(result.GeometryPoint.Y, result.PositionErrorMetres));
        if (!double.IsFinite(lower) || !double.IsFinite(upper) || lower > upper) return Refusal;
        return new(kind, lower, upper, faces[planes.Winner].Normal, result.GeometryPoint, result.FeatureId);
    }

    // The running minimum of face plane enclosures at the axis. The face with the lowest enclosure wins. Equal
    // lower bounds prefer the lower upper bound.
    struct PlaneMinimum
    {
        internal double Lower, Upper;
        internal int Winner;
        double _winnerLower, _winnerUpper;

        internal static PlaneMinimum Empty => new()
        {
            Lower = double.PositiveInfinity,
            Upper = double.PositiveInfinity,
            Winner = -1,
            _winnerLower = double.PositiveInfinity,
            _winnerUpper = double.PositiveInfinity,
        };

        internal void Offer(int face, double lower, double upper)
        {
            if (lower < _winnerLower || (lower == _winnerLower && upper < _winnerUpper))
                (Winner, _winnerLower, _winnerUpper) = (face, lower, upper);
            Lower = Math.Min(Lower, lower);
            Upper = Math.Min(Upper, upper);
        }
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
