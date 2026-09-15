using System;
using System.Diagnostics;
using KhaozEngine.Benchmarks.Journal;

namespace KhaozEngine.Benchmarks.Items;

internal readonly record struct GenerationMeasurement(
    double P50Microseconds,
    double P99Microseconds,
    double MeanMicroseconds,
    double AllocatedBytesPerGeneration,
    double MeanPoolSize,
    double MemoHitRate,
    double MeanAffixCount,
    double CandidateVisitsPerGeneration,
    double NanosecondsPerCandidateVisit);

internal readonly record struct StatMeasurement(
    double Nanoseconds,
    double CachedNanoseconds,
    long AllocatedBytes,
    int LineCount);

internal readonly record struct LoadMeasurement(
    double Milliseconds,
    long AllocatedBytes,
    int ReferenceIdsVisited);

/// <summary>Budgets 5, 6, 9 and 10: the time-and-allocation half of section 16.</summary>
internal static class ItemsWorkMeasurements
{
    internal const int WornItems = 11;
    internal const int AffixesPerItem = 6;

    internal static GenerationMeasurement MeasureGeneration(
        SpikeItemGenerator generator,
        ModCandidateTables tables,
        SyntheticContent content,
        IRandomSource random,
        int count,
        int hotBaseCount,
        int seed)
    {
        var hotBases = new int[Math.Max(hotBaseCount, 1)];
        for (int index = 0; index < hotBases.Length; index++) hotBases[index] = random.NextInt(0, content.BaseCount);

        for (int warmup = 0; warmup < 20_000; warmup++)
            _ = generator.Generate(new GenerationContext(hotBases[warmup % hotBases.Length], 60 + (warmup % 20), 0, 0));

        var samples = new JournalLatencySamples(seed);
        long hitsBefore = tables.MemoHits;
        long missesBefore = tables.MemoMisses;
        long visitsBefore = generator.CandidateVisits;
        long poolTotal = 0;
        long affixTotal = 0;
        double ticksToMicroseconds = 1_000_000.0 / Stopwatch.Frequency;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        for (int index = 0; index < count; index++)
        {
            int baseIndex = hotBases[index % hotBases.Length];
            int itemLevel = 40 + (index % 60);
            long before = Stopwatch.GetTimestamp();
            GenerationResult result = generator.Generate(new GenerationContext(baseIndex, itemLevel, 0, 0));
            samples.Add((Stopwatch.GetTimestamp() - before) * ticksToMicroseconds);
            poolTotal += generator.LastPoolSize;
            affixTotal += result.AffixCount;
        }

        long elapsed = Stopwatch.GetTimestamp() - started;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        long hits = tables.MemoHits - hitsBefore;
        long misses = tables.MemoMisses - missesBefore;
        long visits = generator.CandidateVisits - visitsBefore;
        return new GenerationMeasurement(
            samples.Percentile(0.50),
            samples.Percentile(0.99),
            elapsed * ticksToMicroseconds / count,
            (double)allocated / count,
            (double)poolTotal / count,
            hits + misses == 0 ? 0 : (double)hits / (hits + misses),
            (double)affixTotal / count,
            (double)visits / count,
            visits == 0 ? 0 : elapsed * ticksToMicroseconds * 1_000 / visits);
    }

    /// <summary>The same loop over the WHOLE base catalog, so the 9.2 memo's miss cost is visible.</summary>
    internal static GenerationMeasurement MeasureColdGeneration(
        SpikeItemGenerator generator,
        ModCandidateTables tables,
        SyntheticContent content,
        IRandomSource random,
        int count,
        int seed)
    {
        var samples = new JournalLatencySamples(seed + 1);
        double ticksToMicroseconds = 1_000_000.0 / Stopwatch.Frequency;
        long hitsBefore = tables.MemoHits;
        long missesBefore = tables.MemoMisses;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        for (int index = 0; index < count; index++)
        {
            long before = Stopwatch.GetTimestamp();
            _ = generator.Generate(new GenerationContext(random.NextInt(0, content.BaseCount), random.NextInt(1, 101), 0, 0));
            samples.Add((Stopwatch.GetTimestamp() - before) * ticksToMicroseconds);
        }

        long elapsed = Stopwatch.GetTimestamp() - started;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        long hits = tables.MemoHits - hitsBefore;
        long misses = tables.MemoMisses - missesBefore;
        return new GenerationMeasurement(
            samples.Percentile(0.50),
            samples.Percentile(0.99),
            elapsed * ticksToMicroseconds / count,
            (double)allocated / count,
            0,
            hits + misses == 0 ? 0 : (double)hits / (hits + misses),
            0,
            0,
            0);
    }

    /// <summary>
    /// Budget 6: one stat over eleven worn items carrying six affixes each, folded through 11.6. The
    /// headline is the RECOMPUTE, which is what a read of a dirty stat costs. The cached read is
    /// reported beside it because 11.5 says a read recomputes only when a source changed.
    /// </summary>
    internal static StatMeasurement MeasureStatEvaluation(SyntheticContent content, IRandomSource random)
    {
        var evaluator = new ContentStatEvaluator(content.StatCount);
        var lines = new StatModifierLine[16];
        var tags = new[] { 1, 2, 3 };
        var counts = new int[content.StatCount + 1];
        for (int worn = 0; worn < WornItems; worn++)
        {
            int lineCount = 0;
            for (int line = 0; line < 3; line++)
            {
                int statId = random.NextInt(1, content.StatCount + 1);
                counts[statId]++;
                lines[lineCount++] = new StatModifierLine(statId, StatCombineKind.Flat, random.NextInt(1, 200), 0, 0, 0);
            }

            evaluator.AddSource(new StatSourceKey(1, worn, 0), lines.AsSpan(0, lineCount), tags);

            lineCount = 0;
            for (int affix = 0; affix < AffixesPerItem; affix++)
            {
                int statId = random.NextInt(1, content.StatCount + 1);
                counts[statId]++;
                StatCombineKind combine = random.NextInt(0, 10) switch
                {
                    < 6 => StatCombineKind.Flat,
                    < 9 => StatCombineKind.Increased,
                    _ => StatCombineKind.More,
                };
                int value = combine == StatCombineKind.Flat ? random.NextInt(1, 200) : random.NextInt(100, 2_000);
                lines[lineCount++] = new StatModifierLine(statId, combine, value, 0, random.NextInt(0, 2), 0);
            }

            evaluator.AddSource(new StatSourceKey(2, worn, 1_000 + worn), lines.AsSpan(0, lineCount), tags);
        }

        int hottest = 1;
        for (int statId = 1; statId <= content.StatCount; statId++)
            if (counts[statId] > counts[hottest]) hottest = statId;
        evaluator.SetBase(hottest, 1_000);
        evaluator.SetStatTags(hottest, new[] { 1 });

        var contextTags = new[] { 1, 2, 3 };
        var context = new StatContext(contextTags, 0);
        for (int warmup = 0; warmup < 100_000; warmup++)
        {
            evaluator.MarkDirty();
            _ = evaluator.Recompute(hottest, in context);
        }

        const int iterations = 2_000_000;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        int sink = 0;
        for (int index = 0; index < iterations; index++) sink += evaluator.Recompute(hottest, in context);
        long elapsed = Stopwatch.GetTimestamp() - started;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        evaluator.MarkDirty();
        _ = evaluator.Value(hottest, in context);
        long cachedStarted = Stopwatch.GetTimestamp();
        for (int index = 0; index < iterations; index++) sink += evaluator.Value(hottest, in context);
        long cachedElapsed = Stopwatch.GetTimestamp() - cachedStarted;
        if (sink == int.MinValue) throw new InvalidOperationException("Unreachable, and it keeps the fold from being elided.");

        double ticksToNanoseconds = 1_000_000_000.0 / Stopwatch.Frequency;
        return new StatMeasurement(
            elapsed * ticksToNanoseconds / iterations,
            cachedElapsed * ticksToNanoseconds / iterations,
            allocated,
            evaluator.StatLineCount(hottest));
    }

    /// <summary>Budget 10: <c>Load</c> over ten pages with the full ordered rule set applied.</summary>
    internal static LoadMeasurement MeasureLoad(
        byte[][] pages,
        RemapRuleSet rules,
        SyntheticContent content,
        int iterations)
    {
        var entries = new PageEntry[ContainerPageCodec.MaximumEntries];
        var destination = new byte[128 * 1024];
        for (int warmup = 0; warmup < 400; warmup++) _ = LoadOnce(pages, rules, content, entries, destination);

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        int visited = 0;
        for (int index = 0; index < iterations; index++) visited = LoadOnce(pages, rules, content, entries, destination);
        long elapsed = Stopwatch.GetTimestamp() - started;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        double ticksToMilliseconds = 1_000.0 / Stopwatch.Frequency;
        return new LoadMeasurement(elapsed * ticksToMilliseconds / iterations, allocated / iterations, visited);
    }

    private static int LoadOnce(
        byte[][] pages,
        RemapRuleSet rules,
        SyntheticContent content,
        PageEntry[] entries,
        byte[] destination)
    {
        int visited = 0;
        foreach (byte[] page in pages)
        {
            RemapOutcome outcome = rules.ApplyToPage(page, entries, destination);
            visited += outcome.IdsVisited;
            ReadOnlySpan<byte> source = outcome.BytesWritten == 0 ? page : destination.AsSpan(0, outcome.BytesWritten);
            if (!ContainerPageCodec.TryDecode(source, ContainerPageCodec.ContainerPageSlots, entries, out _, out int entryCount, out _)) continue;
            for (int index = 0; index < entryCount; index++) Validate(source, entries[index], content);
        }

        return visited;
    }

    /// <summary>Step 4 of 5.5, reduced to the two checks a synthetic page can fail: an unresolved base and an unresolved mod.</summary>
    private static bool Validate(ReadOnlySpan<byte> page, in PageEntry entry, SyntheticContent content)
    {
        if (entry.DefinitionId <= 0 || entry.DefinitionId > content.BaseCount) return false;
        ReadOnlySpan<byte> payload = page.Slice(entry.PayloadStart, entry.PayloadLength);
        Span<PayloadField> fields = stackalloc PayloadField[InstancePayload.MaximumFields];
        if (!InstancePayload.TryDecode(payload, fields, out int fieldCount, out _)) return false;
        for (int index = 0; index < fieldCount; index++)
        {
            PayloadField field = fields[index];
            if (field.Kind != InstanceKinds.Affixes && field.Kind != InstanceKinds.Enchantments) continue;
            ReadOnlySpan<byte> body = payload.Slice(field.BodyStart, field.BodyLength);
            int count = body[0];
            int offset = 1;
            for (int affix = 0; affix < count; affix++)
            {
                if (!Varint.TryRead(body, ref offset, Varint.MaximumBytes32, out ulong modId, out _)) return false;
                if (modId == 0 || modId > (ulong)content.ModCount) return false;
                offset += 3;
                if (!Varint.TryRead(body, ref offset, Varint.MaximumBytes32, out _, out _)) return false;
            }
        }

        return true;
    }
}
