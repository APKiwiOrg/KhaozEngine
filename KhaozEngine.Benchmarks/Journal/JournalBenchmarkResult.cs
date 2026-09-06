using System.Text.Json;
using System.Text.Json.Serialization;

namespace KhaozEngine.Benchmarks.Journal;

public sealed record JournalBenchmarkResult
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    public string Mode { get; init; } = "benchmark";
    public string Provider { get; init; } = "sqlite";
    public string Machine { get; init; } = string.Empty;
    public string Framework { get; init; } = string.Empty;
    public int ProcessorCount { get; init; }
    public string Workload { get; init; } = "mmo-mixed-v1";
    public int Seed { get; init; }
    public int Players { get; init; }
    public int Workers { get; init; }
    public int PayloadBytes { get; init; }
    public int OperationsRequested { get; init; }
    public int OperationsCompleted { get; init; }
    public long MutationSubmissions { get; init; }
    public long Applied { get; init; }
    public long Replayed { get; init; }
    public double ThroughputPerSecond { get; init; }
    public double P50Milliseconds { get; init; }
    public double P95Milliseconds { get; init; }
    public double P99Milliseconds { get; init; }
    public double ReplayRate { get; init; }
    public double RetryRate { get; init; }
    public double BusyRate { get; init; }
    public double BackpressureRate { get; init; }
    public long? DatabaseBytes { get; init; }
    public long EventTailLength { get; init; }
    public long ProjectionBytes { get; init; }
    public long CompactionLagVersions { get; init; }
    public long SerializationCount { get; init; }
    public long JournalWriteCount { get; init; }
    public int PeakReplayCandidates { get; init; }
    public long PrunedEventCount { get; init; }
    public double AllocationBytesPerOperation { get; init; }
    public long ChecksumFailures { get; init; }
    public long DuplicateEffectFailures { get; init; }
    public long SequenceFailures { get; init; }
    public long PartialCommitFailures { get; init; }

    [JsonIgnore]
    public bool HasIntegrityFailures => ChecksumFailures != 0
        || DuplicateEffectFailures != 0
        || SequenceFailures != 0
        || PartialCommitFailures != 0;

    [JsonIgnore]
    public int ProcessExitCode => HasIntegrityFailures ? 2 : 0;

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}
