using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// Everything OUTSIDE the mod candidate tables that a roll reads, flattened once at construction so a roll
/// walks no rows at all: the rarity rules and their per kind caps, the rarity weights folded per tag
/// SIGNATURE, the rare name words folded the same way, each base's durability and authored socket list, and
/// each unique template's lines and sockets.
/// <para>
/// <b>It is keyed by SIGNATURE rather than by base, exactly as <see cref="ModCandidateTables"/> is.</b> Tag
/// lists are authored, so a catalog of fifty thousand bases carries a few hundred distinct lists between
/// them, and the rarity weights and the name words both resolve through spec 8.3's first-tag-wins rule over
/// that list. Folding them per signature turns two more per roll merges into a binary search each, at a cost
/// proportional to the signature count rather than to the base count.
/// </para>
/// <para>
/// <b>Two engine schema positions are read by index here and no engine type declares an index constant the
/// way the eighteen Scope B types do.</b> They are named constants rather than literals, and
/// <c>ItemGeneratorTests.The_engine_item_field_indexes_the_generator_reads_are_still_where_it_reads_them</c>
/// pins each one against the schema that defines it, so a reorder fails a fact rather than silently
/// pointing a reader at a neighbouring value of the same kind.
/// </para>
/// </summary>
sealed class GenerationContentTables
{
    /// <summary>Where <c>item.durability_max</c> sits in the engine item schema.</summary>
    internal const int ItemDurabilityMaxIndex = 13;

    /// <summary>Where <c>base_socket.item</c> sits in the engine base socket schema.</summary>
    internal const int BaseSocketItemIndex = 0;

    /// <summary>Where <c>base_socket.sort</c> sits, which is the socket ORDER kind 132 seats.</summary>
    internal const int BaseSocketSortIndex = 1;

    /// <summary>Where <c>base_socket.socket_type</c> sits.</summary>
    internal const int BaseSocketSocketTypeIndex = 2;

    readonly int[] _rarityIds;
    readonly int[] _rarityMinAffixes;
    readonly int[] _rarityMaxAffixes;
    readonly int[] _rarityNamePositions;
    readonly int[] _rarityKindCap;
    readonly int[] _rarityCumulative;

    readonly int[] _nameStart;
    readonly int[] _nameCount;
    readonly int[] _nameWordIds;
    readonly int[] _nameCumulative;

    readonly int[] _baseIds;
    readonly int[] _baseDurability;
    readonly int[] _baseSocketStart;
    readonly int[] _baseSocketCount;
    readonly int[] _baseSocketTypes;

    readonly int[] _uniqueIds;
    readonly int[] _uniqueBaseIds;
    readonly int[] _uniqueLineStart;
    readonly int[] _uniqueLineCount;
    readonly int[] _uniqueLineMods;
    readonly int[] _uniqueLineTiers;
    readonly int[] _uniqueSocketStart;
    readonly int[] _uniqueSocketCount;
    readonly int[] _uniqueSocketTypes;

    GenerationContentTables(IContentSnapshot snapshot, ModCandidateTables tables)
    {
        KindCount = tables.KindCount;
        SignatureCount = tables.Signatures.Count;

        List<ContentRow> rules = InstanceContentChecks.LiveRows(snapshot, InstanceContentTypeIds.RarityRuleTypeId);
        _rarityIds = new int[rules.Count];
        _rarityMinAffixes = new int[rules.Count];
        _rarityMaxAffixes = new int[rules.Count];
        _rarityNamePositions = new int[rules.Count];
        _rarityKindCap = new int[rules.Count * Math.Max(KindCount, 1)];
        int namePositions = 0;
        int maxAffixes = 0;
        for (int index = 0; index < rules.Count; index++)
        {
            ContentRow row = rules[index];
            _rarityIds[index] = row.Id;
            _rarityMinAffixes[index] = Clamp(InstanceContentChecks.Number(row, RarityRuleContentType.MinAffixesIndex) ?? 0);
            _rarityMaxAffixes[index] = Clamp(InstanceContentChecks.Number(row, RarityRuleContentType.MaxAffixesIndex) ?? 0);
            _rarityNamePositions[index] = Clamp(InstanceContentChecks.Number(row, RarityRuleContentType.NameWordPositionsIndex) ?? 0);
            namePositions = Math.Max(namePositions, _rarityNamePositions[index]);
            maxAffixes = Math.Max(maxAffixes, _rarityMaxAffixes[index]);
            FillKindCaps(tables, row, index);
        }

        MaxAffixCount = maxAffixes;
        NamePositionCount = namePositions;
        ApplyKindLimits(snapshot, tables);

        _rarityCumulative = new int[SignatureCount * rules.Count];
        FoldWeights(
            snapshot,
            tables,
            InstanceContentTypeIds.RarityWeightTypeId,
            RarityWeightContentType.RarityRuleIdIndex,
            RarityWeightContentType.TagIdIndex,
            RarityWeightContentType.WeightIndex,
            _rarityIds,
            _rarityCumulative);

        (_nameStart, _nameCount, _nameWordIds, _nameCumulative) = BuildNameTables(snapshot, tables);
        (_baseIds, _baseDurability, _baseSocketStart, _baseSocketCount, _baseSocketTypes) = BuildBases(snapshot);
        (_uniqueIds, _uniqueBaseIds, _uniqueLineStart, _uniqueLineCount, _uniqueLineMods, _uniqueLineTiers,
            _uniqueSocketStart, _uniqueSocketCount, _uniqueSocketTypes) = BuildUniques(snapshot);

        int widestSockets = 0;
        for (int index = 0; index < _baseSocketCount.Length; index++)
        {
            widestSockets = Math.Max(widestSockets, _baseSocketCount[index]);
        }

        for (int index = 0; index < _uniqueSocketCount.Length; index++)
        {
            widestSockets = Math.Max(widestSockets, _uniqueSocketCount[index]);
        }

        int widestLines = 0;
        for (int index = 0; index < _uniqueLineCount.Length; index++)
        {
            widestLines = Math.Max(widestLines, _uniqueLineCount[index]);
        }

        MaxSocketCount = widestSockets;
        MaxUniqueLineCount = widestLines;
    }

    /// <summary>How many mod kinds the version carries, which is the width of the per kind cap table.</summary>
    public int KindCount { get; }

    /// <summary>How many distinct authored tag lists the version's bases carry.</summary>
    public int SignatureCount { get; }

    /// <summary>How many live <c>rarity_rule</c> rows the version carries.</summary>
    public int RarityCount => _rarityIds.Length;

    /// <summary>The widest <c>max_affixes</c> any rarity rule asks for, which sizes the affix scratch.</summary>
    public int MaxAffixCount { get; }

    /// <summary>The widest <c>name_word_positions</c>, which sizes the name scratch.</summary>
    public int NamePositionCount { get; }

    /// <summary>The widest authored socket list, base or unique, which sizes the socket scratch.</summary>
    public int MaxSocketCount { get; }

    /// <summary>The widest unique template line list, which bounds a forced unique's affix list.</summary>
    public int MaxUniqueLineCount { get; }

    /// <summary>Folds one version's rows into the flat tables a roll reads.</summary>
    /// <param name="snapshot">The loaded version.</param>
    /// <param name="tables">The candidate tables, read for their kinds and their tag signatures.</param>
    public static GenerationContentTables Build(IContentSnapshot snapshot, ModCandidateTables tables)
        => new(snapshot, tables);

    /// <summary>The rarity rule row id at one index.</summary>
    public int RarityIdAt(int index) => _rarityIds[index];

    /// <summary>The index one rarity rule id sits at, or -1 for a rule this version has no live row for.</summary>
    public int IndexOfRarity(int rarityId) => GenerationTagSignature.IndexOf(_rarityIds, rarityId);

    /// <summary>The inclusive affix count floor of one rarity rule.</summary>
    public int MinAffixesAt(int index) => _rarityMinAffixes[index];

    /// <summary>The inclusive affix count ceiling of one rarity rule.</summary>
    public int MaxAffixesAt(int index) => _rarityMaxAffixes[index];

    /// <summary>How many rare name positions one rarity rule composes.</summary>
    public int NamePositionsAt(int index) => _rarityNamePositions[index];

    /// <summary>How many mods of one KIND POSITION a rarity rule permits on one item.</summary>
    public int KindCapAt(int rarityIndex, int kindPosition) => _rarityKindCap[(rarityIndex * KindCount) + kindPosition];

    /// <summary>One signature's running rarity weight totals, so a rarity roll is one binary search.</summary>
    public ReadOnlySpan<int> RarityCumulative(int signature)
        => _rarityIds.Length == 0 || (uint)signature >= (uint)SignatureCount
            ? ReadOnlySpan<int>.Empty
            : _rarityCumulative.AsSpan(signature * _rarityIds.Length, _rarityIds.Length);

    /// <summary>One signature's live words for one name POSITION, ascending by word id.</summary>
    public ReadOnlySpan<int> NameWords(int signature, int position) => Slice(_nameWordIds, NameSlot(signature, position));

    /// <summary>Those words' running weight totals.</summary>
    public ReadOnlySpan<int> NameCumulative(int signature, int position) => Slice(_nameCumulative, NameSlot(signature, position));

    /// <summary>The index one base sits at, or -1 for a base this version has no live row for.</summary>
    public int IndexOfBase(int baseId) => GenerationTagSignature.IndexOf(_baseIds, baseId);

    /// <summary>One base's <c>durability_max</c>, or 0 when it declares none.</summary>
    public int DurabilityAt(int index) => _baseDurability[index];

    /// <summary>One base's authored socket types, in <c>sort</c> order, which kind 132 seats verbatim.</summary>
    public ReadOnlySpan<int> BaseSocketsAt(int index)
        => _baseSocketTypes.AsSpan(_baseSocketStart[index], _baseSocketCount[index]);

    /// <summary>The index one unique template sits at, or -1 for one this version has no live row for.</summary>
    public int IndexOfUnique(int templateId) => GenerationTagSignature.IndexOf(_uniqueIds, templateId);

    /// <summary>The base one unique template is seated on.</summary>
    public int UniqueBaseAt(int index) => _uniqueBaseIds[index];

    /// <summary>One unique template's line mod ids, in <c>sort</c> order.</summary>
    public ReadOnlySpan<int> UniqueLineModsAt(int index)
        => _uniqueLineMods.AsSpan(_uniqueLineStart[index], _uniqueLineCount[index]);

    /// <summary>Those lines' tier ordinals, in the same order.</summary>
    public ReadOnlySpan<int> UniqueLineTiersAt(int index)
        => _uniqueLineTiers.AsSpan(_uniqueLineStart[index], _uniqueLineCount[index]);

    /// <summary>One unique template's socket types, in <c>sort</c> order.</summary>
    public ReadOnlySpan<int> UniqueSocketsAt(int index)
        => _uniqueSocketTypes.AsSpan(_uniqueSocketStart[index], _uniqueSocketCount[index]);

    static int Clamp(long value) => value <= 0 ? 0 : value >= int.MaxValue ? int.MaxValue : (int)value;

    static ReadOnlySpan<int> Slice(int[] values, (int Start, int Count) slot)
        => slot.Count == 0 ? ReadOnlySpan<int>.Empty : values.AsSpan(slot.Start, slot.Count);

    (int Start, int Count) NameSlot(int signature, int position)
    {
        if (NamePositionCount == 0 || (uint)signature >= (uint)SignatureCount
            || position < 1 || position > NamePositionCount)
        {
            return (0, 0);
        }

        int slot = (signature * NamePositionCount) + position - 1;
        return (_nameStart[slot], _nameCount[slot]);
    }

    /// <summary>
    /// The prefix and suffix caps a rarity rule carries itself. Spec 8.5 gives the two engine kinds their
    /// own fields and leaves every other kind to <c>rarity_kind_limit</c>, so a kind with neither is capped
    /// at ZERO rather than at the affix count: an unauthored kind is one the rarity does not grant.
    /// </summary>
    void FillKindCaps(ModCandidateTables tables, ContentRow row, int rarityIndex)
    {
        int prefixes = Clamp(InstanceContentChecks.Number(row, RarityRuleContentType.MaxPrefixesIndex) ?? 0);
        int suffixes = Clamp(InstanceContentChecks.Number(row, RarityRuleContentType.MaxSuffixesIndex) ?? 0);
        for (int position = 0; position < KindCount; position++)
        {
            int kind = tables.KindAt(position);
            _rarityKindCap[(rarityIndex * KindCount) + position] = kind switch
            {
                ModContentType.PrefixKind => prefixes,
                ModContentType.SuffixKind => suffixes,
                _ => 0,
            };
        }
    }

    /// <summary>Lays <c>rarity_kind_limit</c> over the two engine kinds' own fields, for kinds 3 and up.</summary>
    void ApplyKindLimits(IContentSnapshot snapshot, ModCandidateTables tables)
    {
        foreach (ContentRow row in InstanceContentChecks.LiveRows(snapshot, InstanceContentTypeIds.RarityKindLimitTypeId))
        {
            int rarityIndex = IndexOfRarity(Clamp(InstanceContentChecks.Number(row, RarityKindLimitContentType.RarityRuleIdIndex) ?? 0));
            int kind = Clamp(InstanceContentChecks.Number(row, RarityKindLimitContentType.ModKindIndex) ?? 0);
            if (rarityIndex < 0 || !tables.TryGetKindPosition(kind, out int position))
            {
                continue;
            }

            _rarityKindCap[(rarityIndex * KindCount) + position] =
                Clamp(InstanceContentChecks.Number(row, RarityKindLimitContentType.MaxCountIndex) ?? 0);
        }
    }

    /// <summary>
    /// One weight child type folded into a per signature cumulative array through spec 8.3's first-tag-wins
    /// rule: the FIRST tag of the base's authored list that carries a row for a parent decides that parent's
    /// weight, and later tags contribute nothing.
    /// </summary>
    static void FoldWeights(
        IContentSnapshot snapshot,
        ModCandidateTables tables,
        ushort weightTypeId,
        int parentIndex,
        int tagIndex,
        int weightIndex,
        int[] parentIds,
        int[] cumulative)
    {
        if (parentIds.Length == 0 || tables.Signatures.Count == 0)
        {
            return;
        }

        var byKey = new Dictionary<long, int>();
        foreach (ContentRow row in InstanceContentChecks.LiveRows(snapshot, weightTypeId))
        {
            int parent = GenerationTagSignature.IndexOf(parentIds, Clamp(InstanceContentChecks.Number(row, parentIndex) ?? 0));
            int tag = Clamp(InstanceContentChecks.Number(row, tagIndex) ?? 0);
            int weight = Clamp(InstanceContentChecks.Number(row, weightIndex) ?? 0);
            if (parent < 0 || tag <= 0 || weight <= 0)
            {
                continue;
            }

            // A duplicate (parent, tag) pair keeps the FIRST row, which is the lowest id, so the fold does
            // not depend on the order rows arrived in.
            _ = byKey.TryAdd(((long)parent << 32) | (uint)tag, weight);
        }

        for (int signature = 0; signature < tables.Signatures.Count; signature++)
        {
            ReadOnlySpan<int> authored = tables.Signatures.TagsOf(signature);
            int running = 0;
            for (int parent = 0; parent < parentIds.Length; parent++)
            {
                int weight = 0;
                for (int position = 0; position < authored.Length && weight == 0; position++)
                {
                    _ = byKey.TryGetValue(((long)parent << 32) | (uint)authored[position], out weight);
                }

                running += weight;
                cumulative[(signature * parentIds.Length) + parent] = running;
            }
        }
    }

    /// <summary>
    /// The rare name words of each (signature, position), which is the same fold as the rarity weights with
    /// a position axis in front of it. Spec 9.4 step 10 does not name a table for this, so it is the
    /// generator's own precomputation and the memory is reported as such.
    /// </summary>
    (int[] Start, int[] Count, int[] WordIds, int[] Cumulative) BuildNameTables(
        IContentSnapshot snapshot,
        ModCandidateTables tables)
    {
        if (NamePositionCount == 0 || SignatureCount == 0)
        {
            return ([], [], [], []);
        }

        List<ContentRow> words = InstanceContentChecks.LiveRows(snapshot, InstanceContentTypeIds.RareNameWordTypeId);
        var byPosition = new List<int>[NamePositionCount];
        for (int position = 0; position < NamePositionCount; position++)
        {
            byPosition[position] = [];
        }

        foreach (ContentRow row in words)
        {
            int position = Clamp(InstanceContentChecks.Number(row, RareNameWordContentType.PositionIndex) ?? 0);
            if (position >= RareNameWordContentType.MinPosition && position <= NamePositionCount)
            {
                byPosition[position - 1].Add(row.Id);
            }
        }

        var start = new int[SignatureCount * NamePositionCount];
        var count = new int[SignatureCount * NamePositionCount];
        var wordIds = new List<int>();
        var cumulative = new List<int>();
        for (int position = 0; position < NamePositionCount; position++)
        {
            int[] ids = [.. byPosition[position]];
            var folded = new int[SignatureCount * Math.Max(ids.Length, 1)];
            FoldWeights(
                snapshot,
                tables,
                InstanceContentTypeIds.RareNameWordWeightTypeId,
                RareNameWordWeightContentType.RareNameWordIdIndex,
                RareNameWordWeightContentType.TagIdIndex,
                RareNameWordWeightContentType.WeightIndex,
                ids,
                folded);

            for (int signature = 0; signature < SignatureCount; signature++)
            {
                int slot = (signature * NamePositionCount) + position;
                start[slot] = wordIds.Count;
                int running = 0;
                for (int word = 0; word < ids.Length; word++)
                {
                    int total = folded[(signature * ids.Length) + word];
                    if (total == running)
                    {
                        continue;
                    }

                    running = total;
                    wordIds.Add(ids[word]);
                    cumulative.Add(running);
                }

                count[slot] = wordIds.Count - start[slot];
            }
        }

        return (start, count, [.. wordIds], [.. cumulative]);
    }

    /// <summary>Each live base's durability declaration and its authored socket list, in <c>sort</c> order.</summary>
    static (int[] Ids, int[] Durability, int[] SocketStart, int[] SocketCount, int[] SocketTypes) BuildBases(
        IContentSnapshot snapshot)
    {
        List<ContentRow> bases = InstanceContentChecks.LiveRows(snapshot, EngineContentTypes.ItemTypeId);
        var ids = new int[bases.Count];
        var durability = new int[bases.Count];
        for (int index = 0; index < bases.Count; index++)
        {
            ids[index] = bases[index].Id;
            durability[index] = Clamp(InstanceContentChecks.Number(bases[index], ItemDurabilityMaxIndex) ?? 0);
        }

        (int[] socketStart, int[] socketCount, int[] socketTypes) = GroupChildren(
            snapshot,
            EngineContentTypes.BaseSocketTypeId,
            BaseSocketItemIndex,
            BaseSocketSortIndex,
            BaseSocketSocketTypeIndex,
            ids);

        return (ids, durability, socketStart, socketCount, socketTypes);
    }

    /// <summary>Each live unique template's lines and sockets, both in <c>sort</c> order.</summary>
    static (int[] Ids, int[] BaseIds, int[] LineStart, int[] LineCount, int[] LineMods, int[] LineTiers,
        int[] SocketStart, int[] SocketCount, int[] SocketTypes) BuildUniques(IContentSnapshot snapshot)
    {
        List<ContentRow> templates = InstanceContentChecks.LiveRows(snapshot, InstanceContentTypeIds.UniqueTemplateTypeId);
        var ids = new int[templates.Count];
        var baseIds = new int[templates.Count];
        for (int index = 0; index < templates.Count; index++)
        {
            ids[index] = templates[index].Id;
            baseIds[index] = Clamp(InstanceContentChecks.Number(templates[index], UniqueTemplateContentType.BaseIdIndex) ?? 0);
        }

        (int[] lineStart, int[] lineCount, int[] lineMods) = GroupChildren(
            snapshot,
            InstanceContentTypeIds.UniqueLineTypeId,
            UniqueLineContentType.UniqueTemplateIdIndex,
            UniqueLineContentType.SortIndex,
            UniqueLineContentType.ModIdIndex,
            ids);
        (_, _, int[] lineTiers) = GroupChildren(
            snapshot,
            InstanceContentTypeIds.UniqueLineTypeId,
            UniqueLineContentType.UniqueTemplateIdIndex,
            UniqueLineContentType.SortIndex,
            UniqueLineContentType.TierOrdinalIndex,
            ids);
        (int[] socketStart, int[] socketCount, int[] socketTypes) = GroupChildren(
            snapshot,
            InstanceContentTypeIds.UniqueSocketTypeId,
            UniqueSocketContentType.UniqueTemplateIdIndex,
            UniqueSocketContentType.SortIndex,
            UniqueSocketContentType.SocketTypeIdIndex,
            ids);

        return (ids, baseIds, lineStart, lineCount, lineMods, lineTiers, socketStart, socketCount, socketTypes);
    }

    /// <summary>
    /// One child type grouped under its parents and flattened, ordered by <c>sort</c> then by row id, which
    /// is what makes "the second socket" and "the second line" stable phrases across a republish.
    /// </summary>
    static (int[] Start, int[] Count, int[] Values) GroupChildren(
        IContentSnapshot snapshot,
        ushort childTypeId,
        int parentIndex,
        int sortIndex,
        int valueIndex,
        int[] parentIds)
    {
        var start = new int[Math.Max(parentIds.Length, 1)];
        var count = new int[Math.Max(parentIds.Length, 1)];
        if (parentIds.Length == 0)
        {
            return (start, count, []);
        }

        var rows = new List<(int Parent, int Sort, int Id, int Value)>();
        foreach (ContentRow row in InstanceContentChecks.LiveRows(snapshot, childTypeId))
        {
            int parent = GenerationTagSignature.IndexOf(parentIds, Clamp(InstanceContentChecks.Number(row, parentIndex) ?? 0));
            if (parent < 0)
            {
                continue;
            }

            rows.Add((
                parent,
                Clamp(InstanceContentChecks.Number(row, sortIndex) ?? 0),
                row.Id,
                Clamp(InstanceContentChecks.Number(row, valueIndex) ?? 0)));
        }

        rows.Sort(static (left, right) =>
        {
            int byParent = left.Parent.CompareTo(right.Parent);
            if (byParent != 0)
            {
                return byParent;
            }

            int bySort = left.Sort.CompareTo(right.Sort);
            return bySort != 0 ? bySort : left.Id.CompareTo(right.Id);
        });

        var values = new int[rows.Count];
        for (int index = 0; index < rows.Count; index++)
        {
            values[index] = rows[index].Value;
            if (count[rows[index].Parent] == 0)
            {
                start[rows[index].Parent] = index;
            }

            count[rows[index].Parent]++;
        }

        return (start, count, values);
    }
}
