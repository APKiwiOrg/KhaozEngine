using KhaozEngine.Primitives;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The ONE weighted draw every roll on these paths goes through, and the one place that knows what to do
/// when a bound collapses to one.
/// <para>
/// <b>A bound of one is not a draw, and that is the whole reason this exists.</b>
/// <see cref="IRandomSource.NextInt"/>'s own contract says a one-wide range consumes nothing, so a real
/// draw over a live weight of 1, an open total of 1, a rarity total of 1 or an affix count whose minimum
/// equals its maximum costs the stream NOTHING, exactly as a discard did. Without an explicit Skip,
/// those logical slots would be omitted as a pool collapsed or ran dry.
/// </para>
/// <para>
/// So a bound at or below one calls <see cref="IRandomSource.Skip"/> and returns 0. This reserves the
/// same logical slot as a live bounded call. An empty weighted slot is discarded through Skip too.
/// The seeded Skip consumes exactly one underlying NextULong, while a live NextInt may consume
/// additional values during rejection sampling. The helper does not guarantee an identical underlying
/// stream stride for every bound, and the cryptographic Skip is a no-op.
/// </para>
/// </summary>
static class BoundedDraw
{
    /// <summary>
    /// A draw in [0, <paramref name="bound"/>) for a bound above one, otherwise Skip and return 0.
    /// Every call reserves one logical slot.
    /// </summary>
    /// <param name="random">The gameplay randomness seam the caller was handed.</param>
    /// <param name="bound">The weight, count or width the draw is taken over. A bound at or below 1
    /// calls Skip and answers 0, covering both collapsed and empty slots.</param>
    internal static int Next(IRandomSource random, int bound)
    {
        if (bound > 1)
        {
            return random.NextInt(0, bound);
        }

        random.Skip();
        return 0;
    }
}
