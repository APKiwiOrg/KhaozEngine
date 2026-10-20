using System;

namespace KhaozEngine.Physics.Bepu;

internal static partial class BoundedGeometryArithmetic
{
    /// <summary>The exact sign of the squared distance from (x,z) to the closed edge minus the squared
    /// disc radius (radius + band)^2 - (height - plane)^2. Coordinates are on the 2^-80 lattice within 256,
    /// and the edge difference must be exactly representable. Otherwise no sign is certified.</summary>
    internal static GeometrySign CompareDiscEdge(double x, double z, double height, double plane,
        double ax, double az, double bx, double bz, double radius, double band)
    {
        foreach (double value in (ReadOnlySpan<double>)[x, z, height, plane, ax, az, bx, bz, radius, band])
            if (!DiscCoordinate(value)) return GeometrySign.Unresolved;
        ExactExpansion.TwoSum(bx, -ax, out double ex, out double exError);
        ExactExpansion.TwoSum(bz, -az, out double ez, out double ezError);
        if (exError != 0 || ezError != 0 || (ex == 0 && ez == 0)) return GeometrySign.Unresolved;
        ExactExpansion.TwoSum(x, -ax, out double dx, out double dxError);
        ExactExpansion.TwoSum(z, -az, out double dz, out double dzError);
        var projection = new ExactExpansion(stackalloc double[13]);
        projection.AddProduct(dx, ex);
        projection.AddProduct(dxError, ex);
        projection.AddProduct(dz, ez);
        projection.AddProduct(dzError, ez);
        bool first = projection.Sign is GeometrySign.Negative or GeometrySign.Zero;
        projection.AddProduct(-ex, ex);
        projection.AddProduct(-ez, ez);
        bool last = projection.Sign is GeometrySign.Positive or GeometrySign.Zero;
        var budget = new ExactExpansion(stackalloc double[33]);
        budget.AddProduct(radius, radius);
        budget.AddProduct(2 * radius, band);
        budget.AddProduct(band, band);
        AddDiscDifferenceSquare(ref budget, height, plane, -1);
        if (first || last)
        {
            // A closest endpoint gives a sum of squares, with no quotient or rounded projection.
            AddDiscDifferenceSquare(ref budget, x, first ? ax : bx, -1);
            AddDiscDifferenceSquare(ref budget, z, first ? az : bz, -1);
            return OppositeDiscSign(budget.Sign);
        }
        var cross = new ExactExpansion(stackalloc double[9]);
        cross.AddProduct(dx, ez);
        cross.AddProduct(dxError, ez);
        cross.AddProduct(-dz, ex);
        cross.AddProduct(-dzError, ex);
        var squared = new ExactExpansion(stackalloc double[5]);
        squared.AddProduct(ex, ex);
        squared.AddProduct(ez, ez);
        var comparison = new ExactExpansion(stackalloc double[257]);
        AddDiscProducts(ref comparison, cross.Components, cross.Components, 1);
        AddDiscProducts(ref comparison, budget.Components, squared.Components, -1);
        return comparison.Sign;
    }

    // At most eight cross components, fourteen budget components and four edge-length components give
    // 2 * (8 * 8 + 14 * 4) = 240 terms. The 257-term buffer includes the required spare component.
    // Degree-four products of these lattice coordinates are multiples of 2^-320 and smaller than 2^42.
    // Every Two-Product residual is therefore representable, including products of expansion components.
    static void AddDiscProducts(ref ExactExpansion sum, ReadOnlySpan<double> a, ReadOnlySpan<double> b, double sign)
    {
        foreach (double x in a)
            foreach (double y in b) sum.AddProduct(sign * x, y);
    }

    static void AddDiscDifferenceSquare(ref ExactExpansion sum, double a, double b, double sign)
    {
        ExactExpansion.TwoSum(a, -b, out double value, out double error);
        sum.AddProduct(sign * value, value);
        sum.AddProduct(sign * 2 * value, error);
        sum.AddProduct(sign * error, error);
    }

    static GeometrySign OppositeDiscSign(GeometrySign sign) => sign switch
    {
        GeometrySign.Positive => GeometrySign.Negative,
        GeometrySign.Negative => GeometrySign.Positive,
        _ => sign,
    };

    internal static bool DiscCoordinate(double value) => double.IsFinite(value) && Math.Abs(value) <= 256 &&
        Math.Truncate(Math.ScaleB(value, 80)) == Math.ScaleB(value, 80);
}
