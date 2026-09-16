using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;

namespace KhaozEngine.Benchmarks.Items;

/// <summary>
/// <see cref="SyntheticContent"/> projected into REAL rows of the shipped content types and handed to
/// <see cref="ContentSnapshotBuilder"/>, so the <c>--items</c> mode measures the generator that ships
/// rather than the spike that proved it could exist.
/// <para>
/// The row SOURCE is unchanged and is still one seed through <c>DeterministicRng</c>: the owner's scale of
/// 2,000 mods with eight tiers and five tag weights each, 50,000 bases over 300 authored tag signatures,
/// 20 rarities and 200 rare name words. What changed is where those numbers land. They used to be flat
/// arrays a spike walked, and they are now <c>mod</c>, <c>mod_tier</c>, <c>mod_tier_weight</c>,
/// <c>stat_line</c>, <c>rarity_rule</c>, <c>rarity_weight</c>, <c>rare_name_word</c> and
/// <c>rare_name_word_weight</c> rows beside the engine's own <c>tag</c>, <c>stat</c>, <c>item</c> and
/// <c>base_socket</c>, which is what <see cref="ModCandidateTables"/> and <see cref="ItemGenerator"/> read.
/// </para>
/// <para>
/// <b>Nothing here is authored content and nothing here is PoE</b> (spec 8.1). The rows carry no names, no
/// balance and no meaning: every number in them came out of a seeded generator at the scale the budgets are
/// measured at, which is the only property this projection needs to keep.
/// </para>
/// <para>
/// <b>Two shapes the flat arrays allowed and a row set cannot.</b> A tier's five weight slots may draw the
/// same tag twice, and one (tier, tag) pair is ONE row rather than two, so a repeat is folded rather than
/// added twice. A weight of zero is no row at all, which is the same answer the table build gives a zero
/// weight it does read.
/// </para>
/// </summary>
internal static class SyntheticContentRows
{
    /// <summary>The one socket type every socketed base seats, which is all the generator needs of 8.7.</summary>
    const int SocketTypeId = 1;

    /// <summary>The per item cap every exclusivity group carries, which is the rule the spike enforced.</summary>
    const int GroupMaxPerItem = 1;

    /// <summary>A registry carrying the engine types and all eighteen of the instance band.</summary>
    internal static ContentTypeRegistry Registry()
    {
        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);
        InstanceContentTypes.Register(registry);
        return registry;
    }

    /// <summary>
    /// The whole synthetic set as one snapshot, carrying the version number the pages of this run are
    /// encoded under so a rolled item's <c>ContentVersion</c> and its page agree.
    /// </summary>
    internal static ContentSnapshot Snapshot(ContentTypeRegistry registry, SyntheticContent content, int versionNumber)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(content);

        ContentSnapshotBuilder builder = new ContentSnapshotBuilder(registry).WithIdentity(versionNumber, string.Empty);
        AddTagsAndStats(builder, registry, content);
        AddBases(builder, registry, content);
        AddMods(builder, registry, content);
        AddTiers(builder, registry, content);
        AddRarities(builder, registry, content);
        AddNameWords(builder, registry, content);
        return builder.Build();
    }

    /// <summary>The 64 tags every weight row is keyed by, and the stats the lines point at.</summary>
    static void AddTagsAndStats(ContentSnapshotBuilder builder, ContentTypeRegistry registry, SyntheticContent content)
    {
        var tags = new TypeWriter(registry, EngineContentTypes.TagTypeId);
        int tagSort = tags.IndexOf(TagContentType.SortField);
        for (int tag = 1; tag <= content.TagCount; tag++)
        {
            ContentFieldValue[] values = tags.Blank();
            tags.Set(values, tagSort, tag);
            builder.AddRow(tags.Row(tag, Key("tag", tag), values));
        }

        var stats = new TypeWriter(registry, EngineContentTypes.StatTypeId);
        int scale = stats.IndexOf(StatContentType.ScaleField);
        int minimum = stats.IndexOf(StatContentType.MinField);
        int maximum = stats.IndexOf(StatContentType.MaxField);
        for (int stat = 1; stat <= content.StatCount; stat++)
        {
            ContentFieldValue[] values = stats.Blank();
            stats.Set(values, scale, 1);
            stats.Set(values, minimum, 0);
            stats.Set(values, maximum, 1_000_000);
            builder.AddRow(stats.Row(stat, Key("stat", stat), values));
        }
    }

    /// <summary>
    /// The 50,000 bases and their sockets. Bases sharing a tag signature share the ENCODED tag list rather
    /// than each building their own, which is the same fact about authored tag lists that makes the
    /// candidate tables worth keying by signature at all.
    /// </summary>
    static void AddBases(ContentSnapshotBuilder builder, ContentTypeRegistry registry, SyntheticContent content)
    {
        var items = new TypeWriter(registry, EngineContentTypes.ItemTypeId);
        int tagsField = items.IndexOf(ItemContentType.TagsField);
        int stackable = items.IndexOf(ItemContentType.StackableField);
        int maxStack = items.IndexOf(ItemContentType.MaxStackField);
        int tradable = items.IndexOf(ItemContentType.TradableField);
        int value = items.IndexOf(ItemContentType.ValueField);
        int durability = items.IndexOf(ItemContentType.DurabilityMaxField);
        int socketMax = items.IndexOf(ItemContentType.SocketMaxField);

        var sockets = new TypeWriter(registry, EngineContentTypes.BaseSocketTypeId);
        int socketItem = sockets.IndexOf(BaseSocketContentType.ItemField);
        int socketSort = sockets.IndexOf(BaseSocketContentType.SortField);
        int socketType = sockets.IndexOf(BaseSocketContentType.SocketTypeField);

        var socketTypes = new TypeWriter(registry, InstanceContentTypeIds.SocketTypeTypeId);
        ContentFieldValue[] socketTypeValues = socketTypes.Blank();
        socketTypes.Set(socketTypeValues, socketTypes.IndexOf(SocketTypeContentType.MaxNestedBytesField), 0);
        builder.AddRow(socketTypes.Row(SocketTypeId, Key("socket_type", SocketTypeId), socketTypeValues));

        var encoded = new Dictionary<int, ContentFieldValue>(content.DistinctTagSignatures);
        int socketRow = 0;
        for (int index = 0; index < content.BaseCount; index++)
        {
            int signature = content.BaseTagSignature[index];
            if (!encoded.TryGetValue(signature, out ContentFieldValue list))
            {
                var ids = new int[content.BaseTagCount[index]];
                for (int tag = 0; tag < ids.Length; tag++)
                {
                    ids[tag] = content.BaseTags[content.BaseTagStart[index] + tag];
                }

                list = ContentRowCodecBase.TagListValue(ids);
                encoded.Add(signature, list);
            }

            int baseId = content.BaseIdOf(index);
            ContentFieldValue[] values = items.Blank();
            values[tagsField] = list;
            items.Set(values, stackable, 0);
            items.Set(values, maxStack, 1);
            items.Set(values, tradable, 1);
            items.Set(values, value, 10);
            items.Set(values, socketMax, content.BaseSocketCount[index]);
            if (content.BaseHasDurability[index])
            {
                items.Set(values, durability, 100);
            }

            builder.AddRow(items.Row(baseId, Key("item", baseId), values));

            for (int seat = 0; seat < content.BaseSocketCount[index]; seat++)
            {
                ContentFieldValue[] socketValues = sockets.Blank();
                sockets.Set(socketValues, socketItem, baseId);
                sockets.Set(socketValues, socketSort, seat + 1);
                sockets.Set(socketValues, socketType, SocketTypeId);
                socketRow++;
                builder.AddRow(sockets.Row(socketRow, Key("base_socket", socketRow), socketValues));
            }
        }
    }

    /// <summary>The mods and the exclusivity groups they name, each group capped at one per item.</summary>
    static void AddMods(ContentSnapshotBuilder builder, ContentTypeRegistry registry, SyntheticContent content)
    {
        var mods = new TypeWriter(registry, InstanceContentTypeIds.ModTypeId);
        int kind = mods.IndexOf(ModContentType.KindField);
        int group = mods.IndexOf(ModContentType.GroupIdField);
        int legacy = mods.IndexOf(ModContentType.LegacyField);

        var groups = new SortedSet<int>();
        for (int index = 0; index < content.ModCount; index++)
        {
            ContentFieldValue[] values = mods.Blank();
            mods.Set(values, kind, content.ModKind[index]);
            mods.Set(values, legacy, content.ModLegacy[index] ? 1 : 0);
            if (content.ModGroup[index] != 0)
            {
                mods.Set(values, group, content.ModGroup[index]);
                groups.Add(content.ModGroup[index]);
            }

            int modId = content.ModIdOf(index);
            builder.AddRow(mods.Row(modId, Key("mod", modId), values));
        }

        var groupWriter = new TypeWriter(registry, InstanceContentTypeIds.ModGroupTypeId);
        int maxPerItem = groupWriter.IndexOf(ModGroupContentType.MaxPerItemField);
        foreach (int id in groups)
        {
            ContentFieldValue[] values = groupWriter.Blank();
            groupWriter.Set(values, maxPerItem, GroupMaxPerItem);
            builder.AddRow(groupWriter.Row(id, Key("mod_group", id), values));
        }
    }

    /// <summary>
    /// The tiers, their tag weights and their stat lines. The tier id is the flat slot plus one, which is
    /// what makes a weight row's parent reference readable back off the source arrays.
    /// </summary>
    static void AddTiers(ContentSnapshotBuilder builder, ContentTypeRegistry registry, SyntheticContent content)
    {
        var tiers = new TypeWriter(registry, InstanceContentTypeIds.ModTierTypeId);
        int modId = tiers.IndexOf(ModTierContentType.ModIdField);
        int ordinal = tiers.IndexOf(ModTierContentType.OrdinalField);
        int levelMin = tiers.IndexOf(ModTierContentType.ItemLevelMinField);
        int levelMax = tiers.IndexOf(ModTierContentType.ItemLevelMaxField);

        var weights = new TypeWriter(registry, InstanceContentTypeIds.ModTierWeightTypeId);
        int weightTier = weights.IndexOf(ModTierWeightContentType.ModTierIdField);
        int weightTag = weights.IndexOf(ModTierWeightContentType.TagIdField);
        int weightValue = weights.IndexOf(ModTierWeightContentType.WeightField);

        var lines = new TypeWriter(registry, InstanceContentTypeIds.StatLineTypeId);
        int lineTier = lines.IndexOf(StatLineContentType.ModTierIdField);
        int lineSort = lines.IndexOf(StatLineContentType.SortField);
        int lineStat = lines.IndexOf(StatLineContentType.StatIdField);
        int lineCombine = lines.IndexOf(StatLineContentType.CombineField);
        int lineMin = lines.IndexOf(StatLineContentType.MinField);
        int lineMax = lines.IndexOf(StatLineContentType.MaxField);

        int weightRow = 0;
        int lineRow = 0;
        Span<int> seen = stackalloc int[SyntheticContent.WeightsPerTier];
        for (int index = 0; index < content.ModCount; index++)
        {
            for (int slot = 0; slot < SyntheticContent.TiersPerMod; slot++)
            {
                int tier = (index * SyntheticContent.TiersPerMod) + slot;
                int tierId = tier + 1;
                ContentFieldValue[] values = tiers.Blank();
                tiers.Set(values, modId, content.ModIdOf(index));
                tiers.Set(values, ordinal, slot + 1);
                tiers.Set(values, levelMin, content.TierLevelMin[tier]);
                tiers.Set(values, levelMax, content.TierLevelMax[tier]);
                builder.AddRow(tiers.Row(tierId, Key("mod_tier", tierId), values));

                int distinct = 0;
                for (int weight = 0; weight < SyntheticContent.WeightsPerTier; weight++)
                {
                    int slotIndex = (tier * SyntheticContent.WeightsPerTier) + weight;
                    int tagId = content.TierWeightTag[slotIndex];
                    int amount = content.TierWeightValue[slotIndex];
                    if (amount <= 0 || Contains(seen[..distinct], tagId))
                    {
                        continue;
                    }

                    seen[distinct++] = tagId;
                    ContentFieldValue[] weightValues = weights.Blank();
                    weights.Set(weightValues, weightTier, tierId);
                    weights.Set(weightValues, weightTag, tagId);
                    weights.Set(weightValues, weightValue, amount);
                    weightRow++;
                    builder.AddRow(weights.Row(weightRow, Key("mod_tier_weight", weightRow), weightValues));
                }

                for (int line = 0; line < content.TierLineCount[tier]; line++)
                {
                    int lineIndex = (tier * SyntheticContent.LinesPerTier) + line;
                    ContentFieldValue[] lineValues = lines.Blank();
                    lines.Set(lineValues, lineTier, tierId);
                    lines.Set(lineValues, lineSort, line + 1);
                    lines.Set(lineValues, lineStat, content.TierLineStat[lineIndex]);
                    lines.Set(lineValues, lineCombine, content.TierLineCombine[lineIndex]);
                    lines.Set(lineValues, lineMin, content.TierLineMin[lineIndex]);
                    lines.Set(lineValues, lineMax, content.TierLineMax[lineIndex]);
                    lineRow++;
                    builder.AddRow(lines.Row(lineRow, Key("stat_line", lineRow), lineValues));
                }
            }
        }
    }

    /// <summary>The rarity rules and their tag weights, which are what step 3 rolls a rarity against.</summary>
    static void AddRarities(ContentSnapshotBuilder builder, ContentTypeRegistry registry, SyntheticContent content)
    {
        var rules = new TypeWriter(registry, InstanceContentTypeIds.RarityRuleTypeId);
        int minAffixes = rules.IndexOf(RarityRuleContentType.MinAffixesField);
        int maxAffixes = rules.IndexOf(RarityRuleContentType.MaxAffixesField);
        int maxPrefixes = rules.IndexOf(RarityRuleContentType.MaxPrefixesField);
        int maxSuffixes = rules.IndexOf(RarityRuleContentType.MaxSuffixesField);
        int namePositions = rules.IndexOf(RarityRuleContentType.NameWordPositionsField);

        var weights = new TypeWriter(registry, InstanceContentTypeIds.RarityWeightTypeId);
        int weightRule = weights.IndexOf(RarityWeightContentType.RarityRuleIdField);
        int weightTag = weights.IndexOf(RarityWeightContentType.TagIdField);
        int weightValue = weights.IndexOf(RarityWeightContentType.WeightField);

        int weightRow = 0;
        for (int index = 0; index < content.RarityCount; index++)
        {
            int rarityId = index + 1;
            ContentFieldValue[] values = rules.Blank();
            rules.Set(values, minAffixes, content.RarityMinAffixes[index]);
            rules.Set(values, maxAffixes, content.RarityMaxAffixes[index]);
            rules.Set(values, maxPrefixes, content.RarityMaxPrefixes[index]);
            rules.Set(values, maxSuffixes, content.RarityMaxSuffixes[index]);
            rules.Set(values, namePositions, content.RarityNameWords[index]);
            builder.AddRow(rules.Row(rarityId, Key("rarity_rule", rarityId), values));

            for (int tag = 0; tag < content.TagCount; tag++)
            {
                int amount = content.RarityWeight[(index * content.TagCount) + tag];
                if (amount <= 0)
                {
                    continue;
                }

                ContentFieldValue[] weightValues = weights.Blank();
                weights.Set(weightValues, weightRule, rarityId);
                weights.Set(weightValues, weightTag, tag + 1);
                weights.Set(weightValues, weightValue, amount);
                weightRow++;
                builder.AddRow(weights.Row(weightRow, Key("rarity_weight", weightRow), weightValues));
            }
        }
    }

    /// <summary>The rare name words and their tag weights, which step 10 draws one position at a time.</summary>
    static void AddNameWords(ContentSnapshotBuilder builder, ContentTypeRegistry registry, SyntheticContent content)
    {
        var words = new TypeWriter(registry, InstanceContentTypeIds.RareNameWordTypeId);
        int position = words.IndexOf(RareNameWordContentType.PositionField);

        var weights = new TypeWriter(registry, InstanceContentTypeIds.RareNameWordWeightTypeId);
        int weightWord = weights.IndexOf(RareNameWordWeightContentType.RareNameWordIdField);
        int weightTag = weights.IndexOf(RareNameWordWeightContentType.TagIdField);
        int weightValue = weights.IndexOf(RareNameWordWeightContentType.WeightField);

        int weightRow = 0;
        for (int index = 0; index < content.NameWordCount; index++)
        {
            int wordId = index + 1;
            ContentFieldValue[] values = words.Blank();
            words.Set(values, position, content.WordPosition[index]);
            builder.AddRow(words.Row(wordId, Key("rare_name_word", wordId), values));

            for (int tag = 0; tag < content.TagCount; tag++)
            {
                int amount = content.WordWeight[(index * content.TagCount) + tag];
                if (amount <= 0)
                {
                    continue;
                }

                ContentFieldValue[] weightValues = weights.Blank();
                weights.Set(weightValues, weightWord, wordId);
                weights.Set(weightValues, weightTag, tag + 1);
                weights.Set(weightValues, weightValue, amount);
                weightRow++;
                builder.AddRow(weights.Row(weightRow, Key("rare_name_word_weight", weightRow), weightValues));
            }
        }
    }

    /// <summary>Whether one tag has already taken a weight row on the tier being written.</summary>
    static bool Contains(ReadOnlySpan<int> seen, int tagId)
    {
        for (int index = 0; index < seen.Length; index++)
        {
            if (seen[index] == tagId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One row key, which is unique within its type and says nothing else.</summary>
    static string Key(string type, int id) => string.Create(CultureInfo.InvariantCulture, $"{type}_{id}");

    /// <summary>
    /// One registered type plus the blank row every row of it is written over. Values are parallel to the
    /// schema BY INDEX, so each field is found by name once and written by index after that.
    /// </summary>
    sealed class TypeWriter
    {
        readonly ContentFieldSchema _schema;
        readonly ContentFieldValue[] _blank;

        internal TypeWriter(ContentTypeRegistry registry, ushort typeId)
        {
            var type = new ContentTypeId(typeId);
            if (!registry.TryGet(type, out ContentTypeRegistration? registration))
            {
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"Content type {typeId} is not registered, so the synthetic set cannot write a row of it."));
            }

            Type = type;
            _schema = registration.Schema;
            _blank = new ContentFieldValue[_schema.Fields.Count];
            for (int index = 0; index < _blank.Length; index++)
            {
                _blank[index] = ContentFieldValue.Absent(_schema.Fields[index].Kind);
            }
        }

        internal ContentTypeId Type { get; }

        /// <summary>The schema index one named field sits at, which is also its index in a row.</summary>
        internal int IndexOf(string field)
        {
            for (int index = 0; index < _schema.Fields.Count; index++)
            {
                if (string.Equals(_schema.Fields[index].Name, field, StringComparison.Ordinal))
                {
                    return index;
                }
            }

            throw new InvalidOperationException(FormattableString.Invariant(
                $"Type {Type.Value} declares no field named '{field}'."));
        }

        /// <summary>A row's values with every field absent, including every marker.</summary>
        internal ContentFieldValue[] Blank() => (ContentFieldValue[])_blank.Clone();

        /// <summary>Writes one numeric field, taking its kind from the schema rather than the caller.</summary>
        internal void Set(ContentFieldValue[] values, int index, long number)
            => values[index] = ContentFieldValue.OfNumber(_schema.Fields[index].Kind, number);

        /// <summary>One live row, which is what a published pack would have decoded to.</summary>
        internal ContentRow Row(int id, string key, ContentFieldValue[] values)
            => new(Type, id, new ContentKey(key), 0, false, values);
    }
}
