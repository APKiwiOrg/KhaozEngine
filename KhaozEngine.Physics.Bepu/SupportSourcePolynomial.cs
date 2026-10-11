using System;
using System.Runtime.CompilerServices;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Exact coefficients of a polynomial in the shared installed-map coefficients. Variables are
/// offsets from the lower coefficient endpoints. Equal monomials are combined before interval evaluation,
/// so source identities cancel over the whole coefficient box, including a point map.</summary>
internal sealed class SupportSourcePolynomialWorkspace
{
    // The spatial interior radical comparison is the largest graph, with at most 51 rented nodes.
    const int MaximumDegree = 8, MaximumVariables = 9, MaximumPolynomials = 64;
    readonly SupportSourcePolynomial[] _polynomials = new SupportSourcePolynomial[MaximumPolynomials];
    readonly GeometryInterval[] _variables = new GeometryInterval[MaximumVariables];
    int _used, _variableCount;
    internal bool IsResolved { get; private set; }

    internal SupportSourcePolynomialWorkspace()
    {
        for (int i = 0; i < _polynomials.Length; i++) _polynomials[i] = new();
    }

    internal void Reset()
    {
        _used = _variableCount = 0;
        IsResolved = true;
    }

    internal SupportSourcePolynomial Constant(double value)
    {
        var result = Rent(0);
        result.AddScalar(0, value);
        return result;
    }

    internal SupportSourcePolynomial Coefficient(GeometryInterval box)
    {
        if (!box.IsResolved) { IsResolved = false; return Constant(0); }
        var result = Rent(1);
        result.AddScalar(0, box.Lower);
        if (box.Lower == box.Upper) return result;
        if (_variableCount == MaximumVariables) { IsResolved = false; return result; }
        GeometryInterval width = GeometryInterval.Exact(box.Upper).Subtract(GeometryInterval.Exact(box.Lower));
        _variables[_variableCount] = GeometryInterval.Enclose(0, width.Upper);
        result.AddScalar(1UL << (4 * _variableCount++), 1);
        return result;
    }

    // All nine coefficient variables are installed before other expressions are rented.
    internal void CompleteMap() => _used = 9;

    internal SupportSourcePolynomial Linear(SupportSourcePolynomial x, SupportSourcePolynomial y,
        SupportSourcePolynomial z, System.Numerics.Vector3 a, System.Numerics.Vector3 b, double translation)
    {
        var result = Rent(1);
        Accumulate(result, x, a.X, b.X);
        Accumulate(result, y, a.Y, b.Y);
        Accumulate(result, z, a.Z, b.Z);
        result.AddScalar(0, translation);
        IsResolved &= result.IsResolved;
        return result;
    }

    static void Accumulate(SupportSourcePolynomial result, SupportSourcePolynomial value, float a, float b)
    {
        for (int i = 0; i < value.Count; i++)
        {
            result.AddScaled(value.Keys[i], value.Values[i], a);
            result.AddScaled(value.Keys[i], value.Values[i], -b);
        }
    }

    internal SupportSourcePolynomial Scale(SupportSourcePolynomial value, double scalar)
    {
        var result = Rent(value.Degree);
        for (int i = 0; i < value.Count; i++)
            result.AddScaled(value.Keys[i], value.Values[i], scalar);
        IsResolved &= value.IsResolved && result.IsResolved;
        return result;
    }

    internal SupportSourcePolynomial Add(SupportSourcePolynomial a, SupportSourcePolynomial b, int sign = 1)
    {
        var result = Rent(Math.Max(a.Degree, b.Degree));
        for (int i = 0; i < a.Count; i++) result.Add(a.Keys[i], a.Values[i]);
        for (int i = 0; i < b.Count; i++) result.Add(b.Keys[i], b.Values[i], sign);
        IsResolved &= a.IsResolved && b.IsResolved && result.IsResolved;
        return result;
    }

    internal SupportSourcePolynomial Multiply(SupportSourcePolynomial a, SupportSourcePolynomial b)
    {
        var result = Rent(a.Degree + b.Degree);
        for (int i = 0; i < a.Count; i++)
            for (int j = 0; j < b.Count; j++)
            {
                if (a.Values[i].IsZero || b.Values[j].IsZero) continue;
                result.AddProduct(a.Keys[i] + b.Keys[j], a.Values[i], b.Values[j]);
            }
        IsResolved &= a.IsResolved && b.IsResolved && result.IsResolved;
        return result;
    }

    internal GeometrySign Sign(SupportSourcePolynomial value)
    {
        if (!IsResolved || !value.IsResolved) return GeometrySign.Unresolved;
        bool variableTerms = false;
        int constant = -1;
        for (int i = 0; i < value.Count; i++)
        {
            if (value.Keys[i] == 0) constant = i;
            else variableTerms |= !value.Values[i].IsZero;
        }
        if (!variableTerms) return constant < 0 ? GeometrySign.Zero : value.Values[constant].Sign;
        bool zero = true;
        GeometryInterval total = GeometryInterval.Exact(0);
        for (int i = 0; i < value.Count; i++)
        {
            if (value.Values[i].IsZero) continue;
            zero = false;
            GeometryInterval term = value.Values[i].Bounds;
            ulong key = value.Keys[i];
            for (int variable = 0; variable < _variableCount; variable++, key >>= 4)
            {
                int power = (int)(key & 15);
                for (int p = 0; p < power; p++) term = term.Multiply(_variables[variable]);
            }
            total = total.Add(term);
        }
        return zero ? GeometrySign.Zero : !total.IsResolved ? GeometrySign.Unresolved :
            total.Lower > 0 ? GeometrySign.Positive : total.Upper < 0 ? GeometrySign.Negative : GeometrySign.Unresolved;
    }

    SupportSourcePolynomial Rent(int degree)
    {
        if (_used == MaximumPolynomials || degree > MaximumDegree)
            throw new InvalidOperationException("Source predicate exceeded its polynomial bound.");
        var result = _polynomials[_used++];
        // The complete map may introduce all nine variables. Subsequent expressions use the actual count.
        int variables = _used <= 9 ? MaximumVariables : _variableCount;
        int capacity = 1;
        for (int i = 1; i <= degree; i++) capacity = capacity * (variables + i) / i;
        result.Reset(degree, capacity);
        return result;
    }
}

internal sealed class SupportSourcePolynomial
{
    int[] _slots = [];
    internal ulong[] Keys { get; private set; } = [];
    internal SupportSourceCoefficient[] Values { get; private set; } = [];
    internal int Count { get; private set; }
    internal int Degree { get; private set; }
    internal bool IsResolved { get; private set; }

    internal void Reset(int degree, int capacity)
    {
        if (Keys.Length < capacity)
        {
            Keys = new ulong[capacity];
            Values = new SupportSourceCoefficient[capacity];
            int slots = 2;
            while (slots < 2 * capacity) slots <<= 1;
            _slots = new int[slots];
        }
        else Array.Clear(_slots);
        Count = 0;
        Degree = degree;
        IsResolved = true;
    }

    internal void Add(ulong key, in SupportSourceCoefficient value, int sign = 1)
    {
        IsResolved &= value.IsResolved;
        if (!IsResolved || value.IsZero) return;
        int index = Index(key);
        if (index < 0) return;
        Values[index].Add(value, sign);
        IsResolved &= Values[index].IsResolved;
    }

    internal void AddScalar(ulong key, double value)
    {
        if (value == 0 || !IsResolved) return;
        int index = Index(key);
        if (index < 0) return;
        Values[index].AddScalar(value);
        IsResolved &= Values[index].IsResolved;
    }

    internal void AddScaled(ulong key, in SupportSourceCoefficient value, double scalar)
    {
        IsResolved &= value.IsResolved && double.IsFinite(scalar);
        if (!IsResolved || value.IsZero || scalar == 0) return;
        int index = Index(key);
        if (index < 0) return;
        Values[index].AddScaled(value, scalar);
        IsResolved &= Values[index].IsResolved;
    }

    internal void AddProduct(ulong key, in SupportSourceCoefficient a, in SupportSourceCoefficient b)
    {
        IsResolved &= a.IsResolved && b.IsResolved;
        if (!IsResolved || a.IsZero || b.IsZero) return;
        int index = Index(key);
        if (index < 0) return;
        Values[index].AddProduct(a, b);
        IsResolved &= Values[index].IsResolved;
    }

    int Index(ulong key)
    {
        int slot = (int)((key * 11400714819323198485UL) >> 32) & (_slots.Length - 1);
        while (_slots[slot] != 0 && Keys[_slots[slot] - 1] != key) slot = (slot + 1) & (_slots.Length - 1);
        int index = _slots[slot] - 1;
        if (index < 0)
        {
            if (Count == Keys.Length) { IsResolved = false; return -1; }
            index = Count++;
            _slots[slot] = index + 1;
            Keys[index] = key;
            Values[index].Reset();
        }
        return index;
    }
}

/// <summary>A bounded nonoverlapping expansion with explicit binary exponents. Two-Sum and fused
/// Two-Product operate on normalized significands, so degree-eight source products cannot underflow.
/// The 128 components cover the shared 4096-bit predicate domain, with spare components for growth.</summary>
internal struct SupportSourceCoefficient
{
    readonly record struct Part(double Value, int Exponent)
    {
        internal static Part Create(double value, int exponent = 0)
        {
            if (value == 0) return default;
            int scale = Math.ILogB(value);
            return new(Math.ScaleB(value, -scale), exponent + scale);
        }
    }

    [InlineArray(128)]
    struct Components { Part _first; }
    Components _parts;
    int _count;
    internal bool IsResolved { get; private set; }
    internal readonly bool IsZero => IsResolved && (_count == 0 || _parts[_count - 1].Value == 0);
    internal readonly GeometrySign Sign => !IsResolved ? GeometrySign.Unresolved : IsZero ? GeometrySign.Zero :
        _parts[_count - 1].Value > 0 ? GeometrySign.Positive : GeometrySign.Negative;
    internal readonly GeometryInterval Bounds
    {
        get
        {
            if (!IsResolved) return default;
            GeometryInterval result = GeometryInterval.Exact(0);
            for (int i = 0; i < _count; i++)
            {
                Part part = _parts[i];
                double value = Math.ScaleB(part.Value, part.Exponent);
                if (!double.IsFinite(value)) return default;
                GeometryInterval term = part.Exponent >= -1022 ? GeometryInterval.Exact(value) :
                    GeometryInterval.Enclose(Math.BitDecrement(value), Math.BitIncrement(value));
                result = result.Add(term);
            }
            return result;
        }
    }

    internal void Reset()
    {
        _count = 0;
        IsResolved = true;
    }

    internal void AddScalar(double value)
    {
        IsResolved &= double.IsFinite(value);
        if (IsResolved) Grow(Part.Create(value));
    }

    internal void Add(in SupportSourceCoefficient other, int sign)
    {
        IsResolved &= other.IsResolved;
        for (int i = 0; i < other._count && IsResolved; i++)
            Grow(new(sign * other._parts[i].Value, other._parts[i].Exponent));
    }

    internal void AddProduct(in SupportSourceCoefficient a, in SupportSourceCoefficient b)
    {
        IsResolved &= a.IsResolved && b.IsResolved;
        for (int i = 0; i < a._count && IsResolved; i++)
            for (int j = 0; j < b._count && IsResolved; j++) AddProduct(a._parts[i], b._parts[j]);
    }

    internal void AddScaled(in SupportSourceCoefficient value, double scalar)
    {
        IsResolved &= value.IsResolved && double.IsFinite(scalar);
        if (!IsResolved || scalar == 0) return;
        Part factor = Part.Create(scalar);
        for (int i = 0; i < value._count && IsResolved; i++) AddProduct(value._parts[i], factor);
    }

    void AddProduct(Part a, Part b)
    {
        if (a.Value == 0 || b.Value == 0) return;
        int exponent = a.Exponent + b.Exponent;
        double product = a.Value * b.Value;
        Grow(Part.Create(Math.FusedMultiplyAdd(a.Value, b.Value, -product), exponent));
        Grow(Part.Create(product, exponent));
    }

    void Grow(Part term)
    {
        if (!IsResolved || term.Value == 0) return;
        Part q = term;
        int kept = 0;
        for (int i = 0; i < _count; i++)
        {
            Part next = _parts[i], error;
            if (q.Value == 0) { q = next; continue; }
            // Disjoint significands cannot change the larger rounded component. Keep the smaller
            // as the exact error instead of scaling it through binary64's underflow boundary.
            if (q.Exponent - next.Exponent > 54) error = next;
            else if (next.Exponent - q.Exponent > 54) { error = q; q = next; }
            else
            {
                int exponent = Math.Max(q.Exponent, next.Exponent);
                ExactExpansion.TwoSum(Math.ScaleB(q.Value, q.Exponent - exponent),
                    Math.ScaleB(next.Value, next.Exponent - exponent), out double sum, out double residual);
                q = Part.Create(sum, exponent);
                error = Part.Create(residual, exponent);
            }
            if (error.Value != 0) _parts[kept++] = error;
        }
        if (q.Value != 0 || kept == 0)
        {
            if (kept == 128) { IsResolved = false; return; }
            _parts[kept++] = q;
        }
        _count = kept;
    }
}
