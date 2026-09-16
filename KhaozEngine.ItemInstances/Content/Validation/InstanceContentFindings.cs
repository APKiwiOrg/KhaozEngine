using System;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The fifteen finding codes of the <c>KEC0100</c> band and the messages that carry them. The band is
/// RESERVED for the item instances types, 100 to 199, so an affix rule and a catalog structure rule are
/// told apart by an operator reading a code rather than by reading a message.
/// <para>
/// <b>A code is a STABLE token.</b> A counter, a test and an operator runbook all key on it, so a code is
/// never renumbered and a withdrawn one is never reissued, exactly as <c>KEC0001</c> to <c>KEC0042</c> are
/// treated. That is why <see cref="All"/> is a pinned list rather than a generated range.
/// </para>
/// <para>
/// <b>Twelve of the fifteen are spec 8.9's twelve checks, two are the weight bounds of
/// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/944">944</see>, and one is the generation tag
/// position ceiling the candidate tables impose.</b> The mapping is not one code per check in two places,
/// and both exceptions are deliberate. Check 2 SPLITS, because an item level
/// range an author inverted and a tier ordinal outside the payload's byte are different mistakes with
/// different fixes. Checks 3 and 12 SHARE <see cref="TierOrdinalMoved"/>, because both are the same
/// refusal seen from two sides: a tier ordinal that a stored payload already names is not where it was.
/// </para>
/// </summary>
public static class InstanceContentFindings
{
    /// <summary>
    /// A child row's parent reference does not resolve, or a <c>currency_guard</c> names a
    /// <c>currency_step</c> of a DIFFERENT currency. Spec 8.9 check 1.
    /// </summary>
    public const string ParentUnresolved = "KEC0100";

    /// <summary>A <c>mod_tier</c> whose <c>item_level_min</c> is above its <c>item_level_max</c>.</summary>
    public const string TierItemLevelRange = "KEC0101";

    /// <summary>
    /// A <c>mod_tier</c> ordinal outside 1 to 255, or duplicated within its own mod. The payload stores the
    /// pair of mod id and ordinal, so the same ordinal on a different mod is ordinary.
    /// </summary>
    public const string TierOrdinal = "KEC0102";

    /// <summary>
    /// PUBLISH ONLY. A tier ordinal moved since the previous published version, or one that was live then is
    /// gone now with no remap rule covering it. Both are a stored affix entry repointed at a range it was
    /// never rolled in.
    /// </summary>
    public const string TierOrdinalMoved = "KEC0103";

    /// <summary>
    /// A <c>stat_line</c> whose <c>stat_id</c> does not resolve, whose <c>combine</c> is not 1, 2 or 3, or
    /// whose <c>min</c> is above its <c>max</c>.
    /// </summary>
    public const string StatLineShape = "KEC0104";

    /// <summary>A <c>mod_group</c> whose <c>max_per_item</c> is below 1.</summary>
    public const string ModGroupCount = "KEC0105";

    /// <summary>
    /// A <c>rarity_rule</c> whose counts contradict each other, or whose <c>upgrade_from</c> chain cycles.
    /// </summary>
    public const string RarityRuleShape = "KEC0106";

    /// <summary>
    /// A <c>unique_line</c> whose mod does not resolve, whose <c>tier_ordinal</c> names no tier of that
    /// mod, or whose mod carries a <c>mod_tier_weight</c> row, which would put a unique's line in the rare
    /// pool for every base carrying that tag.
    /// </summary>
    public const string UniqueLineShape = "KEC0107";

    /// <summary>A <c>socket_type</c> whose accept and reject tag sets overlap.</summary>
    public const string SocketTagOverlap = "KEC0108";

    /// <summary>
    /// A rarity that rolls a name has a position no word of non-zero weight can fill, for a tag that rarity
    /// is reachable at. This is the publish that would produce an item whose name cannot be rolled.
    /// </summary>
    public const string RareNameCoverage = "KEC0109";

    /// <summary>
    /// A <c>crafting_currency</c> carrying more steps than its <c>max_steps</c>, declaring a
    /// <c>max_steps</c> above the v1 ceiling, or carrying two steps with the same <c>sort</c>.
    /// </summary>
    public const string CurrencyStepSet = "KEC0110";

    /// <summary>
    /// PUBLISH ONLY. A <c>rarity_rule</c> key that names a different definition id than it did in the
    /// previous published version. Payload kind 130 stores the id, so moving it repoints every stored item.
    /// </summary>
    public const string RarityRuleIdMoved = "KEC0111";

    /// <summary>
    /// A weight below zero on any of the three weight types. A negative weight makes a prefix array
    /// non-monotonic and unsearchable, and the load-time clamp that saves it changes the odds silently.
    /// </summary>
    public const string WeightBelowZero = "KEC0112";

    /// <summary>
    /// A weight bucket whose members sum past <see cref="int.MaxValue"/>. The prefix total saturates at
    /// load, which silently changes every probability in that bucket.
    /// </summary>
    public const string WeightBucketOverflow = "KEC0113";

    /// <summary>
    /// An <c>item</c> base whose authored tag list is longer than
    /// <see cref="ModCandidateTables.MaxGenerationTagPositions"/>. The candidate tables size their
    /// suppression headers by signatures times bands times kinds times tag POSITIONS, so an unbounded
    /// position count makes the table build unbounded.
    /// </summary>
    public const string GenerationTagPositions = "KEC0114";

    /// <summary>Every code this band emits, ascending, pinned rather than generated.</summary>
    public static readonly string[] All =
    [
        ParentUnresolved,
        TierItemLevelRange,
        TierOrdinal,
        TierOrdinalMoved,
        StatLineShape,
        ModGroupCount,
        RarityRuleShape,
        UniqueLineShape,
        SocketTagOverlap,
        RareNameCoverage,
        CurrencyStepSet,
        RarityRuleIdMoved,
        WeightBelowZero,
        WeightBucketOverflow,
        GenerationTagPositions,
    ];

    internal static string ParentMissing(string childType, int childId, string field, string parentType, long parentId)
        => FormattableString.Invariant(
            $"Row {childId} of type '{childType}' points '{field}' at {parentType} {parentId}, which is not a live row of this version. A child row's parent reference is what makes it a child at all.");

    internal static string GuardOnForeignStep(int guardId, long stepId, int stepCurrency, long guardCurrency)
        => FormattableString.Invariant(
            $"Guard {guardId} belongs to crafting_currency {guardCurrency} and points 'currency_step_id' at currency_step {stepId}, which belongs to crafting_currency {stepCurrency}. A target guard and a step guard are one type told apart by an empty step reference, so a guard reaching into another currency's steps is the one shape that split has to refuse.");

    internal static string TierLevelsInverted(int tierId, long min, long max)
        => FormattableString.Invariant(
            $"Tier {tierId} is gated at item level {min} to {max}, which is an empty range. The gate is inclusive, so a tier reachable at exactly one level carries the same number twice.");

    internal static string OrdinalOutOfRange(int tierId, long ordinal, int min, int max)
        => FormattableString.Invariant(
            $"Tier {tierId} carries ordinal {ordinal}, outside {min} to {max}. The ordinal is a byte in every stored affix entry, so a number the payload cannot hold is a tier no item could ever name.");

    internal static string OrdinalOverPackedCeiling(int tierId, long ordinal, int ceiling)
        => FormattableString.Invariant(
            $"Tier {tierId} carries ordinal {ordinal}, over the candidate table's ceiling of {ceiling}. The table packs the ordinal into the low bits beside the mod id, so an ordinal above the ceiling would spawn as a different tier of the same mod instead of refusing to publish.");

    internal static string GenerationTagsOverCeiling(int itemId, int tags, int ceiling)
        => FormattableString.Invariant(
            $"Item base {itemId} carries {tags} authored tags, over the generation ceiling of {ceiling}. The candidate tables hold one overlap header per tag POSITION for every signature, band and mod kind, so a longer list multiplies the table build rather than costing one row.");

    internal static string OrdinalDuplicated(int tierId, long ordinal, long modId, int firstTierId)
        => FormattableString.Invariant(
            $"Tier {tierId} takes ordinal {ordinal} on mod {modId}, which tier {firstTierId} already holds. The payload stores the pair of mod id and ordinal, so two tiers sharing one ordinal is a stored entry with two readings.");

    internal static string OrdinalReordered(int tierId, long previousMod, long previousOrdinal, long modId, long ordinal)
        => FormattableString.Invariant(
            $"Tier {tierId} was mod {previousMod} ordinal {previousOrdinal} at the previous published version and is mod {modId} ordinal {ordinal} now. A reorder repoints every affix already in the world at a range it was never rolled in, so it is refused and an append is not.");

    internal static string OrdinalRemoved(int tierId, long modId, long ordinal, int previousVersion)
        => FormattableString.Invariant(
            $"Tier {tierId}, mod {modId} ordinal {ordinal}, was live at version {previousVersion} and is not live now, and no remap rule in the set names it. A stored affix entry can still carry that pair, so the removal needs a rule to land on.");

    internal static string StatLineCombine(int lineId, long combine)
        => FormattableString.Invariant(
            $"Stat line {lineId} carries combine {combine}. The three kinds are 1 flat, 2 increased and 3 more, and a fourth would be a line the fold has no rule for.");

    internal static string StatLineRange(int lineId, long min, long max)
        => FormattableString.Invariant(
            $"Stat line {lineId} rolls {min} to {max}, which is an empty range. The range is inclusive, so a fixed line carries the same number twice.");

    internal static string StatLineStat(int lineId, long statId)
        => FormattableString.Invariant(
            $"Stat line {lineId} names stat {statId}, which is not a live row of this version. The line grants a stat, so a stat that is not there is a line that folds into nothing.");

    internal static string ModGroupBelowOne(int groupId, long maxPerItem)
        => FormattableString.Invariant(
            $"Mod group {groupId} permits {maxPerItem} of itself per item. A group with a count below 1 is a group no mod in it could ever be rolled from, which is a retire rather than a count.");

    internal static string RarityAffixCounts(int rarityId, long min, long max)
        => FormattableString.Invariant(
            $"Rarity {rarityId} rolls {min} to {max} affixes, which is an empty range. A rarity that rolls a fixed count carries the same number twice, and one that rolls none carries 0 and 0.");

    internal static string RarityKindCounts(int rarityId, long prefixes, long suffixes, long max)
        => FormattableString.Invariant(
            $"Rarity {rarityId} permits {prefixes} prefixes and {suffixes} suffixes and rolls up to {max} affixes. The two kind limits have to reach the total, or the rarity asks for affixes it has nowhere to put.");

    internal static string RarityCycle(int rarityId, string chain)
        => FormattableString.Invariant(
            $"Rarity {rarityId} sits on an upgrade_from cycle, {chain}. The rarities form a forest, so the chain a set-rarity craft walks has to end.");

    internal static string UniqueLineMod(int lineId, long modId)
        => FormattableString.Invariant(
            $"Unique line {lineId} names mod {modId}, which is not a live row of this version. A unique's line is an ordinary mod row, so a missing one is a line with nothing to grant.");

    internal static string UniqueLineOrdinal(int lineId, long modId, long ordinal)
        => FormattableString.Invariant(
            $"Unique line {lineId} names mod {modId} at tier ordinal {ordinal}, and that mod carries no tier with it. The ordinal is what the stored affix entry holds, so a line naming one that does not exist cannot be written.");

    internal static string UniqueLineWeighted(int lineId, long modId, int weightRowId)
        => FormattableString.Invariant(
            $"Unique line {lineId} names mod {modId}, which carries the mod_tier_weight row {weightRowId}. A unique's lines are mod rows with NO weight row, so a weight quietly adds that mod to the rare pool for every base carrying its tag.");

    internal static string SocketOverlap(int socketTypeId, long tagId, int acceptRowId, int rejectRowId)
        => FormattableString.Invariant(
            $"Socket type {socketTypeId} both accepts tag {tagId} (row {acceptRowId}) and rejects it (row {rejectRowId}). Reject wins over accept, so the accept row is dead and the pair is an author disagreeing with themselves.");

    internal static string RareNameUncovered(int rarityId, int position, long tagId)
        => FormattableString.Invariant(
            $"Rarity {rarityId} rolls a name word at position {position}, and for tag {tagId}, which it is reachable at, no word of that position carries a weight above zero. That publish produces an item whose name cannot be rolled.");

    internal static string CurrencyStepCount(int currencyId, int steps, long maxSteps)
        => FormattableString.Invariant(
            $"Crafting currency {currencyId} declares max_steps {maxSteps} and carries {steps} step rows. The declared ceiling is what a reader sizes its working copy against.");

    internal static string CurrencyMaxSteps(int currencyId, long maxSteps, int ceiling)
        => FormattableString.Invariant(
            $"Crafting currency {currencyId} declares max_steps {maxSteps}, over the v1 ceiling of {ceiling}.");

    internal static string CurrencyDuplicateSort(int stepId, long sort, long currencyId, int firstStepId)
        => FormattableString.Invariant(
            $"Currency step {stepId} takes sort {sort} in crafting_currency {currencyId}, which step {firstStepId} already holds. The sort is the step order, so two steps sharing one is an order with no answer.");

    internal static string RarityIdMoved(string key, int previousId, int id, int previousVersion)
        => FormattableString.Invariant(
            $"Rarity rule '{key}' was definition id {previousId} at version {previousVersion} and is {id} now. Payload kind 130 stores the rarity id, so moving it repoints every stored item at a different rarity.");

    internal static string WeightNegative(string typeKey, int rowId, long weight)
        => FormattableString.Invariant(
            $"Row {rowId} of type '{typeKey}' carries weight {weight}. A negative weight makes the cumulative array the draw searches non-monotonic, and the load-time clamp to zero that rescues it changes the odds with nothing reported.");

    internal static string WeightOverflow(string typeKey, long tagId, long sum)
        => FormattableString.Invariant(
            $"The '{typeKey}' rows keyed by tag {tagId} sum to {sum}, past the int ceiling of {int.MaxValue}. The cumulative total saturates at load, which silently changes every probability in that bucket.");
}
