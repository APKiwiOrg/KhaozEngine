using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.Catalog;
using static KhaozEngine.ItemInstances.InstanceContentChecks;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The rarity and unique families' half of spec 8.9: the parent references of <c>rarity_weight</c>,
/// <c>rarity_kind_limit</c>, <c>unique_line</c> and <c>unique_socket</c>, the rarity rule's counts and its
/// upgrade chain, the unique line's mod and tier, and the publish-only refusal of a rarity id that moved.
/// <para>
/// <b>The two UNIQUENESS rules here are not in spec 8.9's list of twelve</b>, and both are cross-row rules
/// their own type doc comments promised an author: one socket index per template (<c>KEC0115</c>) and one
/// mod kind per rarity (<c>KEC0116</c>). Each is a row that would otherwise give a reader two answers to
/// one question.
/// </para>
/// <para>
/// <b>Every field index here is the TYPE's own constant</b>, never a number repeated in this file, because
/// a private copy of one silently points this sweep at a neighbouring value of the same kind the moment a
/// field is inserted into a schema.
/// </para>
/// </summary>
internal static class RarityAndUniqueChecks
{
    internal static void Run(
        IContentSnapshot candidate,
        IContentSnapshot? previous,
        ICollection<ContentFinding> findings)
    {
        CheckRarityChildren(candidate, findings);
        CheckRarityRules(candidate, findings);
        CheckUniqueChildren(candidate, findings);

        if (previous is not null)
        {
            CheckRarityIdHistory(candidate, previous, findings);
        }
    }

    /// <summary>
    /// <c>KEC0100</c> on the two rarity children and <c>KEC0116</c> on a mod kind two limits claim for one
    /// rarity. The claim is per PAIR rather than per kind: one rarity limiting two kinds and two rarities
    /// limiting one kind are both the ordinary authored shape.
    /// </summary>
    static void CheckRarityChildren(IContentSnapshot candidate, ICollection<ContentFinding> findings)
    {
        foreach (ContentRow row in LiveRows(candidate, InstanceContentTypeIds.RarityWeightTypeId))
        {
            CheckParent(
                candidate,
                findings,
                row,
                InstanceContentTypeIds.RarityWeightTypeKey,
                RarityWeightContentType.RarityRuleIdIndex,
                RarityWeightContentType.RarityRuleIdField,
                InstanceContentTypeIds.RarityRuleTypeId,
                InstanceContentTypeIds.RarityRuleTypeKey);
        }

        var claimed = new Dictionary<(long Rarity, long Kind), int>();
        foreach (ContentRow row in LiveRows(candidate, InstanceContentTypeIds.RarityKindLimitTypeId))
        {
            CheckParent(
                candidate,
                findings,
                row,
                InstanceContentTypeIds.RarityKindLimitTypeKey,
                RarityKindLimitContentType.RarityRuleIdIndex,
                RarityKindLimitContentType.RarityRuleIdField,
                InstanceContentTypeIds.RarityRuleTypeId,
                InstanceContentTypeIds.RarityRuleTypeKey);

            long rarityId = Number(row, RarityKindLimitContentType.RarityRuleIdIndex) ?? 0;
            long modKind = Number(row, RarityKindLimitContentType.ModKindIndex) ?? 0;
            if (claimed.TryGetValue((rarityId, modKind), out int firstId))
            {
                findings.Add(new ContentFinding(
                    row.Type,
                    row.Id,
                    InstanceContentFindings.RarityKindClaimed,
                    InstanceContentFindings.RarityKindTwice(row.Id, modKind, rarityId, firstId)));
                continue;
            }

            claimed.Add((rarityId, modKind), row.Id);
        }
    }

    /// <summary>
    /// <c>KEC0106</c>: the two count rules of spec 8.9 check 6, then the upgrade chain. The cycle walk is
    /// ITERATIVE and marks every row it clears, so a chain of any depth costs one pass over the rows and a
    /// cycle is reported ONCE, at its lowest id, rather than once per row on it.
    /// </summary>
    static void CheckRarityRules(IContentSnapshot candidate, ICollection<ContentFinding> findings)
    {
        List<ContentRow> rows = LiveRows(candidate, InstanceContentTypeIds.RarityRuleTypeId);
        var upgradeFrom = new Dictionary<int, long>(rows.Count);

        foreach (ContentRow row in rows)
        {
            upgradeFrom[row.Id] = Number(row, RarityRuleContentType.UpgradeFromIndex) ?? 0;

            long min = Number(row, RarityRuleContentType.MinAffixesIndex) ?? 0;
            long max = Number(row, RarityRuleContentType.MaxAffixesIndex) ?? 0;
            if (min > max)
            {
                findings.Add(new ContentFinding(
                    row.Type,
                    row.Id,
                    InstanceContentFindings.RarityRuleShape,
                    InstanceContentFindings.RarityAffixCounts(row.Id, min, max)));
            }

            long prefixes = Number(row, RarityRuleContentType.MaxPrefixesIndex) ?? 0;
            long suffixes = Number(row, RarityRuleContentType.MaxSuffixesIndex) ?? 0;
            if (prefixes + suffixes < max)
            {
                findings.Add(new ContentFinding(
                    row.Type,
                    row.Id,
                    InstanceContentFindings.RarityRuleShape,
                    InstanceContentFindings.RarityKindCounts(row.Id, prefixes, suffixes, max)));
            }
        }

        CheckUpgradeChains(rows, upgradeFrom, findings);
    }

    /// <summary>The upgrade chain walk, split out because it is the only stateful part of the rarity pass.</summary>
    static void CheckUpgradeChains(
        List<ContentRow> rows,
        Dictionary<int, long> upgradeFrom,
        ICollection<ContentFinding> findings)
    {
        var cleared = new HashSet<int>();
        var walking = new HashSet<int>();
        var path = new List<int>();

        foreach (ContentRow start in rows)
        {
            if (cleared.Contains(start.Id))
            {
                continue;
            }

            path.Clear();
            walking.Clear();
            int current = start.Id;

            while (true)
            {
                if (cleared.Contains(current))
                {
                    break;
                }

                if (!walking.Add(current))
                {
                    ReportCycle(path, current, findings);
                    break;
                }

                path.Add(current);
                long next = upgradeFrom.TryGetValue(current, out long parent) ? parent : 0;
                if (next <= 0 || !upgradeFrom.ContainsKey((int)next))
                {
                    break;
                }

                current = (int)next;
            }

            foreach (int id in path)
            {
                cleared.Add(id);
            }
        }
    }

    /// <summary>One finding for the cycle the walk closed, at the lowest id on it.</summary>
    static void ReportCycle(List<int> path, int closedAt, ICollection<ContentFinding> findings)
    {
        int from = path.IndexOf(closedAt);
        if (from < 0)
        {
            return;
        }

        int lowest = path[from];
        var chain = new List<string>(path.Count - from + 1);
        for (int i = from; i < path.Count; i++)
        {
            lowest = path[i] < lowest ? path[i] : lowest;
            chain.Add(path[i].ToString(CultureInfo.InvariantCulture));
        }

        chain.Add(closedAt.ToString(CultureInfo.InvariantCulture));

        findings.Add(new ContentFinding(
            Type(InstanceContentTypeIds.RarityRuleTypeId),
            lowest,
            InstanceContentFindings.RarityRuleShape,
            InstanceContentFindings.RarityCycle(lowest, string.Join(" to ", chain))));
    }

    /// <summary>
    /// <c>KEC0100</c> on the two unique children and <c>KEC0107</c> on the line itself: its mod resolves,
    /// its ordinal names a tier of THAT mod, and that mod carries no <c>mod_tier_weight</c> row on any
    /// tier. The last one is what keeps a unique's line off an ordinary rare.
    /// </summary>
    static void CheckUniqueChildren(IContentSnapshot candidate, ICollection<ContentFinding> findings)
    {
        var socketIndexTaken = new Dictionary<(long Template, long Sort), int>();
        foreach (ContentRow row in LiveRows(candidate, InstanceContentTypeIds.UniqueSocketTypeId))
        {
            CheckParent(
                candidate,
                findings,
                row,
                InstanceContentTypeIds.UniqueSocketTypeKey,
                UniqueSocketContentType.UniqueTemplateIdIndex,
                UniqueSocketContentType.UniqueTemplateIdField,
                InstanceContentTypeIds.UniqueTemplateTypeId,
                InstanceContentTypeIds.UniqueTemplateTypeKey);

            long templateId = Number(row, UniqueSocketContentType.UniqueTemplateIdIndex) ?? 0;
            long sort = Number(row, UniqueSocketContentType.SortIndex) ?? 0;
            if (socketIndexTaken.TryGetValue((templateId, sort), out int firstId))
            {
                findings.Add(new ContentFinding(
                    row.Type,
                    row.Id,
                    InstanceContentFindings.UniqueSocketSort,
                    InstanceContentFindings.SocketDuplicateSort(row.Id, sort, templateId, firstId)));
                continue;
            }

            socketIndexTaken.Add((templateId, sort), row.Id);
        }

        List<ContentRow> lines = LiveRows(candidate, InstanceContentTypeIds.UniqueLineTypeId);
        if (lines.Count == 0)
        {
            return;
        }

        var tierOf = new Dictionary<(long Mod, long Ordinal), int>();
        var modOfTier = new Dictionary<int, long>();
        foreach (ContentRow tier in LiveRows(candidate, InstanceContentTypeIds.ModTierTypeId))
        {
            long modId = Number(tier, ModTierContentType.ModIdIndex) ?? 0;
            long ordinal = Number(tier, ModTierContentType.OrdinalIndex) ?? 0;
            modOfTier[tier.Id] = modId;
            _ = tierOf.TryAdd((modId, ordinal), tier.Id);
        }

        var weightedMod = new Dictionary<long, int>();
        foreach (ContentRow weight in LiveRows(candidate, InstanceContentTypeIds.ModTierWeightTypeId))
        {
            long tierId = Number(weight, ModTierWeightContentType.ModTierIdIndex) ?? 0;
            if (tierId is > 0 and <= int.MaxValue && modOfTier.TryGetValue((int)tierId, out long modId))
            {
                _ = weightedMod.TryAdd(modId, weight.Id);
            }
        }

        foreach (ContentRow line in lines)
        {
            CheckParent(
                candidate,
                findings,
                line,
                InstanceContentTypeIds.UniqueLineTypeKey,
                UniqueLineContentType.UniqueTemplateIdIndex,
                UniqueLineContentType.UniqueTemplateIdField,
                InstanceContentTypeIds.UniqueTemplateTypeId,
                InstanceContentTypeIds.UniqueTemplateTypeKey);

            long lineMod = Number(line, UniqueLineContentType.ModIdIndex) ?? 0;
            if (!IsLive(candidate, InstanceContentTypeIds.ModTypeId, lineMod))
            {
                findings.Add(new ContentFinding(
                    line.Type,
                    line.Id,
                    InstanceContentFindings.UniqueLineShape,
                    InstanceContentFindings.UniqueLineMod(line.Id, lineMod)));
                continue;
            }

            long ordinal = Number(line, UniqueLineContentType.TierOrdinalIndex) ?? 0;
            if (!tierOf.ContainsKey((lineMod, ordinal)))
            {
                findings.Add(new ContentFinding(
                    line.Type,
                    line.Id,
                    InstanceContentFindings.UniqueLineShape,
                    InstanceContentFindings.UniqueLineOrdinal(line.Id, lineMod, ordinal)));
            }

            if (weightedMod.TryGetValue(lineMod, out int weightRowId))
            {
                findings.Add(new ContentFinding(
                    line.Type,
                    line.Id,
                    InstanceContentFindings.UniqueLineShape,
                    InstanceContentFindings.UniqueLineWeighted(line.Id, lineMod, weightRowId)));
            }
        }
    }

    /// <summary>
    /// PUBLISH ONLY, <c>KEC0111</c>: a rarity rule key naming a different definition id than it did before.
    /// Payload kind 130 stores the id, so the key moving onto a new id repoints every stored item.
    /// </summary>
    static void CheckRarityIdHistory(
        IContentSnapshot candidate,
        IContentSnapshot previous,
        ICollection<ContentFinding> findings)
    {
        foreach (ContentRow before in LiveRows(previous, InstanceContentTypeIds.RarityRuleTypeId))
        {
            if (!candidate.TryGetId(Type(InstanceContentTypeIds.RarityRuleTypeId), before.Key, out int id)
                || id == before.Id)
            {
                continue;
            }

            findings.Add(new ContentFinding(
                Type(InstanceContentTypeIds.RarityRuleTypeId),
                id,
                InstanceContentFindings.RarityRuleIdMoved,
                InstanceContentFindings.RarityIdMoved(
                    before.Key.ToString(), before.Id, id, previous.VersionNumber)));
        }
    }
}
