using System.Collections.Generic;

namespace KhaozEngine.Catalog.GameTypes;

/// <summary>
/// Which of the cross-type sweep's knob-driven rules a game turns on, under what NAMES, and the game's own
/// whole-catalog rules that run after them.
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

    /// <summary>
    /// The GAME's own whole-catalog rules, run in the sweep's slot AFTER every rule of the package's own, or
    /// empty for none.
    /// </summary>
    /// <remarks>
    /// A rule here is the engine's own <see cref="IContentValidator"/>, handed exactly what the sweep is
    /// handed: the slot's type id, which is <c>food</c>'s, the same candidate and the same findings list. So a
    /// game rule sees every row of every type, accumulates beside the package's findings rather than in a list
    /// of its own, and reaches the report the way the package's do, folded into <c>KEC0040</c> under the
    /// slot's type key with the game's own code in the message.
    /// <para>
    /// <b>It is the one place a game's cross-type rule can go without a second slot.</b> The engine offers a
    /// game no whole-registry slot, so a game rule mounted on a type of its own would run once per mounting,
    /// and one mounted on <c>food</c> in place of <see cref="GameContentChecks"/> would silently drop the
    /// package's sweep. Here it rides the sweep and cannot run without it.
    /// </para>
    /// <para>
    /// The rules run in list order, each once per validation. A rule that THROWS is the slot's throw: the
    /// engine reports one <c>KEC0040</c> naming the slot's type and keeps none of the slot's findings, the
    /// same as for any per-type validator, so a rule that can fail on content reports a finding instead.
    /// </para>
    /// </remarks>
    public IReadOnlyList<IContentValidator> GameRules { get; init; } = [];
}
