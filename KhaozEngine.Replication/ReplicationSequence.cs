using System;

namespace KhaozEngine.Replication;

/// <summary>
/// Serial number arithmetic for format 2 sequences inside one epoch. A candidate is newer than the current sequence
/// exactly when the unsigned difference is 1 through <c>0x7fffffff</c>, so the wrap through <see cref="uint.MaxValue"/>
/// to 0 orders correctly. A difference of exactly half the range has no defined order. Retention and in-flight windows
/// stay far below half the range, and a half-range gap requires a new epoch rather than a guess.
/// </summary>
internal static class ReplicationSequence
{
    /// <summary>The unsigned difference whose order is undefined.</summary>
    internal const uint HalfRange = 0x80000000u;

    /// <summary>True when <paramref name="candidate"/> follows <paramref name="current"/> within half the range.
    /// Equal sequences are a duplicate, not newer.</summary>
    internal static bool IsNewer(uint candidate, uint current)
    {
        uint difference = unchecked(candidate - current);
        return difference != 0 && difference < HalfRange;
    }

    /// <summary>True when the two sequences are exactly half the range apart, in either direction.</summary>
    internal static bool IsAmbiguous(uint candidate, uint current) => unchecked(candidate - current) == HalfRange;

    /// <summary>Returns <paramref name="epoch"/>, or throws for zero. Public stream setup calls this, so a
    /// <see cref="ReplicationPacketId"/> value with epoch zero can exist but can never establish a stream.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The epoch is zero.</exception>
    internal static ulong RequireEpoch(ulong epoch, string paramName)
    {
        if (epoch == 0)
            throw new ArgumentOutOfRangeException(paramName, epoch, "A replication epoch must be nonzero.");
        return epoch;
    }
}
