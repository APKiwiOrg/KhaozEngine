using System;
using System.Numerics;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.Physics;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>A-priori binary32 error bounds. Positive term sums are rounded upward in binary64.</summary>
internal static class MapFloatGeometryError
{
    const double UnitRoundoff = 1d / 16777216;
    const double MinimumSubnormal = (double)float.Epsilon;
    readonly record struct Q(double X, double Y, double Z, double W);

    internal static MapDouble3 Of(PhysicsShape shape, Pose local, MapDoublePose ideal, Quaternion root,
        MapDouble3 rootMagnitude, MapDouble3 poseError, bool compound)
    {
        Q parent = Abs(root), child = Abs(local.Orientation);
        MapDouble3 offset = Abs(MapDouble3.From(local.Position));
        MapDouble3 centreTerms = RotationTerms(parent, offset);
        MapDouble3 centreError = root == Quaternion.Identity ? default : RotationError(parent, offset);
        centreError = Add(centreError, poseError);
        if (compound)
            centreError = Add(centreError, Round(1, Add(Add(rootMagnitude, centreTerms), centreError)));

        var source = MapShapeBounds.Of(shape, Pose.Identity);
        MapDouble3 radius = new(Math.Max(Math.Abs(source.MinX), Math.Abs(source.MaxX)),
            Math.Max(Math.Abs(source.MinY), Math.Abs(source.MaxY)), Math.Max(Math.Abs(source.MinZ), Math.Abs(source.MaxZ)));
        Q orientation = new(ideal.Qx, ideal.Qy, ideal.Qz, ideal.Qw);
        Q orientationError = root == Quaternion.Identity || local.Orientation == Quaternion.Identity ? default : new(
            Error(8, Sum(Mul(parent.W, child.X), Mul(parent.X, child.W), Mul(parent.Y, child.Z), Mul(parent.Z, child.Y))),
            Error(8, Sum(Mul(parent.W, child.Y), Mul(parent.X, child.Z), Mul(parent.Y, child.W), Mul(parent.Z, child.X))),
            Error(8, Sum(Mul(parent.W, child.Z), Mul(parent.X, child.Y), Mul(parent.Y, child.X), Mul(parent.Z, child.W))),
            Error(8, Sum(Mul(parent.W, child.W), Mul(parent.X, child.X), Mul(parent.Y, child.Y), Mul(parent.Z, child.Z))));
        Q upper = new(Sum(Math.Abs(orientation.X), orientationError.X), Sum(Math.Abs(orientation.Y), orientationError.Y),
            Sum(Math.Abs(orientation.Z), orientationError.Z), Sum(Math.Abs(orientation.W), orientationError.W));
        MapDouble3 extentTerms = RotationTerms(upper, radius);
        MapDouble3 extentError = RotationPerturbation(orientation, orientationError, radius);
        if (orientation != new Q(0, 0, 0, 1) || orientationError != default)
            extentError = Add(extentError, RotationError(upper, radius));

        // Base-aligned cylinders and hulls introduce a local centre shift. Charge its rotation, translation and
        // point restoration from input magnitudes too, rather than the small restored geometry or cancelled centre.
        if (shape is CylinderShape or ConvexHullShape)
        {
            MapDouble3 shiftTerms = RotationTerms(child, radius);
            MapDouble3 shiftError = Add(RotationError(child, radius), Round(1, Add(offset, shiftTerms)));
            shiftError = Add(shiftError, Round(1, Scale(radius, 2)));
            centreError = Add(centreError, RotationTerms(parent, shiftError));
            centreError = Add(centreError, RotationError(parent, Add(offset, shiftTerms)));
        }

        MapDouble3 total = Add(centreError, extentError);
        // The final centre +/- extent addition is a separate rounding from child-pose translation.
        return Add(total, Round(1, Add(Add(Add(rootMagnitude, centreTerms), extentTerms), total)));
    }

    // Expanded quaternion rotation polynomial. Every sign is replaced with + before multiplying the input.
    // Each product-to-output path has at most 16 rounding stages for matrix or cross-product evaluation, even
    // without FMA. gamma_16 * this sum bounds any reduction order, including severe cancellation.
    static MapDouble3 RotationTerms(Q q, MapDouble3 v)
    {
        double xx = Mul(q.X, q.X), yy = Mul(q.Y, q.Y), zz = Mul(q.Z, q.Z);
        double xy = Mul(q.X, q.Y), xz = Mul(q.X, q.Z), yz = Mul(q.Y, q.Z);
        double xw = Mul(q.X, q.W), yw = Mul(q.Y, q.W), zw = Mul(q.Z, q.W);
        return new(
            Sum(Mul(Sum(1, Mul(2, Sum(yy, zz))), v.X), Mul(Mul(2, Sum(xy, zw)), v.Y), Mul(Mul(2, Sum(xz, yw)), v.Z)),
            Sum(Mul(Mul(2, Sum(xy, zw)), v.X), Mul(Sum(1, Mul(2, Sum(xx, zz))), v.Y), Mul(Mul(2, Sum(yz, xw)), v.Z)),
            Sum(Mul(Mul(2, Sum(xz, yw)), v.X), Mul(Mul(2, Sum(yz, xw)), v.Y), Mul(Sum(1, Mul(2, Sum(xx, yy))), v.Z)));
    }

    static MapDouble3 RotationError(Q q, MapDouble3 v)
    {
        double growth = Mul(4, Mul(Sum(1, q.X, q.Y, q.Z, q.W), Sum(1, q.X, q.Y, q.Z, q.W)));
        double underflowGrowth = Mul(growth, Sum(1, v.X, v.Y, v.Z));
        MapDouble3 terms = RotationTerms(q, v);
        return new(Error(16, terms.X, underflowGrowth), Error(16, terms.Y, underflowGrowth), Error(16, terms.Z, underflowGrowth));
    }

    // The rotation coefficients are quadratic. |ab - (a+da)(b+db)| is bounded by
    // |a|db + |b|da + da*db. This carries quaternion-product error into each extent component.
    static MapDouble3 RotationPerturbation(Q q, Q e, MapDouble3 v)
    {
        double Product(double a, double b, double da, double db) => Sum(Mul(Math.Abs(a), db), Mul(Math.Abs(b), da), Mul(da, db));
        double xx = Product(q.X, q.X, e.X, e.X), yy = Product(q.Y, q.Y, e.Y, e.Y), zz = Product(q.Z, q.Z, e.Z, e.Z);
        double xy = Product(q.X, q.Y, e.X, e.Y), xz = Product(q.X, q.Z, e.X, e.Z), yz = Product(q.Y, q.Z, e.Y, e.Z);
        double xw = Product(q.X, q.W, e.X, e.W), yw = Product(q.Y, q.W, e.Y, e.W), zw = Product(q.Z, q.W, e.Z, e.W);
        return Scale(new(
            Sum(Mul(Sum(yy, zz), v.X), Mul(Sum(xy, zw), v.Y), Mul(Sum(xz, yw), v.Z)),
            Sum(Mul(Sum(xy, zw), v.X), Mul(Sum(xx, zz), v.Y), Mul(Sum(yz, xw), v.Z)),
            Sum(Mul(Sum(xz, yw), v.X), Mul(Sum(yz, xw), v.Y), Mul(Sum(xx, yy), v.Z))), 2);
    }

    // gamma_n = n*u/(1-n*u). The absolute term handles gradual underflow, with propagated weights for rotations.
    static double Error(int operations, double terms, double underflowGrowth = 1)
    {
        double denominator = 1 - operations * UnitRoundoff;
        double gamma = Math.BitIncrement(operations * UnitRoundoff / denominator);
        double underflow = Math.BitIncrement(operations * MinimumSubnormal / denominator);
        return Sum(Mul(gamma, terms), Mul(underflow, underflowGrowth));
    }

    static MapDouble3 Round(int operations, MapDouble3 terms) => new(Error(operations, terms.X), Error(operations, terms.Y), Error(operations, terms.Z));
    static Q Abs(Quaternion q) => new(Math.Abs((double)q.X), Math.Abs((double)q.Y), Math.Abs((double)q.Z), Math.Abs((double)q.W));
    static MapDouble3 Abs(MapDouble3 v) => new(Math.Abs(v.X), Math.Abs(v.Y), Math.Abs(v.Z));
    static MapDouble3 Add(MapDouble3 a, MapDouble3 b) => new(Sum(a.X, b.X), Sum(a.Y, b.Y), Sum(a.Z, b.Z));
    static MapDouble3 Scale(MapDouble3 v, double factor) => new(Mul(v.X, factor), Mul(v.Y, factor), Mul(v.Z, factor));
    static double Mul(double a, double b) => a == 0 || b == 0 ? 0 : Math.BitIncrement(a * b);
    static double Sum(params double[] terms)
    {
        double sum = 0;
        foreach (double term in terms) if (term != 0) sum = sum == 0 ? term : Math.BitIncrement(sum + term);
        return sum;
    }
}
