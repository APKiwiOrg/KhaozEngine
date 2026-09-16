using System.Text.Json;
using System.Text.Json.Serialization;

namespace KhaozEngine.Benchmarks.Items;

/// <summary>
/// One row per performance budget of the item-instances spec's section 16, plus the scale test its
/// test plan names. Every field is a MEASURED number: nothing here is copied from a derivation.
/// </summary>
public sealed record ItemsBenchmarkResult
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    public string Workload { get; init; } = "items-sqlite-v1";
    public string Provider { get; init; } = "sqlite";
    public string Machine { get; init; } = string.Empty;
    public string Framework { get; init; } = string.Empty;
    public string Configuration { get; init; } = "Release";
    public string Mode { get; init; } = "full";
    public int ProcessorCount { get; init; }
    public int Seed { get; init; }

    public int ContentModCount { get; init; }
    public int ContentBaseCount { get; init; }
    public int ContentTagCount { get; init; }
    public int ContentRarityCount { get; init; }
    public int ContentNameWordCount { get; init; }
    public int ContentBandCount { get; init; }
    public long ContentTagBandEntries { get; init; }
    public int ContentDistinctTagSignatures { get; init; }

    public int Budget1RarePayloadBytes { get; init; }
    public int Budget2RareSlotEntryBytes { get; init; }
    public int Budget3PageBytesCanonicalRares { get; init; }
    public int Budget3PageBytesGeneratedRares { get; init; }
    public double Budget3GeneratedEntryMeanBytes { get; init; }

    public int Budget4CoalescedCommitCount { get; init; }
    public int Budget4CoalescedOwnedBytes { get; init; }
    public int Budget4UncoalescedCommitCount { get; init; }
    public int Budget4UncoalescedOwnedBytes { get; init; }
    public int Budget4CraftEventBytes { get; init; }
    public string Budget4StoreStatus { get; init; } = string.Empty;

    public int Budget5Generations { get; init; }
    public double Budget5P50Microseconds { get; init; }
    public double Budget5P99Microseconds { get; init; }
    public double Budget5MeanMicroseconds { get; init; }
    public double Budget5AllocatedBytesPerGeneration { get; init; }
    public double Budget5ColdP50Microseconds { get; init; }
    public double Budget5ColdP99Microseconds { get; init; }
    public double Budget5MeanPoolSize { get; init; }
    public double Budget5MeanAffixCount { get; init; }
    public double Budget5PoolSuppressedEntries { get; init; }
    public int Budget5InvariantViolations { get; init; }
    public double Budget5WarmP50Microseconds { get; init; }
    public double Budget5WarmP99Microseconds { get; init; }
    public double Budget5WarmAllocatedBytesPerGeneration { get; init; }
    public double Budget5DenseP50Microseconds { get; init; }
    public double Budget5DenseP99Microseconds { get; init; }
    public double Budget5DenseMeanMicroseconds { get; init; }
    public double Budget5DenseMeanAffixCount { get; init; }

    public double Budget6Nanoseconds { get; init; }
    public double Budget6CachedNanoseconds { get; init; }
    public long Budget6AllocatedBytes { get; init; }
    public int Budget6LineCount { get; init; }
    public int Budget6WornItems { get; init; }
    public int Budget6AffixesPerItem { get; init; }

    public int Budget7ChunkCount { get; init; }
    public int Budget7GameMessageBytes { get; init; }
    public int Budget7WireBytes { get; init; }

    public int Budget8DeltaBytes { get; init; }
    public int Budget8MaximumChangedSlotsInOneFrame { get; init; }

    public double Budget9TableBuildMilliseconds { get; init; }
    public long Budget9TableResidentBytes { get; init; }
    public long Budget9TableTotalMemoryDeltaBytes { get; init; }
    public long Budget9TableSelfReportedBytes { get; init; }
    public long Budget9SuppressedEntries { get; init; }
    public int Budget9ConsistencyFailures { get; init; }

    public double Budget10LoadMilliseconds { get; init; }
    public long Budget10LoadAllocatedBytes { get; init; }
    public int Budget10RuleCount { get; init; }
    public int Budget10ReferenceIdsVisited { get; init; }
    public double Budget10LoadWithRewritesMilliseconds { get; init; }
    public long Budget10LoadWithRewritesAllocatedBytes { get; init; }

    public int Budget11PublicViewBytes { get; init; }
    public int Budget11ComponentBytes { get; init; }
    public int Budget11GroundInstances { get; init; }
    public double Budget11TickSeconds { get; init; }
    public double Budget11BytesPerViewerPerSecond { get; init; }

    public int Budget12Players { get; init; }
    public int Budget12PagesPerPlayer { get; init; }
    public long Budget12PageBytesSum { get; init; }
    public long Budget12PageResidentBytes { get; init; }
    public long Budget12AdmittedResidentBytes { get; init; }
    public long Budget12TotalResidentBytes { get; init; }
    public long Budget12TotalMemoryDeltaBytes { get; init; }
    public string Budget12AdmittedStatus { get; init; } = string.Empty;

    public int Budget13Players { get; init; }
    public int Budget13Seconds { get; init; }
    public double Budget13OfferedPerSecond { get; init; }
    public double Budget13AcceptedPerSecond { get; init; }
    public double Budget13CommittedPerSecond { get; init; }
    public double Budget13P50Milliseconds { get; init; }
    public double Budget13P99Milliseconds { get; init; }
    public double Budget13BackpressureRate { get; init; }
    public double Budget13BusyRate { get; init; }
    public double Budget13VersionConflictRate { get; init; }
    public long Budget13ReplayCount { get; init; }
    public long Budget13FailureCount { get; init; }
    public long Budget13DatabaseBytes { get; init; }
    public int Budget13CommitBytes { get; init; }

    public int ScaleInstances { get; init; }
    public int ScalePageCount { get; init; }
    public long ScalePageBytesSum { get; init; }
    public long ScaleResidentBytes { get; init; }
    public long ScaleTotalMemoryDeltaBytes { get; init; }
    public double ScaleBytesPerInstance { get; init; }

    public double TotalSeconds { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}
