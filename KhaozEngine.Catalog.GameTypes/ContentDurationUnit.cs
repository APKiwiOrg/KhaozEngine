using System;

namespace KhaozEngine.Catalog.GameTypes;

/// <summary>
/// The time unit a game's own clock counts in, which is what the duration fields of these types are
/// NAMED after.
/// </summary>
/// <remarks>
/// A duration is the one fact in this package a game cannot inherit. A world that steps a fixed tick stores
/// a number of ticks and a world driven by wall clock stores a number of seconds, and neither spelling is
/// the other's business, so the game declares the unit and every schema factory takes it.
/// <para>
/// <b>It picks the field NAME and nothing else.</b> Field ORDER, kinds, reference targets, visibility,
/// required flags, scales and the row codec are identical under either unit, so the choice costs a name in
/// the generic editor and the localization key derived from it, and never a byte of layout. The same reader
/// code serves both once it reaches the field by name.
/// </para>
/// <para>
/// There is no conversion anywhere here. Reading a stored number as a span of time is the game's, because
/// only the game knows how long its tick is.
/// </para>
/// </remarks>
public enum ContentDurationUnit
{
    /// <summary>Whole steps of the world's own fixed clock, the <c>_ticks</c> spelling.</summary>
    Ticks = 0,

    /// <summary>Whole seconds of wall clock, the <c>_seconds</c> spelling.</summary>
    Seconds = 1,
}

/// <summary>
/// The one statement of how <see cref="ContentDurationUnit"/> spells a duration field, so a type declares
/// the two full names it may carry and never assembles one.
/// </summary>
static class ContentDurationNames
{
    /// <summary>
    /// The name <paramref name="unit"/> selects, refusing a value outside the enum rather than falling back
    /// to one of the two: a schema built on a guess writes a field name nobody authored against.
    /// </summary>
    internal static string Pick(ContentDurationUnit unit, string ticks, string seconds) => unit switch
    {
        ContentDurationUnit.Ticks => ticks,
        ContentDurationUnit.Seconds => seconds,
        _ => throw new ArgumentOutOfRangeException(
            nameof(unit),
            unit,
            "A content duration unit is Ticks or Seconds. A schema cannot be built under any other value."),
    };
}
