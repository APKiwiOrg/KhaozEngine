using System;
using static KhaozEngine.Attachments.ChainCapsuleCollision;

namespace KhaozEngine.Attachments;

internal readonly struct ChainThinTriangleWitness(Offset ab, Offset ac)
{
    // 128 * binary64 epsilon * condition squared must fit the sheet cull's 2^-19 float pad.
    internal const double FastConditionLimit = 1 << 13;

    internal Offset AreaNormal() => new(
        DifferenceOfProducts(ab.Y, ac.Z, ab.Z, ac.Y),
        DifferenceOfProducts(ab.Z, ac.X, ab.X, ac.Z),
        DifferenceOfProducts(ab.X, ac.Y, ab.Y, ac.X));

    internal bool TryPierce(Offset root, in Axis axis, out Offset weights, out double along) =>
        TryIntersect(Difference.Between(axis.Start, root), Difference.Between(axis.End, axis.Start), out weights, out along);

    internal bool TryProject(Offset root, Offset end, Offset normal, out Offset weights, out double travel) =>
        TryIntersect(Difference.Between(end, root), new Difference(normal, default), out weights, out travel);

    // Signed volumes give the line intersection's weights without first rounding its
    // position onto the face. Using the normal as direction projects an axis endpoint.
    private bool TryIntersect(Difference fromRoot, Difference direction, out Offset weights, out double along)
    {
        weights = default;
        along = 0;
        // A determinant has six triple products, each with up to four terms. A
        // difference can double that count. Retain these terms until the edge-side tests.
        const int termsPerDeterminant = 6 * 4;
        var a = new Expansion(stackalloc double[(2 + 4 + 4) * termsPerDeterminant]);
        var b = new Expansion(stackalloc double[4 * termsPerDeterminant]);
        var c = new Expansion(stackalloc double[4 * termsPerDeterminant]);
        a.AddDeterminant(ab, new Difference(ac, default), direction);
        double denominator = a.Leading;
        if (denominator == 0 || !double.IsFinite(denominator))
            return false;

        b.AddDeterminant(ac, direction, fromRoot);
        c.AddDeterminant(ab, fromRoot, direction);
        a.AddNegative(b);
        a.AddNegative(c);

        double sign = Math.Sign(denominator);
        if (a.Leading * sign < 0 || b.Leading * sign < 0 || c.Leading * sign < 0)
            return false;

        var height = new Expansion(stackalloc double[2 * termsPerDeterminant]);
        height.AddDeterminant(ab, new Difference(ac, default), fromRoot);
        along = -height.Leading / denominator;
        weights = new Offset(a.Leading / denominator, b.Leading / denominator, c.Leading / denominator);
        return double.IsFinite(along);
    }

    private static double DifferenceOfProducts(double a, double b, double c, double d)
    {
        double cd = c * d;
        return Math.FusedMultiplyAdd(a, b, -cd) + Math.FusedMultiplyAdd(-c, d, cd);
    }

    // The low part can carry the entire face offset when a capsule axis is very long.
    private readonly record struct Difference(Offset Value, Offset Error)
    {
        internal static Difference Between(Offset a, Offset b)
        {
            TwoSum(a.X, -b.X, out double x, out double lowX);
            TwoSum(a.Y, -b.Y, out double y, out double lowY);
            TwoSum(a.Z, -b.Z, out double z, out double lowZ);
            return new Difference(new Offset(x, y, z), new Offset(lowX, lowY, lowZ));
        }
    }

    private static void TwoSum(double a, double b, out double sum, out double error)
    {
        sum = a + b;
        double part = sum - a;
        error = (a - (sum - part)) + (b - part);
    }

    // Add a rounded operation and its error to a nonoverlapping expansion. The
    // largest component gives the sign and approximates the magnitude for division.
    private ref struct Expansion
    {
        private readonly Span<double> _terms;
        private int _count;

        internal Expansion(Span<double> terms) => _terms = terms;

        internal readonly double Leading => _count == 0 ? 0 : _terms[_count - 1];

        internal void AddDeterminant(Offset a, Difference b, Difference c)
        {
            AddDeterminant(a, b.Value, c.Value);
            if (b.Error != default)
                AddDeterminant(a, b.Error, c.Value);
            if (c.Error != default)
                AddDeterminant(a, b.Value, c.Error);
            if (b.Error != default && c.Error != default)
                AddDeterminant(a, b.Error, c.Error);
        }

        private void AddDeterminant(Offset a, Offset b, Offset c)
        {
            AddTriple(a.X, b.Y, c.Z);
            AddTriple(a.Y, b.Z, c.X);
            AddTriple(a.Z, b.X, c.Y);
            AddTriple(-a.X, b.Z, c.Y);
            AddTriple(-a.Y, b.X, c.Z);
            AddTriple(-a.Z, b.Y, c.X);
        }

        internal void AddNegative(scoped Expansion other)
        {
            foreach (double term in other._terms[..other._count])
                Add(-term);
        }

        private void AddTriple(double a, double b, double c)
        {
            if (a == 0 || b == 0 || c == 0)
                return;

            double product = a * b;
            AddProduct(product, c);
            AddProduct(Math.FusedMultiplyAdd(a, b, -product), c);
        }

        private void AddProduct(double a, double b)
        {
            double product = a * b;
            Add(Math.FusedMultiplyAdd(a, b, -product));
            Add(product);
        }

        private void Add(double value)
        {
            if (value == 0)
                return;

            int kept = 0;
            for (int i = 0; i < _count; i++)
            {
                TwoSum(value, _terms[i], out value, out double error);
                if (error != 0)
                    _terms[kept++] = error;
            }
            if (value != 0)
                _terms[kept++] = value;
            _count = kept;
        }
    }
}
