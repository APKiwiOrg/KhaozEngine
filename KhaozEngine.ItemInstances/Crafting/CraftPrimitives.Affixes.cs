using System;
using KhaozEngine.Catalog;
using KhaozEngine.Primitives;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The seven primitives of spec 10.2 whose subject is the AFFIX LIST: 1 <c>AddRandomMod</c>,
/// 2 <c>RemoveMod</c>, 3 <c>RerollValues</c>, 4 <c>RerollMods</c>, 5 <c>SetRarity</c>,
/// 9 <c>ApplyEnchant</c> and 10 <c>RemoveEnchant</c>.
/// <para>
/// <b>The draws are the GENERATOR's, not a second weighted pick.</b> <c>AddRandomMod</c> is spec 9.4 steps
/// 6 to 8 through <see cref="ItemGenerator.TryDrawAffix"/> and <c>RerollMods</c> and <c>SetRarity</c>'s fill
/// are steps 4 to 9 through <see cref="ItemGenerator.RedrawAffixes"/>, both against the item's OWN base and
/// its OWN item level from kind 2, and both consuming draws from the same <c>IRandomSource</c> the executor
/// is running on. A copy of the pick here would be a second distribution nobody could keep in step.
/// </para>
/// <para>
/// <b>Primitives 2, 3 and 10 take resolved INDEXES rather than a selector.</b> The selector vocabulary of
/// spec 10.3 is the guard half's, and it resolves to indexes into the sorted list before a primitive runs,
/// which is what keeps "<c>RandomOfKind</c> draws exactly once" a property of the SELECTOR rather than
/// something each of the three has to remember.
/// </para>
/// </summary>
public static partial class CraftPrimitives
{
    /// <summary>
    /// Primitive 1. One pick through spec 9.4 steps 6 to 8 against the item's OWN base and item level, never
    /// the crafter's and never the base's, which is what makes item level worth storing per instance.
    /// </summary>
    /// <param name="copy">The craft in progress.</param>
    /// <param name="generator">The generator holding the tables and the random source.</param>
    /// <param name="modKind">The mod kind to pick, which is a parameter here so step 5 draws nothing.</param>
    /// <param name="tierCeiling">The highest tier ordinal to write, or 0 for no ceiling.</param>
    /// <returns>The refusal, or null when the step applied.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="generator"/> is null.</exception>
    public static CraftRefusal? AddRandomMod(
        ref CraftWorkingCopy copy,
        ItemGenerator generator,
        int modKind,
        int tierCeiling)
    {
        ArgumentNullException.ThrowIfNull(generator);
        if (copy.IsRefused)
        {
            return copy.Refusal;
        }

        if (!copy.TryGetScalar(InstancePropertyKind.ItemLevel, out ulong itemLevel))
        {
            return copy.Refuse(new CraftRefusal(CraftRefusalKind.FieldAbsent, InstancePropertyKind.ItemLevel));
        }

        Span<InstanceAffix> affixes = stackalloc InstanceAffix[CraftWorkingCopy.MaxAffixes];
        int count = copy.ReadAffixes(InstancePropertyKind.Affixes, affixes);
        if (count == CraftWorkingCopy.MaxAffixes)
        {
            return copy.Refuse(new CraftRefusal(CraftRefusalKind.AffixListFull, InstancePropertyKind.Affixes));
        }

        if (!generator.TryDrawAffix(
            copy.DefinitionId,
            (int)itemLevel,
            modKind,
            tierCeiling,
            affixes[..count],
            out InstanceAffix drawn))
        {
            return copy.Refuse(new CraftRefusal(CraftRefusalKind.DrawEmpty, modKind));
        }

        affixes[count++] = drawn;
        return copy.SetAffixes(InstancePropertyKind.Affixes, affixes[..count]) ? null : copy.Refusal;
    }

    /// <summary>Primitive 2. Removes the selected entries from kind 131.</summary>
    /// <param name="copy">The craft in progress.</param>
    /// <param name="indexes">Indexes into the SORTED list, which is the order the payload stores.</param>
    /// <returns>The refusal, or null when the step applied.</returns>
    public static CraftRefusal? RemoveMod(ref CraftWorkingCopy copy, scoped ReadOnlySpan<int> indexes)
        => Drop(ref copy, InstancePropertyKind.Affixes, indexes);

    /// <summary>
    /// Primitive 3. A fresh <c>NextRollPosition</c> for every selected entry, same mods and same tiers. The
    /// positions are drawn in ASCENDING INDEX order, so a replay against the same seed rewrites the same
    /// entries with the same values whatever order the selector named them in.
    /// </summary>
    /// <param name="copy">The craft in progress.</param>
    /// <param name="random">The gameplay randomness seam the executor is running on.</param>
    /// <param name="indexes">Indexes into the sorted list.</param>
    /// <returns>The refusal, or null when the step applied.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="random"/> is null.</exception>
    public static CraftRefusal? RerollValues(
        ref CraftWorkingCopy copy,
        IRandomSource random,
        scoped ReadOnlySpan<int> indexes)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (copy.IsRefused)
        {
            return copy.Refusal;
        }

        Span<InstanceAffix> affixes = stackalloc InstanceAffix[CraftWorkingCopy.MaxAffixes];
        int count = copy.ReadAffixes(InstancePropertyKind.Affixes, affixes);
        Span<bool> selected = stackalloc bool[CraftWorkingCopy.MaxAffixes];
        if (Select(ref copy, indexes, count, selected) is CraftRefusal refusal)
        {
            return refusal;
        }

        for (int index = 0; index < count; index++)
        {
            if (selected[index])
            {
                affixes[index] = affixes[index] with { Position = random.NextRollPosition() };
            }
        }

        return copy.SetAffixes(InstancePropertyKind.Affixes, affixes[..count]) ? null : copy.Refusal;
    }

    /// <summary>
    /// Primitive 4. Discards the affixes of the masked kinds and re-runs spec 9.4 steps 4 to 9 for them. The
    /// affixes of every OTHER kind are kept, excluded from the pool before the draw and counted against the
    /// rarity rule's per kind caps, so the reroll cannot walk past a cap the kept half already filled.
    /// </summary>
    /// <param name="copy">The craft in progress.</param>
    /// <param name="generator">The generator holding the tables and the random source.</param>
    /// <param name="modKindMask">Bit <c>kind - 1</c> per mod kind, or 0 for every kind.</param>
    /// <returns>The refusal, or null when the step applied.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="generator"/> is null.</exception>
    public static CraftRefusal? RerollMods(ref CraftWorkingCopy copy, ItemGenerator generator, uint modKindMask)
    {
        ArgumentNullException.ThrowIfNull(generator);
        if (copy.IsRefused)
        {
            return copy.Refusal;
        }

        if (!copy.TryGetScalar(InstancePropertyKind.ItemLevel, out ulong itemLevel))
        {
            return copy.Refuse(new CraftRefusal(CraftRefusalKind.FieldAbsent, InstancePropertyKind.ItemLevel));
        }

        Span<InstanceAffix> affixes = stackalloc InstanceAffix[CraftWorkingCopy.MaxAffixes];
        int count = copy.ReadAffixes(InstancePropertyKind.Affixes, affixes);
        Span<InstanceAffix> keep = stackalloc InstanceAffix[CraftWorkingCopy.MaxAffixes];
        int kept = 0;
        for (int index = 0; index < count; index++)
        {
            if (!Masked(in copy, modKindMask, affixes[index].ModId))
            {
                keep[kept++] = affixes[index];
            }
        }

        _ = copy.TryGetByte(InstancePropertyKind.Rarity, out byte rarityId);
        int total = generator.RedrawAffixes(
            copy.DefinitionId,
            (int)itemLevel,
            rarityId,
            modKindMask,
            picks: -1,
            keep[..kept],
            affixes);

        return copy.SetAffixes(InstancePropertyKind.Affixes, affixes[..total]) ? null : copy.Refusal;
    }

    /// <summary>
    /// Primitive 5, the only one that can both add and remove affixes, in a defined order: TRIM from the
    /// END of the sorted list (highest mod id) when the new rule permits fewer, and FILL by repeating steps
    /// 5 to 8 when it permits more AND the currency asked for a fill. Trimming from the sorted end rather
    /// than at random is what makes the operation reproducible from the journal event WITHOUT a draw, and a
    /// trim consumes no randomness at all.
    /// <para>
    /// <b>The trim is against the rule's affix COUNT and nothing else.</b> A per kind trim would have to
    /// skip entries in the middle of the list, which is not "from the end", and the per kind caps are the
    /// FILL's business because the fill runs through the generator, which applies them at step 5.
    /// </para>
    /// <para>
    /// A <paramref name="rarityId"/> of 0 walks <c>upgrade_from</c>: the answer is the single live
    /// <c>rarity_rule</c> row naming the item's current rarity, and neither none nor two is an answer.
    /// </para>
    /// </summary>
    /// <param name="copy">The craft in progress.</param>
    /// <param name="generator">The generator, asked only when <paramref name="fill"/> is true.</param>
    /// <param name="rarityId">The rarity rule to seat, or 0 to walk <c>upgrade_from</c>.</param>
    /// <param name="fill">Whether the currency asked for a fill.</param>
    /// <returns>The refusal, or null when the step applied.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="generator"/> is null.</exception>
    public static CraftRefusal? SetRarity(
        ref CraftWorkingCopy copy,
        ItemGenerator generator,
        int rarityId,
        bool fill)
    {
        ArgumentNullException.ThrowIfNull(generator);
        if (copy.IsRefused)
        {
            return copy.Refusal;
        }

        if (Resolve(ref copy, rarityId, out ContentRow? rule) is CraftRefusal refusal)
        {
            return refusal;
        }

        if (rule.Id is <= 0 or > RarityRuleContentType.MaxDefinitionId)
        {
            return copy.Refuse(new CraftRefusal(CraftRefusalKind.ValueOutOfRange, rule.Id));
        }

        if (!copy.SetByte(InstancePropertyKind.Rarity, (byte)rule.Id))
        {
            return copy.Refusal;
        }

        int maximum = (int)(InstanceContentChecks.Number(rule, RarityRuleContentType.MaxAffixesIndex) ?? 0);
        int minimum = (int)(InstanceContentChecks.Number(rule, RarityRuleContentType.MinAffixesIndex) ?? 0);
        Span<InstanceAffix> affixes = stackalloc InstanceAffix[CraftWorkingCopy.MaxAffixes];
        int count = copy.ReadAffixes(InstancePropertyKind.Affixes, affixes);
        while (count > maximum && count > 0)
        {
            count--;
        }

        if (fill && count < minimum)
        {
            if (!copy.TryGetScalar(InstancePropertyKind.ItemLevel, out ulong itemLevel))
            {
                return copy.Refuse(new CraftRefusal(CraftRefusalKind.FieldAbsent, InstancePropertyKind.ItemLevel));
            }

            Span<InstanceAffix> keep = stackalloc InstanceAffix[CraftWorkingCopy.MaxAffixes];
            affixes[..count].CopyTo(keep);
            count = generator.RedrawAffixes(
                copy.DefinitionId,
                (int)itemLevel,
                rule.Id,
                ItemGenerator.AllModKinds,
                minimum - count,
                keep[..count],
                affixes);
        }

        return copy.SetAffixes(InstancePropertyKind.Affixes, affixes[..count]) ? null : copy.Refusal;
    }

    /// <summary>
    /// Primitive 9. Writes one entry into kind 133, at <see cref="RollPosition.Bottom"/>, because the step
    /// draws NOTHING and a position has to be something. That is the same reading spec 9.4 step 2 takes for
    /// a unique's lines, and it is why an author composes <c>ApplyEnchant</c> with <c>RerollValues</c> when
    /// the enchant is meant to roll.
    /// </summary>
    /// <param name="copy">The craft in progress.</param>
    /// <param name="modId">The mod row the entry names.</param>
    /// <param name="tierOrdinal">Its authored tier ordinal, 1 to 255.</param>
    /// <returns>The refusal, or null when the step applied.</returns>
    public static CraftRefusal? ApplyEnchant(ref CraftWorkingCopy copy, int modId, int tierOrdinal)
    {
        if (copy.IsRefused)
        {
            return copy.Refusal;
        }

        if (tierOrdinal is < ModTierContentType.MinOrdinal or > ModTierContentType.MaxOrdinal)
        {
            return copy.Refuse(new CraftRefusal(CraftRefusalKind.ValueOutOfRange, tierOrdinal));
        }

        if (!InstanceContentChecks.IsLive(copy.Snapshot, InstanceContentTypeIds.ModTypeId, modId)
            || !HasTier(in copy, modId, tierOrdinal))
        {
            return copy.Refuse(new CraftRefusal(CraftRefusalKind.ContentRowMissing, modId));
        }

        Span<InstanceAffix> entries = stackalloc InstanceAffix[CraftWorkingCopy.MaxAffixes];
        int count = copy.ReadAffixes(InstancePropertyKind.Enchantments, entries);
        if (count == CraftWorkingCopy.MaxAffixes)
        {
            return copy.Refuse(new CraftRefusal(CraftRefusalKind.AffixListFull, InstancePropertyKind.Enchantments));
        }

        entries[count++] = new InstanceAffix(modId, (byte)tierOrdinal, RollPosition.Bottom);
        return copy.SetAffixes(InstancePropertyKind.Enchantments, entries[..count]) ? null : copy.Refusal;
    }

    /// <summary>Primitive 10. Removes the selected entries from kind 133.</summary>
    /// <param name="copy">The craft in progress.</param>
    /// <param name="indexes">Indexes into the sorted list.</param>
    /// <returns>The refusal, or null when the step applied.</returns>
    public static CraftRefusal? RemoveEnchant(ref CraftWorkingCopy copy, scoped ReadOnlySpan<int> indexes)
        => Drop(ref copy, InstancePropertyKind.Enchantments, indexes);

    /// <summary>The removal both primitive 2 and primitive 10 are, over the kind each one names.</summary>
    static CraftRefusal? Drop(ref CraftWorkingCopy copy, ushort kind, scoped ReadOnlySpan<int> indexes)
    {
        if (copy.IsRefused)
        {
            return copy.Refusal;
        }

        Span<InstanceAffix> entries = stackalloc InstanceAffix[CraftWorkingCopy.MaxAffixes];
        int count = copy.ReadAffixes(kind, entries);
        Span<bool> selected = stackalloc bool[CraftWorkingCopy.MaxAffixes];
        if (Select(ref copy, indexes, count, selected) is CraftRefusal refusal)
        {
            return refusal;
        }

        int kept = 0;
        for (int index = 0; index < count; index++)
        {
            if (!selected[index])
            {
                entries[kept++] = entries[index];
            }
        }

        return copy.SetAffixes(kind, entries[..kept]) ? null : copy.Refusal;
    }

    /// <summary>
    /// Marks the selected indexes, refusing one the item does not have. An index past the end is the
    /// selector naming an entry that is not there, which is spec 10.3's <c>ByModId</c> refusal by another
    /// route and is the same answer.
    /// </summary>
    static CraftRefusal? Select(
        ref CraftWorkingCopy copy,
        scoped ReadOnlySpan<int> indexes,
        int count,
        scoped Span<bool> selected)
    {
        foreach (int index in indexes)
        {
            if (index < 0 || index >= count)
            {
                return copy.Refuse(new CraftRefusal(CraftRefusalKind.AffixAbsent, index));
            }

            selected[index] = true;
        }

        return null;
    }

    /// <summary>The rule a <c>SetRarity</c> seats, walking <c>upgrade_from</c> when the parameter is 0.</summary>
    static CraftRefusal? Resolve(ref CraftWorkingCopy copy, int rarityId, out ContentRow rule)
    {
        var type = new ContentTypeId(InstanceContentTypeIds.RarityRuleTypeId);
        rule = null!;
        if (rarityId != 0)
        {
            if (!InstanceContentChecks.IsLive(copy.Snapshot, InstanceContentTypeIds.RarityRuleTypeId, rarityId)
                || !copy.Snapshot.TryGetRow(type, rarityId, out ContentRow? named))
            {
                return copy.Refuse(new CraftRefusal(CraftRefusalKind.ContentRowMissing, rarityId));
            }

            rule = named;
            return null;
        }

        if (!copy.TryGetByte(InstancePropertyKind.Rarity, out byte current) || current == 0)
        {
            return copy.Refuse(new CraftRefusal(CraftRefusalKind.FieldAbsent, InstancePropertyKind.Rarity));
        }

        int answers = 0;
        foreach (ContentRow row in InstanceContentChecks.LiveRows(copy.Snapshot, InstanceContentTypeIds.RarityRuleTypeId))
        {
            if (InstanceContentChecks.Number(row, RarityRuleContentType.UpgradeFromIndex) == current)
            {
                answers++;
                rule = row;
            }
        }

        if (answers == 1)
        {
            return null;
        }

        rule = null!;
        return copy.Refuse(new CraftRefusal(CraftRefusalKind.RarityUpgradeAmbiguous, answers));
    }

    /// <summary>
    /// Whether the <c>(mod id, tier ordinal)</c> pair names a live <c>mod_tier</c> row, which is spec 12.2's
    /// check 8 asked BEFORE the bytes are written rather than after a page is loaded. The ordinal is a key
    /// into the mod's own rows rather than a content id, so no reference target can express it.
    /// </summary>
    static bool HasTier(in CraftWorkingCopy copy, int modId, int tierOrdinal)
    {
        foreach (ContentRow row in InstanceContentChecks.LiveRows(copy.Snapshot, InstanceContentTypeIds.ModTierTypeId))
        {
            if (InstanceContentChecks.Number(row, ModTierContentType.ModIdIndex) == modId
                && InstanceContentChecks.Number(row, ModTierContentType.OrdinalIndex) == tierOrdinal)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether one affix's mod row carries a kind the mask names, read off the mod ROW.</summary>
    static bool Masked(in CraftWorkingCopy copy, uint modKindMask, int modId)
    {
        if (!copy.Snapshot.TryGetRow(new ContentTypeId(InstanceContentTypeIds.ModTypeId), modId, out ContentRow? row))
        {
            return false;
        }

        int kind = (int)(InstanceContentChecks.Number(row, ModContentType.KindIndex) ?? 0);
        return modKindMask == 0
            || modKindMask == ItemGenerator.AllModKinds
            || (kind >= 1 && kind <= ItemGenerator.MaxMaskedModKind && (modKindMask & (1u << (kind - 1))) != 0);
    }
}
