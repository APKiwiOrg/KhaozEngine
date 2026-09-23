using System;

namespace KhaozEngine.Catalog.GameTypes;

/// <summary>
/// The time unit a game's own clock counts in, which names the duration fields of these types and says what
/// the number stored in one of them counts.
/// </summary>
/// <remarks>
/// A duration is the one fact in this package a game cannot inherit. A world that steps a fixed tick stores
/// a number of ticks and a world driven by wall clock stores seconds, and neither spelling is the other's
/// business, so the game declares the unit and every schema factory takes it.
/// <para>
/// <b>It picks the field NAME, and under <see cref="Seconds"/> the field's KIND and SCALE.</b> A tick is
/// whole, so <see cref="Ticks"/> stores a plain <see cref="ContentFieldKind.Int"/> at scale 1. A second is
/// too coarse for a timing, because a world stepping six times a second times things between whole
/// seconds, so <see cref="Seconds"/> stores a <see cref="ContentFieldKind.ScaledInt"/> at scale 100, which
/// is HUNDREDTHS of a second: 2.33 seconds is stored as 233. Field ORDER, reference targets, visibility and
/// required flags are identical under either unit, and so are the row bytes, because both kinds go out as
/// the same varint.
/// </para>
/// <para>
/// There is no conversion anywhere here. The package stores the integer the game authored and the schema
/// carries the scale it is read at. Reading that as a span of time is the game's, because only the game
/// knows how long its tick is.
/// </para>
/// </remarks>
public enum ContentDurationUnit
{
    /// <summary>Whole steps of the world's own fixed clock, a plain integer under the <c>_ticks</c> spelling.</summary>
    Ticks = 0,

    /// <summary>
    /// Hundredths of a second of wall clock, a scaled integer at scale 100 under the <c>_seconds</c> spelling.
    /// </summary>
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
        _ => throw Undefined(unit),
    };

    /// <summary>The refusal every duration helper raises for a value outside the enum.</summary>
    internal static ArgumentOutOfRangeException Undefined(ContentDurationUnit unit) => new(
        nameof(unit),
        unit,
        "A content duration unit is Ticks or Seconds. A schema cannot be built under any other value.");
}

/// <summary>
/// The one statement of how <see cref="ContentDurationUnit"/> STORES a duration field: its kind and its
/// scale. A type declares a duration field through <see cref="Entry"/> and writes neither down, so a fifth
/// duration field cannot store whole seconds by accident.
/// </summary>
static class ContentDurationFields
{
    /// <summary>
    /// The scale a duration carries under <see cref="ContentDurationUnit.Seconds"/>: hundredths of a second.
    /// </summary>
    internal const int SecondsScale = 100;

    /// <summary>
    /// The kind and scale <paramref name="unit"/> stores a duration under, refusing a value outside the enum
    /// for the reason <see cref="ContentDurationNames.Pick"/> does.
    /// </summary>
    internal static (ContentFieldKind Kind, int Scale) Storage(ContentDurationUnit unit) => unit switch
    {
        ContentDurationUnit.Ticks => (ContentFieldKind.Int, 1),
        ContentDurationUnit.Seconds => (ContentFieldKind.ScaledInt, SecondsScale),
        _ => throw ContentDurationNames.Undefined(unit),
    };

    /// <summary>A duration field named <paramref name="name"/>, stored the way <paramref name="unit"/> says.</summary>
    /// <param name="unit">The game's own time unit.</param>
    /// <param name="name">The name the unit already picked, off the type's own accessor.</param>
    /// <param name="visibility">Whether a client ever sees the value.</param>
    /// <param name="required">Whether a live row must carry a value.</param>
    internal static ContentFieldEntry Entry(
        ContentDurationUnit unit,
        string name,
        ContentVisibility visibility,
        bool required)
    {
        (ContentFieldKind kind, int scale) = Storage(unit);
        return new ContentFieldEntry(name, kind, null, visibility, required, scale);
    }
}
