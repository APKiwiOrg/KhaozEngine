using System.Collections.Generic;
using KhaozEngine.Catalog;
using static KhaozEngine.ItemInstances.InstanceContentChecks;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The three checks the weight family needs, <c>KEC0112</c>, <c>KEC0113</c> and <c>KEC0117</c>. The first
/// two close the Scope B half of
/// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/944">944</see>.
/// <para>
/// <b>A negative weight and a saturating sum are both SILENT without this.</b> A negative weight makes the
/// cumulative array a weighted draw searches non-monotonic, and the load-time clamp to zero that rescues
/// it changes the odds with nothing reported anywhere. A bucket summing past <see cref="int.MaxValue"/>
/// saturates the same way, which changes every probability in it.
/// </para>
/// <para>
/// <b>The sum is a <c>long</c> compared against the ceiling, never a checked add.</b> An overflow is a
/// finding the sweep reports and runs past, not an exception that takes the publish down where a finding
/// was expected.
/// </para>
/// <para>
/// <b>The three field indices arrive as ARGUMENTS</b>, one set per type, from each type's own constants.
/// All three happen to put the parent at 0, the tag at 1 and the weight at 2 today, and a private copy of
/// those numbers here would silently read a neighbouring value of the same kind the day one of them gains a
/// field.
/// </para>
/// <para>
/// <b>The BUCKET is the rows sharing one tag, which is a SUPERSET of every bucket a draw actually builds.</b>
/// A candidate table buckets by tag and kind and band, a rarity draw buckets by tag, and a name draw buckets
/// by tag and position. Every one of those is a subset of the rows sharing a tag, so a per-tag sum inside
/// the ceiling is inside it for each of them, and the check needs no knowledge of the runtime tables.
/// </para>
/// <para>
/// <b>A REPEATED (parent, tag) pair is <c>KEC0117</c>, and it is the silent one.</b> Two rows on one tier
/// and one tag are not summed and are not alternatives: the candidate tables record the second as a key
/// already spent and suppress its whole weight, and the per signature folds behind the rarity and the name
/// draws keep the FIRST row and drop the rest. Both halves agree with themselves, so nothing downstream
/// notices, and an author reading two rows and one combined weight is reading a number no draw will see.
/// The finding names the SECOND row, because the first is the one that survives.
/// </para>
/// </summary>
internal static class WeightBoundsChecks
{
    internal static void Run(IContentSnapshot candidate, ICollection<ContentFinding> findings)
    {
        CheckOne(
            candidate,
            findings,
            InstanceContentTypeIds.ModTierWeightTypeId,
            InstanceContentTypeIds.ModTierWeightTypeKey,
            ModTierWeightContentType.ModTierIdIndex,
            ModTierWeightContentType.TagIdIndex,
            ModTierWeightContentType.WeightIndex);
        CheckOne(
            candidate,
            findings,
            InstanceContentTypeIds.RarityWeightTypeId,
            InstanceContentTypeIds.RarityWeightTypeKey,
            RarityWeightContentType.RarityRuleIdIndex,
            RarityWeightContentType.TagIdIndex,
            RarityWeightContentType.WeightIndex);
        CheckOne(
            candidate,
            findings,
            InstanceContentTypeIds.RareNameWordWeightTypeId,
            InstanceContentTypeIds.RareNameWordWeightTypeKey,
            RareNameWordWeightContentType.RareNameWordIdIndex,
            RareNameWordWeightContentType.TagIdIndex,
            RareNameWordWeightContentType.WeightIndex);
    }

    static void CheckOne(
        IContentSnapshot candidate,
        ICollection<ContentFinding> findings,
        ushort typeId,
        string typeKey,
        int parentIndex,
        int tagIndex,
        int weightIndex)
    {
        var order = new List<long>();
        var sums = new Dictionary<long, long>();
        var firstRowOfPair = new Dictionary<(long Parent, long Tag), int>();

        foreach (ContentRow row in LiveRows(candidate, typeId))
        {
            long weight = Number(row, weightIndex) ?? 0;
            if (weight < 0)
            {
                findings.Add(new ContentFinding(
                    row.Type,
                    row.Id,
                    InstanceContentFindings.WeightBelowZero,
                    InstanceContentFindings.WeightNegative(typeKey, row.Id, weight)));
            }

            long tagId = Number(row, tagIndex) ?? 0;
            long parentId = Number(row, parentIndex) ?? 0;

            // Only a row that would REACH a table can be suppressed by one: the build and the folds both
            // drop a weight at or below zero outright, so a zero-weight twin costs an author nothing and is
            // not a repeat of anything.
            if (weight > 0 && !firstRowOfPair.TryAdd((parentId, tagId), row.Id))
            {
                findings.Add(new ContentFinding(
                    row.Type,
                    row.Id,
                    InstanceContentFindings.WeightRowRepeated,
                    InstanceContentFindings.WeightRepeat(typeKey, row.Id, parentId, tagId, firstRowOfPair[(parentId, tagId)])));
            }

            if (!sums.TryGetValue(tagId, out long running))
            {
                order.Add(tagId);
            }

            // A negative weight is counted as the zero the load-time clamp turns it into, so one defect
            // does not manufacture the other.
            sums[tagId] = running + (weight > 0 ? weight : 0);
        }

        // The report walks the tags in first-seen row order rather than the dictionary's, so two sweeps of
        // one candidate agree finding for finding.
        foreach (long tagId in order)
        {
            long sum = sums[tagId];
            if (sum <= int.MaxValue)
            {
                continue;
            }

            findings.Add(new ContentFinding(
                Type(typeId),
                0,
                InstanceContentFindings.WeightBucketOverflow,
                InstanceContentFindings.WeightOverflow(typeKey, tagId, sum)));
        }
    }
}
