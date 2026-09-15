using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using KhaozEngine.Primitives;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>A per-operation cost: nanoseconds and the bytes it allocated on this thread.</summary>
public readonly record struct MicroResult(double Nanoseconds, long AllocatedBytes);

/// <summary>
/// The per-operation budgets: P7's lookup by id, P9's weighted loot draw, P10's text chunk decode, and
/// the stand-in per-type load index P11 composes with. Every loop sinks its result into a static field so
/// the JIT cannot delete the work being timed.
/// </summary>
public static class CatalogMicroBenchmarks
{
    private static long _sink;

    /// <summary>
    /// Budget P7: one array index into <c>Offsets</c>, one bounds check, one span slice. The table, the
    /// id ring and the stopwatch are all allocated BEFORE the allocation baseline is taken, so the
    /// reported figure is the loop's own allocation and not the harness's.
    /// </summary>
    public static MicroResult MeasureLookup(ContentTypeTable table, int[] ring, int iterations)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(ring);
        var clock = new Stopwatch();
        RunLookup(table, ring, Math.Min(iterations, 1_000_000));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long before = GC.GetAllocatedBytesForCurrentThread();
        clock.Start();
        RunLookup(table, ring, iterations);
        clock.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return new MicroResult(clock.Elapsed.TotalMilliseconds * 1_000_000.0 / iterations, allocated);
    }

    /// <summary>The same loop through the typed <c>ItemRowView</c>, which is the hot read of section 9.1.</summary>
    public static MicroResult MeasureTypedItemLookup(ContentRuntime runtime, int[] ring, int iterations)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(ring);
        var clock = new Stopwatch();
        RunTypedLookup(runtime, ring, Math.Min(iterations, 1_000_000));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long before = GC.GetAllocatedBytesForCurrentThread();
        clock.Start();
        RunTypedLookup(runtime, ring, iterations);
        clock.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return new MicroResult(clock.Elapsed.TotalMilliseconds * 1_000_000.0 / iterations, allocated);
    }

    /// <summary>Budget P9: a weighted draw over a 200-entry prefix summed table.</summary>
    public static MicroResult MeasureLootDraw(int[] prefixWeights, int iterations, int seed)
    {
        ArgumentNullException.ThrowIfNull(prefixWeights);
        var clock = new Stopwatch();
        var rng = new DeterministicRng((ulong)seed);
        RunDraw(prefixWeights, Math.Min(iterations, 1_000_000), rng);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long before = GC.GetAllocatedBytesForCurrentThread();
        clock.Start();
        RunDraw(prefixWeights, iterations, rng);
        clock.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return new MicroResult(clock.Elapsed.TotalMilliseconds * 1_000_000.0 / iterations, allocated);
    }

    /// <summary>
    /// A 200-entry prefix summed table drawn from the loaded loot arrays, because the engine's own tables
    /// are four entries each and P9's budget names two hundred.
    /// </summary>
    public static int[] BuildTwoHundredEntryTable(ContentIndexes indexes, int entries)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        int[] prefix = new int[entries];
        int total = 0;
        for (int i = 0; i < entries; i++)
        {
            int source = indexes.LootEntryPrefixWeight.Length > 0
                ? indexes.LootEntryPrefixWeight[i % indexes.LootEntryPrefixWeight.Length]
                : i + 1;
            total += 1 + (source % 997);
            prefix[i] = total;
        }
        return prefix;
    }

    /// <summary>
    /// The stand-in for a registered <c>IContentLoadIndex</c> at boot step 7b: a per-item candidate table
    /// of the size Scope B's generator tables are sized at, built by a real decode pass over the item rows.
    /// </summary>
    public static (double Milliseconds, long Bytes) BuildStandInLoadIndex(ContentRuntime runtime, long targetBytes)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ContentTypeTable items = runtime.Table(ContentTypes.Item);
        int slots = (int)(targetBytes / 4);
        var clock = Stopwatch.StartNew();
        int[] candidates = new int[slots];
        int cursor = 0;
        var row = new ItemRowData();
        while (cursor < slots)
        {
            for (int id = 0; id < items.Offsets.Length && cursor < slots; id++)
            {
                if (items.Offsets[id] < 0) continue;
                ReadOnlySpan<byte> body = items.Body(id);
                if (!ContentRowCodec.TryDecodeItem(body[ContentRuntime.FieldsOffset(body)..], ref row)) continue;
                int span = Math.Min(slots - cursor, 128);
                for (int i = 0; i < span; i++)
                {
                    candidates[cursor++] = id ^ (row.Tag0 * (i + 1)) ^ (row.Value >> (i & 15));
                }
            }
            if (items.RowCount == 0) break;
        }
        clock.Stop();
        _sink += candidates.Length > 0 ? candidates[^1] : 0;
        return (clock.Elapsed.TotalMilliseconds, candidates.LongLength * 4);
    }

    /// <summary>Budget P10: decode one language's text chunks into a dictionary, timed and weighed.</summary>
    public static (double Milliseconds, long HeapBytes, int Entries) MeasureTextDecode(
        FileSystemPackStore store,
        IReadOnlyList<ManifestLanguageEntry> languages,
        string languageTag)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(languages);
        var files = new List<byte[]>();
        foreach (ManifestLanguageEntry language in languages)
        {
            if (!language.Tag.Equals(languageTag, StringComparison.Ordinal)
                && !language.Tag.StartsWith(languageTag + "-x-s", StringComparison.Ordinal))
            {
                continue;
            }
            byte[]? file = store.Get(language.TextHash);
            if (file is not null) files.Add(file);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetTotalMemory(forceFullCollection: true);
        var clock = Stopwatch.StartNew();
        var decoded = new List<Dictionary<string, string>>(files.Count);
        int entries = 0;
        foreach (byte[] file in files)
        {
            if (!ContentTextChunkCodec.TryDecode(file, out Dictionary<string, string>? map, out _) || map is null) continue;
            decoded.Add(map);
            entries += map.Count;
        }
        clock.Stop();
        long after = GC.GetTotalMemory(forceFullCollection: true);
        _sink += decoded.Count;
        GC.KeepAlive(decoded);
        return (clock.Elapsed.TotalMilliseconds, after - before, entries);
    }

    public static long Sink => Volatile.Read(ref _sink);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunLookup(ContentTypeTable table, int[] ring, int iterations)
    {
        int mask = ring.Length - 1;
        long accumulator = 0;
        for (int i = 0; i < iterations; i++)
        {
            ReadOnlySpan<byte> body = table.Body(ring[i & mask]);
            accumulator += body.Length;
        }
        _sink += accumulator;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunTypedLookup(ContentRuntime runtime, int[] ring, int iterations)
    {
        int mask = ring.Length - 1;
        long accumulator = 0;
        for (int i = 0; i < iterations; i++)
        {
            if (runtime.TryGetItem(ring[i & mask], out ItemRowView row)) accumulator += row.MaxStack;
        }
        _sink += accumulator;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunDraw(int[] prefixWeights, int iterations, DeterministicRng rng)
    {
        int total = prefixWeights[^1];
        long accumulator = 0;
        for (int i = 0; i < iterations; i++)
        {
            accumulator += LootRoller.Pick(prefixWeights, rng.Next(total));
        }
        _sink += accumulator;
    }
}
