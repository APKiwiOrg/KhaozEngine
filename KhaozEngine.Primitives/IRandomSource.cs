using System;

namespace KhaozEngine.Primitives;

/// <summary>
/// The engine's one gameplay-randomness seam: a loot draw, a craft roll, a spawn choice and a decoder
/// fuzzer all take one of these rather than owning a generator. Deliberately narrow.
/// <para>
/// There is NO seed, NO state, NO derived stream and no way to ask an instance what it will do next. A
/// source whose seed is readable is a source a crafting system can leak, and a durable record never
/// carries a seed or a draw index: it records the RESOLVED OUTCOME, which is what makes the source
/// swappable at any time.
/// </para>
/// <para>
/// Nothing here returns a float. Every roll, threshold and scaled number on a path a client and a server
/// must agree on is an integer, so a weighted choice is made with <see cref="NextInt"/> over an integer
/// weight total.
/// </para>
/// <para>
/// A consumer takes one as a CONSTRUCTOR PARAMETER, never from an ambient static, a service locator or a
/// default. There is no engine-provided default instance, because a default is how a production server
/// ends up on the test source. A type with no <see cref="IRandomSource"/> cannot roll, which is the
/// property that makes "does this class have gameplay randomness" answerable by reading its signature.
/// </para>
/// </summary>
public interface IRandomSource
{
    /// <summary>
    /// A uniform int in [<paramref name="minInclusive"/>, <paramref name="maxExclusive"/>). The full int
    /// range is legal. A one-wide range consumes no draw.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxExclusive"/> is &lt;= <paramref name="minInclusive"/>, which is a caller bug
    /// rather than a draw.
    /// </exception>
    int NextInt(int minInclusive, int maxExclusive);

    /// <summary>A uniform 64-bit draw.</summary>
    ulong NextULong();

    /// <summary>
    /// A uniform roll position over 0 to 65535, the fixed width every roll on an instance is stored
    /// against, so a re-roll of the same property is comparable with the roll it replaced.
    /// </summary>
    ushort NextRollPosition();

    /// <summary>Fills the whole destination span. An empty span is legal and draws nothing.</summary>
    void NextBytes(Span<byte> destination);

    /// <summary>
    /// Advances the stream by exactly one draw, for a caller that must consume a draw it will not use.
    /// <para>
    /// A generator that must stay POSITION STABLE cannot lean on <see cref="NextInt"/> for its discard: a
    /// one-wide range consumes no draw at all, and so does a real draw whose bound happens to collapse to
    /// one, so the stream position after an item would depend on what the pool held rather than on the
    /// number of picks. This member is the discard that always costs the same.
    /// </para>
    /// <para>
    /// The DEFAULT body is one <c>NextInt(0, 2)</c> with the result thrown away, so a foreign implementation
    /// gets the advance for free. An implementation that knows its own stream overrides it: the seeded source
    /// consumes exactly one underlying draw, and a cryptographic source does nothing at all because a
    /// cryptographic stream has no position to advance.
    /// </para>
    /// </summary>
    void Skip() => _ = NextInt(0, 2);
}
