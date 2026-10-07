using System;
using System.Numerics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>A represented vector and its Euclidean error bound. Default refuses the whole output.</summary>
internal readonly struct GeometryVectorOutput
{
    public bool IsResolved { get; }
    public Vector3 Value { get; }
    public float Error { get; }

    internal GeometryVectorOutput(Vector3 value, float error)
    {
        this = default;
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z) ||
            !float.IsFinite(error) || error < 0) return;
        Value = value;
        Error = error;
        IsResolved = true;
    }
}

/// <summary>Outward vector arithmetic over supplied enclosures. Geometry provenance and caller-specific
/// error ceilings remain separate checks. No operation replaces an uncertain vector with its midpoint.</summary>
internal static class GeometryVectorOperations
{
    internal static GeometryInterval Dot(GeometryVector a, GeometryVector b)
    {
        if (!a.IsResolved || !b.IsResolved) return default;
        return a.X.Multiply(b.X).Add(a.Y.Multiply(b.Y)).Add(a.Z.Multiply(b.Z));
    }

    internal static GeometryVector Cross(GeometryVector a, GeometryVector b)
    {
        if (!a.IsResolved || !b.IsResolved) return default;
        return new(a.Y.Multiply(b.Z).Subtract(a.Z.Multiply(b.Y)),
            a.Z.Multiply(b.X).Subtract(a.X.Multiply(b.Z)),
            a.X.Multiply(b.Y).Subtract(a.Y.Multiply(b.X)));
    }

    internal static GeometryVector Normalize(GeometryVector value)
    {
        if (!value.IsResolved) return default;
        if (TryNormalizeExactly(value, out GeometryVector exact)) return exact;
        // Square retains the self-dependency when a component straddles zero. Dot(value,value)
        // would instead bound products of independently chosen components and lose that fact.
        GeometryInterval squared = value.X.Square().Add(value.Y.Square()).Add(value.Z.Square());
        if (!squared.IsResolved || squared.Lower <= 0) return default;
        GeometryInterval length = squared.Sqrt();
        if (!length.IsResolved || length.Lower <= 0) return default;
        return new(value.X.Divide(length), value.Y.Divide(length), value.Z.Divide(length));
    }

    static bool TryNormalizeExactly(GeometryVector value, out GeometryVector result)
    {
        result = default;
        if (value.X.Lower != value.X.Upper || value.Y.Lower != value.Y.Upper ||
            value.Z.Lower != value.Z.Upper) return false;
        double x = value.X.Lower, y = value.Y.Lower, z = value.Z.Lower;
        double length = Math.Sqrt(x * x + y * y + z * z);
        // Floating arithmetic proposes the norm and quotients. Exact dyadic equalities must
        // certify all four before an interval can become a singleton, including zero components.
        if (!double.IsFinite(length) || length <= 0 ||
            BoundedGeometryArithmetic.CompareSquaredDistances([x, y, z], [0, 0, 0], [length], [0]) != GeometrySign.Zero)
            return false;
        double nx = x / length, ny = y / length, nz = z / length;
        if (!double.IsFinite(nx) || !double.IsFinite(ny) || !double.IsFinite(nz) ||
            BoundedGeometryArithmetic.CompareProducts(nx, length, x, 1) != GeometrySign.Zero ||
            BoundedGeometryArithmetic.CompareProducts(ny, length, y, 1) != GeometrySign.Zero ||
            BoundedGeometryArithmetic.CompareProducts(nz, length, z, 1) != GeometrySign.Zero)
            return false;
        result = new(GeometryInterval.Exact(nx), GeometryInterval.Exact(ny), GeometryInterval.Exact(nz));
        return true;
    }

    internal static GeometryVectorOutput Publish(GeometryVector value)
    {
        if (!value.IsResolved) return default;
        Vector3 representative = new(Representative(value.X), Representative(value.Y), Representative(value.Z));
        if (!float.IsFinite(representative.X) || !float.IsFinite(representative.Y) ||
            !float.IsFinite(representative.Z)) return default;
        if (IsExact(value.X, representative.X) && IsExact(value.Y, representative.Y) && IsExact(value.Z, representative.Z))
            return new(representative, 0);

        GeometryInterval x = value.X.Subtract(GeometryInterval.Exact(representative.X)).Square();
        GeometryInterval y = value.Y.Subtract(GeometryInterval.Exact(representative.Y)).Square();
        GeometryInterval z = value.Z.Subtract(GeometryInterval.Exact(representative.Z)).Square();
        GeometryInterval squaredError = x.Add(y).Add(z);
        if (!squaredError.IsResolved) return default;
        // A squared Euclidean error is nonnegative even if outward additions put the lower bound below zero.
        GeometryInterval error = GeometryInterval.Enclose(0, Math.Max(0, squaredError.Upper)).Sqrt();
        if (!error.IsResolved) return default;
        float publishedError = (float)error.Upper;
        if ((double)publishedError < error.Upper) publishedError = MathF.BitIncrement(publishedError);
        if (!float.IsFinite(publishedError)) return default;
        return new(representative, publishedError);
    }

    // This is only a finite proposal. The subsequent error proof covers every interval point,
    // including midpoint arithmetic and binary32 rounding, without requiring the proposal to be exact.
    static float Representative(GeometryInterval value) => (float)(value.Lower * 0.5 + value.Upper * 0.5);
    static bool IsExact(GeometryInterval value, float represented) =>
        value.Lower == represented && value.Upper == represented;
}
