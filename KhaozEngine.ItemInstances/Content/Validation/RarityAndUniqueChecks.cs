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
/// The field indices below are the positions each type's <c>CreateSchema</c> declares.
/// </para>
/// </summary>
internal static class RarityAndUniqueChecks
{
    const int RarityMinAffixes = 1;
    const int RarityMaxAffixes = 2;
    const int RarityMaxPrefixes = 3;
    const int RarityMaxSuffixes = 4;
    const int RarityUpgradeFrom = 6;

    const int RarityWeightRarityRuleId = 0;
    const int RarityKindLimitRarityRuleId = 0;

    const int UniqueLineTemplateId = 0;
    const int UniqueLineModId = 2;
    const int UniqueLineTierOrdinal = 3;

    const int UniqueSocketTemplateId = 0;

    const int ModTierModId = 0;
    const int ModTierOrdinal = 1;
    const int ModTierWeightModTierId = 0;

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

    /// <summary><c>KEC0100</c> on the two rarity children. Their own values are other checks'.</summary>
    static void CheckRarityChildren(IContentSnapshot candidate, ICollection<ContentFinding> findings)
    {
        foreach (ContentRow row in LiveRows(candidate, InstanceContentTypeIds.RarityWeightTypeId))
        {
            CheckParent(
                candidate,
                findings,
                row,
                InstanceContentTypeIds.RarityWeightTypeKey,
                RarityWeightRarityRuleId,
                RarityWeightContentType.RarityRuleIdField,
                InstanceContentTypeIds.RarityRuleTypeId,
                InstanceContentTypeIds.RarityRuleTypeKey);
        }

        foreach (ContentRow row in LiveRows(candidate, InstanceContentTypeIds.RarityKindLimitTypeId))
        {
            CheckParent(
                candidate,
                findings,
                row,
                InstanceContentTypeIds.RarityKindLimitTypeKey,
                RarityKindLimitRarityRuleId,
                RarityKindLimitContentType.RarityRuleIdField,
                InstanceContentTypeIds.RarityRuleTypeId,
                InstanceContentTypeIds.RarityRuleTypeKey);
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
            upgradeFrom[row.Id] = Number(row, RarityUpgradeFrom) ?? 0;

            long min = Number(row, RarityMinAffixes) ?? 0;
            long max = Number(row, RarityMaxAffixes) ?? 0;
            if (min > max)
            {
                findings.Add(new ContentFinding(
                    row.Type,
                    row.Id,
                    InstanceContentFindings.RarityRuleShape,
                    InstanceContentFindings.RarityAffixCounts(row.Id, min, max)));
            }

            long prefixes = Number(row, RarityMaxPrefixes) ?? 0;
            long suffixes = Number(row, RarityMaxSuffixes) ?? 0;
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
        foreach (ContentRow row in LiveRows(candidate, InstanceContentTypeIds.UniqueSocketTypeId))
        {
            CheckParent(
                candidate,
                findings,
                row,
                InstanceContentTypeIds.UniqueSocketTypeKey,
                UniqueSocketTemplateId,
                UniqueSocketContentType.UniqueTemplateIdField,
                InstanceContentTypeIds.UniqueTemplateTypeId,
                InstanceContentTypeIds.UniqueTemplateTypeKey);
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
            long modId = Number(tier, ModTierModId) ?? 0;
            long ordinal = Number(tier, ModTierOrdinal) ?? 0;
            modOfTier[tier.Id] = modId;
            _ = tierOf.TryAdd((modId, ordinal), tier.Id);
        }

        var weightedMod = new Dictionary<long, int>();
        foreach (ContentRow weight in LiveRows(candidate, InstanceContentTypeIds.ModTierWeightTypeId))
        {
            long tierId = Number(weight, ModTierWeightModTierId) ?? 0;
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
                UniqueLineTemplateId,
                UniqueLineContentType.UniqueTemplateIdField,
                InstanceContentTypeIds.UniqueTemplateTypeId,
                InstanceContentTypeIds.UniqueTemplateTypeKey);

            long lineMod = Number(line, UniqueLineModId) ?? 0;
            if (!IsLive(candidate, InstanceContentTypeIds.ModTypeId, lineMod))
            {
                findings.Add(new ContentFinding(
                    line.Type,
                    line.Id,
                    InstanceContentFindings.UniqueLineShape,
                    InstanceContentFindings.UniqueLineMod(line.Id, lineMod)));
                continue;
            }

            long ordinal = Number(line, UniqueLineTierOrdinal) ?? 0;
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
