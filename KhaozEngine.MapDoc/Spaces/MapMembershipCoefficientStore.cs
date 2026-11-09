using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>A numeric token. Large unsigned magnitudes live in the owning store, never in this value.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct MapMembershipCoefficient
{
    internal readonly long Inline;
    internal readonly int Offset;
    internal readonly ushort Length;
    internal readonly byte Kind;
    internal readonly sbyte Sign;
    internal MapMembershipCoefficient(long value)
    { Inline = value; Offset = 0; Length = 0; Kind = 0; Sign = (sbyte)Math.Sign(value); }
    internal MapMembershipCoefficient(int offset, int length, int sign)
    { Inline = 0; Offset = offset; Length = checked((ushort)length); Kind = 1; Sign = checked((sbyte)sign); }
}

/// <summary>Generation-owned exact tokens and canonical unsigned little-endian magnitude slices.</summary>
internal sealed class MapMembershipCoefficientStore
{
    [ThreadStatic] static MapMembershipCoefficientStore? t_current;
    readonly MapMembershipArenaPages<MapMembershipCoefficient> _tokens;
    readonly MapMembershipArenaPages<byte> _magnitudes;
    MapMembershipCoefficientStore(int tokenCapacity, int magnitudeCapacity)
    { _tokens = MapMembershipArenaPages<MapMembershipCoefficient>.Create(tokenCapacity); _magnitudes = MapMembershipArenaPages<byte>.Create(magnitudeCapacity); }
    internal static MapMembershipCoefficientStore Create(int tokenCapacity = 256, int magnitudeCapacity = 4096)
    { MapMembershipBuild.Inventory(32); return new(tokenCapacity, magnitudeCapacity); }
    internal static MapMembershipCoefficientStore? Current => t_current;
    internal static Scope Begin() => new(t_current ?? Create());
    internal readonly struct Scope : IDisposable
    {
        readonly MapMembershipCoefficientStore? _previous;
        internal MapMembershipCoefficientStore Store { get; }
        internal bool Owns => _previous is null;
        internal Scope(MapMembershipCoefficientStore store) { _previous = t_current; Store = store; t_current = store; }
        public void Dispose() => t_current = _previous;
    }
    internal int Count => _tokens.Count;
    internal int TokenPages => _tokens.PageCount;
    internal int MagnitudePages => _magnitudes.PageCount;
    internal long InventoryBytes => checked(32 + _tokens.InventoryBytes + _magnitudes.InventoryBytes);
    internal MapMembershipCoefficient this[int index] => _tokens[index];
    internal int Append(BigInteger value)
    {
        var magnitude = BigInteger.Abs(value);
        if (magnitude.GetBitLength() > MapMembershipInteger.CoefficientBits) throw new MapExactOverflowException();
        int bytes = magnitude.GetByteCount(isUnsigned: true);
        MapMembershipCoefficient token;
        if (value >= long.MinValue && value <= long.MaxValue) token = new((long)value);
        else
        {
            int oldPages = _magnitudes.PageCount, oldCapacity = _magnitudes.Capacity;
            int offset = _magnitudes.Allocate(bytes);
            if (!magnitude.TryWriteBytes(_magnitudes.Writable(offset, bytes), out int written, isUnsigned: true) || written != bytes)
                throw new InvalidOperationException("Membership magnitude did not fit its admitted slice.");
            token = new(offset, bytes, value.Sign);
            if (MapMembershipBuild.Current is { } build)
            {
                build.Counts.CoefficientPoolPages += _magnitudes.PageCount - oldPages;
                build.Counts.CoefficientPoolCapacity += _magnitudes.Capacity - oldCapacity;
            }
        }
        int index = _tokens.Append(token);
        MapMembershipBuild.Coefficient(bytes);
        return index;
    }
    internal ReadOnlySpan<byte> Magnitude(MapMembershipCoefficient token) => _magnitudes.Read(token.Offset, token.Length);
    internal void Load(MapMembershipCoefficient token, MapMembershipInteger target)
    {
        if (token.Kind == 0) target.Set(token.Inline);
        else target.Load(Magnitude(token), token.Sign);
    }
    internal MapMembershipInterval Interval(MapMembershipCoefficient token)
    {
        if (token.Kind == 0) return MapMembershipInterval.Integer(token.Inline);
        var bytes = Magnitude(token);
        int take = Math.Min(8, bytes.Length);
        ulong leading = 0;
        for (int i = bytes.Length - 1; i >= bytes.Length - take; i--) leading = (leading << 8) | bytes[i];
        double d = Math.ScaleB((double)leading, 8 * (bytes.Length - take));
        if (!double.IsFinite(d)) return MapMembershipInterval.Uncertain;
        // Truncated tail and binary64 rounding together fit in one outward ULP on each side.
        var positive = new MapMembershipInterval(Math.BitDecrement(d), Math.BitIncrement(d));
        return token.Sign < 0 ? positive.Negate() : positive;
    }
    internal int Compare(MapMembershipCoefficient a, MapMembershipCoefficient b)
    {
        Span<uint> words = stackalloc uint[2 * MapMembershipInteger.Words];
        var left = new MapMembershipInteger(words[..MapMembershipInteger.Words]);
        var right = new MapMembershipInteger(words[MapMembershipInteger.Words..]);
        Load(a, left); Load(b, right);
        int sign = left.Sign.CompareTo(right.Sign);
        return sign != 0 ? sign : left.Sign * MapMembershipInteger.CompareMagnitude(left, right);
    }
    internal void Freeze() { _tokens.Freeze(); _magnitudes.Freeze(); }
    internal IEnumerable<(object Identity, long Bytes)> Inventory()
    {
        yield return (this, 32);
        foreach (var page in _tokens.Inventory()) yield return page;
        foreach (var page in _magnitudes.Inventory()) yield return page;
    }
}
