using System;
using System.Numerics;
using BepuUtilities;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Three complete scalar enclosures. Default and any unresolved component refuse the vector.</summary>
internal readonly struct GeometryVector
{
    public GeometryInterval X { get; }
    public GeometryInterval Y { get; }
    public GeometryInterval Z { get; }
    public bool IsResolved => X.IsResolved && Y.IsResolved && Z.IsResolved;

    public GeometryVector(GeometryInterval x, GeometryInterval y, GeometryInterval z)
    {
        this = default;
        if (!x.IsResolved || !y.IsResolved || !z.IsResolved) return;
        X = x;
        Y = y;
        Z = z;
    }
}

/// <summary>Outward arithmetic for a supplied represented row-basis matrix.
/// This does not establish matrix/pose correspondence, rigidity, shape ownership or geometric eligibility.</summary>
internal static class RepresentedGeometryTransforms
{
    internal const float MaximumPositionMagnitude = 2048f;
    internal const float MaximumLocalMagnitude = 64f;
    internal const double SquaredUnitTolerance = 1d / (1 << 20);

    /// <summary>Encloses the pinned backend quaternion polynomial and its binary32 evaluation.
    /// Input bounds and squared unit band are necessary checks, not installed-shape provenance,
    /// output-domain acceptance, geometric eligibility or an error-ceiling certificate.</summary>
    public static GeometryVector PosePoint(in Pose pose, Vector3 local)
    {
        if (!Within(pose.Position, MaximumPositionMagnitude) || !Within(local, MaximumLocalMagnitude))
            return default;
        Quaternion q = pose.Orientation;
        if (!QuaternionNormWithinBand(q)) return default;
        QuaternionCoefficients(q, out GeometryVector basisX, out GeometryVector basisY, out GeometryVector basisZ);
        var rotated = new GeometryVector(
            Dot(local, basisX.X, basisY.X, basisZ.X),
            Dot(local, basisX.Y, basisY.Y, basisZ.Y),
            Dot(local, basisX.Z, basisY.Z, basisZ.Z));
        if (!rotated.IsResolved) return default;
        return new(AddTranslation(rotated.X, pose.Position.X), AddTranslation(rotated.Y, pose.Position.Y),
            AddTranslation(rotated.Z, pose.Position.Z));
    }

    internal static bool QuaternionNormWithinBand(Quaternion q)
    {
        GeometryInterval x = GeometryInterval.Exact(q.X), y = GeometryInterval.Exact(q.Y);
        GeometryInterval z = GeometryInterval.Exact(q.Z), w = GeometryInterval.Exact(q.W);
        GeometryInterval norm = x.Square().Add(y.Square()).Add(z.Square()).Add(w.Square());
        if (!norm.IsResolved) return false;
        double lower = 1d - SquaredUnitTolerance, upper = 1d + SquaredUnitTolerance;
        if (norm.Upper < lower || norm.Lower > upper) return false;
        // Only undecided closed endpoints require exact work. Four represented component squares
        // use the shared dyadic operations and the same pre-allocation 4096-bit limits.
        GeometrySign low = norm.Lower >= lower ? GeometrySign.Positive
            : BoundedGeometryArithmetic.CompareSumOfFourSquares(q.X, q.Y, q.Z, q.W, lower);
        GeometrySign high = norm.Upper <= upper ? GeometrySign.Negative
            : BoundedGeometryArithmetic.CompareSumOfFourSquares(q.X, q.Y, q.Z, q.W, upper);
        return (low is GeometrySign.Positive or GeometrySign.Zero) && (high is GeometrySign.Negative or GeometrySign.Zero);
    }

    // Bepu 2.4.0 Matrix3x3.cs:279-304 and Matrix3x3Wide.cs:220-241 have the same
    // coefficient expression graph. Each stage retains its real value and binary32 rounding.
    // These are coefficient/operation enclosures, not independently rounded geometry vertices.
    internal static void QuaternionCoefficients(Quaternion q, out GeometryVector basisX,
        out GeometryVector basisY, out GeometryVector basisZ)
    {
        GeometryInterval x = GeometryInterval.Exact(q.X), y = GeometryInterval.Exact(q.Y);
        GeometryInterval z = GeometryInterval.Exact(q.Z), w = GeometryInterval.Exact(q.W);
        GeometryInterval x2 = Add(x, x), y2 = Add(y, y), z2 = Add(z, z);
        GeometryInterval xx = Multiply(x2, x), yy = Multiply(y2, y), zz = Multiply(z2, z);
        GeometryInterval xy = Multiply(x2, y), xz = Multiply(x2, z), xw = Multiply(x2, w);
        GeometryInterval yz = Multiply(y2, z), yw = Multiply(y2, w), zw = Multiply(z2, w);
        GeometryInterval one = GeometryInterval.Exact(1);
        basisX = new(Subtract(Subtract(one, yy), zz), Add(xy, zw), Subtract(xz, yw));
        basisY = new(Subtract(xy, zw), Subtract(Subtract(one, xx), zz), Add(yz, xw));
        basisZ = new(Add(xz, yw), Subtract(yz, xw), Subtract(Subtract(one, xx), yy));
    }

    public static GeometryVector Point(in Matrix3x3 matrix, Vector3 translation, Vector3 local)
    {
        GeometryVector rotated = Direction(matrix, local);
        if (!rotated.IsResolved) return default;
        return new(AddTranslation(rotated.X, translation.X), AddTranslation(rotated.Y, translation.Y),
            AddTranslation(rotated.Z, translation.Z));
    }

    public static GeometryVector Direction(in Matrix3x3 matrix, Vector3 local) => new(
        Dot(local, matrix.X.X, matrix.Y.X, matrix.Z.X),
        Dot(local, matrix.X.Y, matrix.Y.Y, matrix.Z.Y),
        Dot(local, matrix.X.Z, matrix.Y.Z, matrix.Z.Z));

    static GeometryInterval AddTranslation(GeometryInterval value, float translation) =>
        value.Add(GeometryInterval.Exact(translation)).EncloseSingleRounding();

    static GeometryInterval Multiply(GeometryInterval a, GeometryInterval b) => a.Multiply(b).EncloseSingleRounding();
    static GeometryInterval Add(GeometryInterval a, GeometryInterval b) => a.Add(b).EncloseSingleRounding();
    static GeometryInterval Subtract(GeometryInterval a, GeometryInterval b) => a.Subtract(b).EncloseSingleRounding();

    static GeometryInterval Dot(Vector3 v, float x, float y, float z) =>
        Dot(v, GeometryInterval.Exact(x), GeometryInterval.Exact(y), GeometryInterval.Exact(z));

    static GeometryInterval Dot(Vector3 v, GeometryInterval x, GeometryInterval y, GeometryInterval z) =>
        Add(Add(Multiply(GeometryInterval.Exact(v.X), x), Multiply(GeometryInterval.Exact(v.Y), y)),
            Multiply(GeometryInterval.Exact(v.Z), z));

    static bool Within(Vector3 value, float bound) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) &&
        MathF.Abs(value.X) <= bound && MathF.Abs(value.Y) <= bound && MathF.Abs(value.Z) <= bound;
}
