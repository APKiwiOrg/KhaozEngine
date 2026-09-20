using System;

namespace KhaozEngine.Catalog.GameTypes;

/// <summary>
/// Every answer this package needs from a GAME, in one object, so
/// <see cref="GameContentTypes.Register"/> can build all thirteen registrations without the caller naming a
/// validator anywhere.
/// </summary>
/// <remarks>
/// <b>Every member is REQUIRED.</b> A seam a game forgot would be a rule that silently never fired, and a
/// rule that never fires is worse than no rule: a publish reports clean and a boot then meets the row the
/// check existed for. The compiler asks for all of them rather than a default answering true.
/// <para>
/// There is nothing here but the game's own answers. No balance number, no enum, no roster and no name: the
/// package holds the rules and this object holds who to ask.
/// </para>
/// </remarks>
public sealed class GameContentOptions
{
    /// <summary>The four answers a <c>recipe</c> row needs, which is the largest seam of the set.</summary>
    public required RecipeValidatorOptions Recipe { get; init; }

    /// <summary>
    /// Whether the game stands for a <c>skill_curve</c> row's skill number at all. False is
    /// <see cref="GameContentFindings.SkillCurveUnknownSkill"/>.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="RecipeValidatorOptions.IsPayableSkill"/> on purpose. A curve row is a
    /// statement about ANY skill the game has, including one no recipe may be listed under, so the two
    /// questions have different answers and one predicate could not serve both.
    /// </remarks>
    public required Func<long, bool> IsKnownSkill { get; init; }

    /// <summary>
    /// Which of the cross-type sweep's knob-driven rules to run, and the knob NAMES they read.
    /// </summary>
    /// <remarks>
    /// Required like the rest, and <see cref="GameContentSweepOptions.None"/> is the answer for a world with
    /// no tuning table. A game that left it out would silently lose three rules, which is the same failure
    /// a missing predicate is.
    /// </remarks>
    public required GameContentSweepOptions Sweep { get; init; }
}
