using System;

namespace KhaozEngine.Catalog.GameTypes;

/// <summary>
/// The four things a <c>recipe</c> row carries that only the GAME can answer, handed to
/// <see cref="RecipeContentType.Validator"/> as predicates over the raw stored number.
/// </summary>
/// <remarks>
/// A recipe's skill, station and repeat mode are durable numbers this package gives no meaning to, and the
/// rules that matter are about whether the game recognises one. So the rules live here and the ANSWERS come
/// from the caller, which is what keeps a roster, an enum and a station list out of the engine.
/// <para>
/// <b>Two separate skill predicates, deliberately.</b> A skill nothing can be paid into and a skill that is
/// real but not open yet are different defects with different fixes, and the fleet numbers them separately
/// as <see cref="GameContentFindings.RecipeSkillNotPayable"/> and
/// <see cref="GameContentFindings.RecipeLockedSkill"/>. One predicate could only report one of them.
/// <see cref="IsOpenSkill"/> is asked ONLY of a skill <see cref="IsPayableSkill"/> already accepted, so a
/// game never has to make it answer for a number that means nothing.
/// </para>
/// <para>
/// Every member is REQUIRED. A predicate a game forgot would be a rule that silently never fired, so the
/// compiler asks for all four rather than a default answering true.
/// </para>
/// <para>
/// A predicate is asked about a stored <see cref="long"/> rather than about an enum, because that is what a
/// row actually carries. A game whose numbers are a byte-backed enum checks the range BEFORE it casts: a
/// cast from a long is unchecked and would fold 256 onto the first constant rather than refusing it.
/// </para>
/// </remarks>
public sealed class RecipeValidatorOptions
{
    /// <summary>
    /// The station number that means NO STATION, which is 0 and is never offered to
    /// <see cref="IsNameableStation"/>.
    /// </summary>
    /// <remarks>
    /// Zero is the absence convention everywhere else in the catalog, so it is the one station value this
    /// package reserves: it is the PLACE a resolver answers when a character is standing nowhere in
    /// particular, and a row naming it is <see cref="GameContentFindings.RecipeStationNone"/> rather than
    /// <see cref="GameContentFindings.RecipeUnknownStation"/>. A game that has a real station at 0 has no
    /// way to say so, which is the cost of reserving it.
    /// </remarks>
    public const long NoStation = 0;

    /// <summary>
    /// Whether the game stands for this repeat mode number at all. False is
    /// <see cref="GameContentFindings.RecipeUnknownRepeatMode"/>.
    /// </summary>
    public required Func<long, bool> IsKnownRepeatMode { get; init; }

    /// <summary>
    /// Whether experience can be paid into this skill number at all. False is
    /// <see cref="GameContentFindings.RecipeSkillNotPayable"/>, which covers a number nothing stands for and
    /// a real skill that no recipe may be listed under.
    /// </summary>
    public required Func<long, bool> IsPayableSkill { get; init; }

    /// <summary>
    /// Whether a skill <see cref="IsPayableSkill"/> already accepted is OPEN in this build. False is
    /// <see cref="GameContentFindings.RecipeLockedSkill"/>: the row names a real skill, and a locked one
    /// refuses every award, so the step would cost its time and pay nothing.
    /// </summary>
    public required Func<long, bool> IsOpenSkill { get; init; }

    /// <summary>
    /// Whether the game stands for this station number. Asked only of a value other than
    /// <see cref="NoStation"/>, and false is <see cref="GameContentFindings.RecipeUnknownStation"/>.
    /// </summary>
    public required Func<long, bool> IsNameableStation { get; init; }
}
