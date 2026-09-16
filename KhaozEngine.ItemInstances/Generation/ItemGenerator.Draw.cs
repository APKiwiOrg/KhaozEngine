using System;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// Steps 1 to 8 of spec 9.4, in the numbered order, with no reordering for convenience. Nothing in here is
/// a function of the candidate pool's SIZE: opening the pool is a handful of scalar reads per tag position,
/// and a pick is a walk of the tag positions, a walk of the dead entries behind the draw, and a binary
/// search. The first draft of spec 9.2 walked the merged pool once per pick and measured 4,454 candidate
/// visits and 47.8 microseconds at the median, so a loop over the candidate array here is that design
/// coming back.
/// </summary>
public sealed partial class ItemGenerator
{
    /// <summary>
    /// The kind mask naming EVERY mod kind, which an ordinary roll uses and which a craft parameter of 0
    /// resolves to. It short circuits <see cref="InMask"/>, so a kind above
    /// <see cref="MaxMaskedModKind"/>, which a mask cannot name at all, is still rolled by the generator's
    /// own path and only a CRAFT pays that ceiling.
    /// </summary>
    public const uint AllModKinds = uint.MaxValue;

    /// <summary>
    /// The highest mod kind a mask can name, because the mask is one authored <c>currency_step</c>
    /// parameter and a parameter is 32 bits. Spec 8.2 runs a mod kind to 255, so the two numbers disagree
    /// and the smaller one is a CRAFT AUTHORING ceiling rather than a silent alias onto another kind.
    /// </summary>
    public const int MaxMaskedModKind = 32;

    /// <summary>
    /// Spec 9.4 steps 6 to 8 as ONE pick over an affix list that ALREADY EXISTS, which is what the
    /// <c>AddRandomMod</c> craft primitive is. It lives HERE rather than in the crafting framework because
    /// a second weighted pick is a second distribution, and spec 9.2's whole argument is that there is
    /// exactly one of those.
    /// <para>
    /// The draw COUNT is one weighted draw plus one roll position, always, exactly as a generation pick is:
    /// a pick whose live pool is empty still consumes both and answers false, so a seeded session does not
    /// diverge at the first craft whose pool ran dry.
    /// </para>
    /// <para>
    /// <b>The tier ceiling is applied to the RESULT, not to the pool.</b> Re-weighting the pool for a
    /// ceiling would be a second distribution and filtering after the draw would be the rejection loop
    /// contracts 13.4 forbids, so the drawn mod keeps its mod and its position and its ordinal comes down
    /// to the highest LIVE tier of that same mod at or below the ceiling. A mod with no such tier answers
    /// false, and the consequence worth stating is that a ceiling piles the mass of the excluded tiers onto
    /// the ceiling tier rather than spreading it over the rest of the pool.
    /// </para>
    /// </summary>
    /// <param name="baseId">The item row the target is made from, which carries the tags the draw is keyed by.</param>
    /// <param name="itemLevel">The target's OWN item level from kind 2, which selects the band.</param>
    /// <param name="modKind">The mod kind to pick. The kind is a PARAMETER here, so step 5 draws nothing.</param>
    /// <param name="tierCeiling">The highest tier ordinal to write, or 0 for no ceiling.</param>
    /// <param name="present">The affixes already on the item, excluded before the draw (step 6).</param>
    /// <param name="affix">The pick, when the answer is true.</param>
    /// <exception cref="ArgumentOutOfRangeException">The item level or the base is one the generator refuses
    /// at its own door, in the same class as every other value a caller hands in by code.</exception>
    public bool TryDrawAffix(
        int baseId,
        int itemLevel,
        int modKind,
        int tierCeiling,
        ReadOnlySpan<InstanceAffix> present,
        out InstanceAffix affix)
    {
        affix = default;
        var context = new GenerationContext(baseId, itemLevel, 0, 0, 0);
        _ = RefuseAtTheDoor(in context);
        OpenPool(SignatureOf(baseId), _tables.BandOf(itemLevel));                             // step 1
        SeatPresent(present);                                                                 // step 6

        // A LONG, because this is the UNION over the base's tag positions and the tables type answers a
        // long for the same sum. The build refuses a union past int.MaxValue, so the cast at the draw is
        // safe, and a per-position int accumulator here would have wrapped silently instead.
        long liveWeight = 0;
        bool known = _tables.TryGetKindPosition(modKind, out int kindPosition);
        for (int tag = 0; known && tag < _tagCount; tag++)
        {
            liveWeight += _slotLiveWeight[(kindPosition * _tagPositions) + tag];
        }

        if (!known || liveWeight <= 0)
        {
            _random.Skip();
            _ = _random.NextRollPosition();
            return false;
        }

        int entry = Resolve(kindPosition, BoundedDraw.Next(_random, (int)liveWeight));        // step 7
        ushort position = _random.NextRollPosition();                                         // step 8
        int modId = ModCandidateTables.ModIdOf(entry);
        int ordinal = ModCandidateTables.TierOrdinalOf(entry);
        if (tierCeiling > 0 && ordinal > tierCeiling)
        {
            ordinal = CeilingTier(kindPosition, modId, tierCeiling);
        }

        affix = new InstanceAffix(modId, (byte)ordinal, position);
        return ordinal > 0;
    }

    /// <summary>
    /// Spec 9.4 steps 4 to 9 over an affix list that already exists, restricted to a KIND MASK, which is
    /// what the <c>RerollMods</c> and <c>SetRarity</c> craft primitives are. The kept affixes are excluded
    /// before the draw and COUNT against the rarity rule's per kind caps, so a reroll of the prefixes
    /// cannot walk past a cap the suffixes already filled.
    /// </summary>
    /// <param name="baseId">The item row the target is made from.</param>
    /// <param name="itemLevel">The target's own item level from kind 2.</param>
    /// <param name="rarityId">The target's rarity rule from kind 130, or 0 for an item with no rarity.</param>
    /// <param name="modKindMask">Bit <c>kind - 1</c> per mod kind, or 0 for <see cref="AllModKinds"/>.</param>
    /// <param name="picks">How many picks to make, or -1 to roll the count through step 4 and take off what
    /// <paramref name="keep"/> already holds. Step 4 is ONE draw and it is skipped entirely when a caller
    /// names the count, which is what makes a fill's draw count a function of the step list.</param>
    /// <param name="keep">The affixes the craft is keeping, which are excluded and counted.</param>
    /// <param name="destination">Where the kept and drawn affixes are written, at least 255 long, which is
    /// the most kind 131's byte count can hold. The order is NOT sorted here: the encoder sorts ascending
    /// by mod id whatever order it is handed, which is step 9 and the one place it lives.</param>
    /// <returns>How many affixes were written.</returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than 255.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The item level or the base is one the generator refuses
    /// at its own door.</exception>
    public int RedrawAffixes(
        int baseId,
        int itemLevel,
        int rarityId,
        uint modKindMask,
        int picks,
        ReadOnlySpan<InstanceAffix> keep,
        Span<InstanceAffix> destination)
    {
        if (destination.Length < byte.MaxValue)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"A redraw writes up to {byte.MaxValue} affixes, because kind 131's count is a byte, and the span holds {destination.Length}."),
                nameof(destination));
        }

        var context = new GenerationContext(baseId, itemLevel, 0, 0, 0);
        _ = RefuseAtTheDoor(in context);
        OpenPool(SignatureOf(baseId), _tables.BandOf(itemLevel));                             // step 1
        SeatPresent(keep);                                                                    // step 6

        int rarityIndex = _content.IndexOfRarity(rarityId);
        int requested = picks < 0 ? RollAffixCount(rarityIndex) - keep.Length : picks;        // step 4
        int placed = DrawAffixes(                                                             // steps 5 to 8
            rarityIndex,
            Math.Max(requested, 0),
            modKindMask == 0 ? AllModKinds : modKindMask);

        int total = 0;
        for (int index = 0; index < keep.Length && total < destination.Length; index++)
        {
            destination[total++] = keep[index];
        }

        for (int index = 0; index < placed && total < destination.Length; index++)
        {
            destination[total++] = _affixes[index];
        }

        return total;
    }

    /// <summary>
    /// Step 6 pre-seeded from the affixes the item already carries: every one leaves the pool with its
    /// whole run of tiers and its group, and counts against its kind's cap.
    /// </summary>
    void SeatPresent(ReadOnlySpan<InstanceAffix> present)
    {
        foreach (InstanceAffix affix in present)
        {
            if (_tables.TryGetKindPosition(_tables.KindOf(affix.ModId), out int position))
            {
                _kindPlaced[position]++;
            }

            Exclude(affix.ModId);
            ExcludeGroup(affix.ModId);
        }
    }

    /// <summary>
    /// The highest LIVE tier ordinal of one mod at or below a ceiling, across the base's own tag tables, or
    /// 0 when the mod has none in this band. A mod's tiers are contiguous in a bucket, because the bucket is
    /// sorted by the packed key, so the answer is one binary search per tag position and no walk.
    /// </summary>
    int CeilingTier(int kindPosition, int modId, int ceiling)
    {
        int best = 0;
        int target = ModCandidateTables.Pack(modId, Math.Min(ceiling, ModCandidateTables.MaxTierOrdinal)) + 1;
        for (int tag = 0; tag < _tagCount; tag++)
        {
            int slot = (kindPosition * _tagPositions) + tag;
            if (_slotLength[slot] == 0)
            {
                continue;
            }

            ReadOnlySpan<int> packed = _tables.BucketPacked(_slotBucket[slot]);
            int at = LowerBoundKey(packed, target) - 1;
            if (at >= 0 && ModCandidateTables.ModIdOf(packed[at]) == modId)
            {
                best = Math.Max(best, ModCandidateTables.TierOrdinalOf(packed[at]));
            }
        }

        return best;
    }

    /// <summary>Whether one mod kind is in a mask, which every bit set answers without looking.</summary>
    static bool InMask(uint kindMask, int kind)
        => kindMask == AllModKinds
            || (kind >= 1 && kind <= MaxMaskedModKind && (kindMask & (1u << (kind - 1))) != 0);

    /// <summary>
    /// Step 1: the base's tag tables for this band, one per (kind, tag position), with the precomputed
    /// overlap already deducted. Nothing is merged and nothing is copied, so a roll starts at a fixed cost
    /// in the base's TAG COUNT rather than one in the size of its candidate pool.
    /// </summary>
    void OpenPool(int signature, int band)
    {
        ReadOnlySpan<int> tags = signature < 0 ? ReadOnlySpan<int>.Empty : _tables.Signatures.TagsOf(signature);
        _tagCount = Math.Min(tags.Length, _tagPositions);
        _groupCount = 0;
        Array.Clear(_kindPlaced);

        for (int kind = 0; kind < _kindCount; kind++)
        {
            int live = 0;
            for (int tag = 0; tag < _tagPositions; tag++)
            {
                int slot = (kind * _tagPositions) + tag;
                _runCount[slot] = 0;
                if (tag >= _tagCount)
                {
                    _slotBucket[slot] = -1;
                    _slotHeader[slot] = -1;
                    _slotLength[slot] = 0;
                    _slotLiveWeight[slot] = 0;
                    continue;
                }

                int bucket = _tables.BucketOf(tags[tag], kind, band);
                int header = _tables.HeaderOf(signature, kind, band, tag);
                _slotBucket[slot] = bucket;
                _slotHeader[slot] = header;
                _slotLength[slot] = _tables.BucketLength(bucket);
                _slotLiveWeight[slot] = _tables.BucketTotal(bucket) - _tables.SuppressedWeightAt(header);
                live += _slotLength[slot] - _tables.SuppressedCountAt(header);
            }

            _liveCount[kind] = live;
        }
    }

    /// <summary>
    /// Step 3: the rarity, rolled against the base's tags through spec 8.3's first-tag-wins rule, unless the
    /// caller forced one. A forced rarity consumes NO draw, which is why two items are only comparable on
    /// their draw count when both were rolled the same way.
    /// </summary>
    int ResolveRarity(in GenerationContext context, int signature)
    {
        if (context.ForcedRarityId != 0)
        {
            int forced = _content.IndexOfRarity(context.ForcedRarityId);
            return forced >= 0
                ? forced
                : throw new InvalidOperationException(FormattableString.Invariant(
                    $"This version carries no live rarity_rule row under id {context.ForcedRarityId}, so there is no affix count, no kind cap and no name format to roll with."));
        }

        ReadOnlySpan<int> cumulative = _content.RarityCumulative(signature);
        int total = cumulative.Length == 0 ? 0 : cumulative[^1];
        int draw = BoundedDraw.Next(_random, total);
        return total == 0 ? -1 : LowerBound(cumulative, draw);
    }

    /// <summary>
    /// Step 4: the affix count, one draw over the rule's inclusive range. A rule with no rarity at all still
    /// draws and discards, so an item's draw count does not depend on whether its base had a rarity weight.
    /// </summary>
    int RollAffixCount(int rarityIndex)
    {
        if (rarityIndex < 0)
        {
            _random.Skip();
            return 0;
        }

        int minimum = _content.MinAffixesAt(rarityIndex);
        int maximum = Math.Max(_content.MaxAffixesAt(rarityIndex), minimum);
        if (maximum > byte.MaxValue)
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"Rarity rule {_content.RarityIdAt(rarityIndex)} asks for up to {maximum} affixes and kind 131's count is a BYTE, so at most {byte.MaxValue} fit. A publish refuses a rule past {RarityRuleContentType.MaxAffixCount}, so this rule reached a boot without one."));
        }

        return minimum + BoundedDraw.Next(_random, maximum - minimum + 1);
    }

    /// <summary>
    /// Steps 5 to 8, repeated until the requested count of picks have been MADE. The kind is decided FIRST,
    /// weighted by the LIVE candidate count of each kind still under its per kind cap, so a rarity
    /// permitting three prefixes and three suffixes does not produce six prefixes because prefixes happen
    /// to outnumber suffixes in the pool.
    /// </summary>
    int DrawAffixes(int rarityIndex, int requested) => DrawAffixes(rarityIndex, requested, AllModKinds);

    /// <summary>
    /// The same steps restricted to a KIND MASK, which is what the <c>RerollMods</c> craft primitive needs
    /// and what the ordinary roll gets with every bit set. <see cref="AllModKinds"/> short circuits the mask
    /// test, so a roll through <see cref="DrawAffixes(int, int)"/> makes exactly the calls it always made,
    /// in the order it always made them, whatever kinds the version carries.
    /// </summary>
    int DrawAffixes(int rarityIndex, int requested, uint kindMask)
    {
        int placed = 0;
        for (int pick = 0; pick < requested; pick++)
        {
            int openTotal = 0;
            for (int kind = 0; kind < _kindCount; kind++)
            {
                if (IsOpen(rarityIndex, kind, kindMask))
                {
                    openTotal += _liveCount[kind];
                }
            }

            int kindDraw = BoundedDraw.Next(_random, openTotal);                             // step 5
            int chosen = ChooseKind(rarityIndex, openTotal, kindDraw, kindMask);

            // A LONG for the same reason TryDrawAffix's is: this is the UNION over the base's tag positions,
            // which the build refuses past int.MaxValue and which an int accumulator would have wrapped.
            long liveWeight = 0;
            for (int tag = 0; chosen >= 0 && tag < _tagCount; tag++)
            {
                liveWeight += _slotLiveWeight[(chosen * _tagPositions) + tag];
            }

            if (chosen < 0 || liveWeight <= 0)
            {
                // A pick whose live pool is EMPTY still draws twice and places nothing. Without these two
                // discards one item consumes fewer draws than another of the same rarity on the same base,
                // and a seeded session diverges at the first item whose pool empties. Skip rather than
                // NextInt(0, 1), because a one-wide range consumes NOTHING and a discard that costs the
                // stream nothing is the same as no discard at all.
                _random.Skip();
                _ = _random.NextRollPosition();
                continue;
            }

            int entry = Resolve(chosen, BoundedDraw.Next(_random, (int)liveWeight));         // step 7
            ushort position = _random.NextRollPosition();                                    // step 8
            int modId = ModCandidateTables.ModIdOf(entry);
            if (placed < _affixes.Length)
            {
                _affixes[placed++] = new InstanceAffix(
                    modId,
                    (byte)ModCandidateTables.TierOrdinalOf(entry),
                    position);
            }

            _kindPlaced[chosen]++;
            Exclude(modId);                                                                  // step 6
            ExcludeGroup(modId);
        }

        return placed;
    }

    /// <summary>Whether one kind is still under its cap, still has a live candidate and is in the mask.</summary>
    bool IsOpen(int rarityIndex, int kindPosition, uint kindMask)
        => _liveCount[kindPosition] > 0
            && rarityIndex >= 0
            && _kindPlaced[kindPosition] < _content.KindCapAt(rarityIndex, kindPosition)
            && InMask(kindMask, _tables.KindAt(kindPosition));

    /// <summary>The kind the count-weighted draw landed on, or -1 when nothing is open.</summary>
    int ChooseKind(int rarityIndex, int openTotal, int draw, uint kindMask)
    {
        if (openTotal <= 0)
        {
            return -1;
        }

        int running = 0;
        for (int kind = 0; kind < _kindCount; kind++)
        {
            if (!IsOpen(rarityIndex, kind, kindMask))
            {
                continue;
            }

            running += _liveCount[kind];
            if (draw < running)
            {
                return kind;
            }
        }

        return -1;
    }

    /// <summary>
    /// Step 7's draw over a pool with the overlap and the exclusions SUBTRACTED rather than filtered. The
    /// draw picks a tag position by its live weight, then walks that table's dead entries in index order to
    /// shift the draw back into the table's own cumulative array before the binary search. <b>The landing
    /// entry is live by construction</b>: consecutive dead entries share one shifted position, so a dead
    /// entry is either wholly behind the draw or wholly ahead of it and can never be the answer.
    /// </summary>
    int Resolve(int kindPosition, int draw)
    {
        int slot = kindPosition * _tagPositions;
        while (draw >= _slotLiveWeight[slot])
        {
            draw -= _slotLiveWeight[slot];
            slot++;
        }

        ReadOnlySpan<int> cumulative = _tables.BucketCumulative(_slotBucket[slot]);
        ReadOnlySpan<ushort> suppressed = _tables.SuppressedIndexesAt(_slotHeader[slot]);
        int suppressCursor = 0;
        int runCursor = slot * _maxRuns;
        int runEnd = runCursor + _runCount[slot];
        int shift = 0;
        while (true)
        {
            int suppressIndex = suppressCursor < suppressed.Length ? suppressed[suppressCursor] : int.MaxValue;
            int runIndex = runCursor < runEnd ? _runFirst[runCursor] : int.MaxValue;
            if (suppressIndex == int.MaxValue && runIndex == int.MaxValue)
            {
                break;
            }

            if (suppressIndex < runIndex)
            {
                int before = suppressIndex == 0 ? 0 : cumulative[suppressIndex - 1];
                if (before - shift > draw)
                {
                    break;
                }

                shift += cumulative[suppressIndex] - before;
                suppressCursor++;
                continue;
            }

            int runBefore = runIndex == 0 ? 0 : cumulative[runIndex - 1];
            if (runBefore - shift > draw)
            {
                break;
            }

            // The run's weight is its FULL weight, and the suppressed entries inside it are stepped over
            // rather than added again, which is the other half of "never deducted twice".
            shift += _runWeight[runCursor];
            int last = _runLast[runCursor];
            while (suppressCursor < suppressed.Length && suppressed[suppressCursor] < last)
            {
                suppressCursor++;
            }

            runCursor++;
        }

        return _tables.BucketPacked(_slotBucket[slot])[LowerBound(cumulative, draw + shift)];
    }

    /// <summary>
    /// Step 6: removes every tier of one mod from every tag table of its kind BEFORE the next draw, which is
    /// spec 9.3's filter-before-draw rule. A mod's tiers are CONTIGUOUS in a table, because the table is
    /// sorted by the packed key, so one run covers them all and finding it is a binary search. An entry the
    /// overlap already suppressed is not deducted twice, and a legacy mod needs no rule here at all because
    /// it was never in a table.
    /// </summary>
    void Exclude(int modId)
    {
        if (!_tables.TryGetKindPosition(_tables.KindOf(modId), out int kindPosition))
        {
            return;
        }

        int target = ModCandidateTables.Pack(modId, 0);
        for (int tag = 0; tag < _tagCount; tag++)
        {
            int slot = (kindPosition * _tagPositions) + tag;
            int length = _slotLength[slot];
            if (length == 0)
            {
                continue;
            }

            ReadOnlySpan<int> packed = _tables.BucketPacked(_slotBucket[slot]);
            int first = LowerBoundKey(packed, target);
            if (first >= length || ModCandidateTables.ModIdOf(packed[first]) != modId)
            {
                continue;
            }

            int last = first + 1;
            while (last < length && ModCandidateTables.ModIdOf(packed[last]) == modId)
            {
                last++;
            }

            SeatRun(slot, kindPosition, first, last);
        }
    }

    /// <summary>
    /// Records one excluded run in its table's sorted run list and subtracts what it takes out of the live
    /// weight and the live count. A run already seated is left alone, which is what makes a mod excluded
    /// twice, once on its own and once through its group, cost nothing the second time.
    /// <para>
    /// <b>A full list THROWS rather than dropping the run.</b> A run that is not seated is a mod that stays
    /// LIVE, so the next pick can draw a mod the rule already excluded and an item can leave with two mods
    /// of a group capped at one, silently and forever. The list is sized from the true worst case by
    /// <see cref="GenerationTables"/>, so this is unreachable on a caller that stays inside the rarity
    /// rule's own affix count, and it is the loud answer for one that does not.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The slot's run list is full.</exception>
    void SeatRun(int slot, int kindPosition, int first, int last)
    {
        int runCursor = slot * _maxRuns;
        int runs = _runCount[slot];
        int position = runs;
        for (int run = 0; run < runs; run++)
        {
            if (_runFirst[runCursor + run] == first)
            {
                return;
            }

            if (_runFirst[runCursor + run] > first)
            {
                position = run;
                break;
            }
        }

        if (runs == _maxRuns)
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"This roll asked to exclude a {runs + 1}st run from one tag table, over the ceiling of {_maxRuns} the tables sized from the widest rarity rule's affix count and the widest mod group. Dropping the run instead would leave the mod LIVE, so an item could carry two mods of a group capped at one."));
        }

        ReadOnlySpan<int> cumulative = _tables.BucketCumulative(_slotBucket[slot]);
        ReadOnlySpan<ushort> suppressed = _tables.SuppressedIndexesAt(_slotHeader[slot]);
        int weight = cumulative[last - 1] - (first == 0 ? 0 : cumulative[first - 1]);
        int suppressedWeight = 0;
        int suppressedEntries = 0;
        for (int index = 0; index < suppressed.Length; index++)
        {
            int entry = suppressed[index];
            if (entry < first)
            {
                continue;
            }

            if (entry >= last)
            {
                break;
            }

            suppressedWeight += cumulative[entry] - (entry == 0 ? 0 : cumulative[entry - 1]);
            suppressedEntries++;
        }

        for (int run = runs; run > position; run--)
        {
            _runFirst[runCursor + run] = _runFirst[runCursor + run - 1];
            _runLast[runCursor + run] = _runLast[runCursor + run - 1];
            _runWeight[runCursor + run] = _runWeight[runCursor + run - 1];
        }

        _runFirst[runCursor + position] = first;
        _runLast[runCursor + position] = last;
        _runWeight[runCursor + position] = weight;
        _runCount[slot] = runs + 1;
        _slotLiveWeight[slot] -= weight - suppressedWeight;
        _liveCount[kindPosition] -= last - first - suppressedEntries;
    }

    /// <summary>
    /// The other half of step 6: once a <c>mod_group</c> is at <c>max_per_item</c>, every OTHER mod of that
    /// group leaves the pool the same way. The count is per generation and is a short scan rather than a
    /// dense array, because the group ids a version carries are sparse and at most one group per placement
    /// can be touched.
    /// </summary>
    void ExcludeGroup(int modId)
    {
        int group = _tables.GroupOf(modId);
        if (group == 0)
        {
            return;
        }

        int maximum = _tables.MaxPerItem(group);
        if (maximum <= 0 || Bump(group) < maximum)
        {
            return;
        }

        foreach (int member in _tables.GroupMembers(group))
        {
            Exclude(member);
        }
    }

    /// <summary>How many of one group this generation has placed, counting this placement.</summary>
    int Bump(int group)
    {
        for (int index = 0; index < _groupCount; index++)
        {
            if (_groupIds[index] == group)
            {
                return ++_groupPlaced[index];
            }
        }

        if (_groupCount == _groupIds.Length)
        {
            return int.MaxValue;
        }

        _groupIds[_groupCount] = group;
        _groupPlaced[_groupCount] = 1;
        _groupCount++;
        return 1;
    }

    /// <summary>The first index whose running total exceeds the draw, which is the weighted pick of 9.3.</summary>
    static int LowerBound(ReadOnlySpan<int> cumulative, int draw)
    {
        int low = 0;
        int high = cumulative.Length - 1;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (cumulative[middle] <= draw)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>The first index whose packed key is at or past the target, which finds a mod's run of tiers.</summary>
    static int LowerBoundKey(ReadOnlySpan<int> packed, int target)
    {
        int low = 0;
        int high = packed.Length;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (packed[middle] < target)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}
