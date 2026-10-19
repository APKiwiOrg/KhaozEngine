using System;

namespace KhaozEngine.Physics.Bepu;

internal readonly record struct SweepLinear(int Constant, int Slope);
internal readonly record struct SweepDistanceTerms(SweepLinear X, SweepLinear Y, SweepLinear Z,
    int RadiusSquaredScale, int Components);

/// <summary>Handles into the original-operand graph and closed linear eligibility inequalities.</summary>
internal readonly record struct SweepFeatureRegime(int A, int B, int C,
    SweepLinear[] Constraints, int ConstraintCount, bool Empty = false, SweepDistanceTerms? Distance = null)
{
    internal static SweepFeatureRegime Polynomial(SweepExactWorkspace exact, double a, double b, double c)
    {
        exact.Reset();
        return new(exact.Constant(a), exact.Constant(b), exact.Constant(c), [], 0);
    }
}

/// <summary>Closed constrained convex quadratics on original fractions. An isolated exact minimum
/// is an event even when no dyadic evaluation is nonpositive. No result is published before completion.</summary>
internal static class CapsuleSweepFeaturePolynomial
{
    readonly record struct Fraction(int Numerator, int Denominator);
    readonly record struct Cell(double From, double To, int Depth);

    internal static bool TryFirstContact(in SweepFeatureRegime regime, double from, double to,
        double maximumFractionWidth, ref CapsuleSweepWork work, SweepExactWorkspace exact,
        out double lower, out double? upper) => TryBound(regime, from, to, maximumFractionWidth,
            false, ref work, exact, out lower, out upper);

    // Another completed regime already supplies an upper event witness. This method proves a
    // lower bound for this regime before that known bracket. A null upper here can carry an
    // uncertain cell into the aggregate bracket. It is never published as Clear or as a new Hit.
    internal static bool TryBeforeKnownContact(in SweepFeatureRegime regime, double to,
        double maximumFractionWidth, ref CapsuleSweepWork work, SweepExactWorkspace exact,
        out double lower, out double? provedUpper) => TryBound(regime, 0, to, maximumFractionWidth,
            true, ref work, exact, out lower, out provedUpper);

    static bool TryBound(in SweepFeatureRegime regime, double from, double to,
        double maximumFractionWidth, bool lowerBoundOnly, ref CapsuleSweepWork work, SweepExactWorkspace exact,
        out double lower, out double? upper)
    {
        lower = 0; upper = null;
        if (!exact.IsResolved || !double.IsFinite(from) || !double.IsFinite(to) || from < 0 || to > 1 || from > to ||
            !double.IsFinite(maximumFractionWidth) || maximumFractionWidth <= 0 || maximumFractionWidth > 1 ||
            !work.Cell()) return false;
        if (regime.Empty) { lower = to; return true; }
        bool scalar = exact.IsExact(regime.A) && exact.IsExact(regime.B) && exact.IsExact(regime.C);
        for (int i = 0; i < regime.ConstraintCount; i++)
            scalar &= exact.IsExact(regime.Constraints[i].Constant) && exact.IsExact(regime.Constraints[i].Slope);
        bool complete = scalar
            ? Exact(regime, from, to, maximumFractionWidth, ref work, exact, out double lo, out double? hi)
            : Enclosed(regime, from, to, maximumFractionWidth, lowerBoundOnly, ref work, exact, out lo, out hi);
        if (!complete || !exact.IsResolved) return false;
        lower = lo; upper = hi;
        return true;
    }

    static GeometrySign Compare(Fraction a, Fraction b, SweepExactWorkspace w) => w.Sign(w.Subtract(
        w.Multiply(a.Numerator, b.Denominator), w.Multiply(b.Numerator, a.Denominator)));

    static int Evaluate(in SweepFeatureRegime r, Fraction t, SweepExactWorkspace w) => w.Add(
        w.Add(w.Multiply(r.A, w.Square(t.Numerator)),
            w.Multiply(r.B, w.Multiply(t.Numerator, t.Denominator))), w.Multiply(r.C, w.Square(t.Denominator)));

    static bool NonPositive(GeometrySign sign) => sign is GeometrySign.Negative or GeometrySign.Zero;

    static bool Exact(in SweepFeatureRegime r, double from, double to, double width,
        ref CapsuleSweepWork work, SweepExactWorkspace w, out double lower, out double? upper)
    {
        lower = 0; upper = null;
        int one = w.Constant(1);
        Fraction lo = new(w.Constant(from), one), hi = new(w.Constant(to), one);
        for (int i = 0; i < r.ConstraintCount; i++)
        {
            SweepLinear constraint = r.Constraints[i];
            GeometrySign slope = w.Sign(constraint.Slope);
            if (slope == GeometrySign.Unresolved) return false;
            if (slope == GeometrySign.Zero)
            {
                if (w.Sign(constraint.Constant) == GeometrySign.Negative) { lower = to; return w.IsResolved; }
                continue;
            }
            Fraction boundary = slope == GeometrySign.Positive
                ? new(w.Negate(constraint.Constant), constraint.Slope)
                : new(constraint.Constant, w.Negate(constraint.Slope));
            GeometrySign order = Compare(boundary, slope == GeometrySign.Positive ? lo : hi, w);
            if (order == GeometrySign.Unresolved) return false;
            if (slope == GeometrySign.Positive && order == GeometrySign.Positive) lo = boundary;
            if (slope == GeometrySign.Negative && order == GeometrySign.Negative) hi = boundary;
        }
        GeometrySign feasible = Compare(lo, hi, w);
        if (feasible == GeometrySign.Unresolved) return false;
        if (feasible == GeometrySign.Positive) { lower = to; return true; }
        GeometrySign a = w.Sign(r.A);
        if (a is GeometrySign.Unresolved or GeometrySign.Negative) return false;
        GeometrySign initial = w.Sign(Evaluate(r, lo, w));
        if (initial == GeometrySign.Unresolved) return false;
        Fraction minimum = lo;
        bool starts = NonPositive(initial);
        if (!starts)
        {
            if (a == GeometrySign.Zero) minimum = w.Sign(r.B) == GeometrySign.Negative ? hi : lo;
            else
            {
                minimum = new(w.Negate(r.B), w.Multiply(w.Constant(2), r.A));
                GeometrySign left = Compare(minimum, lo, w), right = Compare(minimum, hi, w);
                if (left == GeometrySign.Unresolved || right == GeometrySign.Unresolved) return false;
                if (left == GeometrySign.Negative) minimum = lo;
                else if (right == GeometrySign.Positive) minimum = hi;
            }
            GeometrySign attained = w.Sign(Evaluate(r, minimum, w));
            if (attained == GeometrySign.Unresolved) return false;
            if (attained == GeometrySign.Positive) { lower = to; return true; }
        }
        // The complete constrained minimum proves event existence before any isolation work.
        // Cross-multiplied rational comparisons keep non-dyadic eligibility endpoints exact.
        int checkpoint = w.Checkpoint;
        if (starts && TryRepresentedFraction(lo, w, out double exactStart))
        { lower = exactStart; upper = exactStart; return true; }
        w.Restore(checkpoint);
        if (!starts && w.Sign(Evaluate(r, minimum, w)) == GeometrySign.Zero &&
            TryRepresentedFraction(minimum, w, out double exactMinimum))
        { lower = exactMinimum; upper = exactMinimum; return true; }
        w.Restore(checkpoint);
        Fraction start = new(w.Constant(from), one);
        GeometrySign atStart = Compare(lo, start, w);
        w.Restore(checkpoint);
        if (starts && atStart == GeometrySign.Zero) { lower = from; upper = from; return true; }
        if (atStart == GeometrySign.Unresolved) return false;
        double leftBound = from, rightBound = to;
        for (int depth = 0; depth < CapsuleGeneralSweepPolicy.MaximumDepth; depth++)
        {
            if (rightBound - leftBound <= width) { lower = leftBound; upper = rightBound; return true; }
            if (!work.Cell()) return false;
            double middle = leftBound + (rightBound - leftBound) * 0.5;
            if (!(middle > leftBound && middle < rightBound)) return false;
            Fraction point = new(w.Constant(middle), one);
            GeometrySign before = Compare(point, lo, w);
            bool by = false;
            if (before == GeometrySign.Unresolved) return false;
            if (before != GeometrySign.Negative)
            {
                if (starts) by = true;
                else
                {
                    GeometrySign beyondMinimum = Compare(point, minimum, w);
                    if (beyondMinimum == GeometrySign.Unresolved) return false;
                    if (beyondMinimum != GeometrySign.Negative) by = true;
                    else
                    {
                        GeometrySign value = w.Sign(Evaluate(r, point, w));
                        if (value == GeometrySign.Unresolved) return false;
                        if (value == GeometrySign.Zero) { lower = middle; upper = middle; return true; }
                        by = NonPositive(value);
                    }
                }
            }
            if (by) rightBound = middle;
            else leftBound = middle;
            w.Restore(checkpoint);
        }
        return false;
    }

    static bool TryRepresentedFraction(Fraction value, SweepExactWorkspace w, out double result)
    {
        result = 0;
        GeometryInterval quotient = w.Bounds(value.Numerator).Divide(w.Bounds(value.Denominator));
        if (!quotient.IsResolved) return false;
        double proposal = quotient.Lower + (quotient.Upper - quotient.Lower) * 0.5;
        if (!double.IsFinite(proposal) || Compare(value, new(w.Constant(proposal), w.Constant(1)), w) != GeometrySign.Zero)
            return false;
        result = proposal;
        return true;
    }

    static GeometryInterval Linear(SweepLinear value, GeometryInterval t, SweepExactWorkspace w) =>
        w.Bounds(value.Constant).Add(w.Bounds(value.Slope).Multiply(t));
    static GeometryInterval Value(in SweepFeatureRegime r, GeometryInterval t, SweepExactWorkspace w)
    {
        if (r.Distance is not SweepDistanceTerms distance)
            return w.Bounds(r.A).Multiply(t.Square()).Add(w.Bounds(r.B).Multiply(t)).Add(w.Bounds(r.C));
        GeometryInterval squared = Linear(distance.X, t, w).Square();
        if (distance.Components == 3)
            squared = squared.Add(Linear(distance.Y, t, w).Square()).Add(Linear(distance.Z, t, w).Square());
        return squared.Subtract(w.Bounds(distance.RadiusSquaredScale));
    }

    static bool Witness(in SweepFeatureRegime r, double t, SweepExactWorkspace w)
    {
        GeometryInterval point = GeometryInterval.Exact(t);
        for (int i = 0; i < r.ConstraintCount; i++)
        {
            GeometryInterval constraint = Linear(r.Constraints[i], point, w);
            if (!constraint.IsResolved || constraint.Lower < 0) return false;
        }
        GeometryInterval value = Value(r, point, w);
        return value.IsResolved && value.Upper <= 0;
    }

    static bool Enclosed(in SweepFeatureRegime r, double from, double to, double width,
        bool lowerBoundOnly, ref CapsuleSweepWork work, SweepExactWorkspace w, out double lower, out double? upper)
    {
        lower = 0; upper = null;
        if (Witness(r, from, w)) { lower = from; upper = from; return true; }
        Span<Cell> cells = stackalloc Cell[CapsuleGeneralSweepPolicy.MaximumDepth + 1];
        cells[0] = new(from, to, 0);
        int count = 1;
        bool initial = true;
        while (count > 0)
        {
            if (!initial && !work.Cell()) return false;
            initial = false;
            Cell cell = cells[--count];
            GeometryInterval t = GeometryInterval.Enclose(cell.From, cell.To);
            bool excluded = false;
            for (int i = 0; i < r.ConstraintCount; i++)
            {
                GeometryInterval condition = Linear(r.Constraints[i], t, w);
                if (!condition.IsResolved) return false;
                excluded |= condition.Upper < 0;
            }
            if (excluded) continue;
            // Center only the interval evaluation, never the original operands or trajectory.
            // This quadratic identity bounds the complete cell without the dependency loss of
            // evaluating t*t and t independently across a long interval.
            double centre = cell.From + (cell.To - cell.From) * 0.5;
            GeometryInterval point = GeometryInterval.Exact(centre), offset = t.Subtract(point);
            GeometryInterval derivative = w.Bounds(r.A).Multiply(point).Multiply(GeometryInterval.Exact(2)).Add(w.Bounds(r.B));
            GeometryInterval value = r.Distance is not null ? Value(r, t, w) :
                Value(r, point, w).Add(derivative.Multiply(offset)).Add(w.Bounds(r.A).Multiply(offset.Square()));
            if (!value.IsResolved) return false;
            if (value.Lower > 0) continue;
            if (cell.To - cell.From <= width)
            {
                // The first undecided cell can straddle a family of enclosed roots. Every
                // preceding cell is proved separated. A later robust witness within the same
                // requested width encloses that first event, even across a dyadic boundary.
                double witness = Math.Min(to, cell.From + width);
                if (witness - cell.From > width) witness = Math.BitDecrement(witness);
                if (Witness(r, witness, w)) { lower = cell.From; upper = witness; return true; }
                if (lowerBoundOnly) { lower = cell.From; return true; }
            }
            if (cell.Depth == CapsuleGeneralSweepPolicy.MaximumDepth) return false;
            double middle = cell.From + (cell.To - cell.From) * 0.5;
            if (!(middle > cell.From && middle < cell.To)) return false;
            cells[count++] = new(middle, cell.To, cell.Depth + 1);
            cells[count++] = new(cell.From, middle, cell.Depth + 1);
        }
        lower = to;
        return true;
    }
}
