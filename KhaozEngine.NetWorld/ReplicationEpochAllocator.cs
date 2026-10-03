namespace KhaozEngine.NetWorld;

/// <summary>
/// Issues format 2 stream epochs for one server lifetime. Epochs are nonzero, strictly increasing and never reused.
/// A writer reset never touches the allocator. Once <see cref="ulong.MaxValue"/> has been issued no further epoch
/// exists, <see cref="TryNext"/> returns false and the host keeps admission closed rather than wrapping to zero.
/// </summary>
internal sealed class ReplicationEpochAllocator
{
    /// <summary>The last epoch issued, or 0 before the first.</summary>
    internal ulong HighWater { get; private set; }

    /// <summary>Issues the next epoch.</summary>
    /// <param name="epoch">The new nonzero epoch, or 0 when exhausted.</param>
    /// <returns>False once <see cref="ulong.MaxValue"/> has been issued.</returns>
    internal bool TryNext(out ulong epoch)
    {
        if (HighWater == ulong.MaxValue)
        {
            epoch = 0;
            return false;
        }
        HighWater = checked(HighWater + 1);
        epoch = HighWater;
        return true;
    }

    /// <summary>Test seam: records <paramref name="lastIssued"/> as the last epoch issued.</summary>
    internal void SeedForTest(ulong lastIssued) => HighWater = lastIssued;
}
