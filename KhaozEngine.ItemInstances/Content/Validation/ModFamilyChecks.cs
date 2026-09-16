using System.Collections.Generic;
using KhaozEngine.Catalog;
using static KhaozEngine.ItemInstances.InstanceContentChecks;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The mod family's half of spec 8.9: the parent references of <c>mod_tier</c>, <c>mod_tier_weight</c> and
/// <c>stat_line</c>, the tier's item level gate and ordinal, the stat line's shape, the mod group's count,
/// and the publish-only refusal of a tier ordinal that moved or vanished.
/// <para>
/// <b>The two CANDIDATE TABLE ceilings are here too</b>, because both are refusals the generator's tables
/// impose on the mod family's own rows rather than rules of spec 8.9. A tier ordinal past
/// <see cref="ModCandidateTables.MaxTierOrdinal"/> would alias onto another tier of the same mod, and a
/// base's authored tag list past <see cref="ModCandidateTables.MaxGenerationTagPositions"/> multiplies the
/// overlap header space. Both are loud at PUBLISH rather than silent in a table, and
/// <see cref="ModCandidateTables.Build"/> refuses either one that reached a boot without a publish.
/// </para>
/// <para>
/// <b>Every field index here is the TYPE's own constant</b>, never a number repeated in this file. A
/// positional row walk reads a field by its index, so a private copy of one silently points this sweep at a
/// neighbouring value of the same kind the moment a field is inserted into a schema.
/// </para>
/// </summary>
internal static class ModFamilyChecks
{
    internal static void Run(
        IContentSnapshot candidate,
        IContentSnapshot? previous,
        IReadOnlyList<RemapRule> rules,
        ICollection<ContentFinding> findings)
    {
        CheckModGroups(candidate, findings);
        CheckTiers(candidate, findings);
        CheckBaseTagPositions(candidate, findings);
        CheckTierWeightParents(candidate, findings);
        CheckStatLines(candidate, findings);

        if (previous is not null)
        {
            CheckTierHistory(candidate, previous, rules, findings);
        }
    }

    /// <summary><c>KEC0105</c>: a group permitting fewer than one of itself is a retire rather than a count.</summary>
    static void CheckModGroups(IContentSnapshot candidate, ICollection<ContentFinding> findings)
    {
        foreach (ContentRow row in LiveRows(candidate, InstanceContentTypeIds.ModGroupTypeId))
        {
            long maxPerItem = Number(row, ModGroupContentType.MaxPerItemIndex) ?? 0;
            if (maxPerItem >= 1)
            {
                continue;
            }

            findings.Add(new ContentFinding(
                row.Type,
                row.Id,
                InstanceContentFindings.ModGroupCount,
                InstanceContentFindings.ModGroupBelowOne(row.Id, maxPerItem)));
        }
    }

    /// <summary>
    /// <c>KEC0100</c> on the tier's mod, <c>KEC0101</c> on its item level gate and <c>KEC0102</c> on its
    /// ordinal. The ordinal is unique WITHIN its mod rather than globally, because the payload stores the
    /// pair.
    /// </summary>
    static void CheckTiers(IContentSnapshot candidate, ICollection<ContentFinding> findings)
    {
        var taken = new Dictionary<(long Mod, long Ordinal), int>();
        foreach (ContentRow row in LiveRows(candidate, InstanceContentTypeIds.ModTierTypeId))
        {
            CheckParent(
                candidate,
                findings,
                row,
                InstanceContentTypeIds.ModTierTypeKey,
                ModTierContentType.ModIdIndex,
                ModTierContentType.ModIdField,
                InstanceContentTypeIds.ModTypeId,
                InstanceContentTypeIds.ModTypeKey);

            long min = Number(row, ModTierContentType.ItemLevelMinIndex) ?? 0;
            long max = Number(row, ModTierContentType.ItemLevelMaxIndex) ?? 0;
            if (min > max)
            {
                findings.Add(new ContentFinding(
                    row.Type,
                    row.Id,
                    InstanceContentFindings.TierItemLevelRange,
                    InstanceContentFindings.TierLevelsInverted(row.Id, min, max)));
            }

            long ordinal = Number(row, ModTierContentType.OrdinalIndex) ?? 0;
            if (ordinal is < ModTierContentType.MinOrdinal or > ModTierContentType.MaxOrdinal)
            {
                findings.Add(new ContentFinding(
                    row.Type,
                    row.Id,
                    InstanceContentFindings.TierOrdinal,
                    InstanceContentFindings.OrdinalOutOfRange(
                        row.Id, ordinal, ModTierContentType.MinOrdinal, ModTierContentType.MaxOrdinal)));
                continue;
            }

            if (ordinal > ModCandidateTables.MaxTierOrdinal)
            {
                findings.Add(new ContentFinding(
                    row.Type,
                    row.Id,
                    InstanceContentFindings.TierOrdinal,
                    InstanceContentFindings.OrdinalOverPackedCeiling(
                        row.Id, ordinal, ModCandidateTables.MaxTierOrdinal)));
                continue;
            }

            long modId = Number(row, ModTierContentType.ModIdIndex) ?? 0;
            if (taken.TryGetValue((modId, ordinal), out int firstId))
            {
                findings.Add(new ContentFinding(
                    row.Type,
                    row.Id,
                    InstanceContentFindings.TierOrdinal,
                    InstanceContentFindings.OrdinalDuplicated(row.Id, ordinal, modId, firstId)));
                continue;
            }

            taken.Add((modId, ordinal), row.Id);
        }
    }

    /// <summary>
    /// <c>KEC0114</c> on an item base whose authored tag list is longer than the generation ceiling. The row
    /// is a Scope A type read through the candidate snapshot like any other, because the ceiling is the
    /// candidate TABLES' and the base is where the list is authored.
    /// </summary>
    static void CheckBaseTagPositions(IContentSnapshot candidate, ICollection<ContentFinding> findings)
    {
        var tags = new List<int>();
        foreach (ContentRow row in LiveRows(candidate, EngineContentTypes.ItemTypeId))
        {
            GenerationTagSignature.ReadTags(row, tags);
            if (tags.Count <= ModCandidateTables.MaxGenerationTagPositions)
            {
                continue;
            }

            findings.Add(new ContentFinding(
                row.Type,
                row.Id,
                InstanceContentFindings.GenerationTagPositions,
                InstanceContentFindings.GenerationTagsOverCeiling(
                    row.Id, tags.Count, ModCandidateTables.MaxGenerationTagPositions)));
        }
    }

    /// <summary><c>KEC0100</c> on a weight row's tier. The weight's own bounds are the weight sweep's.</summary>
    static void CheckTierWeightParents(IContentSnapshot candidate, ICollection<ContentFinding> findings)
    {
        foreach (ContentRow row in LiveRows(candidate, InstanceContentTypeIds.ModTierWeightTypeId))
        {
            CheckParent(
                candidate,
                findings,
                row,
                InstanceContentTypeIds.ModTierWeightTypeKey,
                ModTierWeightContentType.ModTierIdIndex,
                ModTierWeightContentType.ModTierIdField,
                InstanceContentTypeIds.ModTierTypeId,
                InstanceContentTypeIds.ModTierTypeKey);
        }
    }

    /// <summary>
    /// <c>KEC0100</c> on the line's tier and <c>KEC0104</c> on the three things spec 8.9 check 4 names: the
    /// stat resolves, the combine is one of the three kinds, and the range is not empty.
    /// </summary>
    static void CheckStatLines(IContentSnapshot candidate, ICollection<ContentFinding> findings)
    {
        foreach (ContentRow row in LiveRows(candidate, InstanceContentTypeIds.StatLineTypeId))
        {
            CheckParent(
                candidate,
                findings,
                row,
                InstanceContentTypeIds.StatLineTypeKey,
                StatLineContentType.ModTierIdIndex,
                StatLineContentType.ModTierIdField,
                InstanceContentTypeIds.ModTierTypeId,
                InstanceContentTypeIds.ModTierTypeKey);

            long statId = Number(row, StatLineContentType.StatIdIndex) ?? 0;
            if (!IsLive(candidate, EngineContentTypes.StatTypeId, statId))
            {
                findings.Add(new ContentFinding(
                    row.Type,
                    row.Id,
                    InstanceContentFindings.StatLineShape,
                    InstanceContentFindings.StatLineStat(row.Id, statId)));
            }

            long combine = Number(row, StatLineContentType.CombineIndex) ?? 0;
            if (combine is not (StatLineContentType.CombineFlat
                or StatLineContentType.CombineIncreased
                or StatLineContentType.CombineMore))
            {
                findings.Add(new ContentFinding(
                    row.Type,
                    row.Id,
                    InstanceContentFindings.StatLineShape,
                    InstanceContentFindings.StatLineCombine(row.Id, combine)));
            }

            long min = Number(row, StatLineContentType.MinIndex) ?? 0;
            long max = Number(row, StatLineContentType.MaxIndex) ?? 0;
            if (min > max)
            {
                findings.Add(new ContentFinding(
                    row.Type,
                    row.Id,
                    InstanceContentFindings.StatLineShape,
                    InstanceContentFindings.StatLineRange(row.Id, min, max)));
            }
        }
    }

    /// <summary>
    /// PUBLISH ONLY, <c>KEC0103</c>, and it is spec 8.9's checks 3 and 12 under one code because both are
    /// the same refusal from two sides. A tier row whose pair of mod id and ordinal MOVED is a reorder, and
    /// a pair that was live before and is not live now needs a remap rule to land on, because a stored
    /// affix entry can still name it.
    /// </summary>
    static void CheckTierHistory(
        IContentSnapshot candidate,
        IContentSnapshot previous,
        IReadOnlyList<RemapRule> rules,
        ICollection<ContentFinding> findings)
    {
        var covered = new HashSet<int>();
        foreach (RemapRule rule in rules)
        {
            if (rule.Type.Value == InstanceContentTypeIds.ModTierTypeId)
            {
                covered.Add(rule.FromId);
            }
        }

        foreach (ContentRow before in LiveRows(previous, InstanceContentTypeIds.ModTierTypeId))
        {
            long previousMod = Number(before, ModTierContentType.ModIdIndex) ?? 0;
            long previousOrdinal = Number(before, ModTierContentType.OrdinalIndex) ?? 0;

            if (!candidate.TryGetRow(Type(InstanceContentTypeIds.ModTierTypeId), before.Id, out ContentRow? now)
                || now.IsRetired)
            {
                if (!covered.Contains(before.Id))
                {
                    findings.Add(new ContentFinding(
                        before.Type,
                        before.Id,
                        InstanceContentFindings.TierOrdinalMoved,
                        InstanceContentFindings.OrdinalRemoved(
                            before.Id, previousMod, previousOrdinal, previous.VersionNumber)));
                }

                continue;
            }

            long modId = Number(now, ModTierContentType.ModIdIndex) ?? 0;
            long ordinal = Number(now, ModTierContentType.OrdinalIndex) ?? 0;
            if (modId == previousMod && ordinal == previousOrdinal)
            {
                continue;
            }

            findings.Add(new ContentFinding(
                now.Type,
                now.Id,
                InstanceContentFindings.TierOrdinalMoved,
                InstanceContentFindings.OrdinalReordered(now.Id, previousMod, previousOrdinal, modId, ordinal)));
        }
    }
}
