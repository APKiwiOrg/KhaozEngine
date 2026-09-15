using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace KhaozEngine.Primitives;

/// <summary>
/// The <see cref="IRandomSource"/> a hosted server runs: every draw comes from the OS through
/// <see cref="RandomNumberGenerator"/>, so nothing about the stream is predictable and nothing about it is
/// reproducible.
/// <para>
/// Uniform integer draws use REJECTION SAMPLING rather than modulo, because modulo bias on a crafting roll
/// is a real edge a player can farm: a range that does not divide the draw width makes the low buckets
/// fractionally likelier, forever, on every roll of every player.
/// </para>
/// </summary>
public sealed class CryptographicRandomSource : IRandomSource
{
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
    public ulong NextULong()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        RandomNumberGenerator.Fill(bytes);
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    /// <inheritdoc />
    public ushort NextRollPosition()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        RandomNumberGenerator.Fill(bytes);
        return BinaryPrimitives.ReadUInt16LittleEndian(bytes);
    }

    /// <inheritdoc />
    public void NextBytes(Span<byte> destination)
    {
        if (destination.IsEmpty) return;
        RandomNumberGenerator.Fill(destination);
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
            draw = NextULong();
        }
        while (draw < threshold);
        return (uint)(draw % bound);
    }
}
