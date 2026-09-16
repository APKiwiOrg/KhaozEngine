namespace KhaozEngine.ItemInstances;

/// <summary>
/// The fifteen guard kinds of spec 10.3, CLOSED. A guard is a PRECONDITION evaluated against the working
/// copy before a step runs, and it is a kind plus at most two integer parameters, so a guard set encodes in
/// a few bytes and a refusal names a guard rather than a message.
/// <para>
/// <b>The numbers ARE the authored <c>currency_guard.guard_kind</c> values</b>, 1 to 15, which
/// <see cref="CurrencyGuardContentType.MinGuardKind"/> and <see cref="CurrencyGuardContentType.MaxGuardKind"/>
/// already bound on both sides of the row codec. They are durable the moment one currency row names one, so
/// none of them moves.
/// </para>
/// <para>
/// <b>Guards are ANDed and there is no OR, no NOT and no nesting.</b> An OR is two currency rows, which is
/// one more authored row and no evaluator. A guard carries two INTEGERS and never another guard, so the
/// shape itself is what makes an expression tree impossible rather than a rule a reviewer enforces.
/// </para>
/// <para>
/// <b>Two of the fifteen are also STANDING rules, which no currency authors and none can opt out of.</b>
/// <see cref="IsCorruptible"/> and <see cref="NotLegacy"/> are refused by
/// <see cref="CraftStandingRules"/> whatever a currency's guard set says. They stay in this vocabulary
/// because the working copy reports a standing refusal BY KIND, and because writing one down on a step is a
/// legal, redundant way for an author to document intent.
/// </para>
/// </summary>
public enum CraftGuardKind : byte
{
    /// <summary>Kind 130 equals the rarity id. Parameter A is the rarity rule id.</summary>
    RarityIs = 1,

    /// <summary>
    /// The item's rarity is the named one or something the named one UPGRADES FROM, walked down the
    /// <c>upgrade_from</c> chain. Parameter A is the rarity rule id.
    /// </summary>
    RarityIsAtMost = 2,

    /// <summary>
    /// The item carries at most that many affixes of that mod kind. Parameter A is the mod kind, or 0 for
    /// every kind, and parameter B is the count.
    /// </summary>
    AffixCountAtMost = 3,

    /// <summary>The item carries at least that many, with the same two parameters.</summary>
    AffixCountAtLeast = 4,

    /// <summary>Kind 131 or kind 133 carries that mod. Parameter A is the mod id.</summary>
    HasMod = 5,

    /// <summary>Neither list carries it. Parameter A is the mod id.</summary>
    LacksMod = 6,

    /// <summary>The BASE carries that tag, read off the <c>item</c> row. Parameter A is the tag id.</summary>
    HasTag = 7,

    /// <summary>Kind 2 is inside the range, inclusive. Parameters A and B are the minimum and maximum.</summary>
    ItemLevelBetween = 8,

    /// <summary>Kind 3 is inside the range, inclusive, with an absent kind 3 reading as quality 0.</summary>
    QualityBetween = 9,

    /// <summary>Kind 132's socket count is inside the range, inclusive.</summary>
    SocketCountBetween = 10,

    /// <summary>That socket's contained definition is 0. Parameter A is the socket index.</summary>
    SocketEmpty = 11,

    /// <summary>Kind 128's state equals it. Parameter A is 1 for identified and 0 for not.</summary>
    IsIdentified = 12,

    /// <summary>That bit of kind 1 equals that value. Parameter A is the bit and parameter B is 0 or 1.</summary>
    FlagIs = 13,

    /// <summary>
    /// No affix on the item names a mod row carrying <c>legacy</c>. As an AUTHORED guard this is the
    /// stronger statement that the item carries NONE. The STANDING half of the rule is different and lives
    /// in <see cref="CraftStandingRules"/>: a primitive that would ADD a legacy row refuses whatever the
    /// guard set says.
    /// </summary>
    NotLegacy = 14,

    /// <summary>
    /// Kind 1 bit 0 is clear. STANDING, because "corrupted" means "cannot be modified further" and a
    /// refusal a currency can FORGET is not that.
    /// </summary>
    IsCorruptible = 15,
}
