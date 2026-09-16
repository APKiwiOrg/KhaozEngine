using System.Collections.Generic;
using KhaozEngine.Catalog;
using static KhaozEngine.ItemInstances.InstanceContentChecks;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// Spec 8.9 check 9, <c>KEC0109</c>, in its two clauses. First, every <c>rare_name_word</c> sits at a
/// position the payload can hold. Second, the expensive one: for every rarity that rolls a name and every
/// base tag that rarity is reachable at, every position it rolls has at least one word of non-zero weight.
/// That second one is the check that stops a publish producing an item whose name cannot be rolled.
/// <para>
/// <b>The first clause is the one the codec cannot cover.</b> The row codec bounds the field on both sides
/// too, and a publish validates BEFORE it encodes, so an authored 0 would sweep clean and then throw at
/// pack time with no finding naming the row.
/// </para>
/// <para>
/// <b>It is a cross product and it is written as ONE pass plus one sweep.</b> The pass builds the set of
/// pairs of tag and position that any word of non-zero weight covers, and the sweep asks the rarities
/// about it. It never walks bases: a base is reached only through its TAGS, so the cost is the number of
/// rarities times their reachable tags times their position counts, never the 50,000 bases spec 9 sizes
/// against.
/// </para>
/// <para>
/// Every part of it is VACUOUS over an empty row set. No rarity means no sweep, a rarity with
/// <c>name_word_positions</c> of 0 keeps its base name and is skipped, and a rarity reachable at no tag has
/// nothing to cover.
/// </para>
/// </summary>
internal static class RareNameCoverageCheck
{
    internal static void Run(IContentSnapshot candidate, ICollection<ContentFinding> findings)
    {
        Dictionary<int, long> wordPosition = CheckWordsAndWeightParents(candidate, findings);

        List<ContentRow> rarities = LiveRows(candidate, InstanceContentTypeIds.RarityRuleTypeId);
        if (rarities.Count == 0)
        {
            return;
        }

        var covered = new HashSet<(long Tag, long Position)>();
        foreach (ContentRow weight in LiveRows(candidate, InstanceContentTypeIds.RareNameWordWeightTypeId))
        {
            long share = Number(weight, RareNameWordWeightContentType.WeightIndex) ?? 0;
            long wordId = Number(weight, RareNameWordWeightContentType.RareNameWordIdIndex) ?? 0;
            if (share <= 0 || wordId is <= 0 or > int.MaxValue
                || !wordPosition.TryGetValue((int)wordId, out long position))
            {
                continue;
            }

            _ = covered.Add((Number(weight, RareNameWordWeightContentType.TagIdIndex) ?? 0, position));
        }

        Dictionary<int, List<long>> reachableTags = ReachableTags(candidate);

        foreach (ContentRow rarity in rarities)
        {
            long positions = Number(rarity, RarityRuleContentType.NameWordPositionsIndex) ?? 0;
            if (positions <= 0 || !reachableTags.TryGetValue(rarity.Id, out List<long>? tags))
            {
                continue;
            }

            for (long position = 1; position <= positions; position++)
            {
                foreach (long tagId in tags)
                {
                    if (covered.Contains((tagId, position)))
                    {
                        continue;
                    }

                    findings.Add(new ContentFinding(
                        rarity.Type,
                        rarity.Id,
                        InstanceContentFindings.RareNameCoverage,
                        InstanceContentFindings.RareNameUncovered(rarity.Id, (int)position, tagId)));
                    break;
                }
            }
        }
    }

    /// <summary>
    /// <c>KEC0100</c> on each word weight's word, <c>KEC0109</c>'s first clause on each word's own position,
    /// and the word to position map the coverage pass needs, so the word rows are walked once.
    /// </summary>
    static Dictionary<int, long> CheckWordsAndWeightParents(
        IContentSnapshot candidate,
        ICollection<ContentFinding> findings)
    {
        foreach (ContentRow row in LiveRows(candidate, InstanceContentTypeIds.RareNameWordWeightTypeId))
        {
            CheckParent(
                candidate,
                findings,
                row,
                InstanceContentTypeIds.RareNameWordWeightTypeKey,
                RareNameWordWeightContentType.RareNameWordIdIndex,
                RareNameWordWeightContentType.RareNameWordIdField,
                InstanceContentTypeIds.RareNameWordTypeId,
                InstanceContentTypeIds.RareNameWordTypeKey);
        }

        List<ContentRow> words = LiveRows(candidate, InstanceContentTypeIds.RareNameWordTypeId);
        var positions = new Dictionary<int, long>(words.Count);
        foreach (ContentRow word in words)
        {
            long position = Number(word, RareNameWordContentType.PositionIndex) ?? 0;
            positions[word.Id] = position;
            if (position is < RareNameWordContentType.MinPosition or > RareNameWordContentType.MaxPosition)
            {
                findings.Add(new ContentFinding(
                    word.Type,
                    word.Id,
                    InstanceContentFindings.RareNameCoverage,
                    InstanceContentFindings.RareNameWordPosition(
                        word.Id,
                        position,
                        RareNameWordContentType.MinPosition,
                        RareNameWordContentType.MaxPosition)));
            }
        }

        return positions;
    }

    /// <summary>
    /// The tags each rarity can actually be drawn at, which is a <c>rarity_weight</c> row above zero. In
    /// row id order, so the tag a finding names is the same on every run.
    /// </summary>
    static Dictionary<int, List<long>> ReachableTags(IContentSnapshot candidate)
    {
        var reachable = new Dictionary<int, List<long>>();
        foreach (ContentRow row in LiveRows(candidate, InstanceContentTypeIds.RarityWeightTypeId))
        {
            long weight = Number(row, RarityWeightContentType.WeightIndex) ?? 0;
            long rarityId = Number(row, RarityWeightContentType.RarityRuleIdIndex) ?? 0;
            if (weight <= 0 || rarityId is <= 0 or > int.MaxValue)
            {
                continue;
            }

            if (!reachable.TryGetValue((int)rarityId, out List<long>? tags))
            {
                tags = [];
                reachable.Add((int)rarityId, tags);
            }

            long tagId = Number(row, RarityWeightContentType.TagIdIndex) ?? 0;
            if (!tags.Contains(tagId))
            {
                tags.Add(tagId);
            }
        }

        return reachable;
    }
}
