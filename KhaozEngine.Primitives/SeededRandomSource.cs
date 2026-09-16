using System;
using System.Buffers.Binary;

namespace KhaozEngine.Primitives;

/// <summary>
/// The deterministic <see cref="IRandomSource"/>, for tests and for any seeded replay. It WRAPS
/// <see cref="DeterministicRng"/> rather than reimplementing a generator, so the engine keeps exactly one
/// seeded stream definition and the known vectors already pinning that stream keep doing their job.
/// <para>
/// The constructor takes the seed and the seed is not readable back off the instance, so a test that wants
/// to assert on a seed asserts on the one it passed in. The bounded draw is REJECTION SAMPLING over the
/// wrapped generator's 64-bit output rather than the wrapped modulo helper, both because modulo bias on a
/// roll is an edge a player can farm and because the full int range overflows an int-typed bound.
/// </para>
/// <para>
/// A hosted server may run this source behind an explicit host option, and a host that does should log a
/// warning on every boot it is set.
/// </para>
/// </summary>
public sealed class SeededRandomSource : IRandomSource
{
    private readonly DeterministicRng _rng;

    /// <summary>Starts the stream at <paramref name="seed"/>. The same seed always gives the same sequence.</summary>
    public SeededRandomSource(ulong seed) => _rng = new DeterministicRng(seed);

    /// <inheritdoc />
    public int NextInt(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive)
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), maxExclusive, "maxExclusive must be greater than minInclusive.");

        // Widened before the subtract, so a full-range draw (int.MinValue to int.MaxValue) does not overflow.
        uint range = (uint)((long)maxExclusive - minInclusive);
        if (range == 1) return minInclusive;
        return (int)(minInclusive + (long)NextBelow(range));
    }

    /// <inheritdoc />
    public ulong NextULong() => _rng.NextULong();

    /// <inheritdoc />
    public ushort NextRollPosition() => (ushort)(_rng.NextULong() >> 48);

    /// <summary>
    /// Advances the stream by EXACTLY ONE draw of the wrapped generator. No range and no rejection loop,
    /// because a discard that went through <see cref="NextInt"/> would cost a draw the caller cannot predict
    /// and the whole point of this member is a discard whose cost is fixed.
    /// </summary>
    public void Skip() => _ = _rng.NextULong();

    /// <inheritdoc />
    public void NextBytes(Span<byte> destination)
    {
        while (destination.Length >= sizeof(ulong))
        {
            BinaryPrimitives.WriteUInt64LittleEndian(destination, _rng.NextULong());
            destination = destination[sizeof(ulong)..];
        }

        if (destination.IsEmpty) return;

        Span<byte> tail = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(tail, _rng.NextULong());
        tail[..destination.Length].CopyTo(destination);
    }

    // Lemire's rejection bound: reject the draws below 2^64 mod range, so the remaining draws divide evenly
    // and the modulo that follows carries no bias. For any range a content roll uses, the rejection
    // probability is under 2^-32, so this is one draw in practice.
    private uint NextBelow(uint range)
    {
        ulong bound = range;
        ulong threshold = (0UL - bound) % bound;
        ulong draw;
        do
        {
            draw = _rng.NextULong();
        }
        while (draw < threshold);
        return (uint)(draw % bound);
    }
}
