using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>
/// A set of body region indices below <see cref="MaxRegions"/>, held as two 64 bit words so it copies as a value
/// and never allocates.
/// </summary>
public readonly struct RegionSet : IEquatable<RegionSet>
{
    /// <summary>The most regions one body carries. Valid indices are 0 to 127.</summary>
    public const int MaxRegions = 128;

    readonly ulong low;
    readonly ulong high;

    RegionSet(ulong low, ulong high)
    {
        this.low = low;
        this.high = high;
    }

    /// <summary>The set with no regions.</summary>
    public static RegionSet Empty => default;

    /// <summary>True when the set holds no region.</summary>
    public bool IsEmpty => (low | high) == 0;

    /// <summary>The number of regions in the set.</summary>
    public int Count => BitOperations.PopCount(low) + BitOperations.PopCount(high);

    /// <summary>This set with <paramref name="region"/> added.</summary>
    public RegionSet With(int region)
    {
        Check(region);
        return region < 64 ? new(low | (1UL << region), high) : new(low, high | (1UL << (region - 64)));
    }

    /// <summary>True when the set holds <paramref name="region"/>.</summary>
    public bool Contains(int region)
    {
        Check(region);
        return ((region < 64 ? low >> region : high >> (region - 64)) & 1) != 0;
    }

    /// <summary>The regions in either set.</summary>
    public RegionSet Union(RegionSet other) => new(low | other.low, high | other.high);

    /// <inheritdoc />
    public bool Equals(RegionSet other) => low == other.low && high == other.high;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RegionSet other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(low, high);

    /// <summary>True when both sets hold the same regions.</summary>
    public static bool operator ==(RegionSet left, RegionSet right) => left.Equals(right);

    /// <summary>True when the sets differ.</summary>
    public static bool operator !=(RegionSet left, RegionSet right) => !left.Equals(right);

    static void Check(int region)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(region);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(region, MaxRegions);
    }
}
