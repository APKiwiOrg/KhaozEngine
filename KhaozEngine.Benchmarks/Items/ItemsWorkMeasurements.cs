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
    double MeanAffixCount,
    double DeadEntriesPerGeneration);

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
        int levelSpan,
        int seed)
    {
        var hotBases = new int[Math.Max(hotBaseCount, 1)];
        for (int index = 0; index < hotBases.Length; index++) hotBases[index] = random.NextInt(0, content.BaseCount);

        for (int warmup = 0; warmup < 20_000; warmup++)
            _ = generator.Generate(new GenerationContext(hotBases[warmup % hotBases.Length], 40 + (warmup % levelSpan), 0, 0));

        var samples = new JournalLatencySamples(seed);
        long deadBefore = generator.DeadEntriesWalked;
        long poolTotal = 0;
        long affixTotal = 0;
        double ticksToMicroseconds = 1_000_000.0 / Stopwatch.Frequency;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        for (int index = 0; index < count; index++)
        {
            int baseIndex = hotBases[index % hotBases.Length];
            int itemLevel = 40 + (index % levelSpan);
            long before = Stopwatch.GetTimestamp();
            GenerationResult result = generator.Generate(new GenerationContext(baseIndex, itemLevel, 0, 0));
            samples.Add((Stopwatch.GetTimestamp() - before) * ticksToMicroseconds);
            poolTotal += generator.LastPoolSize;
            affixTotal += result.AffixCount;
        }

        long elapsed = Stopwatch.GetTimestamp() - started;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        long dead = generator.DeadEntriesWalked - deadBefore;
        return new GenerationMeasurement(
            samples.Percentile(0.50),
            samples.Percentile(0.99),
            elapsed * ticksToMicroseconds / count,
            (double)allocated / count,
            (double)poolTotal / count,
            (double)affixTotal / count,
            (double)dead / count);
    }

    /// <summary>
    /// The same loop over the WHOLE base catalog at every item level, which is the coldest shape the
    /// tables can be asked for: every roll lands on a different (tag signature, band) pair.
    /// </summary>
    internal static GenerationMeasurement MeasureColdGeneration(
        SpikeItemGenerator generator,
        SyntheticContent content,
        IRandomSource random,
        int count,
        int seed)
    {
        var samples = new JournalLatencySamples(seed + 1);
        double ticksToMicroseconds = 1_000_000.0 / Stopwatch.Frequency;
        long deadBefore = generator.DeadEntriesWalked;
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
        long dead = generator.DeadEntriesWalked - deadBefore;
        return new GenerationMeasurement(
            samples.Percentile(0.50),
            samples.Percentile(0.99),
            elapsed * ticksToMicroseconds / count,
            (double)allocated / count,
            0,
            0,
            (double)dead / count);
    }

    /// <summary>
    /// The revision of 9.2 and 9.4 moved the filter from a per-pick pass to a subtraction, so the
    /// properties the pass used to enforce are asserted here rather than assumed: no mod twice, no
    /// exclusivity group twice, no legacy mod, no tier outside its gate, and the per kind caps. It runs
    /// off the timed path and every count it returns must be zero.
    /// </summary>
    internal static int ValidateInvariants(
        SpikeItemGenerator generator,
        ModCandidateTables tables,
        SyntheticContent content,
        IRandomSource random,
        int count)
    {
        int violations = 0;
        Span<int> mods = stackalloc int[16];
        Span<int> groups = stackalloc int[16];
        Span<PayloadField> fields = stackalloc PayloadField[InstancePayload.MaximumFields];
        for (int index = 0; index < count; index++)
        {
            int baseIndex = random.NextInt(0, content.BaseCount);
            int itemLevel = random.NextInt(1, 101);
            GenerationResult result = generator.Generate(new GenerationContext(baseIndex, itemLevel, 0, 0));
            int band = tables.BandOf(itemLevel);
            if (!InstancePayload.TryDecode(result.Payload, fields, out int fieldCount, out _))
            {
                violations++;
                continue;
            }

            int modCount = 0;
            int groupCount = 0;
            int prefixes = 0;
            int suffixes = 0;
            for (int field = 0; field < fieldCount; field++)
            {
                if (fields[field].Kind != InstanceKinds.Affixes) continue;
                ReadOnlySpan<byte> body = result.Payload.AsSpan(fields[field].BodyStart, fields[field].BodyLength);
                int affixes = body[0];
                int offset = 1;
                for (int affix = 0; affix < affixes; affix++)
                {
                    if (!Varint.TryRead(body, ref offset, Varint.MaximumBytes32, out ulong modId, out _)) break;
                    int tier = body[offset];
                    offset += 3;
                    _ = Varint.TryRead(body, ref offset, Varint.MaximumBytes32, out _, out _);
                    int modIndex = (int)modId - 1;
                    if (content.ModLegacy[modIndex]) violations++;
                    int tierSlot = (modIndex * SyntheticContent.TiersPerMod) + tier - 1;
                    if (content.TierLevelMin[tierSlot] > itemLevel || content.TierLevelMax[tierSlot] < itemLevel) violations++;
                    if (tables.BandOf(content.TierLevelMin[tierSlot]) > band) violations++;
                    for (int prior = 0; prior < modCount; prior++)
                        if (mods[prior] == (int)modId) violations++;
                    if (modCount < mods.Length) mods[modCount++] = (int)modId;
                    int group = content.ModGroup[modIndex];
                    if (group != 0)
                    {
                        for (int prior = 0; prior < groupCount; prior++)
                            if (groups[prior] == group) violations++;
                        if (groupCount < groups.Length) groups[groupCount++] = group;
                    }

                    if (content.ModKind[modIndex] == 1) prefixes++;
                    else suffixes++;
                }
            }

            int rarityIndex = result.RarityId - 1;
            if (prefixes > content.RarityMaxPrefixes[rarityIndex]) violations++;
            if (suffixes > content.RarityMaxSuffixes[rarityIndex]) violations++;
            if (result.AffixCount > content.RarityMaxAffixes[rarityIndex]) violations++;
        }

        return violations + (int)Math.Min(generator.ExclusionOverflows, int.MaxValue);
    }

    /// <summary>
    /// Budget 6: ONE stat folded over every line eleven worn items put on it. The budget's own
    /// derivation is eleven worn items times up to thirteen lines each, about 140 lines, so the lines
    /// all land on the measured stat rather than spreading across the catalog: a stat touched by four
    /// of them would answer a much easier question than the one the budget asked. The headline is the
    /// RECOMPUTE, which is what a read of a dirty stat costs, and the cached read is beside it because
    /// 11.5 says a read recomputes only when a source changed.
    /// </summary>
    internal static StatMeasurement MeasureStatEvaluation(SyntheticContent content, IRandomSource random)
    {
        const int hottest = 1;
        const int baseLinesPerItem = 3;
        const int affixLinesPerItem = 10;
        var evaluator = new ContentStatEvaluator(content.StatCount);
        var lines = new StatModifierLine[16];
        var tags = new[] { 1, 2, 3 };
        for (int worn = 0; worn < WornItems; worn++)
        {
            for (int line = 0; line < baseLinesPerItem; line++)
                lines[line] = new StatModifierLine(hottest, StatCombineKind.Flat, random.NextInt(1, 200), 0, 0, 0);
            evaluator.AddSource(new StatSourceKey(1, worn, 0), lines.AsSpan(0, baseLinesPerItem), tags);

            for (int line = 0; line < affixLinesPerItem; line++)
            {
                StatCombineKind combine = random.NextInt(0, 10) switch
                {
                    < 6 => StatCombineKind.Flat,
                    < 9 => StatCombineKind.Increased,
                    _ => StatCombineKind.More,
                };
                int value = combine == StatCombineKind.Flat ? random.NextInt(1, 200) : random.NextInt(100, 2_000);
                lines[line] = new StatModifierLine(hottest, combine, value, 0, random.NextInt(0, 2), 0);
            }

            evaluator.AddSource(new StatSourceKey(2, worn, 1_000 + worn), lines.AsSpan(0, affixLinesPerItem), tags);
        }

        evaluator.SetBase(hottest, 1_000);
        evaluator.SetStatTags(hottest, new[] { 1 });

        var contextTags = new[] { 1, 2, 3 };
        var context = new StatContext(contextTags, 0);
        for (int warmup = 0; warmup < 200_000; warmup++)
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
