using System.Collections.Generic;

namespace KhaozEngine.Catalog.GameTypes;

/// <summary>
/// Which of the cross-type sweep's knob-driven rules a game turns on, and under what NAMES.
/// </summary>
/// <remarks>
/// <b>A knob name is one world's vocabulary and never this package's.</b> A tuning row's key says what a
/// world tunes, so the sweep holds the RULES and a game says which rows they read. Four of the seven rules
/// need a name to do that, and the other three read nothing but rows.
/// <para>
/// <b>A null name disables that rule, and an empty list disables the required-knob rule.</b> A game with no
/// level cap has no rule to run, and refusing to register would make an optional rule mandatory. A disabled
/// rule reports nothing at all rather than reporting everything or passing everything, which are the two
/// ways a default would have been wrong.
/// </para>
/// <para>
/// A knob's stored number is at <see cref="GameTuningContentType.ValueScale"/>, and the comparisons scale
/// the AUTHORED number up rather than dividing the knob down, so a fractional knob is compared exactly
/// instead of through a rounding nobody authored.
/// </para>
/// </remarks>
public sealed class GameContentSweepOptions
{
    /// <summary>
    /// Every knob-driven rule off, which is what a game with no tuning table wants and what makes the
    /// choice explicit rather than defaulted.
    /// </summary>
    /// <remarks>
    /// The three rules that read no knob still run: a shelf item's tradability, a recipe with no output and
    /// a tool tier whose item lacks its family tag are statements about rows alone.
    /// </remarks>
    public static GameContentSweepOptions None { get; } = new();

    /// <summary>
    /// The knob holding the highest level a character can reach, or null to run neither level rule.
    /// </summary>
    /// <remarks>
    /// One name drives two codes, because it is one fact:
    /// <see cref="GameContentFindings.SweepGatheringLevelOverCap"/> on a node and
    /// <see cref="GameContentFindings.SweepRecipeLevelOverCap"/> on a recipe. A row gated above it can never
    /// be worked by anybody.
    /// </remarks>
    public string? MaxLevelKnob { get; init; }

    /// <summary>
    /// The knob holding the ceiling a gathering chance is held under, in basis points, or null to skip
    /// <see cref="GameContentFindings.SweepGatheringChanceOverCeiling"/>.
    /// </summary>
    public string? MaxChanceKnob { get; init; }

    /// <summary>
    /// Every knob name this build READS, or empty to skip
    /// <see cref="GameContentFindings.SweepTuningKnobMissing"/>.
    /// </summary>
    /// <remarks>
    /// The rule is ASYMMETRIC on purpose. A name here with no row is a finding, because the boot would fall
    /// back to a default the pack does not name, and a row whose name is NOT here is IGNORED, because a knob
    /// a newer build writes must not stop an older server loading the pack.
    /// <para>
    /// It is also gated on the table being authored at all. A candidate carrying no tuning rows makes no
    /// claim about the knobs, which is a fixture, a partial import or a pack that ships none, and a boot
    /// then falls back wholesale rather than half way. A candidate carrying SOME of them is the dangerous
    /// shape, because the boot silently mixes authored knobs with defaults nothing in the pack names.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> RequiredKnobs { get; init; } = [];
}
