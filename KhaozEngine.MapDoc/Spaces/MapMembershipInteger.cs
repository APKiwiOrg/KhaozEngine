using System;
using System.Numerics;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Signed integer registers in caller-owned storage. No operation rents or grows storage.</summary>
internal readonly ref struct MapMembershipInteger(Span<uint> storage)
{
    // A prepared plane clears at most nine 64-bit coordinate denominators. Its largest coefficient has
    // degree 30, plus five sign and sum bits. Substitution adds two 64-bit point factors and two sum bits.
    // Comparing two substituted ratios doubles that bound and adds one subtraction bit.
    internal const int InputBits = 64;
    internal const int CoefficientBits = 30 * InputBits + 5;
    internal const int PredicateBits = 2 * (CoefficientBits + 2 * InputBits + 2) + 1;
    internal const int Limbs = (PredicateBits + 31) / 32;
    internal const int Words = Limbs + 2;
    readonly Span<uint> _storage = storage;
    internal int Sign { get => unchecked((int)_storage[0]); set => _storage[0] = unchecked((uint)value); }
    internal int Length { get => (int)_storage[1]; set => _storage[1] = (uint)value; }
    uint this[int i] { get => _storage[i + 2]; set => _storage[i + 2] = value; }
    internal void Clear() => _storage.Clear();
    internal void Trim()
    {
        int n = Length;
        while (n > 0 && this[n - 1] == 0) n--;
        Length = n;
        if (n == 0) Sign = 0;
    }
    internal void Set(long value)
    {
        Clear();
        if (value == 0) return;
        ulong magnitude = value < 0 ? (ulong)(-(Int128)value) : (ulong)value;
        this[0] = (uint)magnitude;
        this[1] = (uint)(magnitude >> 32);
        Length = this[1] == 0 ? 1 : 2;
        Sign = value < 0 ? -1 : 1;
    }
    internal void SetBit(int index) => this[index / 32] |= 1U << (index % 32);
    internal void Load(ReadOnlySpan<byte> magnitude, int sign)
    {
        Clear();
        if (magnitude.Length > Limbs * 4) throw new InvalidOperationException("Uncertified exact coefficient.");
        for (int i = 0; i < magnitude.Length; i++) this[i / 4] |= (uint)magnitude[i] << ((i % 4) * 8);
        Length = (magnitude.Length + 3) / 4;
        Sign = sign;
        Trim();
    }
    internal void CopyTo(MapMembershipInteger target)
    {
        _storage.CopyTo(target._storage);
    }
    internal int BitLength => Length == 0 ? 0 : (Length - 1) * 32 + 32 - BitOperations.LeadingZeroCount(this[Length - 1]);
    internal int TrailingZeros
    {
        get
        {
            for (int i = 0; i < Length; i++) if (this[i] != 0) return i * 32 + BitOperations.TrailingZeroCount(this[i]);
            return 0;
        }
    }
    internal static int CompareMagnitude(MapMembershipInteger a, MapMembershipInteger b)
    {
        if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
        for (int i = a.Length - 1; i >= 0; i--) if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        return 0;
    }
    internal static void Add(MapMembershipInteger a, MapMembershipInteger b, MapMembershipInteger result)
    {
        if (a.Sign == 0) { b.CopyTo(result); return; }
        if (b.Sign == 0) { a.CopyTo(result); return; }
        if (a.Sign != b.Sign)
        {
            int compare = CompareMagnitude(a, b);
            if (compare == 0) { result.Clear(); return; }
            if (compare > 0) { SubtractMagnitude(a, b, result); result.Sign = a.Sign; }
            else { SubtractMagnitude(b, a, result); result.Sign = b.Sign; }
            return;
        }
        int count = Math.Max(a.Length, b.Length);
        ulong carry = 0;
        for (int i = 0; i < count; i++)
        {
            ulong sum = (i < a.Length ? a[i] : 0UL) + (i < b.Length ? b[i] : 0UL) + carry;
            result[i] = (uint)sum;
            carry = sum >> 32;
        }
        if (carry != 0)
        {
            if (count == Limbs) throw new InvalidOperationException("Certified predicate capacity exceeded.");
            result[count++] = (uint)carry;
        }
        result.Length = count;
        result.Sign = a.Sign;
    }
    // Supports result == a. Each iteration reads its current limb before writing it.
    internal static void SubtractMagnitude(MapMembershipInteger a, MapMembershipInteger b, MapMembershipInteger result)
    {
        int count = a.Length;
        long borrow = 0;
        for (int i = 0; i < count; i++)
        {
            long difference = (long)a[i] - (i < b.Length ? b[i] : 0L) - borrow;
            result[i] = unchecked((uint)difference);
            borrow = difference < 0 ? 1 : 0;
        }
        result.Length = count;
        result.Sign = 1;
        result.Trim();
    }
    internal static void Multiply(MapMembershipInteger a, MapMembershipInteger b, MapMembershipInteger result)
    {
        result.Clear();
        if (a.Sign == 0 || b.Sign == 0) return;
        if (a.Length + b.Length > Limbs) throw new InvalidOperationException("Certified predicate capacity exceeded.");
        for (int i = 0; i < a.Length; i++)
        {
            ulong carry = 0;
            for (int j = 0; j < b.Length; j++)
            {
                ulong product = (ulong)a[i] * b[j] + result[i + j] + carry;
                result[i + j] = (uint)product;
                carry = product >> 32;
            }
            result[i + b.Length] = (uint)carry;
        }
        result.Length = a.Length + b.Length;
        result.Sign = a.Sign * b.Sign;
        result.Trim();
    }
    internal void ShiftRight(int bits)
    {
        int words = bits / 32, remainder = bits % 32, count = Math.Max(Length - words, 0);
        for (int i = 0; i < count; i++)
        {
            uint next = i + words + 1 < Length ? this[i + words + 1] : 0;
            this[i] = remainder == 0 ? this[i + words] : (this[i + words] >> remainder) | (next << (32 - remainder));
        }
        Length = count;
        Trim();
    }
    internal static void ShiftLeft(MapMembershipInteger source, int bits, MapMembershipInteger target)
    {
        target.Clear();
        if (source.Sign == 0) return;
        int words = bits / 32, remainder = bits % 32;
        int count = source.Length + words + (remainder == 0 ? 0 : 1);
        if (count > Limbs) throw new InvalidOperationException("Certified predicate capacity exceeded.");
        ulong carry = 0;
        for (int i = 0; i < source.Length; i++)
        {
            ulong shifted = ((ulong)source[i] << remainder) | carry;
            target[i + words] = (uint)shifted;
            carry = shifted >> 32;
        }
        if (remainder != 0) target[source.Length + words] = (uint)carry;
        target.Length = count;
        target.Sign = source.Sign;
        target.Trim();
    }
    internal bool TryInt64(out long value)
    {
        value = 0;
        if (Length > 2) return false;
        ulong magnitude = Length == 0 ? 0 : this[0] | (Length == 2 ? (ulong)this[1] << 32 : 0);
        if (Sign < 0)
        {
            if (magnitude > 1UL << 63) return false;
            value = unchecked(-(long)magnitude);
        }
        else
        {
            if (magnitude > long.MaxValue) return false;
            value = (long)magnitude;
        }
        return true;
    }
    bool Bit(int index) => (this[index / 32] & (1U << (index % 32))) != 0;
    internal double ToDouble()
    {
        int bits = BitLength;
        if (bits == 0) return 0;
        int shift = Math.Max(0, bits - 53);
        ulong significand = 0;
        for (int i = bits - 1; i >= shift; i--) significand = (significand << 1) | (Bit(i) ? 1UL : 0);
        // BigInteger's released binary64 conversion truncates the magnitude to its top 53 bits.
        return Sign * Math.ScaleB((double)significand, shift);
    }
}
