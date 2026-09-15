using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>
/// Collects each phase's numbers and turns them into the result record. A phase that did not run leaves
/// its fields null, so a baseline never claims a zero it did not measure.
/// </summary>
public sealed class CatalogResultBuilder
{
    private readonly CatalogBenchmarkConfig _config;
    private readonly PublishOutcome _publish;

    public CatalogResultBuilder(CatalogBenchmarkConfig config, PublishOutcome publish)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(publish);
        _config = config;
        _publish = publish;
    }

    public ContentLoadTiming? Timing { get; set; }
    public ValidationReport? Report { get; set; }
    public ContentRuntime? Runtime { get; set; }
    public long? RuntimeHeapBytes { get; set; }
    public long? RuntimeAllocatedBytes { get; set; }
    public long? RuntimeApproximateBytes { get; set; }
    public MicroResult? Lookup { get; set; }
    public MicroResult? TypedLookup { get; set; }
    public MicroResult? LootDraw { get; set; }
    public int? LootDrawEntries { get; set; }
    public EditOutcome? Edit { get; set; }
    public EditOutcome? ComparisonEdit { get; set; }
    public FetchOutcome? Fetch { get; set; }
    public double? TextDecodeMs { get; set; }
    public long? TextDecodeHeapBytes { get; set; }
    public int? TextDecodeEntryCount { get; set; }
    public double? ComposeBootMs { get; set; }
    public double? ComposeProcessStartMs { get; set; }
    public double? ComposeP3Ms { get; set; }
    public double? ComposeP10Ms { get; set; }
    public double? ComposeLoadIndexMs { get; set; }
    public long? ComposeLoadIndexBytes { get; set; }
    public long? ComposeHeapBytes { get; set; }
    public bool ComposePublishedThisRun { get; set; }

    public CatalogBenchmarkResult Build()
    {
        int itemChunks = 0;
        int textEntries = 0;
        foreach (KeyValuePair<(ushort TypeId, int ChunkIndex), ChunkRecord> pair in _publish.Chunks)
        {
            if (pair.Key.TypeId == ContentTypes.Item) itemChunks++;
        }
        foreach (TextChunkRecord text in _publish.TextChunks) textEntries += text.EntryCount;

        // A rehydrated pack still has true STORED sizes, because those are a property of the bytes on disk.
        // What it has no honest answer for is anything the encoder counted while producing them, so those
        // stay null rather than reporting the zero they were never given.
        bool encoded = !_publish.Rehydrated;

        Dictionary<string, double>? attribution = ComposeBootMs is null ? null : new Dictionary<string, double>
        {
            ["p3LoadMs"] = ComposeP3Ms ?? 0,
            ["p10TextMs"] = ComposeP10Ms ?? 0,
            ["standInLoadIndexMs"] = ComposeLoadIndexMs ?? 0,
        };

        return new CatalogBenchmarkResult
        {
            Machine = Environment.MachineName,
            OperatingSystem = RuntimeInformation.OSDescription + " " + RuntimeInformation.OSArchitecture,
            Framework = RuntimeInformation.FrameworkDescription,
            Configuration = IsDebug() ? "Debug" : "Release",
            ProcessorCount = Environment.ProcessorCount,
            Phases = _config.Phases.ToString(),
            Seed = _config.Seed,
            Definitions = _config.Definitions,
            Types = _config.Types,
            ChunkSlots = _config.ChunkSlots,
            Languages = _config.Languages,
            EditCount = _config.EditCount,
            LinkBitsPerSecond = _config.LinkBitsPerSecond,
            FetchConcurrency = _config.FetchConcurrency,

            ServerPackStoredBytes = _publish.ServerStoredBytes,
            ClientPackStoredBytes = _publish.ClientStoredBytes,
            ServerPackUncompressedBytes = _publish.ServerUncompressedBytes,
            ClientPackUncompressedBytes = _publish.ClientUncompressedBytes,
            ServerManifestBytes = _publish.ServerManifestBytes,
            ClientManifestBytes = _publish.ClientManifestBytes,
            TextStoredBytes = _publish.TextStoredBytes,
            TextUncompressedBytes = encoded ? _publish.TextUncompressedBytes : null,
            TextChunkCount = _publish.TextChunks.Count,
            TextEntryCount = encoded ? textEntries : null,
            ChunkCount = _publish.Chunks.Count,
            ItemChunkCount = itemChunks,
            TotalRows = encoded ? _publish.TotalRows : null,
            MeanItemRowBytes = encoded ? _publish.MeanItemRowBytes : null,
            ContentKeyBodyBytes = encoded ? _publish.KeyBodyBytes : null,
            LargestRowBytes = encoded ? _publish.LargestRowBytes : null,
            LargestChunkUncompressedBytes = encoded ? _publish.LargestChunkUncompressedBytes : null,
            PublishFullMs = encoded ? _publish.TotalMs : null,

            LoadTotalMs = Timing?.TotalMs,
            LoadManifestMs = Timing?.ManifestMs,
            LoadFetchMs = Timing?.FetchMs,
            LoadVerifyMs = Timing?.VerifyMs,
            LoadDecodeMs = Timing?.DecodeMs,
            LoadIndexMs = Timing?.IndexMs,
            LoadValidateMs = Timing?.ValidateMs,
            RuntimeHeapBytes = RuntimeHeapBytes,
            RuntimeAllocatedBytes = RuntimeAllocatedBytes,
            RuntimeApproximateBytes = RuntimeApproximateBytes,

            ColdStartMs = Fetch?.WallClockMs,
            ColdStartLocalWorkMs = Fetch?.LocalWorkMs,
            ColdStartManifestMs = Fetch?.ManifestMs,
            ColdStartVerifyMs = Fetch?.VerifyMs,
            ColdStartTransferFloorMs = Fetch?.TransferFloorMs,
            ColdStartBytes = Fetch?.BytesFetched,
            ColdStartChunks = Fetch?.ChunksFetched,
            ColdStartRetries = Fetch?.Retries,
            ColdStartFailures = Fetch?.Failures,

            EditPublishMs = Edit?.PublishMs,
            EditValidateMs = Edit?.ValidateMs,
            EditEncodeMs = Edit?.EncodeMs,
            EditWriteMs = Edit?.WriteMs,
            EditManifestMs = Edit?.ManifestMs,
            EditAffectedChunks = Edit?.AffectedChunkCount,
            EditChunkBytes = Edit?.ChunkBytesWritten,
            EditClientManifestBytes = Edit?.ClientManifestBytes,
            EditClientDownloadBytes = Edit?.ClientDownloadBytes,
            ComparisonChunkSlots = ComparisonEdit?.ChunkSlots,
            ComparisonEditChunkBytes = ComparisonEdit?.ChunkBytesWritten,
            ComparisonEditClientManifestBytes = ComparisonEdit?.ClientManifestBytes,
            ComparisonEditClientDownloadBytes = ComparisonEdit?.ClientDownloadBytes,

            LookupNanoseconds = Lookup?.Nanoseconds,
            LookupAllocatedBytes = Lookup?.AllocatedBytes,
            TypedItemLookupNanoseconds = TypedLookup?.Nanoseconds,
            TypedItemLookupAllocatedBytes = TypedLookup?.AllocatedBytes,

            ValidatorSweepMs = Report?.TotalMs,
            ValidatorStructureMs = Report?.StructureMs,
            ValidatorSchemaMs = Report?.SchemaMs,
            ValidatorReferenceMs = Report?.ReferenceMs,
            ValidatorCodecMs = Report?.CodecMs,
            ValidatorRuleMs = Report?.RuleMs,
            ValidatorRowsSwept = Report?.RowsSwept,
            ValidatorFindings = Report?.Findings.Count,

            LootDrawNanoseconds = LootDraw?.Nanoseconds,
            LootDrawAllocatedBytes = LootDraw?.AllocatedBytes,
            LootDrawTableEntries = LootDrawEntries,

            TextDecodeMs = TextDecodeMs,
            TextDecodeHeapBytes = TextDecodeHeapBytes,
            TextDecodeEntryCount = TextDecodeEntryCount,

            ComposeProcessStartMs = ComposeProcessStartMs,
            ComposeBootMs = ComposeBootMs,
            ComposePublishedThisRun = ComposePublishedThisRun,
            ComposeP3Ms = ComposeP3Ms,
            ComposeP10Ms = ComposeP10Ms,
            ComposeLoadIndexMs = ComposeLoadIndexMs,
            ComposeLoadIndexBytes = ComposeLoadIndexBytes,
            ComposeHeapBytes = ComposeHeapBytes,
            ComposeAttribution = attribution,
        };
    }

    private static bool IsDebug()
    {
#if DEBUG
        return true;
#else
        return false;
#endif
    }
}
