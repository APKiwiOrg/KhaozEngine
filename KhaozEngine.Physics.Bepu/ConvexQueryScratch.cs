using System;
using System.Numerics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Query-private exact expressions, interval pending cells and operational simplex proposals.</summary>
internal sealed class ConvexQueryScratch
{
    internal ConvexQueryScratch(int maximumCoefficients = SweepExactWorkspace.MaximumNumbers)
    {
        if (maximumCoefficients < 0 || maximumCoefficients > SweepExactWorkspace.MaximumNumbers)
            throw new ArgumentOutOfRangeException(nameof(maximumCoefficients));
        Polynomials = new(maximumCoefficients);
    }

    internal SweepExactWorkspace Exact { get; } = new();
    internal ConvexPolynomialWorkspace Polynomials { get; }
    internal ConvexRootCell[] RootCells { get; } = new ConvexRootCell[ConvexQueryPolicy.MaximumDepth + 1];
    internal Vector3[] ProposalSimplex { get; } = new Vector3[4];
    internal ConvexCircularEdges CircularEdges { get; } = new();
    // Callers derive a fraction width from the actual path and the existing spatial encoding budget.
    internal double MaximumFractionWidth { get; set; } = 0.001f / 4d;
}

internal readonly record struct ConvexRootCell(double Lower, double Upper, int Depth);

/// <summary>A fixed coefficient arena. Exhaustion has no managed-array fallback.</summary>
internal sealed class ConvexPolynomialWorkspace(int capacity)
{
    readonly ConvexRational[] _coefficients = new ConvexRational[capacity];
    internal int Used { get; private set; }
    internal int HighWater { get; private set; }

    internal ConvexPolynomial Create(int length)
    {
        if (length <= 0 || length > _coefficients.Length - Used) throw new ConvexExactRefusalException();
        int offset = Used;
        Used += length;
        HighWater = Math.Max(HighWater, Used);
        return new(this, _coefficients, offset, length);
    }

    internal ConvexPolynomial Constant(ConvexRational value)
    {
        ConvexPolynomial result = Create(1);
        result.Set(0, value);
        return result;
    }

    internal ConvexPolynomial Import(ConvexPolynomial value)
    {
        if (ReferenceEquals(value.Workspace, this)) return value;
        ConvexPolynomial result = Create(value.Degree + 1);
        for (int i = 0; i <= value.Degree; i++) result.Set(i, value[i]);
        return result;
    }

    internal void Restore(int checkpoint)
    {
        Array.Clear(_coefficients, checkpoint, Used - checkpoint);
        Used = checkpoint;
    }
}
