using System;
using System.Numerics;
using BepuPhysics;
using BepuUtilities;

namespace KhaozEngine.Physics.Bepu;

/// <summary>One common source-backed affine operator, not independent world-vertex boxes.
/// Coefficients contain both the real quaternion polynomial and the pinned scalar/SIMD maps.
/// A strictly positive determinant carries exact local topology. It grants no output error budget.</summary>
internal readonly struct InstalledPoseOperator
{
    internal FeaturePoint X { get; }
    internal FeaturePoint Y { get; }
    internal FeaturePoint Z { get; }
    internal FeaturePoint Translation { get; }
    internal FeaturePoint RepresentedX { get; }
    internal FeaturePoint RepresentedY { get; }
    internal FeaturePoint RepresentedZ { get; }
    internal FeatureNumber Determinant { get; }
    internal GeometryVector GramDiagonalDeparture { get; }
    internal GeometryVector GramOffDiagonal { get; }
    internal FeaturePoint InverseTransposeX { get; }
    internal FeaturePoint InverseTransposeY { get; }
    internal FeaturePoint InverseTransposeZ { get; }
    internal bool IsResolved { get; }
    internal bool IsExactlyRigid { get; }

    InstalledPoseOperator(FeaturePoint x, FeaturePoint y, FeaturePoint z, Vector3 translation,
        FeaturePoint representedX, FeaturePoint representedY, FeaturePoint representedZ)
    {
        this = default;
        X = x; Y = y; Z = z;
        Translation = FeaturePoint.Exact(translation);
        RepresentedX = representedX; RepresentedY = representedY; RepresentedZ = representedZ;
        FeaturePoint yz = FeaturePoint.Cross(y, z), zx = FeaturePoint.Cross(z, x), xy = FeaturePoint.Cross(x, y);
        Determinant = FeaturePoint.Dot(x, yz);
        if (Determinant.Sign != GeometrySign.Positive) return;
        FeatureNumber xx = FeaturePoint.Dot(x, x), yy = FeaturePoint.Dot(y, y), zz = FeaturePoint.Dot(z, z);
        FeatureNumber xdy = FeaturePoint.Dot(x, y), xdz = FeaturePoint.Dot(x, z), ydz = FeaturePoint.Dot(y, z);
        FeatureNumber one = FeatureNumber.Exact(1);
        GramDiagonalDeparture = new(xx.Subtract(one).Bounds, yy.Subtract(one).Bounds, zz.Subtract(one).Bounds);
        GramOffDiagonal = new(xdy.Bounds, xdz.Bounds, ydz.Bounds);
        FeatureNumber reciprocal = one.Divide(Determinant);
        InverseTransposeX = FeaturePoint.Scale(yz, reciprocal);
        InverseTransposeY = FeaturePoint.Scale(zx, reciprocal);
        InverseTransposeZ = FeaturePoint.Scale(xy, reciprocal);
        IsResolved = GramDiagonalDeparture.IsResolved && GramOffDiagonal.IsResolved &&
            InverseTransposeX.IsResolved && InverseTransposeY.IsResolved && InverseTransposeZ.IsResolved;
        IsExactlyRigid = IsResolved && EqualsExact(xx, 1) && EqualsExact(yy, 1) && EqualsExact(zz, 1) &&
            EqualsExact(xdy, 0) && EqualsExact(xdz, 0) && EqualsExact(ydz, 0) && EqualsExact(Determinant, 1);
    }

    internal static bool TryCreate(in RigidPose pose, out InstalledPoseOperator result)
    {
        result = default;
        if (!Within(pose.Position, RepresentedGeometryTransforms.MaximumPositionMagnitude) ||
            !RepresentedGeometryTransforms.QuaternionNormWithinBand(pose.Orientation)) return false;
        Quaternion q = pose.Orientation;
        RealCoefficients(q, out FeaturePoint realX, out FeaturePoint realY, out FeaturePoint realZ);
        RepresentedGeometryTransforms.QuaternionCoefficients(q, out GeometryVector graphX,
            out GeometryVector graphY, out GeometryVector graphZ);
        Matrix3x3.CreateFromQuaternion(q, out Matrix3x3 scalar);
        var wideQ = new QuaternionWide
        {
            X = new Vector<float>(q.X),
            Y = new Vector<float>(q.Y),
            Z = new Vector<float>(q.Z),
            W = new Vector<float>(q.W),
        };
        Matrix3x3Wide.CreateFromQuaternion(wideQ, out Matrix3x3Wide wide);
        FeaturePoint x = Certify(realX, graphX, scalar.X, wide.X, out FeaturePoint representedX);
        FeaturePoint y = Certify(realY, graphY, scalar.Y, wide.Y, out FeaturePoint representedY);
        FeaturePoint z = Certify(realZ, graphZ, scalar.Z, wide.Z, out FeaturePoint representedZ);
        if (!x.IsResolved || !y.IsResolved || !z.IsResolved) return false;
        result = new(x, y, z, pose.Position, representedX, representedY, representedZ);
        return result.IsResolved;
    }

    // Real coefficients use the same polynomial as Matrix3x3.cs:279-304. FeatureNumber's bounded
    // equalities may certify a real coefficient exactly, but a finite Matrix3x3 alone never can.
    static void RealCoefficients(Quaternion q, out FeaturePoint basisX, out FeaturePoint basisY,
        out FeaturePoint basisZ)
    {
        FeatureNumber x = FeatureNumber.Exact(q.X), y = FeatureNumber.Exact(q.Y);
        FeatureNumber z = FeatureNumber.Exact(q.Z), w = FeatureNumber.Exact(q.W);
        FeatureNumber x2 = x.Add(x), y2 = y.Add(y), z2 = z.Add(z);
        FeatureNumber xx = x2.Multiply(x), yy = y2.Multiply(y), zz = z2.Multiply(z);
        FeatureNumber xy = x2.Multiply(y), xz = x2.Multiply(z), xw = x2.Multiply(w);
        FeatureNumber yz = y2.Multiply(z), yw = y2.Multiply(w), zw = z2.Multiply(w);
        FeatureNumber one = FeatureNumber.Exact(1);
        basisX = new(one.Subtract(yy).Subtract(zz), xy.Add(zw), xz.Subtract(yw));
        basisY = new(xy.Subtract(zw), one.Subtract(xx).Subtract(zz), yz.Add(xw));
        basisZ = new(xz.Add(yw), yz.Subtract(xw), one.Subtract(xx).Subtract(yy));
    }

    static FeaturePoint Certify(FeaturePoint real, GeometryVector graph, Vector3 scalar, Vector3Wide wide,
        out FeaturePoint represented)
    {
        FeatureNumber x = Certify(real.X, graph.X, scalar.X, wide.X, out FeatureNumber rx);
        FeatureNumber y = Certify(real.Y, graph.Y, scalar.Y, wide.Y, out FeatureNumber ry);
        FeatureNumber z = Certify(real.Z, graph.Z, scalar.Z, wide.Z, out FeatureNumber rz);
        represented = new(rx, ry, rz);
        return new(x, y, z);
    }

    static FeatureNumber Certify(FeatureNumber real, GeometryInterval graph, float scalar, Vector<float> wide,
        out FeatureNumber represented)
    {
        represented = default;
        if (!real.IsResolved || !graph.IsResolved || !Contains(graph, scalar)) return default;
        double low = scalar, high = scalar;
        for (int i = 0; i < Vector<float>.Count; i++)
        {
            if (!Contains(graph, wide[i])) return default;
            low = Math.Min(low, wide[i]); high = Math.Max(high, wide[i]);
        }
        represented = low == high ? FeatureNumber.Exact(low) : FeatureNumber.Enclosed(GeometryInterval.Enclose(low, high));
        // This strengthening requires the independently evaluated real polynomial AND every
        // actual represented source conversion to agree. No coefficient midpoint enters it.
        if (real.IsExact && low == real.Value && high == real.Value) return real;
        return FeatureNumber.Enclosed(GeometryInterval.Enclose(Math.Min(real.Bounds.Lower, low),
            Math.Max(real.Bounds.Upper, high)));
    }

    internal FeaturePoint Direction(FeaturePoint local) => !IsResolved ? default : FeaturePoint.Add(
        FeaturePoint.Add(FeaturePoint.Scale(X, local.X), FeaturePoint.Scale(Y, local.Y)), FeaturePoint.Scale(Z, local.Z));

    internal FeaturePoint Point(Vector3 local) => FeaturePoint.Add(Direction(FeaturePoint.Exact(local)), Translation);

    internal FeaturePoint Edge(Vector3 a, Vector3 b) =>
        Direction(FeaturePoint.Subtract(FeaturePoint.Exact(b), FeaturePoint.Exact(a)));

    // Transpose is the pinned operational local map. It is deliberately not called an inverse.
    internal GeometryVector RoundedTranspose(GeometryVector world) => new(
        InstalledPoseOperations.Dot(world, RepresentedX.Bounds),
        InstalledPoseOperations.Dot(world, RepresentedY.Bounds),
        InstalledPoseOperations.Dot(world, RepresentedZ.Bounds));

    internal GeometryVector RoundedDirection(GeometryVector local) => new(
        InstalledPoseOperations.Dot(local, new(RepresentedX.X.Bounds, RepresentedY.X.Bounds, RepresentedZ.X.Bounds)),
        InstalledPoseOperations.Dot(local, new(RepresentedX.Y.Bounds, RepresentedY.Y.Bounds, RepresentedZ.Y.Bounds)),
        InstalledPoseOperations.Dot(local, new(RepresentedX.Z.Bounds, RepresentedY.Z.Bounds, RepresentedZ.Z.Bounds)));

    internal static bool Contains(GeometryInterval bounds, double value) => bounds.IsResolved &&
        double.IsFinite(value) && bounds.Lower <= value && bounds.Upper >= value;

    internal static bool Within(Vector3 value, float bound) => float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && MathF.Abs(value.X) <= bound && MathF.Abs(value.Y) <= bound && MathF.Abs(value.Z) <= bound;

    static bool EqualsExact(FeatureNumber value, double expected) => value.IsExact && value.Value == expected;
}
