using System;
using System.Globalization;
using System.IO;

namespace KhaozEngine.Benchmarks.Items;

/// <summary>
/// Prints the section 16 table as it now reads: one row per budget, the target beside the measurement,
/// and a verdict. A budget that misses is printed as a MISS with its number, never softened.
/// </summary>
public static class ItemsBenchmarkReport
{
    public static void Write(ItemsBenchmarkResult result, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(output);
        CultureInfo culture = CultureInfo.InvariantCulture;
        const string header = "{0,3} {1,-44} {2,-30} {3,-34} {4,-6}";
        output.WriteLine(string.Format(culture, header, "#", "budget", "target", "measured", "verdict"));
        output.WriteLine(new string('-', 3 + 44 + 30 + 34 + 6 + 4));

        Write(output, culture, header, 1, "Bytes per rare item, payload", "at most 80",
            $"{result.Budget1RarePayloadBytes} bytes", result.Budget1RarePayloadBytes <= 80);
        Write(output, culture, header, 2, "Bytes per rare item, slot entry", "at most 96",
            $"{result.Budget2RareSlotEntryBytes} bytes", result.Budget2RareSlotEntryBytes <= 96);
        Write(output, culture, header, 3, "Page commit size, 100 rares", "at most 8 KB",
            $"{result.Budget3PageBytesCanonicalRares} bytes", result.Budget3PageBytesCanonicalRares <= 8_192);
        Write(output, culture, header, 4, "Write volume, crafts in one held action", "at most 20 KB and 1 commit",
            $"{result.Budget4CoalescedOwnedBytes} bytes in {result.Budget4CoalescedCommitCount} commit",
            result.Budget4CoalescedOwnedBytes <= 20_480 && result.Budget4CoalescedCommitCount == 1);
        Write(output, culture, header, 5, "Rare generation time", "under 20 us per item",
            $"p50 {result.Budget5P50Microseconds:F2} us, p99 {result.Budget5P99Microseconds:F2} us",
            result.Budget5P99Microseconds < 20);
        Write(output, culture, header, 6, "Stat evaluation per attack", "under 2 us, 0 bytes",
            $"{result.Budget6Nanoseconds:F0} ns, {result.Budget6AllocatedBytes} bytes",
            result.Budget6Nanoseconds < 2_000 && result.Budget6AllocatedBytes == 0);
        Write(output, culture, header, 7, "Container page sync, cold open", "at most 8 KB and 8 frames",
            $"{result.Budget7GameMessageBytes} bytes in {result.Budget7ChunkCount} frames",
            result.Budget7GameMessageBytes <= 8_192 && result.Budget7ChunkCount <= 8);
        Write(output, culture, header, 8, "Steady-state sync after one craft", "1 frame, at most 96 bytes",
            $"{result.Budget8DeltaBytes} bytes in 1 frame", result.Budget8DeltaBytes is > 0 and <= 96);
        Write(output, culture, header, 9, "Generator table build at 2,000 mods", "under 500 ms, under 40 MB",
            $"{result.Budget9TableBuildMilliseconds:F0} ms, {Megabytes(result.Budget9TableResidentBytes)} MB",
            result.Budget9TableBuildMilliseconds < 500 && result.Budget9TableResidentBytes < 40L * 1024 * 1024);
        Write(output, culture, header, 10, "Container load, 10 pages and 200 rules", "under 5 ms, under 200 KB",
            $"{result.Budget10LoadMilliseconds:F3} ms, {result.Budget10LoadAllocatedBytes} bytes",
            result.Budget10LoadMilliseconds < 5 && result.Budget10LoadAllocatedBytes < 204_800);
        Write(output, culture, header, 11, "Ground bytes per viewer per second", "at most 8 KB per second",
            $"{result.Budget11BytesPerViewerPerSecond:F0} bytes per second",
            result.Budget11BytesPerViewerPerSecond <= 8_192);
        Write(output, culture, header, 12, "Resident page bytes at 1,000 players", "under 250 MB",
            $"{Megabytes(result.Budget12TotalResidentBytes)} MB at {result.Budget12Players} players",
            result.Budget12TotalResidentBytes < 250L * 1024 * 1024);
        Write(output, culture, header, 13, "Commits per second offered, 4 Hz", "4,000 offered, store to find",
            $"offered {result.Budget13OfferedPerSecond:F0}/s, sustained {result.Budget13CommittedPerSecond:F0}/s",
            result.Budget13OfferedPerSecond >= result.Budget13Players * 3.9);

        output.WriteLine();
        output.WriteLine(string.Create(culture, $"scale test: {result.ScaleInstances:N0} instances in {result.ScalePageCount:N0} pages, {Megabytes(result.ScaleResidentBytes)} MB resident, {result.ScaleBytesPerInstance:F1} bytes per instance (page bytes {Megabytes(result.ScalePageBytesSum)} MB, GetTotalMemory delta {Megabytes(result.ScaleTotalMemoryDeltaBytes)} MB)"));
        output.WriteLine(string.Create(culture, $"budget 3 through generated rares: {result.Budget3PageBytesGeneratedRares} bytes per page, {result.Budget3GeneratedEntryMeanBytes:F1} bytes per entry"));
        output.WriteLine(string.Create(culture, $"budget 4 uncoalesced: {result.Budget4UncoalescedOwnedBytes} bytes in {result.Budget4UncoalescedCommitCount} commits, craft event {result.Budget4CraftEventBytes} bytes, store answered {result.Budget4StoreStatus}"));
        output.WriteLine(string.Create(culture, $"budget 5 detail: mean {result.Budget5MeanMicroseconds:F2} us, {result.Budget5AllocatedBytesPerGeneration:F0} bytes per generation, pool {result.Budget5MeanPoolSize:F0} live candidates, mean affixes {result.Budget5MeanAffixCount:F2}"));
        output.WriteLine(string.Create(culture, $"budget 5 cost shape: {result.Budget5PoolSuppressedEntries:F1} overlap-suppressed pool entries per generation, {result.Budget5InvariantViolations} invariant violations"));
        output.WriteLine(string.Create(culture, $"budget 5 hot cache (64 bases, 4 item levels): p50 {result.Budget5WarmP50Microseconds:F2} us, p99 {result.Budget5WarmP99Microseconds:F2} us, {result.Budget5WarmAllocatedBytesPerGeneration:F0} bytes per generation"));
        output.WriteLine(string.Create(culture, $"budget 5 at the top rarity: p50 {result.Budget5DenseP50Microseconds:F2} us, p99 {result.Budget5DenseP99Microseconds:F2} us, mean {result.Budget5DenseMeanMicroseconds:F2} us over {result.Budget5DenseMeanAffixCount:F2} affixes"));
        output.WriteLine(string.Create(culture, $"budget 5 cold catalog: p50 {result.Budget5ColdP50Microseconds:F2} us, p99 {result.Budget5ColdP99Microseconds:F2} us"));
        output.WriteLine(string.Create(culture, $"budget 6 detail: {result.Budget6LineCount} lines on the hottest stat over {result.Budget6WornItems} worn items, cached read {result.Budget6CachedNanoseconds:F1} ns"));
        output.WriteLine(string.Create(culture, $"budget 8 detail: {result.Budget8MaximumChangedSlotsInOneFrame} changed rare slots fit one frame"));
        output.WriteLine(string.Create(culture, $"budget 10 with rewrites: {result.Budget10LoadWithRewritesMilliseconds:F3} ms, {result.Budget10LoadWithRewritesAllocatedBytes} bytes, {result.Budget10ReferenceIdsVisited} reference ids visited per load"));
        output.WriteLine(string.Create(culture, $"budget 12 detail: pages {Megabytes(result.Budget12PageResidentBytes)} MB, admitted layer {Megabytes(result.Budget12AdmittedResidentBytes)} MB, page bytes {Megabytes(result.Budget12PageBytesSum)} MB, GetTotalMemory delta {Megabytes(result.Budget12TotalMemoryDeltaBytes)} MB, {result.Budget12AdmittedStatus}"));
        output.WriteLine(string.Create(culture, $"budget 13 detail: accepted {result.Budget13AcceptedPerSecond:F0}/s, p50 {result.Budget13P50Milliseconds:F1} ms, p99 {result.Budget13P99Milliseconds:F1} ms, backpressure {result.Budget13BackpressureRate:P1}, busy {result.Budget13BusyRate:P1}, version conflict {result.Budget13VersionConflictRate:P1}, commit {result.Budget13CommitBytes} bytes, database {Megabytes(result.Budget13DatabaseBytes)} MB"));
        output.WriteLine(string.Create(culture, $"budget 9 detail: GetTotalMemory delta {Megabytes(result.Budget9TableTotalMemoryDeltaBytes)} MB against a heap-size delta of {Megabytes(result.Budget9TableResidentBytes)} MB, array sum {Megabytes(result.Budget9TableSelfReportedBytes)} MB, {result.Budget9SuppressedEntries:N0} suppressed entries, {result.Budget9ConsistencyFailures} consistency failures"));
        output.WriteLine(string.Create(culture, $"total run {result.TotalSeconds:F1} s"));
        output.WriteLine();
    }

    private static void Write(TextWriter output, CultureInfo culture, string header, int number, string budget, string target, string measured, bool meets)
    {
        output.WriteLine(string.Format(culture, header, number, budget, target, measured, meets ? "MEETS" : "MISS"));
    }

    private static string Megabytes(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture);
}
