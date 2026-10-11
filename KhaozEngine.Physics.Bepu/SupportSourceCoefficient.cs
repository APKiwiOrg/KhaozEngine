using System;
using System.Numerics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>A bounded nonoverlapping expansion with explicit binary exponents. Two-Sum and fused
/// Two-Product operate on normalized significands, so degree-eight source products cannot underflow.
/// Up to 128 components cover the shared 4096-bit predicate domain, with spare components for growth.
/// Storage grows only to the observed component count and is reused by the workspace.</summary>
internal sealed class SupportSourceCoefficient
{
    internal readonly record struct Part(double Value, int Exponent)
    {
        internal static Part Create(double value, int exponent = 0)
        {
            if (value == 0) return default;
            int scale = Math.ILogB(value);
            return new(Math.ScaleB(value, -scale), exponent + scale);
        }
    }

    const int MaximumParts = 128;
    readonly BufferPool _pool;
    Buffer? _buffer;
    Part[] _parts = [];
    int _count;
    internal bool IsResolved { get; private set; }
    internal bool IsZero => IsResolved && (_count == 0 || _parts[_count - 1].Value == 0);
    internal GeometrySign Sign => !IsResolved ? GeometrySign.Unresolved : IsZero ? GeometrySign.Zero :
        _parts[_count - 1].Value > 0 ? GeometrySign.Positive : GeometrySign.Negative;
    internal GeometryInterval Bounds
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

    internal SupportSourceCoefficient(BufferPool? pool = null) => _pool = pool ?? new();

    internal void Reset()
    {
        _pool.Return(_buffer);
        _buffer = null;
        _parts = [];
        _count = 0;
        IsResolved = true;
    }

    internal void AddScalar(double value)
    {
        IsResolved &= double.IsFinite(value);
        if (IsResolved) Grow(Part.Create(value));
    }

    internal void Add(SupportSourceCoefficient other, int sign)
    {
        IsResolved &= other.IsResolved;
        for (int i = 0; i < other._count && IsResolved; i++)
            Grow(new(sign * other._parts[i].Value, other._parts[i].Exponent));
    }

    internal void AddProduct(SupportSourceCoefficient a, SupportSourceCoefficient b)
    {
        IsResolved &= a.IsResolved && b.IsResolved;
        for (int i = 0; i < a._count && IsResolved; i++)
            for (int j = 0; j < b._count && IsResolved; j++) AddProduct(a._parts[i], b._parts[j]);
    }

    internal void AddScaled(SupportSourceCoefficient value, double scalar)
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
            if (kept == MaximumParts) { IsResolved = false; return; }
            if (kept == _parts.Length)
            {
                Buffer grown = _pool.Rent(Math.Min(MaximumParts, Math.Max(1, 2 * _parts.Length)));
                _parts.CopyTo(grown.Parts, 0);
                _pool.Return(_buffer);
                _buffer = grown;
                _parts = grown.Parts;
            }
            _parts[kept++] = q;
        }
        _count = kept;
    }

    internal sealed class Buffer(int capacity)
    {
        internal readonly Part[] Parts = new Part[capacity];
        internal Buffer? Next;
    }

    // Recycle expansions by size rather than by monomial position. Cancellation moves live terms,
    // so retaining a buffer at an array index alone would allocate again on an identical warm graph.
    internal sealed class BufferPool
    {
        readonly Buffer?[] _free = new Buffer[8];
        internal Buffer Rent(int capacity)
        {
            int bin = BitOperations.Log2((uint)capacity);
            if (_free[bin] is not { } result) return new(capacity);
            _free[bin] = result.Next;
            result.Next = null;
            return result;
        }

        internal void Return(Buffer? buffer)
        {
            if (buffer is null) return;
            int bin = BitOperations.Log2((uint)buffer.Parts.Length);
            buffer.Next = _free[bin];
            _free[bin] = buffer;
        }
    }
}
