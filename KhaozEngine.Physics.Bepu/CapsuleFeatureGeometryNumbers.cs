using System.Numerics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>A feature expression evaluated by the shared interval kernel, optionally with a
/// certificate that its value is exactly representable. An interval midpoint is never a certificate.</summary>
internal readonly struct FeatureNumber
{
    internal GeometryInterval Bounds { get; }
    internal bool IsExact { get; }
    internal double Value { get; }
    internal bool IsResolved => Bounds.IsResolved;

    FeatureNumber(GeometryInterval bounds, bool exact = false, double value = 0)
    {
        Bounds = bounds;
        IsExact = bounds.IsResolved && exact;
        Value = IsExact ? value : 0;
    }

    internal static FeatureNumber Exact(double value) => new(GeometryInterval.Exact(value), true, value);
    internal static FeatureNumber Enclosed(GeometryInterval bounds) => new(bounds);

    // Exact operands in the ExactExpansion domain take a short path. Their outward bounds are one step either side
    // of a finite result, so they always resolve, and an exact result replaces them. The bounds are only computed
    // when the result is not exact.
    internal FeatureNumber Add(FeatureNumber other)
    {
        if (IsExact && other.IsExact)
        {
            double exactSum = Value + other.Value;
            if (ExactExpansion.InRange(Value) && ExactExpansion.InRange(other.Value) && ExactExpansion.InRange(exactSum))
            {
                // Two-Sum's error is exactly a+b-sum. The test below holds exactly when that is zero, and inside this
                // domain its comparator never refuses, so both decide the same.
                ExactExpansion.TwoSum(Value, other.Value, out _, out double error);
                return error == 0 ? Exact(exactSum) : new(Bounds.Add(other.Bounds));
            }
        }
        GeometryInterval bounds = Bounds.Add(other.Bounds);
        if (!bounds.IsResolved || !IsExact || !other.IsExact) return new(bounds);
        double sum = Value + other.Value;
        // |sum-a| == |b| together with the correct sign proves sum-a == b. The shared
        // exact distance comparator certifies the subtraction, without a second exact-add kernel.
        GeometrySign magnitude = BoundedGeometryArithmetic.CompareSquaredDistances([sum], [Value], [other.Value], [0d]);
        bool direction = other.Value < 0 ? sum <= Value : sum >= Value;
        return magnitude == GeometrySign.Zero && direction ? Exact(sum) : new(bounds);
    }

    internal FeatureNumber Negate() => !IsResolved ? default : IsExact ? Exact(-Value)
        : new(GeometryInterval.Enclose(-Bounds.Upper, -Bounds.Lower));
    internal FeatureNumber Subtract(FeatureNumber other) => Add(other.Negate());

    internal FeatureNumber Multiply(FeatureNumber other)
    {
        if (IsExact && other.IsExact && ExactExpansion.InRange(Value) && ExactExpansion.InRange(other.Value))
        {
            double exactProduct = Value * other.Value;
            return BoundedGeometryArithmetic.CompareProducts(Value, other.Value, exactProduct, 1) == GeometrySign.Zero
                ? Exact(exactProduct) : new(Bounds.Multiply(other.Bounds));
        }
        GeometryInterval bounds = Bounds.Multiply(other.Bounds);
        if (!bounds.IsResolved || !IsExact || !other.IsExact) return new(bounds);
        double product = Value * other.Value;
        return BoundedGeometryArithmetic.CompareProducts(Value, other.Value, product, 1) == GeometrySign.Zero
            ? Exact(product) : new(bounds);
    }

    internal FeatureNumber Divide(FeatureNumber other)
    {
        if (IsExact && other.IsExact && other.Value != 0 && ExactExpansion.InRange(Value) &&
            ExactExpansion.InRange(other.Value))
        {
            double exactQuotient = Value / other.Value;
            return BoundedGeometryArithmetic.CompareProducts(exactQuotient, other.Value, Value, 1) == GeometrySign.Zero
                ? Exact(exactQuotient) : new(Bounds.Divide(other.Bounds));
        }
        GeometryInterval bounds = Bounds.Divide(other.Bounds);
        if (!bounds.IsResolved || !IsExact || !other.IsExact || other.Value == 0) return new(bounds);
        double quotient = Value / other.Value;
        return BoundedGeometryArithmetic.CompareProducts(quotient, other.Value, Value, 1) == GeometrySign.Zero
            ? Exact(quotient) : new(bounds);
    }

    internal GeometrySign Sign => !IsResolved ? GeometrySign.Unresolved
        : IsExact ? Value < 0 ? GeometrySign.Negative : Value > 0 ? GeometrySign.Positive : GeometrySign.Zero
        : Bounds.Lower > 0 ? GeometrySign.Positive : Bounds.Upper < 0 ? GeometrySign.Negative
        : GeometrySign.Unresolved;

    internal GeometrySign Compare(FeatureNumber other) => Subtract(other).Sign;
}

/// <summary>Witness expressions, rather than rounded geometric proposals. Vector bounds use the
/// existing shared primitives. The exact flags are consumed only after every expression is certified.</summary>
internal readonly record struct FeaturePoint(FeatureNumber X, FeatureNumber Y, FeatureNumber Z)
{
    internal bool IsResolved => X.IsResolved && Y.IsResolved && Z.IsResolved;
    internal bool IsExact => X.IsExact && Y.IsExact && Z.IsExact;
    internal GeometryVector Bounds => new(X.Bounds, Y.Bounds, Z.Bounds);
    internal static FeaturePoint Exact(Vector3 value) => new(FeatureNumber.Exact(value.X),
        FeatureNumber.Exact(value.Y), FeatureNumber.Exact(value.Z));
    internal static FeaturePoint Add(FeaturePoint a, FeaturePoint b) => new(a.X.Add(b.X), a.Y.Add(b.Y), a.Z.Add(b.Z));
    internal static FeaturePoint Subtract(FeaturePoint a, FeaturePoint b) =>
        new(a.X.Subtract(b.X), a.Y.Subtract(b.Y), a.Z.Subtract(b.Z));
    internal static FeaturePoint Scale(FeaturePoint value, FeatureNumber scale) =>
        new(value.X.Multiply(scale), value.Y.Multiply(scale), value.Z.Multiply(scale));
    internal static FeatureNumber Dot(FeaturePoint a, FeaturePoint b)
    {
        FeatureNumber proposed = a.X.Multiply(b.X).Add(a.Y.Multiply(b.Y)).Add(a.Z.Multiply(b.Z));
        return proposed.IsExact ? proposed : FeatureNumber.Enclosed(GeometryVectorOperations.Dot(a.Bounds, b.Bounds));
    }

    internal static FeaturePoint Cross(FeaturePoint a, FeaturePoint b)
    {
        FeatureNumber x = a.Y.Multiply(b.Z).Subtract(a.Z.Multiply(b.Y));
        FeatureNumber y = a.Z.Multiply(b.X).Subtract(a.X.Multiply(b.Z));
        FeatureNumber z = a.X.Multiply(b.Y).Subtract(a.Y.Multiply(b.X));
        if (x.IsExact && y.IsExact && z.IsExact) return new(x, y, z);
        GeometryVector bounds = GeometryVectorOperations.Cross(a.Bounds, b.Bounds);
        return new(x.IsExact ? x : FeatureNumber.Enclosed(bounds.X),
            y.IsExact ? y : FeatureNumber.Enclosed(bounds.Y), z.IsExact ? z : FeatureNumber.Enclosed(bounds.Z));
    }

    internal bool Within(double maximum) => IsResolved &&
        X.Bounds.Lower >= -maximum && X.Bounds.Upper <= maximum &&
        Y.Bounds.Lower >= -maximum && Y.Bounds.Upper <= maximum &&
        Z.Bounds.Lower >= -maximum && Z.Bounds.Upper <= maximum;

    internal bool SameExact(FeaturePoint other) => IsExact && other.IsExact &&
        X.Value == other.X.Value && Y.Value == other.Y.Value && Z.Value == other.Z.Value;

    internal static GeometrySign CompareDistances(FeaturePoint axis, FeaturePoint geometry,
        FeaturePoint otherAxis, FeaturePoint otherGeometry)
    {
        if (!axis.IsExact || !geometry.IsExact || !otherAxis.IsExact || !otherGeometry.IsExact)
            return GeometrySign.Unresolved;
        return BoundedGeometryArithmetic.CompareSquaredDistances(
            [axis.X.Value, axis.Y.Value, axis.Z.Value], [geometry.X.Value, geometry.Y.Value, geometry.Z.Value],
            [otherAxis.X.Value, otherAxis.Y.Value, otherAxis.Z.Value],
            [otherGeometry.X.Value, otherGeometry.Y.Value, otherGeometry.Z.Value]);
    }

    internal static GeometrySign CompareDistanceToRadius(FeaturePoint axis, FeaturePoint geometry, double radius)
    {
        if (!axis.IsExact || !geometry.IsExact) return GeometrySign.Unresolved;
        return BoundedGeometryArithmetic.CompareSquaredDistances(
            [axis.X.Value, axis.Y.Value, axis.Z.Value], [geometry.X.Value, geometry.Y.Value, geometry.Z.Value],
            [radius], [0d]);
    }
}
