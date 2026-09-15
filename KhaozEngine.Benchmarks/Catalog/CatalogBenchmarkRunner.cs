using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Primitives;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>
/// The <c>--catalog</c> mode: publishes a synthetic catalog, then measures every budget of spec section
/// 14 against it. Phases are separable so the stress figure can be run in pieces, and every phase reports
/// its attribution rather than one number, because a miss is diagnosed by term.
/// </summary>
public static class CatalogBenchmarkRunner
{
    /// <summary>The size Scope B's generator tables are sized at, which P11's third term stands in for.</summary>
    public const long StandInLoadIndexBytes = 25L * 1024 * 1024;

    public static async Task<CatalogBenchmarkResult> RunAsync(
        CatalogBenchmarkConfig config,
        TextWriter log,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(log);

        bool temporaryRoot = config.PackRoot is null;
        string root = config.PackRoot ?? Path.Combine(Path.GetTempPath(),
            "khaoz-catalog-" + Guid.NewGuid().ToString("N"));
        try
        {
            return await RunCoreAsync(config, root, log, cancellation).ConfigureAwait(false);
        }
        finally
        {
            if (temporaryRoot && Directory.Exists(root))
            {
                try { Directory.Delete(root, recursive: true); }
                catch (IOException) { /* a benchmark scratch directory is not worth failing a run over */ }
            }
        }
    }

    private static async Task<CatalogBenchmarkResult> RunCoreAsync(
        CatalogBenchmarkConfig config,
        string root,
        TextWriter log,
        CancellationToken cancellation)
    {
        var store = new FileSystemPackStore(root);
        var content = new SyntheticContentSet(config);
        var publisher = new CatalogPublisher(content, store);

        bool published = false;
        PublishOutcome publish;
        IReadOnlyList<string>? pointer = store.GetVersionPointer(1);
        if (pointer is { Count: >= 2 } && store.Exists(pointer[0]))
        {
            log.WriteLine("reusing the pack already at " + root);
            publish = Rehydrate(content, store, pointer[0], pointer[1]);
        }
        else
        {
            log.WriteLine(Line("publishing", config.Definitions, config.ChunkSlots, config.Languages));
            publish = publisher.PublishFull(1, config.Languages, cancellation);
            published = true;
            log.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"published in {publish.TotalMs:F0} ms, {publish.Chunks.Count} chunks, {publish.TextChunks.Count} text chunks"));
        }

        var builder = new CatalogResultBuilder(config, publish);

        if (config.Phases.HasFlag(CatalogPhases.Load) || config.Phases.HasFlag(CatalogPhases.Edit))
        {
            MeasureLoad(config, content, store, publish, builder, log, cancellation);
        }

        if (config.Phases.HasFlag(CatalogPhases.Text))
        {
            (double ms, long heap, int entries) = CatalogMicroBenchmarks.MeasureTextDecode(
                store, publish.ServerManifest.Languages, CatalogPublisher.LanguageTag(0));
            builder.TextDecodeMs = ms;
            builder.TextDecodeHeapBytes = heap;
            builder.TextDecodeEntryCount = entries;
            log.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"P10 text decode {ms:F1} ms, {heap / 1048576.0:F1} MB resident, {entries} entries"));
        }

        if (config.Phases.HasFlag(CatalogPhases.Edit))
        {
            MeasureEdit(config, content, store, publish, builder, log, cancellation);
        }

        if (config.Phases.HasFlag(CatalogPhases.Fetch))
        {
            await MeasureFetchAsync(config, store, publish, builder, log, cancellation).ConfigureAwait(false);
        }

        if (config.Phases.HasFlag(CatalogPhases.Compose))
        {
            MeasureCompose(config, content, store, publish, builder, published, log);
        }

        return builder.Build();
    }

    private static void MeasureLoad(
        CatalogBenchmarkConfig config,
        SyntheticContentSet content,
        FileSystemPackStore store,
        PublishOutcome publish,
        CatalogResultBuilder builder,
        TextWriter log,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long heapBefore = GC.GetTotalMemory(forceFullCollection: true);
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);

        ContentRuntime runtime = ContentRuntime.Load(store, publish.ServerManifestHash, content.Types, verifyChunkHashes: true);
        var validator = new ContentValidator(content.Types);
        var validateClock = Stopwatch.StartNew();
        ValidationReport report = validator.Validate(runtime, []);
        validateClock.Stop();
        runtime.Timing.ValidateMs = validateClock.Elapsed.TotalMilliseconds;

        long heapAfter = GC.GetTotalMemory(forceFullCollection: true);
        long allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);

        builder.Timing = runtime.Timing;
        builder.RuntimeHeapBytes = heapAfter - heapBefore;
        builder.RuntimeAllocatedBytes = allocatedAfter - allocatedBefore;
        builder.RuntimeApproximateBytes = runtime.ApproximateBytes();
        builder.Report = report;
        builder.Runtime = runtime;

        log.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"P3 load {runtime.Timing.TotalMs:F1} ms (manifest {runtime.Timing.ManifestMs:F1}, fetch {runtime.Timing.FetchMs:F1}, "
            + $"verify {runtime.Timing.VerifyMs:F1}, decode {runtime.Timing.DecodeMs:F1}, index {runtime.Timing.IndexMs:F1}, "
            + $"validate {runtime.Timing.ValidateMs:F1}), heap {(heapAfter - heapBefore) / 1048576.0:F1} MB"));
        log.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"P8 validator {report.TotalMs:F1} ms over {report.RowsSwept} rows, {report.Findings.Count} findings"));

        if (!config.Phases.HasFlag(CatalogPhases.Load)) return;

        int[] liveIds = LiveIdRing(runtime.Table(ContentTypes.Item));
        int iterations = config.Quick ? 5_000_000 : 50_000_000;
        MicroResult lookup = CatalogMicroBenchmarks.MeasureLookup(runtime.ItemTable, liveIds, iterations);
        MicroResult typed = CatalogMicroBenchmarks.MeasureTypedItemLookup(runtime, liveIds, iterations);
        builder.Lookup = lookup;
        builder.TypedLookup = typed;
        log.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"P7 lookup {lookup.Nanoseconds:F2} ns, {lookup.AllocatedBytes} B; typed item view {typed.Nanoseconds:F2} ns, {typed.AllocatedBytes} B"));

        int[] table = CatalogMicroBenchmarks.BuildTwoHundredEntryTable(runtime.Indexes, 200);
        MicroResult draw = CatalogMicroBenchmarks.MeasureLootDraw(table, iterations, config.Seed);
        builder.LootDraw = draw;
        builder.LootDrawEntries = table.Length;
        log.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"P9 loot draw {draw.Nanoseconds:F2} ns, {draw.AllocatedBytes} B over {table.Length} entries"));
    }

    private static void MeasureEdit(
        CatalogBenchmarkConfig config,
        SyntheticContentSet content,
        FileSystemPackStore store,
        PublishOutcome publish,
        CatalogResultBuilder builder,
        TextWriter log,
        CancellationToken cancellation)
    {
        double validateMs = builder.Report?.TotalMs ?? 0;
        var edits = new List<int>(config.EditCount);
        var rng = new DeterministicRng((ulong)config.Seed ^ 0x9E3779B97F4A7C15UL);
        for (int i = 0; i < config.EditCount; i++) edits.Add(1 + rng.Next(Math.Max(1, config.DenseDefinitions)));

        var publisher = new CatalogPublisher(content, store);
        EditOutcome edit = publisher.PublishEdit(2, publish, edits, validateMs, cancellation);
        builder.Edit = edit;
        log.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"P5 edit publish {edit.PublishMs:F1} ms (validate {edit.ValidateMs:F1}, encode {edit.EncodeMs:F1}, "
            + $"manifest {edit.ManifestMs:F1}, write {edit.WriteMs:F1})"));
        log.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"P6 download {edit.ClientDownloadBytes / 1024.0:F1} KB = chunk {edit.ChunkBytesWritten / 1024.0:F1} KB "
            + $"+ manifest {edit.ClientManifestBytes / 1024.0:F1} KB, at {edit.ChunkSlots} slots"));

        if (config.Definitions > 200_000)
        {
            log.WriteLine("P6 comparison at " + config.ComparisonChunkSlots
                + " slots skipped: it needs a second full publish and this run is past 200,000 definitions");
            return;
        }

        var comparisonConfig = config with { ChunkSlots = config.ComparisonChunkSlots, PackRoot = null };
        string comparisonRoot = Path.Combine(Path.GetTempPath(), "khaoz-catalog-cmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var comparisonStore = new FileSystemPackStore(comparisonRoot);
            var comparisonContent = new SyntheticContentSet(comparisonConfig);
            var comparisonPublisher = new CatalogPublisher(comparisonContent, comparisonStore);
            PublishOutcome comparisonPublish = comparisonPublisher.PublishFull(1, config.Languages, cancellation);
            EditOutcome comparisonEdit = comparisonPublisher.PublishEdit(2, comparisonPublish, edits, 0, cancellation);
            builder.ComparisonEdit = comparisonEdit;
            log.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"P6 comparison at {comparisonEdit.ChunkSlots} slots: {comparisonEdit.ClientDownloadBytes / 1024.0:F1} KB = "
                + $"chunk {comparisonEdit.ChunkBytesWritten / 1024.0:F1} KB + manifest {comparisonEdit.ClientManifestBytes / 1024.0:F1} KB"));
        }
        finally
        {
            if (Directory.Exists(comparisonRoot))
            {
                try { Directory.Delete(comparisonRoot, recursive: true); }
                catch (IOException) { /* scratch */ }
            }
        }
    }

    private static async Task MeasureFetchAsync(
        CatalogBenchmarkConfig config,
        FileSystemPackStore store,
        PublishOutcome publish,
        CatalogResultBuilder builder,
        TextWriter log,
        CancellationToken cancellation)
    {
        int port = FreePort();
        using var server = new PackHttpServer(store, port);
        server.Start();
        FetchOutcome fetch = await ClientFetchSimulator.RunAsync(
            server, publish.ClientManifestHash, config.LinkBitsPerSecond, config.FetchConcurrency, cancellation)
            .ConfigureAwait(false);
        builder.Fetch = fetch;
        log.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"P4 cold start {fetch.WallClockMs / 1000.0:F2} s over {config.LinkBitsPerSecond / 1_000_000} Mbit, "
            + $"local work {fetch.LocalWorkMs:F0} ms, {fetch.BytesFetched / 1048576.0:F1} MB, {fetch.ChunksFetched} objects, "
            + $"transfer floor {fetch.TransferFloorMs / 1000.0:F2} s, {fetch.Retries} retries, {fetch.Failures} failures"));
    }

    private static void MeasureCompose(
        CatalogBenchmarkConfig config,
        SyntheticContentSet content,
        FileSystemPackStore store,
        PublishOutcome publish,
        CatalogResultBuilder builder,
        bool publishedThisRun,
        TextWriter log)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long heapBefore = GC.GetTotalMemory(forceFullCollection: true);
        var boot = Stopwatch.StartNew();

        ContentRuntime runtime = ContentRuntime.Load(store, publish.ServerManifestHash, content.Types, verifyChunkHashes: true);
        var validator = new ContentValidator(content.Types);
        var validateClock = Stopwatch.StartNew();
        _ = validator.Validate(runtime, []);
        validateClock.Stop();
        runtime.Timing.ValidateMs = validateClock.Elapsed.TotalMilliseconds;
        double p3 = runtime.Timing.TotalMs;

        (double textMs, long _, int _) = CatalogMicroBenchmarks.MeasureTextDecode(
            store, publish.ServerManifest.Languages, CatalogPublisher.LanguageTag(0));

        (double indexMs, long indexBytes) = CatalogMicroBenchmarks.BuildStandInLoadIndex(runtime, StandInLoadIndexBytes);

        using (var listener = new TcpListenerHandle()) { listener.Open(); }
        boot.Stop();
        long heapAfter = GC.GetTotalMemory(forceFullCollection: true);

        using Process self = Process.GetCurrentProcess();
        double sinceProcessStart = (DateTime.Now - self.StartTime).TotalMilliseconds;

        builder.ComposeBootMs = boot.Elapsed.TotalMilliseconds;
        // Only a run that found the pack already published can call this a cold boot. A run that published
        // first has its own publish inside the elapsed time, so the field stays null rather than lying.
        builder.ComposeProcessStartMs = publishedThisRun ? null : sinceProcessStart;
        builder.ComposeP3Ms = p3;
        builder.ComposeP10Ms = textMs;
        builder.ComposeLoadIndexMs = indexMs;
        builder.ComposeLoadIndexBytes = indexBytes;
        builder.ComposeHeapBytes = heapAfter - heapBefore;
        builder.ComposePublishedThisRun = publishedThisRun;

        string note = publishedThisRun ? " (this run also published, so the process figure is not a boot)" : string.Empty;
        log.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"P11 composed boot {boot.Elapsed.TotalMilliseconds:F0} ms to the listener (P3 {p3:F0}, P10 {textMs:F0}, "
            + $"load index {indexMs:F0}), heap {(heapAfter - heapBefore) / 1048576.0:F1} MB; "
            + $"process start to listener {sinceProcessStart:F0} ms{note}"));
        GC.KeepAlive(runtime);
    }

    private static PublishOutcome Rehydrate(
        SyntheticContentSet content,
        FileSystemPackStore store,
        string serverManifestHash,
        string clientManifestHash)
    {
        byte[] serverFile = store.Get(serverManifestHash)!;
        byte[] clientFile = store.Get(clientManifestHash)!;
        ContentManifestCodec.TryDecode(serverFile, out ContentManifest? serverManifest, out _);
        ContentManifestCodec.TryDecode(clientFile, out ContentManifest? clientManifest, out _);
        var chunks = new Dictionary<(ushort, int), ChunkRecord>();
        var text = new List<TextChunkRecord>();
        foreach (ManifestTypeEntry type in serverManifest!.Types)
        {
            foreach (ManifestChunkEntry chunk in type.Chunks)
            {
                byte[]? file = store.Get(chunk.Hash);
                chunks[(type.TypeId, chunk.ChunkIndex)] = new ChunkRecord(type.TypeId, chunk.ChunkIndex,
                    type.Visibility, chunk.Hash, file?.Length ?? 0, chunk.UncompressedBytes, 0);
            }
        }
        foreach (ManifestLanguageEntry language in serverManifest.Languages)
        {
            byte[]? file = store.Get(language.TextHash);
            text.Add(new TextChunkRecord(language.Tag, language.TextHash, file?.Length ?? 0, 0, 0));
        }
        var outcome = new PublishOutcome
        {
            VersionNumber = serverManifest.VersionNumber,
            ServerManifest = serverManifest,
            ClientManifest = clientManifest!,
            ServerManifestHash = serverManifestHash,
            ClientManifestHash = clientManifestHash,
            ServerManifestBytes = serverFile.Length,
            ClientManifestBytes = clientFile.Length,
            Chunks = chunks,
            TextChunks = text,
            RuleChunkHash = serverManifest.RemapRuleChunkHash,
            RuleChunkStoredBytes = store.Get(serverManifest.RemapRuleChunkHash)?.Length ?? 0,
        };
        GC.KeepAlive(content);
        return outcome;
    }

    /// <summary>
    /// A power-of-two ring of live ids, scattered by a fixed stride. Power of two so the timed loop
    /// indexes with a mask rather than a division, and scattered so it is not a sequential cache walk.
    /// </summary>
    private static int[] LiveIdRing(ContentTypeTable table)
    {
        var ids = new List<int>(table.RowCount);
        for (int id = 0; id < table.Offsets.Length; id++)
        {
            if (table.Offsets[id] >= 0) ids.Add(id);
        }
        int size = 1;
        while (size * 2 <= ids.Count) size *= 2;
        const int stride = 7919;
        int[] ring = new int[size];
        for (int i = 0; i < size; i++) ring[i] = ids[(int)(((long)i * stride) % ids.Count)];
        return ring;
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static string Line(string what, int definitions, int chunkSlots, int languages) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{what}: {definitions} definitions, {chunkSlots} item chunk slots, {languages} language(s)");

    /// <summary>The listener opening is P11's finish line, so it is a real socket rather than a flag.</summary>
    private sealed class TcpListenerHandle : IDisposable
    {
        private TcpListener? _listener;

        public void Open()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
        }

        public void Dispose() => _listener?.Stop();
    }
}
