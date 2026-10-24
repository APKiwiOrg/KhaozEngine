using System;
using System.Collections.Generic;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Square-free Sturm isolation with original closed domains. Failed signs never prove emptiness.</summary>
internal static class ConvexAlgebraicRoots
{
    internal static bool TryIsolate(in ConvexFeatureRegime regime, double from, double to,
        ref ConvexQueryWork work, ConvexQueryScratch scratch, out ConvexRootBracket first)
    {
        first = default;
        if (work.Exhausted || regime.Polynomial is null || !double.IsFinite(from) || !double.IsFinite(to) ||
            from < 0 || to > 1 || from > to || !double.IsFinite(scratch.MaximumFractionWidth) ||
            scratch.MaximumFractionWidth <= 0 || scratch.MaximumFractionWidth > 1) return false;
        int checkpoint = scratch.Polynomials.Used;
        try
        {
            if (!work.Feature() || !work.Cell()) return false;
            ConvexPolynomial equation = scratch.Polynomials.Import(regime.Polynomial);
            if (equation.IsZero)
            {
                if (FeasibleAt(regime, from, ref work)) { first = new(true, true, from, from); return true; }
                // For an identically zero equation, a nonempty closed feasible set starts at
                // a domain boundary or at from. The product retains all those boundaries.
                equation = scratch.Polynomials.Constant(ConvexRational.One);
                foreach (var domain in regime.Domains ?? [])
                {
                    if (!work.Feature()) throw new ConvexExactRefusalException();
                    if (!domain.IsZero) equation = equation.Multiply(domain);
                }
                foreach (var radical in regime.RadicalDomains ?? [])
                {
                    if (!work.Feature()) throw new ConvexExactRefusalException();
                    ConvexPolynomial zero = radical.SquaredDifference(scratch.Polynomials);
                    if (!zero.IsZero) equation = equation.Multiply(zero);
                    else
                    {
                        // Squaring can erase the complete equation while the original
                        // inequality still changes sign at an unsquared coefficient zero.
                        if (!radical.K.IsZero) equation = equation.Multiply(radical.K);
                        if (!radical.L.IsZero) equation = equation.Multiply(radical.L);
                    }
                    if (!radical.D.IsZero) equation = equation.Multiply(radical.D);
                }
            }
            if (equation.IsZero || equation.Degree == 0)
            { first = new(true, false, from, to); return true; }
            ConvexPolynomial squareFree = SquareFree(equation, ref work);
            if (squareFree.Evaluate(ConvexRational.From(from)).IsZero && FeasibleAt(regime, from, ref work))
            { first = new(true, true, from, from); return true; }
            ConvexPolynomial[] sturm = Sturm(squareFree, ref work);
            int pending = 1;
            scratch.RootCells[0] = new(from, to, 0);
            while (pending > 0)
            {
                if (!work.Cell()) return false;
                ConvexRootCell cell = scratch.RootCells[--pending];
                int roots = Count(sturm, cell.Lower, cell.Upper, ref work);
                if (roots == 0) continue;
                if (roots == 1)
                {
                    if (squareFree.Evaluate(ConvexRational.From(cell.Upper)).IsZero)
                    {
                        if (!FeasibleAt(regime, cell.Upper, ref work)) continue;
                        first = new(true, true, cell.Upper, cell.Upper); return true;
                    }
                    // A unique infeasible root needs no spatially narrow bracket. Prove the
                    // original domains first, then retain that proof while narrowing this root.
                    if (!FeasibleRoot(regime, squareFree, sturm, ref cell, ref work)) continue;
                    if (!NarrowRoot(squareFree, sturm, ref cell, scratch.MaximumFractionWidth, ref work)) return false;
                    first = new(true, true, cell.Lower, cell.Upper); return true;
                }
                Split(cell, out var left, out var right);
                if (pending + 2 > scratch.RootCells.Length) throw new ConvexExactRefusalException();
                scratch.RootCells[pending++] = right;
                scratch.RootCells[pending++] = left;
            }
            first = new(true, false, from, to);
            return true;
        }
        catch (ConvexExactRefusalException)
        {
            work.Refuse();
            first = default;
            return false;
        }
        finally { scratch.Polynomials.Restore(checkpoint); }
    }

    // The retained equation and Sturm chain live in the existing coefficient arena.
    // Sign/refine operations restore only their temporaries and never reset query work.
    internal static bool TryRetain(ConvexPolynomial polynomial, double from, double to,
        ref ConvexQueryWork work, ConvexQueryScratch scratch, out Retained root)
    {
        root = null!;
        if (work.Exhausted || work.Descendants == 0 || !ConvexIntervalCertificate.ValidInterval(from, to)) return false;
        int checkpoint = scratch.Polynomials.Used;
        bool retained = false;
        try
        {
            if (!work.Feature() || !work.Cell()) return false;
            var equation = SquareFree(scratch.Polynomials.Import(polynomial), ref work);
            if (equation.Degree == 0) return false;
            var chain = Sturm(equation, ref work);
            var cell = new ConvexRootCell(from, to, 0);
            if (equation.Evaluate(ConvexRational.From(from)).IsZero) cell = new(from, from, 0);
            else if (Count(chain, from, to, ref work) != 1) return false;
            root = new(equation, chain, cell, scratch);
            retained = true;
            return true;
        }
        catch (ConvexExactRefusalException) { return work.Refuse(); }
        finally { if (!retained) scratch.Polynomials.Restore(checkpoint); }
    }

    internal sealed class Retained(ConvexPolynomial equation, ConvexPolynomial[] chain,
        ConvexRootCell cell, ConvexQueryScratch owner)
    {
        readonly int _generation = owner.CertificateGeneration, _floor = owner.Polynomials.Used;
        ConvexRootCell _cell = cell;
        internal bool IsAlive => _generation == owner.CertificateGeneration && owner.Polynomials.Used >= _floor;
        internal GeometryInterval Bounds => IsAlive ? GeometryInterval.Enclose(_cell.Lower, _cell.Upper) : default;
        internal ConvexQueryScratch Owner => owner;
        internal bool TrySign(ConvexPolynomial predicate, ref ConvexQueryWork work, ConvexQueryScratch scratch, out int sign)
        {
            sign = 0;
            if (!IsAlive || !ReferenceEquals(owner, scratch) || work.Exhausted) return false;
            int checkpoint = scratch.Polynomials.Used;
            try { sign = SignAtRoot(predicate, equation, chain, ref _cell, ref work); return true; }
            catch (ConvexExactRefusalException) { return work.Refuse(); }
            finally { scratch.Polynomials.Restore(checkpoint); }
        }
        internal bool TryRadicalSign(ConvexRadicalPredicate predicate, ref ConvexQueryWork work, ConvexQueryScratch scratch, out int sign)
        {
            sign = 0;
            if (!IsAlive || !ReferenceEquals(owner, scratch) || work.Exhausted) return false;
            int checkpoint = scratch.Polynomials.Used;
            try
            {
                if (!TrySign(predicate.D, ref work, scratch, out int d) || d < 0 ||
                    !TrySign(predicate.K, ref work, scratch, out int k) || !TrySign(predicate.L, ref work, scratch, out int l)) return false;
                if (d == 0 || l == 0) { sign = k; return true; }
                if (k == 0 || k == l) { sign = l; return true; }
                if (!TrySign(predicate.SquaredDifference(scratch.Polynomials), ref work, scratch, out int square)) return false;
                sign = RadicalSign(k, l, d, square);
                return true;
            }
            catch (ConvexExactRefusalException) { return work.Refuse(); }
            finally { scratch.Polynomials.Restore(checkpoint); }
        }
        internal bool TryRefine(double width, ref ConvexQueryWork work, ConvexQueryScratch scratch)
        {
            if (!IsAlive || !ReferenceEquals(owner, scratch) || !double.IsFinite(width) || width <= 0 || work.Exhausted) return false;
            try { return NarrowRoot(equation, chain, ref _cell, width, ref work); }
            catch (ConvexExactRefusalException) { return work.Refuse(); }
        }
    }

    static ConvexPolynomial Normalize(ConvexPolynomial value)
    {
        ConvexRational leading = value[value.Degree];
        // A positive scale preserves every variation in the Sturm chain.
        return value.IsZero ? value : value.Scale(ConvexRational.One / (leading.Sign < 0 ? -leading : leading));
    }

    static bool NarrowEnough(ConvexRootCell cell, double width, ref ConvexQueryWork work)
    {
        if (!work.Feature()) throw new ConvexExactRefusalException();
        return (ConvexRational.From(cell.Upper) - ConvexRational.From(cell.Lower))
            .CompareTo(ConvexRational.From(width)) <= 0;
    }

    static bool NarrowRoot(ConvexPolynomial equation, ConvexPolynomial[] chain, ref ConvexRootCell cell,
        double width, ref ConvexQueryWork work)
    {
        while (!NarrowEnough(cell, width, ref work))
        {
            if (!work.Cell()) return false;
            Split(cell, out var left, out var right);
            // The parent contains exactly one root. Its absence from the left half proves
            // it is in the right half, without visiting an additional empty pending cell.
            cell = Count(chain, left.Lower, left.Upper, ref work) > 0 ? left : right;
            if (equation.Evaluate(ConvexRational.From(cell.Upper)).IsZero)
                cell = new(cell.Upper, cell.Upper, cell.Depth);
        }
        return true;
    }

    static ConvexPolynomial Gcd(ConvexPolynomial a, ConvexPolynomial b, ref ConvexQueryWork work)
    {
        b = a.Workspace!.Import(b);
        while (!b.IsZero)
        {
            a.Divide(b, ref work, out var remainder);
            a = b; b = Normalize(remainder);
        }
        return Normalize(a);
    }

    static ConvexPolynomial SquareFree(ConvexPolynomial polynomial, ref ConvexQueryWork work)
    {
        ConvexPolynomial divisor = Gcd(polynomial, polynomial.Derivative(), ref work);
        ConvexPolynomial result = polynomial.Divide(divisor, ref work, out var remainder);
        if (!remainder.IsZero) throw new ConvexExactRefusalException();
        return Normalize(result);
    }

    static ConvexPolynomial[] Sturm(ConvexPolynomial polynomial, ref ConvexQueryWork work)
    {
        var chain = new List<ConvexPolynomial> { polynomial };
        if (polynomial.Degree == 0) return chain.ToArray();
        chain.Add(Normalize(polynomial.Derivative()));
        while (chain[^1].Degree > 0)
        {
            chain[^2].Divide(chain[^1], ref work, out var remainder);
            if (remainder.IsZero) break;
            chain.Add(Normalize(remainder.Scale(-ConvexRational.One)));
        }
        return chain.ToArray();
    }

    static int Variations(ConvexPolynomial[] chain, double at, ref ConvexQueryWork work)
    {
        int previous = 0, changes = 0;
        ConvexRational point = ConvexRational.From(at);
        foreach (var polynomial in chain)
        {
            if (!work.Feature()) throw new ConvexExactRefusalException();
            int sign = polynomial.Evaluate(point).Sign;
            if (sign == 0) continue;
            if (previous != 0 && previous != sign) changes++;
            previous = sign;
        }
        return changes;
    }

    // With zeros omitted, endpoint variations count (lower, upper]. The original
    // closed lower endpoint is checked separately, including all closed predicates.
    static int Count(ConvexPolynomial[] chain, double lower, double upper, ref ConvexQueryWork work)
    {
        int count = Variations(chain, lower, ref work) - Variations(chain, upper, ref work);
        if (count < 0) throw new ConvexExactRefusalException();
        return count;
    }

    static void Split(ConvexRootCell cell, out ConvexRootCell left, out ConvexRootCell right)
    {
        if (cell.Depth >= ConvexQueryPolicy.MaximumDepth) throw new ConvexExactRefusalException();
        double middle = cell.Lower + (cell.Upper - cell.Lower) * 0.5;
        if (!(middle > cell.Lower && middle < cell.Upper)) throw new ConvexExactRefusalException();
        left = new(cell.Lower, middle, cell.Depth + 1);
        right = new(middle, cell.Upper, cell.Depth + 1);
    }

    static bool FeasibleAt(in ConvexFeatureRegime regime, double at, ref ConvexQueryWork work)
    {
        ConvexRational point = ConvexRational.From(at);
        foreach (var predicate in regime.Domains ?? [])
        {
            if (!work.Feature()) throw new ConvexExactRefusalException();
            if (predicate.Evaluate(point).Sign < 0) return false;
        }
        foreach (var predicate in regime.RadicalDomains ?? [])
        {
            if (!work.Feature()) throw new ConvexExactRefusalException();
            ConvexRational kValue = predicate.K.Evaluate(point), lValue = predicate.L.Evaluate(point);
            ConvexRational dValue = predicate.D.Evaluate(point);
            int k = kValue.Sign, l = lValue.Sign, d = dValue.Sign;
            if (d < 0) return false;
            int square = (kValue * kValue - lValue * lValue * dValue).Sign;
            if (RadicalSign(k, l, d, square) < 0) return false;
        }
        return true;
    }

    static int RadicalSign(int k, int l, int d, int square)
    {
        if (d == 0 || l == 0) return k;
        if (k == 0 || k == l) return l;
        return k * square;
    }

    static bool FeasibleRoot(in ConvexFeatureRegime regime, ConvexPolynomial equation,
        ConvexPolynomial[] chain, ref ConvexRootCell cell, ref ConvexQueryWork work)
    {
        foreach (var predicate in regime.Domains ?? [])
            if (SignAtRoot(predicate, equation, chain, ref cell, ref work) < 0) return false;
        foreach (var predicate in regime.RadicalDomains ?? [])
        {
            int d = SignAtRoot(predicate.D, equation, chain, ref cell, ref work);
            if (d < 0) return false;
            int k = SignAtRoot(predicate.K, equation, chain, ref cell, ref work);
            int l = SignAtRoot(predicate.L, equation, chain, ref cell, ref work);
            int square = SignAtRoot(predicate.SquaredDifference(equation.Workspace!), equation, chain, ref cell, ref work);
            if (RadicalSign(k, l, d, square) < 0) return false;
        }
        return true;
    }

    static int SignAtRoot(ConvexPolynomial predicate, ConvexPolynomial equation,
        ConvexPolynomial[] chain, ref ConvexRootCell cell, ref ConvexQueryWork work)
    {
        if (!work.Feature()) throw new ConvexExactRefusalException();
        if (predicate.IsZero) return 0;
        if (cell.Lower == cell.Upper) return predicate.Evaluate(ConvexRational.From(cell.Lower)).Sign;
        ConvexPolynomial gcd = Gcd(equation, predicate, ref work);
        if (gcd.Degree > 0 && Count(Sturm(gcd, ref work), cell.Lower, cell.Upper, ref work) > 0) return 0;
        while (true)
        {
            int sign = RangeSign(predicate, cell.Lower, cell.Upper, ref work);
            if (sign != 0) return sign;
            if (!work.Cell()) throw new ConvexExactRefusalException();
            Split(cell, out var left, out var right);
            cell = Count(chain, left.Lower, left.Upper, ref work) > 0 ? left : right;
            if (equation.Evaluate(ConvexRational.From(cell.Upper)).IsZero)
            {
                cell = new(cell.Upper, cell.Upper, cell.Depth);
                return predicate.Evaluate(ConvexRational.From(cell.Upper)).Sign;
            }
        }
    }

    static int RangeSign(ConvexPolynomial polynomial, double from, double to, ref ConvexQueryWork work)
    {
        ConvexRational a = ConvexRational.From(from), b = ConvexRational.From(to);
        ConvexRational lower = polynomial[polynomial.Degree], upper = lower;
        for (int i = polynomial.Degree - 1; i >= 0; i--)
        {
            if (!work.Feature()) throw new ConvexExactRefusalException();
            ConvexRational la = lower * a, lb = lower * b, ua = upper * a, ub = upper * b;
            lower = Min(Min(la, lb), Min(ua, ub)) + polynomial[i];
            upper = Max(Max(la, lb), Max(ua, ub)) + polynomial[i];
        }
        return lower.Sign > 0 ? 1 : upper.Sign < 0 ? -1 : 0;
    }

    static ConvexRational Min(ConvexRational a, ConvexRational b) => a.CompareTo(b) <= 0 ? a : b;
    static ConvexRational Max(ConvexRational a, ConvexRational b) => a.CompareTo(b) >= 0 ? a : b;
}
