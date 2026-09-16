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
        int draw = _random.NextInt(0, Math.Max(total, DiscardBound));
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
            _ = _random.NextInt(0, DiscardBound);
            return 0;
        }

        int minimum = _content.MinAffixesAt(rarityIndex);
        int maximum = Math.Max(_content.MaxAffixesAt(rarityIndex), minimum);
        if (maximum > byte.MaxValue)
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"Rarity rule {_content.RarityIdAt(rarityIndex)} asks for up to {maximum} affixes and kind 131's count is a BYTE, so at most {byte.MaxValue} fit. A publish refuses a rule past {RarityRuleContentType.MaxAffixCount}, so this rule reached a boot without one."));
        }

        return _random.NextInt(minimum, maximum + 1);
    }

    /// <summary>
    /// Steps 5 to 8, repeated until the requested count of picks have been MADE. The kind is decided FIRST,
    /// weighted by the LIVE candidate count of each kind still under its per kind cap, so a rarity
    /// permitting three prefixes and three suffixes does not produce six prefixes because prefixes happen
    /// to outnumber suffixes in the pool.
    /// </summary>
    int DrawAffixes(int rarityIndex, int requested)
    {
        int placed = 0;
        for (int pick = 0; pick < requested; pick++)
        {
            int openTotal = 0;
            for (int kind = 0; kind < _kindCount; kind++)
            {
                if (IsOpen(rarityIndex, kind))
                {
                    openTotal += _liveCount[kind];
                }
            }

            int kindDraw = _random.NextInt(0, Math.Max(openTotal, DiscardBound));            // step 5
            int chosen = ChooseKind(rarityIndex, openTotal, kindDraw);
            int liveWeight = 0;
            for (int tag = 0; chosen >= 0 && tag < _tagCount; tag++)
            {
                liveWeight += _slotLiveWeight[(chosen * _tagPositions) + tag];
            }

            if (chosen < 0 || liveWeight <= 0)
            {
                // A pick whose live pool is EMPTY still draws twice and places nothing. Without these two
                // discards one item consumes fewer draws than another of the same rarity on the same base,
                // and a seeded session diverges at the first item whose pool empties. NextInt(0, 1) rather
                // than nothing, because step 7's real draw is NextInt(0, liveWeight) and a weight total of
                // zero is not a legal argument.
                _ = _random.NextInt(0, DiscardBound);
                _ = _random.NextRollPosition();
                continue;
            }

            int entry = Resolve(chosen, _random.NextInt(0, liveWeight));                     // step 7
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

    /// <summary>Whether one kind is still under its cap and still has a live candidate.</summary>
    bool IsOpen(int rarityIndex, int kindPosition)
        => _liveCount[kindPosition] > 0
            && rarityIndex >= 0
            && _kindPlaced[kindPosition] < _content.KindCapAt(rarityIndex, kindPosition);

    /// <summary>The kind the count-weighted draw landed on, or -1 when nothing is open.</summary>
    int ChooseKind(int rarityIndex, int openTotal, int draw)
    {
        if (openTotal <= 0)
        {
            return -1;
        }

        int running = 0;
        for (int kind = 0; kind < _kindCount; kind++)
        {
            if (!IsOpen(rarityIndex, kind))
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
    /// </summary>
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
            return;
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
