using System;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Exact coefficients of a polynomial in the shared installed-map coefficients. Variables are
/// offsets from the lower coefficient endpoints. Equal monomials are combined before interval evaluation,
/// so source identities cancel over the whole coefficient box, including a point map.</summary>
internal sealed class SupportSourcePolynomialWorkspace
{
    // The spatial interior radical comparison is the largest graph, with at most 51 rented nodes.
    const int MaximumDegree = 8, MaximumVariables = 9, MaximumPolynomials = 64;
    readonly SupportSourcePolynomial[] _polynomials = new SupportSourcePolynomial[MaximumPolynomials];
    readonly SupportSourceCoefficient.BufferPool _buffers = new();
    readonly GeometryInterval[] _variables = new GeometryInterval[MaximumVariables];
    int _used, _variableCount;
    internal bool IsResolved { get; private set; }

    internal SupportSourcePolynomialWorkspace()
    {
        for (int i = 0; i < _polynomials.Length; i++) _polynomials[i] = new(_buffers);
    }

    internal void Reset()
    {
        foreach (SupportSourcePolynomial polynomial in _polynomials) polynomial.ReleaseCoefficients();
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
        int maximumTerms = 1;
        for (int i = 1; i <= degree; i++) maximumTerms = maximumTerms * (variables + i) / i;
        result.Reset(degree, maximumTerms);
        return result;
    }
}

/// <summary>Only nonzero monomials are live. Removed terms recycle their coefficient storage, and both
/// the term table and individual expansions grow on demand and retain their high-water for warm reuse.</summary>
internal sealed class SupportSourcePolynomial
{
    readonly SupportSourceCoefficient.BufferPool _buffers;
    int[] _slots = [];
    ulong[] _keys = [];
    SupportSourceCoefficient[] _values = [];
    int _maximumTerms;
    internal ulong[] Keys => _keys;
    internal SupportSourceCoefficient[] Values => _values;
    internal int Count { get; private set; }
    internal int Degree { get; private set; }
    internal bool IsResolved { get; private set; }

    internal SupportSourcePolynomial(SupportSourceCoefficient.BufferPool buffers) => _buffers = buffers;

    internal void ReleaseCoefficients()
    {
        foreach (SupportSourceCoefficient? value in Values) value?.Reset();
    }

    internal void Reset(int degree, int maximumTerms)
    {
        Array.Clear(_slots);
        Count = 0;
        Degree = degree;
        _maximumTerms = maximumTerms;
        IsResolved = true;
    }

    internal void Add(ulong key, SupportSourceCoefficient value, int sign = 1)
    {
        IsResolved &= value.IsResolved;
        if (!IsResolved || value.IsZero) return;
        int index = Index(key);
        if (index < 0) return;
        Values[index].Add(value, sign);
        Complete(index);
    }

    internal void AddScalar(ulong key, double value)
    {
        if (value == 0 || !IsResolved) return;
        int index = Index(key);
        if (index < 0) return;
        Values[index].AddScalar(value);
        Complete(index);
    }

    internal void AddScaled(ulong key, SupportSourceCoefficient value, double scalar)
    {
        IsResolved &= value.IsResolved && double.IsFinite(scalar);
        if (!IsResolved || value.IsZero || scalar == 0) return;
        int index = Index(key);
        if (index < 0) return;
        Values[index].AddScaled(value, scalar);
        Complete(index);
    }

    internal void AddProduct(ulong key, SupportSourceCoefficient a, SupportSourceCoefficient b)
    {
        IsResolved &= a.IsResolved && b.IsResolved;
        if (!IsResolved || a.IsZero || b.IsZero) return;
        int index = Index(key);
        if (index < 0) return;
        Values[index].AddProduct(a, b);
        Complete(index);
    }

    void Complete(int index)
    {
        IsResolved &= Values[index].IsResolved;
        if (!IsResolved || !Values[index].IsZero) return;
        // Keep live terms compact without losing the removed coefficient's reusable buffer.
        _slots[Find(Keys[index])] = -1;
        int last = --Count;
        if (last != index)
        {
            (_values[index], _values[last]) = (_values[last], _values[index]);
            Keys[index] = Keys[last];
            _slots[Find(Keys[index])] = index + 1;
        }
        Keys[last] = 0;
    }

    int Find(ulong key)
    {
        int slot = (int)((key * 11400714819323198485UL) >> 32) & (_slots.Length - 1);
        int removed = -1;
        for (int probes = 0; probes < _slots.Length; probes++, slot = (slot + 1) & (_slots.Length - 1))
        {
            int entry = _slots[slot];
            if (entry > 0 && Keys[entry - 1] == key) return slot;
            if (entry < 0 && removed < 0) removed = slot;
            if (entry == 0) return removed < 0 ? slot : removed;
        }
        return removed;
    }

    int Index(ulong key)
    {
        int slot = _slots.Length == 0 ? -1 : Find(key);
        if (slot >= 0 && _slots[slot] > 0) return _slots[slot] - 1;
        if (Count == _maximumTerms) { IsResolved = false; return -1; }
        if (Count == Keys.Length)
        {
            int capacity = Math.Min(_maximumTerms, Math.Max(1, 2 * Keys.Length));
            Array.Resize(ref _keys, capacity);
            Array.Resize(ref _values, capacity);
            int slots = 2;
            while (slots < 2 * capacity) slots <<= 1;
            _slots = new int[slots];
            for (int i = 0; i < Count; i++) _slots[Find(Keys[i])] = i + 1;
            slot = Find(key);
        }
        if (slot < 0) { IsResolved = false; return -1; }
        int index = Count++;
        _slots[slot] = index + 1;
        Keys[index] = key;
        (_values[index] ??= new(_buffers)).Reset();
        return index;
    }
}
