using System;
using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// Everything one content version contributes to a roll, built ONCE per snapshot and immutable afterwards:
/// the candidate tables of spec 9.2, the content fold beside them, and the run ceiling a generator sizes its
/// exclusion lists from.
/// <para>
/// <b>This type exists so spec 9.1's replay shape is true rather than nearly true.</b> A replay harness
/// builds a SECOND <see cref="ItemGenerator"/> over the same tables, and 9.1 costs that at "one object and
/// no table build". The candidate tables were already shared, but the content fold and the run ceiling were
/// built PER GENERATOR, and the ceiling walks every entry of every bucket, so at the benchmark's content
/// scale a second generator cost tens of milliseconds re-deriving what the first already had. Both are
/// functions of the snapshot and of nothing else, so both belong here.
/// </para>
/// <para>
/// A generator built over one of these allocates its own scratch and reads no content at all, which is what
/// <c>ItemGeneratorTests.A_SECOND_generator_over_one_table_set_costs_one_object_rather_than_a_second_fold</c>
/// pins through a counting snapshot.
/// </para>
/// </summary>
public sealed class GenerationTables
{
    /// <summary>
    /// The most excluded runs one tag table may ever hold, whatever the pack says. Each run list costs
    /// three ints per run per (kind, tag position) slot in every generator, so a pathological pack could
    /// size the scratch in gigabytes without it.
    /// <para>
    /// <b>It is a REFUSAL rather than a clamp.</b> Clamping the worst case down to it and letting the seat
    /// drop what does not fit is the fail-open shape: a run that is not seated is a mod that stays LIVE, so
    /// an item can carry two mods of a group capped at one and nothing anywhere reports it.
    /// </para>
    /// </summary>
    internal const int RunCeiling = 4_096;

    GenerationTables(ModCandidateTables candidates, IContentSnapshot snapshot)
    {
        Candidates = candidates;
        ContentVersion = snapshot.VersionNumber;
        Content = GenerationContentTables.Build(snapshot, candidates);
        RunsPerSlot = MeasureRunsPerSlot(candidates, Content);
    }

    /// <summary>The candidate tables of spec 9.2, built at boot and immutable for the process.</summary>
    public ModCandidateTables Candidates { get; }

    /// <summary>The version these were folded from, which every roll's result names.</summary>
    public int ContentVersion { get; }

    /// <summary>The rarity rules, weights, name words, base durability and sockets, and unique templates.</summary>
    internal GenerationContentTables Content { get; }

    /// <summary>How many excluded runs one tag table may need to hold, which sizes a generator's scratch.</summary>
    internal int RunsPerSlot { get; }

    /// <summary>Folds one version once. Every generator over this version then costs one object.</summary>
    /// <param name="candidates">The candidate tables built from the same snapshot.</param>
    /// <param name="snapshot">The loaded version, read here and never again by a roll.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static GenerationTables Build(ModCandidateTables candidates, IContentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(snapshot);
        return new GenerationTables(candidates, snapshot);
    }

    /// <summary>
    /// How many excluded runs one tag table may need to hold, as the TRUE worst case rather than a number
    /// that happens to fit. A placement seats one run for its own mod and one more for every member of its
    /// group, so the affix count times one plus the widest group bounds it, and the widest bucket bounds it
    /// again because runs are disjoint by mod id and a run holds at least one entry.
    /// <para>
    /// A worst case past <see cref="RunCeiling"/> is REFUSED here rather than clamped, because a clamp puts
    /// the failure at the seat, where the only thing left to do is drop a run and leave a mod live.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The worst case is past the ceiling.</exception>
    static int MeasureRunsPerSlot(ModCandidateTables tables, GenerationContentTables content)
    {
        int widestGroup = 1;
        int widestBucket = 1;
        for (int bucket = 0; bucket < tables.BucketCount; bucket++)
        {
            widestBucket = Math.Max(widestBucket, tables.BucketLength(bucket));
            ReadOnlySpan<int> packed = tables.BucketPacked(bucket);
            for (int entry = 0; entry < packed.Length; entry++)
            {
                int group = tables.GroupOf(ModCandidateTables.ModIdOf(packed[entry]));
                if (group != 0)
                {
                    widestGroup = Math.Max(widestGroup, tables.GroupMembers(group).Length);
                }
            }
        }

        long affixes = Math.Max(content.MaxAffixCount, 1);
        long needed = Math.Min((affixes * widestGroup) + affixes, widestBucket);
        if (needed > RunCeiling)
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"A roll on this version could seat up to {needed} excluded runs in one tag table, over the ceiling of {RunCeiling}: {affixes} affixes times a widest mod group of {widestGroup} members, plus one run each, inside a widest bucket of {widestBucket} entries. Each run costs three ints per slot in every generator, and clamping the scratch down instead would leave a mod LIVE the moment the list filled."));
        }

        return (int)Math.Max(needed, 1);
    }
}
