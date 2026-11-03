using KhaozEngine.Benchmarks.Items;

namespace KhaozEngine.Tests.Benchmarks;

/// <summary>One field inventory for engine budget fences and explicit benchmark acceptance.</summary>
internal static class ItemsBenchmarkFields
{
    /// <summary>The budgets whose measurement is only meaningful above zero. The rest are counters a clean run
    /// leaves at zero on purpose, which is why they are not in this list.</summary>
    internal static readonly string[] MeasuredBudgets =
    {
        nameof(ItemsBenchmarkResult.Budget1RarePayloadBytes),
        nameof(ItemsBenchmarkResult.Budget2RareSlotEntryBytes),
        nameof(ItemsBenchmarkResult.Budget3PageBytesCanonicalRares),
        nameof(ItemsBenchmarkResult.Budget3PageBytesGeneratedRares),
        nameof(ItemsBenchmarkResult.Budget3GeneratedEntryMeanBytes),
        nameof(ItemsBenchmarkResult.Budget4CoalescedCommitCount),
        nameof(ItemsBenchmarkResult.Budget4CoalescedOwnedBytes),
        nameof(ItemsBenchmarkResult.Budget4UncoalescedCommitCount),
        nameof(ItemsBenchmarkResult.Budget4UncoalescedOwnedBytes),
        nameof(ItemsBenchmarkResult.Budget4CraftEventBytes),
        nameof(ItemsBenchmarkResult.Budget5Generations),
        nameof(ItemsBenchmarkResult.Budget5P50Microseconds),
        nameof(ItemsBenchmarkResult.Budget5P99Microseconds),
        nameof(ItemsBenchmarkResult.Budget5MeanMicroseconds),
        nameof(ItemsBenchmarkResult.Budget5AllocatedBytesPerGeneration),
        nameof(ItemsBenchmarkResult.Budget5ColdP50Microseconds),
        nameof(ItemsBenchmarkResult.Budget5ColdP99Microseconds),
        nameof(ItemsBenchmarkResult.Budget5MeanPoolSize),
        nameof(ItemsBenchmarkResult.Budget5MeanAffixCount),
        nameof(ItemsBenchmarkResult.Budget5PoolSuppressedEntries),
        nameof(ItemsBenchmarkResult.Budget5WarmP50Microseconds),
        nameof(ItemsBenchmarkResult.Budget5WarmP99Microseconds),
        nameof(ItemsBenchmarkResult.Budget5WarmAllocatedBytesPerGeneration),
        nameof(ItemsBenchmarkResult.Budget5DenseP50Microseconds),
        nameof(ItemsBenchmarkResult.Budget5DenseP99Microseconds),
        nameof(ItemsBenchmarkResult.Budget5DenseMeanMicroseconds),
        nameof(ItemsBenchmarkResult.Budget5DenseMeanAffixCount),
        nameof(ItemsBenchmarkResult.Budget6Nanoseconds),
        nameof(ItemsBenchmarkResult.Budget6LineCount),
        nameof(ItemsBenchmarkResult.Budget6WornItems),
        nameof(ItemsBenchmarkResult.Budget7ChunkCount),
        nameof(ItemsBenchmarkResult.Budget7GameMessageBytes),
        nameof(ItemsBenchmarkResult.Budget7WireBytes),
        nameof(ItemsBenchmarkResult.Budget8DeltaBytes),
        nameof(ItemsBenchmarkResult.Budget8MaximumChangedSlotsInOneFrame),
        nameof(ItemsBenchmarkResult.Budget9TableBuildMilliseconds),
        nameof(ItemsBenchmarkResult.Budget9TableSelfReportedBytes),
        nameof(ItemsBenchmarkResult.Budget9SuppressedEntries),
        nameof(ItemsBenchmarkResult.Budget10LoadMilliseconds),
        nameof(ItemsBenchmarkResult.Budget10RuleCount),
        nameof(ItemsBenchmarkResult.Budget10ReferenceIdsVisited),
        nameof(ItemsBenchmarkResult.Budget11PublicViewBytes),
        nameof(ItemsBenchmarkResult.Budget11ComponentBytes),
        nameof(ItemsBenchmarkResult.Budget11BytesPerViewerPerSecond),
        nameof(ItemsBenchmarkResult.Budget12PageBytesSum),
        nameof(ItemsBenchmarkResult.Budget12PageResidentBytes),
        nameof(ItemsBenchmarkResult.Budget12AdmittedResidentBytes),
        nameof(ItemsBenchmarkResult.Budget12TotalResidentBytes),
        nameof(ItemsBenchmarkResult.Budget13OfferedPerSecond),
        nameof(ItemsBenchmarkResult.Budget13AcceptedPerSecond),
        nameof(ItemsBenchmarkResult.Budget13CommitBytes),
        nameof(ItemsBenchmarkResult.Budget13DatabaseBytes),
        nameof(ItemsBenchmarkResult.ScaleInstances),
        nameof(ItemsBenchmarkResult.ScalePageCount),
        nameof(ItemsBenchmarkResult.ScaleBytesPerInstance),
        nameof(ItemsBenchmarkResult.TotalSeconds),
    };
}
