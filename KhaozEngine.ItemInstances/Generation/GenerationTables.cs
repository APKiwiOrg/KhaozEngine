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
    /// The most excluded runs one tag table may ever hold, whatever the pack says. A placement seats one run
    /// per table and its group seats one more per member, so a pathological pack could size the scratch in
    /// gigabytes without it.
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
    /// How many excluded runs one tag table may need to hold. A placement seats one run and its group seats
    /// one per member, so the affix count times the widest group bounds it, and the bucket length bounds it
    /// again because runs are disjoint by mod id.
    /// </summary>
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

        long needed = (long)Math.Max(content.MaxAffixCount, 1) * (1 + widestGroup);
        return (int)Math.Clamp(Math.Min(needed, widestBucket), 1, RunCeiling);
    }
}
