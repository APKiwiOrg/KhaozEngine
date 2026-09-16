using System;
using KhaozEngine.Catalog;
using KhaozEngine.Primitives;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The six selector kinds of spec 10.3, CLOSED, and the smallest of the three vocabularies. Primitives 2,
/// 3 and 10 choose the entries they act on through one of these, and through nothing else.
/// <para>
/// <b>The numbers ARE the authored value</b>, written into a <c>currency_step</c> parameter pair as the
/// kind then its parameter, so they are durable the moment one currency row names one.
/// </para>
/// </summary>
public enum CraftSelectorKind : byte
{
    /// <summary>The entry naming that mod, or a refusal when the item does not carry it.</summary>
    ByModId = 1,

    /// <summary>The entry at that index of the SORTED list, which is the order the payload stores.</summary>
    ByIndex = 2,

    /// <summary>One uniform draw over the entries of that mod kind, and the ONLY selector that draws.</summary>
    RandomOfKind = 3,

    /// <summary>Every entry of that mod kind, in sorted order.</summary>
    AllOfKind = 4,

    /// <summary>The entry with the LOWEST tier ordinal, ties broken by lower mod id.</summary>
    LowestTier = 5,

    /// <summary>The entry with the HIGHEST tier ordinal, ties broken by lower mod id.</summary>
    HighestTier = 6,
}

/// <summary>
/// One selector: a kind out of the closed six plus one integer, whose meaning is the kind's. It resolves to
/// INDEXES into the sorted entry list, which is what primitives 2, 3 and 10 take.
/// <para>
/// <b><see cref="CraftSelectorKind.RandomOfKind"/> is the only kind that draws, and it draws exactly
/// ONCE.</b> That is what makes a currency's draw count a function of its STEP LIST rather than of the item
/// it hits, which is the same reproducibility property spec 9.3 gives the generator one level up. The draw
/// goes through <see cref="BoundedDraw"/>, so a selection with ONE candidate costs the stream a draw
/// exactly as a selection with nine does, and so does a selection with none.
/// </para>
/// <para>
/// <b>Resolving is the one place the selector vocabulary lives.</b> A primitive takes indexes and never a
/// selector, so no primitive has to remember the draw rule and no two of them can drift apart on it.
/// </para>
/// </summary>
/// <param name="Kind">Which entries to choose.</param>
/// <param name="Parameter">A mod id, an index, or a mod kind, whose meaning is the <paramref name="Kind"/>'s.
/// A mod kind of 0 means every kind.</param>
public readonly record struct CraftSelector(CraftSelectorKind Kind, int Parameter)
{
    /// <summary>
    /// The entries this selector chooses, as indexes into the SORTED list of that kind's entries.
    /// </summary>
    /// <param name="copy">The craft in progress, which is where a refusal is recorded.</param>
    /// <param name="entryKind">131 for affixes or 133 for enchantments.</param>
    /// <param name="random">The gameplay randomness seam the executor is running on, touched by
    /// <see cref="CraftSelectorKind.RandomOfKind"/> and by no other kind.</param>
    /// <param name="indexes">Where to write, at least <see cref="CraftWorkingCopy.MaxAffixes"/> long.</param>
    /// <param name="count">How many indexes were written.</param>
    /// <returns>The refusal, or null when the selection resolved.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="random"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="indexes"/> is too short, which is a caller error
    /// rather than a fact about the item.</exception>
    public CraftRefusal? Resolve(
        ref CraftWorkingCopy copy,
        ushort entryKind,
        IRandomSource random,
        scoped Span<int> indexes,
        out int count)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (indexes.Length < CraftWorkingCopy.MaxAffixes)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"A selection resolves up to {CraftWorkingCopy.MaxAffixes} indexes and the span holds {indexes.Length}."),
                nameof(indexes));
        }

        count = 0;
        if (copy.IsRefused)
        {
            return copy.Refusal;
        }

        Span<InstanceAffix> entries = stackalloc InstanceAffix[CraftWorkingCopy.MaxAffixes];
        int held = copy.ReadAffixes(entryKind, entries);
        switch (Kind)
        {
            case CraftSelectorKind.ByModId:
                for (int index = 0; index < held; index++)
                {
                    if (entries[index].ModId == Parameter)
                    {
                        indexes[count++] = index;
                        return null;
                    }
                }

                return copy.Refuse(new CraftRefusal(CraftRefusalKind.AffixAbsent, Parameter));

            case CraftSelectorKind.ByIndex:
                if (Parameter < 0 || Parameter >= held)
                {
                    return copy.Refuse(new CraftRefusal(CraftRefusalKind.AffixAbsent, Parameter));
                }

                indexes[count++] = Parameter;
                return null;

            case CraftSelectorKind.RandomOfKind:
                return Draw(ref copy, entries[..held], random, indexes, out count);

            case CraftSelectorKind.AllOfKind:
                for (int index = 0; index < held; index++)
                {
                    if (IsKind(in copy, entries[index].ModId))
                    {
                        indexes[count++] = index;
                    }
                }

                // A kind the item carries none of selects NOTHING rather than refusing: the currency asked
                // for every entry of a kind and the answer is that there are none.
                return null;

            case CraftSelectorKind.LowestTier:
            case CraftSelectorKind.HighestTier:
                return Extreme(ref copy, entries[..held], indexes, out count);

            default:
                return copy.Refuse(new CraftRefusal(CraftRefusalKind.AffixAbsent, Parameter));
        }
    }

    /// <summary>
    /// The ONE draw the selector vocabulary makes, through <see cref="BoundedDraw"/>, so a live pool of one
    /// and a live pool of none both cost the stream exactly what a live pool of nine costs. An empty pool
    /// still draws and then answers <see cref="CraftRefusalKind.DrawEmpty"/>, which is the same answer the
    /// generator's own pick gives for the same reason.
    /// </summary>
    CraftRefusal? Draw(
        ref CraftWorkingCopy copy,
        scoped ReadOnlySpan<InstanceAffix> entries,
        IRandomSource random,
        scoped Span<int> indexes,
        out int count)
    {
        count = 0;
        Span<int> candidates = stackalloc int[CraftWorkingCopy.MaxAffixes];
        int pool = 0;
        for (int index = 0; index < entries.Length; index++)
        {
            if (IsKind(in copy, entries[index].ModId))
            {
                candidates[pool++] = index;
            }
        }

        int drawn = BoundedDraw.Next(random, pool);
        if (pool == 0)
        {
            return copy.Refuse(new CraftRefusal(CraftRefusalKind.DrawEmpty, Parameter));
        }

        indexes[count++] = candidates[drawn];
        return null;
    }

    /// <summary>
    /// The entry at one end of the tier range, TIES BROKEN BY LOWER MOD ID. The list arrives ascending by
    /// mod id, so taking only a STRICT improvement as the scan walks is the tie break, with no second
    /// comparison and no sort.
    /// </summary>
    CraftRefusal? Extreme(
        ref CraftWorkingCopy copy,
        scoped ReadOnlySpan<InstanceAffix> entries,
        scoped Span<int> indexes,
        out int count)
    {
        count = 0;
        bool highest = Kind == CraftSelectorKind.HighestTier;
        int best = -1;
        for (int index = 0; index < entries.Length; index++)
        {
            if (!IsKind(in copy, entries[index].ModId))
            {
                continue;
            }

            if (best < 0
                || (highest && entries[index].Tier > entries[best].Tier)
                || (!highest && entries[index].Tier < entries[best].Tier))
            {
                best = index;
            }
        }

        if (best < 0)
        {
            return copy.Refuse(new CraftRefusal(CraftRefusalKind.AffixAbsent, Parameter));
        }

        indexes[count++] = best;
        return null;
    }

    /// <summary>
    /// Whether one entry's mod row carries the kind this selector names, with a parameter of 0 matching
    /// every kind. The kind is read off the mod ROW rather than the entry, because the payload stores the
    /// mod id and the content owns what kind it is.
    /// </summary>
    bool IsKind(in CraftWorkingCopy copy, int modId)
    {
        if (Parameter == 0)
        {
            return true;
        }

        return copy.Snapshot.TryGetRow(new ContentTypeId(InstanceContentTypeIds.ModTypeId), modId, out ContentRow? row)
            && InstanceContentChecks.Number(row, ModContentType.KindIndex) == Parameter;
    }
}
