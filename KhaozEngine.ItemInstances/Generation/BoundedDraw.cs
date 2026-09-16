using KhaozEngine.Primitives;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The ONE weighted draw every roll on these paths goes through, and the one place that knows what to do
/// when a bound collapses to one.
/// <para>
/// <b>A bound of one is not a draw, and that is the whole reason this exists.</b>
/// <see cref="IRandomSource.NextInt"/>'s own contract says a one-wide range consumes nothing, so a real
/// draw over a live weight of 1, an open total of 1, a rarity total of 1 or an affix count whose minimum
/// equals its maximum costs the stream NOTHING, exactly as a discard did. The stream position after an item
/// then depends on what its pool happened to hold rather than on how many picks it made, and a seeded
/// session diverges at the first item whose pool ran dry, which is what spec 9.3 forbids.
/// </para>
/// <para>
/// So a collapsed bound takes <see cref="IRandomSource.Skip"/>, which advances the stream by exactly one
/// draw and has the one answer the bound allows. The discard of spec 9.4 step 8 is the same call with a
/// bound of zero, because a discard and a collapsed draw are the same thing seen from two sides.
/// </para>
/// </summary>
static class BoundedDraw
{
    /// <summary>
    /// A draw in [0, <paramref name="bound"/>), costing the stream exactly one draw whatever the bound is.
    /// </summary>
    /// <param name="random">The gameplay randomness seam the caller was handed.</param>
    /// <param name="bound">The weight, count or width the draw is taken over. Anything at or below 1 has a
    /// single answer, so it skips instead and answers 0.</param>
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
