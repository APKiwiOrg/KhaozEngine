using System.Collections.Generic;
using KhaozEngine.Catalog;
using static KhaozEngine.ItemInstances.InstanceContentChecks;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The mod family's half of spec 8.9: the parent references of <c>mod_tier</c>, <c>mod_tier_weight</c> and
/// <c>stat_line</c>, the tier's item level gate and ordinal, the stat line's shape, the mod group's count,
/// and the publish-only refusal of a tier ordinal that moved or vanished.
/// <para>
/// The field indices below are the positions each type's <c>CreateSchema</c> declares, which is what the
/// positional row walk already means by a field. They are named rather than inlined so a schema change
/// lands in one place per type.
/// </para>
/// </summary>
internal static class ModFamilyChecks
{
    const int ModGroupMaxPerItem = 0;

    const int ModTierModId = 0;
    const int ModTierOrdinal = 1;
    const int ModTierItemLevelMin = 2;
    const int ModTierItemLevelMax = 3;

    const int ModTierWeightModTierId = 0;

    const int StatLineModTierId = 0;
    const int StatLineStatId = 2;
    const int StatLineCombine = 3;
    const int StatLineMin = 4;
    const int StatLineMax = 5;

    internal static void Run(
        IContentSnapshot candidate,
        IContentSnapshot? previous,
        ICollection<ContentFinding> findings)
    {
        CheckModGroups(candidate, findings);
        CheckTiers(candidate, findings);
        CheckTierWeightParents(candidate, findings);
        CheckStatLines(candidate, findings);

        if (previous is not null)
        {
            CheckTierHistory(candidate, previous, findings);
        }
    }

    /// <summary><c>KEC0105</c>: a group permitting fewer than one of itself is a retire rather than a count.</summary>
    static void CheckModGroups(IContentSnapshot candidate, ICollection<ContentFinding> findings)
    {
        foreach (ContentRow row in LiveRows(candidate, InstanceContentTypeIds.ModGroupTypeId))
        {
            long maxPerItem = Number(row, ModGroupMaxPerItem) ?? 0;
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
                ModTierModId,
                ModTierContentType.ModIdField,
                InstanceContentTypeIds.ModTypeId,
                InstanceContentTypeIds.ModTypeKey);

            long min = Number(row, ModTierItemLevelMin) ?? 0;
            long max = Number(row, ModTierItemLevelMax) ?? 0;
            if (min > max)
            {
                findings.Add(new ContentFinding(
                    row.Type,
                    row.Id,
                    InstanceContentFindings.TierItemLevelRange,
                    InstanceContentFindings.TierLevelsInverted(row.Id, min, max)));
            }

            long ordinal = Number(row, ModTierOrdinal) ?? 0;
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

            long modId = Number(row, ModTierModId) ?? 0;
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
                ModTierWeightModTierId,
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
                StatLineModTierId,
                StatLineContentType.ModTierIdField,
                InstanceContentTypeIds.ModTierTypeId,
                InstanceContentTypeIds.ModTierTypeKey);

            long statId = Number(row, StatLineStatId) ?? 0;
            if (!IsLive(candidate, EngineContentTypes.StatTypeId, statId))
            {
                findings.Add(new ContentFinding(
                    row.Type,
                    row.Id,
                    InstanceContentFindings.StatLineShape,
                    InstanceContentFindings.StatLineStat(row.Id, statId)));
            }

            long combine = Number(row, StatLineCombine) ?? 0;
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

            long min = Number(row, StatLineMin) ?? 0;
            long max = Number(row, StatLineMax) ?? 0;
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
        ICollection<ContentFinding> findings)
    {
        var covered = new HashSet<int>();
        foreach (RemapRule rule in candidate.Rules)
        {
            if (rule.Type.Value == InstanceContentTypeIds.ModTierTypeId)
            {
                covered.Add(rule.FromId);
            }
        }

        foreach (ContentRow before in LiveRows(previous, InstanceContentTypeIds.ModTierTypeId))
        {
            long previousMod = Number(before, ModTierModId) ?? 0;
            long previousOrdinal = Number(before, ModTierOrdinal) ?? 0;

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

            long modId = Number(now, ModTierModId) ?? 0;
            long ordinal = Number(now, ModTierOrdinal) ?? 0;
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
