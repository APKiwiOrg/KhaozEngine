using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The ONE evaluator for spec 10.3's fifteen guard kinds, stateless, reading the working copy and the
/// published content version it is handed and nothing ambient.
/// <para>
/// <b>Guards are ANDed, and the FIRST false is the one reported.</b> A set is a span rather than a tree,
/// evaluated in the authored <c>sort</c> order the caller hands it in, and there is no operator anywhere in
/// the vocabulary to compose two differently.
/// </para>
/// <para>
/// <b>A refusal NAMES the guard kind and carries no message</b> (<see cref="Failure"/>), because a kind is
/// a value a counter can bucket and a client can localize while a message is a string only its author
/// reads.
/// </para>
/// <para>
/// <b>Nothing here writes, and nothing here draws.</b> A guard is a question, so evaluating one costs no
/// randomness and leaves the payload exactly where it was. The only thing a failing guard does to the
/// working copy is record the refusal a TARGET guard's failure is.
/// </para>
/// </summary>
public static class CraftGuardEvaluator
{
    /// <summary>The refusal a failing guard is, which names the KIND and carries no message.</summary>
    /// <param name="kind">The guard that failed.</param>
    public static CraftRefusal Failure(CraftGuardKind kind) => new(CraftRefusalKind.GuardFailed, (int)kind);

    /// <summary>
    /// One guard set, ANDed, in the order it was handed in. A TARGET set that fails records the refusal on
    /// the working copy, so the whole craft stops and nothing is consumed. A STEP set that fails leaves the
    /// copy untouched, so the step is skipped and the craft continues.
    /// </summary>
    /// <param name="scope">Whether this is the craft's precondition or one step's.</param>
    /// <param name="guards">The set, in authored <c>sort</c> order. An empty set passes.</param>
    /// <param name="copy">The craft in progress.</param>
    /// <param name="snapshot">The content version the guards read.</param>
    /// <param name="failedIndex">Which guard failed, or -1 when none did.</param>
    /// <returns>What the set answered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    public static CraftGuardOutcome EvaluateSet(
        CraftGuardScope scope,
        scoped ReadOnlySpan<CraftGuard> guards,
        ref CraftWorkingCopy copy,
        IContentSnapshot snapshot,
        out int failedIndex)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        failedIndex = -1;
        if (copy.IsRefused)
        {
            // A craft that has already refused has no preconditions left to ask about.
            return CraftGuardOutcome.Refused;
        }

        for (int index = 0; index < guards.Length; index++)
        {
            CraftGuard guard = guards[index];
            if (Evaluate(guard.Kind, guard.ParameterA, guard.ParameterB, in copy, snapshot))
            {
                continue;
            }

            failedIndex = index;
            if (scope == CraftGuardScope.Step)
            {
                return CraftGuardOutcome.Skipped;
            }

            _ = copy.Refuse(Failure(guard.Kind));
            return CraftGuardOutcome.Refused;
        }

        return CraftGuardOutcome.Passed;
    }

    /// <summary>
    /// One guard, answered against the working copy. A kind outside the closed fifteen answers FALSE,
    /// because a question this build cannot ask has not been shown to hold.
    /// </summary>
    /// <param name="kind">Which question to ask.</param>
    /// <param name="a">Its first parameter, or 0.</param>
    /// <param name="b">Its second, or 0.</param>
    /// <param name="copy">The craft in progress.</param>
    /// <param name="snapshot">The content version the guard reads, for a rarity rule, a mod row or a base.</param>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    public static bool Evaluate(CraftGuardKind kind, int a, int b, in CraftWorkingCopy copy, IContentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return kind switch
        {
            CraftGuardKind.RarityIs => RarityOf(in copy) == a,
            CraftGuardKind.RarityIsAtMost => AtMost(in copy, snapshot, a),
            CraftGuardKind.AffixCountAtMost => CountOfKind(in copy, snapshot, a) <= b,
            CraftGuardKind.AffixCountAtLeast => CountOfKind(in copy, snapshot, a) >= b,
            CraftGuardKind.HasMod => Carries(in copy, a),
            CraftGuardKind.LacksMod => !Carries(in copy, a),
            CraftGuardKind.HasTag => BaseCarries(in copy, snapshot, a),
            CraftGuardKind.ItemLevelBetween => Between(Scalar(in copy, InstancePropertyKind.ItemLevel), a, b),
            CraftGuardKind.QualityBetween => Between(Scalar(in copy, InstancePropertyKind.Quality), a, b),
            CraftGuardKind.SocketCountBetween => Between(copy.SocketCount, a, b),
            CraftGuardKind.SocketEmpty => SocketEmpty(in copy, a),
            CraftGuardKind.IsIdentified => (Identified(in copy) ? 1 : 0) == a,
            CraftGuardKind.FlagIs => FlagIs(in copy, a, b),
            CraftGuardKind.NotLegacy => !CarriesLegacy(in copy),
            CraftGuardKind.IsCorruptible => !CraftStandingRules.IsCorrupted(in copy),
            _ => false,
        };
    }

    /// <summary>Kind 130's rarity rule id, or 0 for an item carrying no rarity.</summary>
    static int RarityOf(in CraftWorkingCopy copy)
        => copy.TryGetByte(InstancePropertyKind.Rarity, out byte rarity) ? rarity : 0;

    /// <summary>
    /// Whether the item's rarity is the named one or one the named one UPGRADES FROM, walked down the
    /// <c>upgrade_from</c> chain from the guard's own rule.
    /// <para>
    /// <b>It walks rather than comparing ids, and the direction matters.</b> A rarity id is an authoring
    /// number with no order in it: a rarity added late sits above the whole ladder and below every id on
    /// it, so <c>current &lt;= parameter</c> answers a question about when the author typed the row. The
    /// chain is the only thing in the content that knows which rarity is above which.
    /// </para>
    /// <para>
    /// <b>Spec 10.3's "true when" column reads the chain from the ITEM's rule instead</b>, which reaches the
    /// parameter exactly when the item is at or ABOVE it and therefore inverts the guard's own name. This
    /// takes the name, because "at most magic" passing on a rare item makes the guard useless for the one
    /// thing an author writes it for.
    /// </para>
    /// <para>
    /// An item carrying no kind 130 at all is at most NOTHING, because no rule names it and the chain is
    /// the only order there is.
    /// </para>
    /// </summary>
    static bool AtMost(in CraftWorkingCopy copy, IContentSnapshot snapshot, int rarityId)
    {
        int current = RarityOf(in copy);
        var type = new ContentTypeId(InstanceContentTypeIds.RarityRuleTypeId);
        int walked = rarityId;

        // Bounded by the rule count, so a cycle an author published stops rather than spinning.
        for (int step = 0; step <= RarityRuleContentType.MaxDefinitionId && walked > 0; step++)
        {
            if (walked == current)
            {
                return true;
            }

            if (!snapshot.TryGetRow(type, walked, out ContentRow? rule))
            {
                return false;
            }

            walked = (int)(InstanceContentChecks.Number(rule, RarityRuleContentType.UpgradeFromIndex) ?? 0);
        }

        return false;
    }

    /// <summary>
    /// How many kind 131 entries name a mod of that kind, or the whole list when the kind is 0. Kind 133 is
    /// NOT counted: an enchantment is not an affix and no rarity rule's counts include one.
    /// </summary>
    static int CountOfKind(in CraftWorkingCopy copy, IContentSnapshot snapshot, int modKind)
    {
        Span<InstanceAffix> affixes = stackalloc InstanceAffix[CraftWorkingCopy.MaxAffixes];
        int count = copy.ReadAffixes(InstancePropertyKind.Affixes, affixes);
        if (modKind == 0)
        {
            return count;
        }

        int matched = 0;
        for (int index = 0; index < count; index++)
        {
            if (ModKindOf(snapshot, affixes[index].ModId) == modKind)
            {
                matched++;
            }
        }

        return matched;
    }

    /// <summary>
    /// Whether kind 131 OR kind 133 names that mod. Both, because an enchantment is a mod the item carries
    /// and an author asking "does it have this" means the item rather than one of its two lists.
    /// </summary>
    static bool Carries(in CraftWorkingCopy copy, int modId)
        => Holds(in copy, InstancePropertyKind.Affixes, modId) || Holds(in copy, InstancePropertyKind.Enchantments, modId);

    /// <summary>Whether one list names that mod.</summary>
    static bool Holds(in CraftWorkingCopy copy, ushort kind, int modId)
    {
        Span<InstanceAffix> entries = stackalloc InstanceAffix[CraftWorkingCopy.MaxAffixes];
        int count = copy.ReadAffixes(kind, entries);
        for (int index = 0; index < count; index++)
        {
            if (entries[index].ModId == modId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether either list names a mod row this version marks <c>legacy</c>.</summary>
    static bool CarriesLegacy(in CraftWorkingCopy copy)
        => HoldsLegacy(in copy, InstancePropertyKind.Affixes) || HoldsLegacy(in copy, InstancePropertyKind.Enchantments);

    /// <summary>Whether one list names a legacy mod row.</summary>
    static bool HoldsLegacy(in CraftWorkingCopy copy, ushort kind)
    {
        Span<InstanceAffix> entries = stackalloc InstanceAffix[CraftWorkingCopy.MaxAffixes];
        int count = copy.ReadAffixes(kind, entries);
        for (int index = 0; index < count; index++)
        {
            if (copy.IsLegacyMod(entries[index].ModId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the BASE's authored tag list carries that tag, which is the <c>item</c> row rather than
    /// anything in the payload. The instance has no tags of its own and never will: a tag on an instance
    /// would be a second representation of the same fact, and contracts 4.6 keeps exactly one.
    /// </summary>
    static bool BaseCarries(in CraftWorkingCopy copy, IContentSnapshot snapshot, int tagId)
    {
        if (tagId <= 0
            || !snapshot.TryGetRow(new ContentTypeId(EngineContentTypes.ItemTypeId), copy.DefinitionId, out ContentRow? item))
        {
            return false;
        }

        var tags = new List<int>();
        GenerationTagSignature.ReadTags(item, tags);
        return tags.Contains(tagId);
    }

    /// <summary>One scalar field's value, or 0 for a field the item does not carry.</summary>
    static long Scalar(in CraftWorkingCopy copy, ushort kind)
        => copy.TryGetScalar(kind, out ulong value) ? (long)value : 0;

    /// <summary>One inclusive range test, which is what four of the fifteen kinds are.</summary>
    static bool Between(long value, int minimum, int maximum) => value >= minimum && value <= maximum;

    /// <summary>
    /// Whether that socket exists and holds nothing. The socket list is heap allocated rather than
    /// stack allocated because a socket entry carries its contained payload as managed memory.
    /// </summary>
    static bool SocketEmpty(in CraftWorkingCopy copy, int index)
    {
        int count = copy.SocketCount;
        if (index < 0 || index >= count)
        {
            return false;
        }

        var sockets = new InstanceSocket[count];
        _ = copy.ReadSockets(sockets);
        return sockets[index].ContainedDefinitionId == 0;
    }

    /// <summary>
    /// Whether the item reads as identified. An item carrying no kind 128 at all was never unidentified,
    /// so it reads as identified rather than as state 0, which is what stops a target guard of
    /// <c>IsIdentified(1)</c> refusing every ordinary item in the game.
    /// </summary>
    static bool Identified(in CraftWorkingCopy copy)
        => !copy.TryGetIdentification(out bool identified, out _) || identified;

    /// <summary>Whether one bit of kind 1 equals that value, with a bit outside the field answering false.</summary>
    static bool FlagIs(in CraftWorkingCopy copy, int bit, int value)
    {
        if (bit is < 0 or > 31 || value is < 0 or > 1)
        {
            return false;
        }

        uint bits = (uint)Scalar(in copy, InstancePropertyKind.Flags);
        return ((bits >> bit) & 1u) == (uint)value;
    }

    /// <summary>One mod row's authored kind, or 0 for a mod this version has no row for.</summary>
    static int ModKindOf(IContentSnapshot snapshot, int modId)
        => snapshot.TryGetRow(new ContentTypeId(InstanceContentTypeIds.ModTypeId), modId, out ContentRow? row)
            ? (int)(InstanceContentChecks.Number(row, ModContentType.KindIndex) ?? 0)
            : 0;
}
