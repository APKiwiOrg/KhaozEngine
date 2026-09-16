using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using KhaozEngine.Primitives;

namespace KhaozEngine.Benchmarks.Items;

/// <summary>
/// The <c>--items</c> mode: one measurement per performance budget of the item-instances spec's
/// section 16, plus the scale test its test plan names, against synthetic content at the owner's scale
/// and the REAL journal for anything that commits.
/// <para>
/// <b>Generation and the candidate tables are the SHIPPED ones.</b> The synthetic set is published through
/// <see cref="ContentSnapshotBuilder"/> into real rows, budget 9 times
/// <see cref="ModCandidateTables.Build(IContentSnapshot)"/> over them, and every roll in the run goes
/// through <see cref="ItemGenerator"/>, so budgets 5 and 9 describe what the engine ships rather than what
/// a spike proved could exist. The byte-format phases beside them still measure the mode's own clean
/// implementations of the two design documents, which is what they were always for.
/// </para>
/// </summary>
public static class ItemsBenchmarkRunner
{
    internal const int ContentVersion = 7;

    public static async Task<ItemsBenchmarkResult> RunAsync(
        ItemsBenchmarkConfig config,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(output);
        config.Validate();
        CultureInfo culture = CultureInfo.InvariantCulture;
        var total = Stopwatch.StartNew();

        output.WriteLine("KhaozEngine item-instances benchmark - the thirteen budgets of section 16");
        output.WriteLine(string.Create(culture, $"cores={Environment.ProcessorCount}  framework={RuntimeInformation.FrameworkDescription}  mode={(config.Quick ? "quick" : "full")}  seed={config.Seed}"));
        output.WriteLine();

        SyntheticContent content = SyntheticContent.Build(config.Seed, config.ModCount, config.BaseCount);
        output.WriteLine(string.Create(culture, $"content: {content.ModCount} mods, {content.BaseCount} bases, {content.TagCount} tags, {content.RarityCount} rarities, {content.NameWordCount} rare name words, {content.DistinctTagSignatures} distinct tag signatures"));

        var rowTimer = Stopwatch.StartNew();
        ContentTypeRegistry registry = SyntheticContentRows.Registry();
        ContentSnapshot snapshot = SyntheticContentRows.Snapshot(registry, content, ContentVersion);
        rowTimer.Stop();
        output.WriteLine(string.Create(culture, $"content rows: {RowCount(snapshot):N0} rows over {snapshot.Types.Count} types, published into version {snapshot.VersionNumber} in {rowTimer.Elapsed.TotalMilliseconds:F1} ms"));

        long tableTotalMemoryBefore = ResidentMemory.ReadTotalMemory();
        long tableMemoryBefore = ResidentMemory.Read();
        var tableTimer = Stopwatch.StartNew();
        ModCandidateTables tables = ModCandidateTables.Build(snapshot);
        tableTimer.Stop();
        long tableMemoryAfter = ResidentMemory.Read();
        long tableTotalMemoryAfter = ResidentMemory.ReadTotalMemory();
        long tableResident = Math.Max(0, tableMemoryAfter - tableMemoryBefore);
        output.WriteLine(string.Create(culture, $"generator tables: {tables.TagCount} tags x {tables.KindCount} kinds x {tables.BandCount} bands, {tables.TableEntries:N0} entries, {tables.SuppressedEntries:N0} suppressed, built in {tableTimer.Elapsed.TotalMilliseconds:F1} ms"));
        output.WriteLine();

        var random = new SeededRandomSource(unchecked((ulong)config.Seed));
        var allocator = new InstanceIdAllocator(new BenchmarkInstanceIdStore(), 0);
        var generator = new ItemGenerator(GenerationTables.Build(tables, snapshot), random, allocator);

        CodecMeasurements codec = ItemsCodecMeasurements.Measure(generator, content, random, ContentVersion);
        int violations = ItemsWorkMeasurements.ValidateInvariants(
            generator, tables, content, random, config.Quick ? 20_000 : 100_000);
        GenerationMeasurement generation = ItemsWorkMeasurements.MeasureGeneration(
            generator, tables, content, random, config.Generations, config.HotBaseCount, 60, config.Seed);
        GenerationMeasurement warm = ItemsWorkMeasurements.MeasureGeneration(
            generator, tables, content, random, Math.Min(config.Generations, 200_000), 64, 4, config.Seed + 2);
        GenerationMeasurement dense = ItemsWorkMeasurements.MeasureGeneration(
            generator, tables, content, random, Math.Min(config.Generations, 200_000), config.HotBaseCount, 60,
            config.Seed + 3, content.RarityCount);
        GenerationMeasurement cold = ItemsWorkMeasurements.MeasureColdGeneration(
            generator, content, random, Math.Min(config.Generations / 10, 100_000), config.Seed);
        StatMeasurement stat = ItemsWorkMeasurements.MeasureStatEvaluation(snapshot, random);

        byte[][] bankPages = BuildBank(generator, content, random, config.BankPagesPerPlayer);
        var noOpRules = BuildRules(content, 200, hits: 0, config.Seed);
        var hitRules = BuildRules(content, 200, hits: 20, config.Seed);
        LoadMeasurement load = ItemsWorkMeasurements.MeasureLoad(bankPages, new RemapRuleSet(noOpRules, ContentVersion), content, 400);
        LoadMeasurement loadWithHits = ItemsWorkMeasurements.MeasureLoad(bankPages, new RemapRuleSet(hitRules, ContentVersion), content, 400);

        using var scope = ItemsJournalScope.Create(config.DatabasePath);
        CraftBatchMeasurement craft = await ItemsJournalMeasurements.MeasureCraftBatchAsync(
            scope, config.Crafts, config.Seed, ContentVersion, cancellationToken).ConfigureAwait(false);

        GeneratedRares pool = GeneratedRares.Build(generator, content, random, 10_000);
        ResidentMeasurement resident = ItemsMemoryMeasurements.MeasureResident(
            pool, allocator, config.Players, config.BankPagesPerPlayer, ContentVersion);
        ScaleMeasurement scale = ItemsMemoryMeasurements.MeasureScale(
            pool, allocator, config.ScaleInstances, ContentVersion);
        GC.KeepAlive(pool);

        byte[] throughputPage = CanonicalRare.BuildPage(0, ContentVersion);
        ThroughputMeasurement throughput = await ItemsJournalMeasurements.MeasureThroughputAsync(
            scope, config.Players, config.ThroughputSeconds, config.Seed, throughputPage, ContentVersion, cancellationToken).ConfigureAwait(false);
        long databaseBytes = scope.DatabaseBytes();
        total.Stop();

        var result = new ItemsBenchmarkResult
        {
            Mode = config.Quick ? "quick" : "full",
            Machine = Environment.MachineName,
            Framework = RuntimeInformation.FrameworkDescription,
            ProcessorCount = Environment.ProcessorCount,
            Seed = config.Seed,
            ContentModCount = content.ModCount,
            ContentBaseCount = content.BaseCount,
            ContentTagCount = content.TagCount,
            ContentRarityCount = content.RarityCount,
            ContentNameWordCount = content.NameWordCount,
            ContentBandCount = tables.BandCount,
            ContentTagBandEntries = tables.TableEntries,
            ContentDistinctTagSignatures = content.DistinctTagSignatures,
            Budget1RarePayloadBytes = codec.RarePayloadBytes,
            Budget2RareSlotEntryBytes = codec.RareSlotEntryBytes,
            Budget3PageBytesCanonicalRares = codec.CanonicalPageBytes,
            Budget3PageBytesGeneratedRares = codec.GeneratedPageBytes,
            Budget3GeneratedEntryMeanBytes = codec.GeneratedEntryMeanBytes,
            Budget4CoalescedCommitCount = craft.CoalescedCommits,
            Budget4CoalescedOwnedBytes = craft.CoalescedOwnedBytes,
            Budget4UncoalescedCommitCount = craft.UncoalescedCommits,
            Budget4UncoalescedOwnedBytes = craft.UncoalescedOwnedBytes,
            Budget4CraftEventBytes = craft.CraftEventBytes,
            Budget4StoreStatus = craft.StoreStatus,
            Budget5Generations = config.Generations,
            Budget5P50Microseconds = generation.P50Microseconds,
            Budget5P99Microseconds = generation.P99Microseconds,
            Budget5MeanMicroseconds = generation.MeanMicroseconds,
            Budget5AllocatedBytesPerGeneration = generation.AllocatedBytesPerGeneration,
            Budget5ColdP50Microseconds = cold.P50Microseconds,
            Budget5ColdP99Microseconds = cold.P99Microseconds,
            Budget5MeanPoolSize = generation.MeanPoolSize,
            Budget5MeanAffixCount = generation.MeanAffixCount,
            Budget5PoolSuppressedEntries = generation.PoolSuppressedEntries,
            Budget5InvariantViolations = violations,
            Budget5WarmP50Microseconds = warm.P50Microseconds,
            Budget5WarmP99Microseconds = warm.P99Microseconds,
            Budget5WarmAllocatedBytesPerGeneration = warm.AllocatedBytesPerGeneration,
            Budget5DenseP50Microseconds = dense.P50Microseconds,
            Budget5DenseP99Microseconds = dense.P99Microseconds,
            Budget5DenseMeanMicroseconds = dense.MeanMicroseconds,
            Budget5DenseMeanAffixCount = dense.MeanAffixCount,
            Budget6Nanoseconds = stat.Nanoseconds,
            Budget6CachedNanoseconds = stat.CachedNanoseconds,
            Budget6AllocatedBytes = stat.AllocatedBytes,
            Budget6LineCount = stat.LineCount,
            Budget6WornItems = ItemsWorkMeasurements.WornItems,
            Budget6AffixesPerItem = ItemsWorkMeasurements.AffixesPerItem,
            Budget7ChunkCount = codec.ChunkCount,
            Budget7GameMessageBytes = codec.FragmentGameMessageBytes,
            Budget7WireBytes = codec.FragmentWireBytes,
            Budget8DeltaBytes = codec.DeltaBytes,
            Budget8MaximumChangedSlotsInOneFrame = codec.MaximumChangedSlotsInOneFrame,
            Budget9TableBuildMilliseconds = tableTimer.Elapsed.TotalMilliseconds,
            Budget9TableResidentBytes = tableResident,
            Budget9TableTotalMemoryDeltaBytes = tableTotalMemoryAfter - tableTotalMemoryBefore,
            Budget9TableSelfReportedBytes = tables.ResidentBytes,
            Budget9SuppressedEntries = tables.SuppressedEntries,
            Budget9ConsistencyFailures = tables.ConsistencyFailures,
            Budget10LoadMilliseconds = load.Milliseconds,
            Budget10LoadAllocatedBytes = load.AllocatedBytes,
            Budget10RuleCount = noOpRules.Count,
            Budget10ReferenceIdsVisited = load.ReferenceIdsVisited,
            Budget10LoadWithRewritesMilliseconds = loadWithHits.Milliseconds,
            Budget10LoadWithRewritesAllocatedBytes = loadWithHits.AllocatedBytes,
            Budget11PublicViewBytes = codec.PublicViewBytes,
            Budget11ComponentBytes = codec.GroundComponentBytes,
            Budget11GroundInstances = ItemsCodecMeasurements.GroundInstancesInInterest,
            Budget11TickSeconds = ItemsCodecMeasurements.TileTickSeconds,
            Budget11BytesPerViewerPerSecond = codec.GroundBytesPerViewerPerSecond,
            Budget12Players = config.Players,
            Budget12PagesPerPlayer = config.BankPagesPerPlayer,
            Budget12PageBytesSum = resident.PageBytesSum,
            Budget12PageResidentBytes = resident.PageResidentBytes,
            Budget12AdmittedResidentBytes = resident.AdmittedResidentBytes,
            Budget12TotalResidentBytes = resident.TotalResidentBytes,
            Budget12TotalMemoryDeltaBytes = resident.TotalMemoryDeltaBytes,
            Budget12AdmittedStatus = resident.AdmittedStatus,
            Budget13Players = config.Players,
            Budget13Seconds = config.ThroughputSeconds,
            Budget13OfferedPerSecond = throughput.OfferedPerSecond,
            Budget13AcceptedPerSecond = throughput.AcceptedPerSecond,
            Budget13CommittedPerSecond = throughput.CommittedPerSecond,
            Budget13P50Milliseconds = throughput.P50Milliseconds,
            Budget13P99Milliseconds = throughput.P99Milliseconds,
            Budget13BackpressureRate = throughput.BackpressureRate,
            Budget13BusyRate = throughput.BusyRate,
            Budget13VersionConflictRate = throughput.VersionConflictRate,
            Budget13ReplayCount = throughput.ReplayCount,
            Budget13FailureCount = throughput.FailureCount,
            Budget13DatabaseBytes = databaseBytes,
            Budget13CommitBytes = throughput.CommitBytes,
            ScaleInstances = scale.Instances,
            ScalePageCount = scale.PageCount,
            ScalePageBytesSum = scale.PageBytesSum,
            ScaleResidentBytes = scale.ResidentBytes,
            ScaleTotalMemoryDeltaBytes = scale.TotalMemoryDeltaBytes,
            ScaleBytesPerInstance = scale.BytesPerInstance,
            TotalSeconds = total.Elapsed.TotalSeconds,
        };

        ItemsBenchmarkReport.Write(result, output);
        return result;
    }

    /// <summary>Every row the published snapshot carries, which is what the table build reads.</summary>
    private static long RowCount(ContentSnapshot snapshot)
    {
        long rows = 0;
        foreach (ContentTypeId type in snapshot.Types) rows += snapshot.Rows(type).Count;
        return rows;
    }

    private static byte[][] BuildBank(ItemGenerator generator, SyntheticContent content, IRandomSource random, int pages)
    {
        var bank = new byte[pages][];
        for (int page = 0; page < pages; page++)
            bank[page] = ItemsCodecMeasurements.BuildGeneratedPage(generator, content, random, page, ContentVersion).Page;
        return bank;
    }

    /// <summary>
    /// Two hundred rules, the shape budget 10 is derived against: every rule a no-op on a page holding
    /// no reference, plus an optional tail that DOES hit so the rewrite path has a number too.
    /// </summary>
    private static List<RemapRule> BuildRules(SyntheticContent content, int count, int hits, int seed)
    {
        var rules = new List<RemapRule>(count);
        var rng = new KhaozEngine.Primitives.DeterministicRng(unchecked((ulong)seed) + 77);
        for (int index = 0; index < count - hits; index++)
            rules.Add(new RemapRule(
                index + 1,
                ContentVersion + 1,
                index % 2 == 0 ? ContentTypeIds.Mod : ContentTypeIds.Item,
                1,
                content.ModCount + content.BaseCount + index + 1,
                content.ModCount + content.BaseCount + index + 2));
        for (int index = 0; index < hits; index++)
        {
            int from = rng.Next(1, content.ModCount + 1);
            rules.Add(new RemapRule(count - hits + index + 1, ContentVersion + 1, ContentTypeIds.Mod, 1, from, content.ModCount + index + 1));
        }

        return rules;
    }
}
