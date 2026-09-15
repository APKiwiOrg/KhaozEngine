using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>
/// Every number one <c>--catalog</c> run produced, one group per budget of spec section 14, plus the
/// machine, framework, configuration and seed that make a baseline comparable. A phase that did not run
/// leaves its group null rather than reporting a zero.
/// </summary>
public sealed record CatalogBenchmarkResult
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    public string Mode { get; init; } = "catalog";
    public string Machine { get; init; } = string.Empty;
    public string OperatingSystem { get; init; } = string.Empty;
    public string Framework { get; init; } = string.Empty;
    public string Configuration { get; init; } = "Release";
    public int ProcessorCount { get; init; }
    public string Phases { get; init; } = string.Empty;

    public int Seed { get; init; }
    public int Definitions { get; init; }
    public int Types { get; init; }
    public int ChunkSlots { get; init; }
    public int Languages { get; init; }
    public int EditCount { get; init; }
    public long LinkBitsPerSecond { get; init; }
    public int FetchConcurrency { get; init; }

    // P1 and P2: pack size.
    public long? ServerPackStoredBytes { get; init; }
    public long? ClientPackStoredBytes { get; init; }
    public long? ServerPackUncompressedBytes { get; init; }
    public long? ClientPackUncompressedBytes { get; init; }
    public int? ServerManifestBytes { get; init; }
    public int? ClientManifestBytes { get; init; }
    public long? TextStoredBytes { get; init; }
    public long? TextUncompressedBytes { get; init; }
    public int? TextChunkCount { get; init; }
    public int? TextEntryCount { get; init; }
    public int? ChunkCount { get; init; }
    public int? ItemChunkCount { get; init; }
    public long? TotalRows { get; init; }
    public double? MeanItemRowBytes { get; init; }
    public long? ContentKeyBodyBytes { get; init; }
    public int? LargestRowBytes { get; init; }
    public int? LargestChunkUncompressedBytes { get; init; }
    public double? PublishFullMs { get; init; }

    // P3: server load time and memory.
    public double? LoadTotalMs { get; init; }
    public double? LoadManifestMs { get; init; }
    public double? LoadFetchMs { get; init; }
    public double? LoadVerifyMs { get; init; }
    public double? LoadDecodeMs { get; init; }
    public double? LoadIndexMs { get; init; }
    public double? LoadValidateMs { get; init; }
    public long? RuntimeHeapBytes { get; init; }
    public long? RuntimeAllocatedBytes { get; init; }
    public long? RuntimeApproximateBytes { get; init; }

    // P4 and P4b: client cold start over the shaped link.
    public double? ColdStartMs { get; init; }
    public double? ColdStartLocalWorkMs { get; init; }
    public double? ColdStartManifestMs { get; init; }
    public double? ColdStartVerifyMs { get; init; }
    public double? ColdStartTransferFloorMs { get; init; }
    public long? ColdStartBytes { get; init; }
    public int? ColdStartChunks { get; init; }
    public int? ColdStartRetries { get; init; }
    public int? ColdStartFailures { get; init; }

    // P5 and P6: the one-item edit.
    public double? EditPublishMs { get; init; }
    public double? EditValidateMs { get; init; }
    public double? EditEncodeMs { get; init; }
    public double? EditWriteMs { get; init; }
    public double? EditManifestMs { get; init; }
    public int? EditAffectedChunks { get; init; }
    public long? EditChunkBytes { get; init; }
    public int? EditClientManifestBytes { get; init; }
    public long? EditClientDownloadBytes { get; init; }
    public int? ComparisonChunkSlots { get; init; }
    public long? ComparisonEditChunkBytes { get; init; }
    public int? ComparisonEditClientManifestBytes { get; init; }
    public long? ComparisonEditClientDownloadBytes { get; init; }

    // P7: lookup by id.
    public double? LookupNanoseconds { get; init; }
    public long? LookupAllocatedBytes { get; init; }
    public double? TypedItemLookupNanoseconds { get; init; }
    public long? TypedItemLookupAllocatedBytes { get; init; }

    // P8: the validator sweep.
    public double? ValidatorSweepMs { get; init; }
    public double? ValidatorStructureMs { get; init; }
    public double? ValidatorSchemaMs { get; init; }
    public double? ValidatorReferenceMs { get; init; }
    public double? ValidatorCodecMs { get; init; }
    public double? ValidatorRuleMs { get; init; }
    public long? ValidatorRowsSwept { get; init; }
    public int? ValidatorFindings { get; init; }

    // P9: the weighted loot draw.
    public double? LootDrawNanoseconds { get; init; }
    public long? LootDrawAllocatedBytes { get; init; }
    public int? LootDrawTableEntries { get; init; }

    // P10: the text chunk decode.
    public double? TextDecodeMs { get; init; }
    public long? TextDecodeHeapBytes { get; init; }
    public int? TextDecodeEntryCount { get; init; }
    public long? TextCatalogApproximateBytes { get; init; }
    public double? TextGetCachedNanoseconds { get; init; }
    public long? TextGetCachedAllocatedBytes { get; init; }
    public double? TextGetUncachedNanoseconds { get; init; }
    public long? TextGetUncachedAllocatedBytes { get; init; }
    public int? TextGetProbeKeys { get; init; }

    // P11: the composed cold boot.
    public double? ComposeProcessStartMs { get; init; }
    public double? ComposeBootMs { get; init; }
    public bool ComposePublishedThisRun { get; init; }
    public double? ComposeP3Ms { get; init; }
    public double? ComposeP10Ms { get; init; }
    public double? ComposeLoadIndexMs { get; init; }
    public long? ComposeLoadIndexBytes { get; init; }
    public long? ComposeHeapBytes { get; init; }
    public Dictionary<string, double>? ComposeAttribution { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}
