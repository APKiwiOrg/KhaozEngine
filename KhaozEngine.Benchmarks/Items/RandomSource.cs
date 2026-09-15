using System;
using KhaozEngine.Primitives;

namespace KhaozEngine.Benchmarks.Items;

/// <summary>
/// The spike's local copy of the contracts 14.1 random seam. The real seam lands in
/// KhaozEngine.Primitives through the program itself, so nothing here is added to that package: this
/// shape exists only so the generator takes its randomness in a constructor, which is what contracts
/// 14.4 asks of a type that rolls.
/// </summary>
internal interface IRandomSource
{
    int NextInt(int minInclusive, int maxExclusive);

    ulong NextULong();

    /// <summary>Uniform over 0 through 65535, the roll position of contracts 6.4.</summary>
    ushort NextRollPosition();

    void NextBytes(Span<byte> destination);
}

/// <summary>
/// Wraps <see cref="DeterministicRng"/> so the spike keeps exactly one seeded stream definition, which
/// is contracts 14.2's rule for the seeded implementation. The seed is not readable back off it.
/// </summary>
internal sealed class SeededRandomSource : IRandomSource
{
    private readonly DeterministicRng rng;

    internal SeededRandomSource(ulong seed) => rng = new DeterministicRng(seed);

    public int NextInt(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive)
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), maxExclusive, "maxExclusive must exceed minInclusive.");
        return rng.Next(minInclusive, maxExclusive);
    }

    public ulong NextULong() => rng.NextULong();

    public ushort NextRollPosition() => (ushort)(rng.NextULong() & 0xFFFF);

    public void NextBytes(Span<byte> destination)
    {
        int index = 0;
        while (index < destination.Length)
        {
            ulong draw = rng.NextULong();
            for (int byteIndex = 0; byteIndex < 8 && index < destination.Length; byteIndex++)
                destination[index++] = (byte)(draw >> (byteIndex * 8));
        }
    }
}
