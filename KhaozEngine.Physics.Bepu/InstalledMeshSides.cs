using System.Numerics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Strict forward/operational half-space agreement for a whole upright axis.
/// This is not a contact solver or a ray proxy. Grazing, crossing and uncertain neighborhoods refuse.</summary>
internal static class InstalledMeshSides
{
    internal static bool Agree(InstalledPoseOperator transform, Vector3 a, Vector3 b, Vector3 c,
        FeaturePoint origin, FeaturePoint normal, FeaturePoint lower, FeaturePoint upper,
        out GeometrySign lowSide, out GeometrySign highSide)
    {
        lowSide = FeaturePoint.Dot(normal, FeaturePoint.Subtract(lower, origin)).Sign;
        highSide = FeaturePoint.Dot(normal, FeaturePoint.Subtract(upper, origin)).Sign;
        // Preserve the already proved exact-rigid subset and its exact zero incident-wall signs.
        if (transform.IsExactlyRigid) return true;
        if (!Strict(lowSide) || !Strict(highSide) || lowSide != highSide) return false;
        GeometryVector rawA = FeaturePoint.Exact(a).Bounds;
        GeometryVector rawB = FeaturePoint.Exact(b).Bounds, rawC = FeaturePoint.Exact(c).Bounds;
        GeometryVector ab = InstalledPoseOperations.Subtract(rawB, rawA);
        GeometryVector ac = InstalledPoseOperations.Subtract(rawC, rawA);
        GeometryVector localNormal = InstalledPoseOperations.Cross(ac, ab);
        // Triangle.cs:57-68 and Mesh.cs:197-228 use the represented transpose as their local map.
        // Enclose the frame subtraction, local transform, raw-A subtraction and plane dot, with
        // operation rounding. This includes its systematic noninverse departure, not just ulps.
        GeometryVector rayLow = transform.RoundedTranspose(InstalledPoseOperations.Subtract(lower.Bounds, transform.Translation.Bounds));
        GeometryVector rayHigh = transform.RoundedTranspose(InstalledPoseOperations.Subtract(upper.Bounds, transform.Translation.Bounds));
        if (Sign(InstalledPoseOperations.Dot(localNormal, InstalledPoseOperations.Subtract(rayLow, rawA))) != lowSide ||
            Sign(InstalledPoseOperations.Dot(localNormal, InstalledPoseOperations.Subtract(rayHigh, rawA))) != highSide) return false;

        // CapsuleTriangleTester.cs:109-122,139-147 centers the triangle using rounded sums and
        // the represented 1/3 constant. Its localOffsetA is -(Ahat^T*offsetB+triangleCenter).
        // Endpoint evaluation additionally includes the rounded transformed upright axis.
        GeometryVector triangleCenter = InstalledPoseOperations.Scale(
            InstalledPoseOperations.Add(rawC, InstalledPoseOperations.Add(rawA, rawB)), GeometryInterval.Exact(1f / 3f));
        FeaturePoint center = FeaturePoint.Scale(FeaturePoint.Add(lower, upper), FeatureNumber.Exact(0.5));
        FeatureNumber halfLength = upper.Y.Subtract(lower.Y).Multiply(FeatureNumber.Exact(0.5));
        if (!center.IsResolved || !halfLength.IsResolved) return false;
        GeometryVector offsetB = InstalledPoseOperations.Subtract(transform.Translation.Bounds, center.Bounds);
        GeometryVector localCenter = InstalledPoseOperations.Negate(InstalledPoseOperations.Add(
            transform.RoundedTranspose(offsetB), triangleCenter));
        GeometryVector localAxis = transform.RoundedTranspose(FeaturePoint.Exact(Vector3.UnitY).Bounds);
        GeometryVector halfAxis = InstalledPoseOperations.Scale(localAxis, halfLength.Bounds);
        GeometryVector centeredLow = InstalledPoseOperations.Subtract(localCenter, halfAxis);
        GeometryVector centeredHigh = InstalledPoseOperations.Add(localCenter, halfAxis);
        GeometryVector contactNormal = InstalledPoseOperations.Normalize(localNormal);
        if (Sign(InstalledPoseOperations.Dot(contactNormal, centeredLow)) != lowSide ||
            Sign(InstalledPoseOperations.Dot(contactNormal, centeredHigh)) != highSide) return false;
        // Also bound the pinned center/half-extent expressions themselves. Their minimum and
        // maximum side values cover the entire local axis. No backend backface threshold is used.
        GeometryInterval centerSide = InstalledPoseOperations.Dot(contactNormal, localCenter);
        GeometryInterval axisSide = InstalledPoseOperations.Dot(contactNormal, localAxis);
        GeometryInterval extent = InstalledPoseOperations.Multiply(halfLength.Bounds, InstalledPoseOperations.Abs(axisSide));
        return Sign(InstalledPoseOperations.Subtract(centerSide, extent)) == lowSide &&
            Sign(InstalledPoseOperations.Add(centerSide, extent)) == highSide;
    }

    static bool Strict(GeometrySign sign) => sign is GeometrySign.Positive or GeometrySign.Negative;
    static GeometrySign Sign(GeometryInterval value) => !value.IsResolved ? GeometrySign.Unresolved
        : value.Lower > 0 ? GeometrySign.Positive : value.Upper < 0 ? GeometrySign.Negative : GeometrySign.Unresolved;
}
