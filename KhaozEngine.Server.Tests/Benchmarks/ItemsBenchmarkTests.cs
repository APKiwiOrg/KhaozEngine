using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.Benchmarks.Items;
using Xunit;

namespace KhaozEngine.Tests.Benchmarks;

/// <summary>
/// The structural fence around the <c>--items</c> mode, beside the mode itself exactly as
/// <c>MutationJournalBenchmarkTests</c> sits beside <c>--journal</c>. A benchmark that only runs by hand is a
/// benchmark nobody runs, and this is the half that runs in CI.
/// <para>
/// <b>It asserts STRUCTURE and never numbers.</b> A budget's measured value belongs in the checked-in baseline
/// JSON, which a human diffs. What these facts pin is that every budget the runner claims to measure is
/// present, is a number, and is not zero where zero would mean the leg never ran. A test asserting a
/// microsecond figure goes red on a busy runner and teaches everyone to rerun it.
/// </para>
/// <para>
/// <b>Budgets 5 and 9 measure the SHIPPED generator and its tables.</b> The mode publishes its synthetic
/// rows through <c>ContentSnapshotBuilder</c>, times <c>ModCandidateTables.Build</c> over them and rolls
/// every item through <c>ItemGenerator</c>, so a regression in either one moves a number here. That is what
/// the budget 5 and budget 9 fields below are fenced for, and it is why
/// <c>Budget9ConsistencyFailures</c> is asserted at exactly zero: it is the table build's own self check
/// rather than a timing.
/// </para>
/// <para>
/// <b>The ONE number here that is a design property rather than a timing is budget 4's commit count.</b>
/// Twenty crafts in one held action are ONE commit. That is the property the whole of spec 6 exists for, and
/// what this fact pins is the SPIKE's own number: the <c>--items</c> mode coalesces through its own runner
/// and a regression in <c>ContainerCommitBuilder</c> cannot make this red.
/// <c>Twenty_crafts_in_one_held_action_are_ONE_commit</c> in <c>ContainerCommitBuilderTests</c> is the fact
/// that guards the SHIPPED builder. Rewiring the commit, codec and visibility halves onto the shipped types
/// is <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/951">#951</see>, still open, and it wants
/// doing before the next re-bake of budgets 1, 2, 3, 4, 8 and 11, so spec 16's measured column describes
/// what ships for those too.
/// </para>
/// <para>
/// Nothing here writes process-global state, so no test needs a collection attribute.
/// </para>
/// </summary>
public sealed class ItemsBenchmarkTests
{
    /// <summary>The budgets whose measurement is only meaningful above zero. The rest are counters a clean run
    /// leaves at zero on purpose, which is why they are not in this list.</summary>
    static readonly string[] MeasuredBudgets =
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
        nameof(ItemsBenchmarkResult.Budget5DeadEntriesPerGeneration),
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

    [Fact]
    public void Parse_accepts_explicit_options_and_rejects_hard_limit_violations()
    {
        string database = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "items-benchmark.db"));
        string output = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "items-benchmark.json"));
        ItemsBenchmarkConfig config = ItemsBenchmarkConfig.Parse(new[]
        {
            "--items", "--players", "2000", "--generations", "5000", "--crafts", "7", "--seed", "915",
            "--database", database, "--output", output,
        });

        Assert.False(config.Quick);
        Assert.Equal(2_000, config.Players);
        Assert.Equal(5_000, config.Generations);
        Assert.Equal(7, config.Crafts);
        Assert.Equal(915, config.Seed);
        Assert.Equal(database, config.DatabasePath);
        Assert.Equal(output, config.OutputPath);

        Assert.Throws<ArgumentOutOfRangeException>(() => ItemsBenchmarkConfig.Parse(
            new[] { "--items", "--players", (ItemsBenchmarkConfig.MaximumPlayers + 1).ToString(CultureInfo.InvariantCulture) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ItemsBenchmarkConfig.Parse(new[] { "--items", "--players", "0" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ItemsBenchmarkConfig.Parse(
            new[] { "--items", "--generations", (ItemsBenchmarkConfig.MaximumGenerations + 1).ToString(CultureInfo.InvariantCulture) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ItemsBenchmarkConfig.Parse(new[] { "--items", "--generations", "999" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ItemsBenchmarkConfig.Parse(
            new[] { "--items", "--crafts", (ItemsBenchmarkConfig.MaximumCrafts + 1).ToString(CultureInfo.InvariantCulture) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ItemsBenchmarkConfig.Parse(new[] { "--items", "--crafts", "0" }));
        Assert.Throws<ArgumentException>(() => ItemsBenchmarkConfig.Parse(new[] { "--items", "--database", "relative.db" }));
        Assert.Throws<ArgumentException>(() => ItemsBenchmarkConfig.Parse(new[] { "--items", "--output", "relative.json" }));
        Assert.Throws<ArgumentException>(() => ItemsBenchmarkConfig.Parse(
            new[] { "--items", "--output", Path.Combine(Path.GetTempPath(), "baseline.txt") }));
        Assert.Throws<ArgumentException>(() => ItemsBenchmarkConfig.Parse(
            new[] { "--items", "--database", output, "--output", output }));
        Assert.Throws<ArgumentException>(() => ItemsBenchmarkConfig.Parse(new[] { "--items", "--unknown", "1" }));
        Assert.Throws<ArgumentException>(() => ItemsBenchmarkConfig.Parse(new[] { "--items", "--players" }));
        Assert.Throws<ArgumentException>(() => ItemsBenchmarkConfig.Parse(new[] { "--items", "--seed", "nine" }));
    }

    [Fact]
    public void Quick_shrinks_only_the_scales_the_caller_did_not_name()
    {
        ItemsBenchmarkConfig full = ItemsBenchmarkConfig.Parse(new[] { "--items" });
        ItemsBenchmarkConfig quick = ItemsBenchmarkConfig.Parse(new[] { "--items", "--quick" });
        ItemsBenchmarkConfig asked = ItemsBenchmarkConfig.Parse(new[] { "--items", "--quick", "--players", "3", "--generations", "1000" });

        Assert.True(quick.Players < full.Players);
        Assert.True(quick.Generations < full.Generations);
        Assert.True(quick.ThroughputSeconds < full.ThroughputSeconds);
        Assert.True(quick.ScaleInstances < full.ScaleInstances);
        Assert.True(quick.HotBaseCount < full.HotBaseCount);
        Assert.Equal(3, asked.Players);
        Assert.Equal(1_000, asked.Generations);

        // The owner's content scale is not a knob in either mode: budget 9 measures the table build at it.
        Assert.Equal(full.ModCount, quick.ModCount);
        Assert.Equal(full.BaseCount, quick.BaseCount);
        Assert.Equal(full.BankPagesPerPlayer, quick.BankPagesPerPlayer);
        Assert.Equal(full.Crafts, quick.Crafts);
    }

    [Fact]
    public void Every_budget_5_and_budget_9_field_is_fenced_rather_than_only_written()
    {
        // These two budgets are the ones the shipped generator and the shipped candidate tables move, so
        // every field of theirs is either non-zero in a clean run, which is MeasuredBudgets above, or is
        // named here with the reason it may legitimately sit at zero or below it. A field in neither list
        // is a number nothing would notice going missing.
        string[] countersAndReadings =
        {
            // Two counters a clean run leaves at exactly zero on purpose, asserted at zero by the quick run.
            nameof(ItemsBenchmarkResult.Budget5InvariantViolations),
            nameof(ItemsBenchmarkResult.Budget9ConsistencyFailures),

            // Two memory readings taken either side of the build, so a collection landing inside one makes
            // the answer legitimately zero or negative. The self-reported byte count beside them is the
            // deterministic one and it IS fenced.
            nameof(ItemsBenchmarkResult.Budget9TableResidentBytes),
            nameof(ItemsBenchmarkResult.Budget9TableTotalMemoryDeltaBytes),
        };

        var unfenced = new List<string>();
        int fields = 0;
        foreach (PropertyInfo property in typeof(ItemsBenchmarkResult).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.Name.StartsWith("Budget5", StringComparison.Ordinal)
                && !property.Name.StartsWith("Budget9", StringComparison.Ordinal))
            {
                continue;
            }

            fields++;
            if (!MeasuredBudgets.Contains(property.Name, StringComparer.Ordinal)
                && !countersAndReadings.Contains(property.Name, StringComparer.Ordinal))
            {
                unfenced.Add(property.Name);
            }
        }

        Assert.Equal(Array.Empty<string>(), unfenced.ToArray());
        Assert.True(fields >= 20, $"The sweep found only {fields} budget 5 and budget 9 fields, so it read nothing.");
    }

    [Fact]
    public void Result_json_round_trips_and_is_stable()
    {
        var result = new ItemsBenchmarkResult
        {
            Machine = "test-machine",
            Framework = ".NET test",
            ProcessorCount = 4,
            Seed = 915,
            Budget4CoalescedCommitCount = 1,
            Budget4CoalescedOwnedBytes = 9_519,
            Budget4UncoalescedCommitCount = 20,
            Budget5P50Microseconds = 1.9,
            TotalSeconds = 13.75,
        };

        string first = result.ToJson();
        Assert.Equal(first, result.ToJson());
        using JsonDocument parsed = JsonDocument.Parse(first);
        Assert.Equal("items-sqlite-v1", parsed.RootElement.GetProperty("workload").GetString());
        Assert.Equal(1, parsed.RootElement.GetProperty("budget4CoalescedCommitCount").GetInt32());

        ItemsBenchmarkResult back = JsonSerializer.Deserialize<ItemsBenchmarkResult>(
            first, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(first, back.ToJson());
        Assert.Equal(result, back);
    }

    [Fact]
    public async Task Output_writer_atomically_creates_and_safely_overwrites_a_readable_file()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"items-output-{Guid.NewGuid():N}");
        string output = Path.Combine(directory, "items-sqlite-v1-seed915.json");
        try
        {
            var first = new ItemsBenchmarkResult { Seed = 915, Budget4CoalescedCommitCount = 1 };
            ItemsBenchmarkResult second = first with { Budget4CoalescedOwnedBytes = 9_519 };

            await ItemsBenchmarkOutput.WriteAsync(first, output);
            Assert.Equal(first.ToJson() + Environment.NewLine, await File.ReadAllTextAsync(output, Encoding.UTF8));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));

            await ItemsBenchmarkOutput.WriteAsync(second, output);
            Assert.Equal(second.ToJson() + Environment.NewLine, await File.ReadAllTextAsync(output, Encoding.UTF8));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));

            await Assert.ThrowsAsync<ArgumentException>(() => ItemsBenchmarkOutput.WriteAsync(first, "relative.json"));
            await Assert.ThrowsAsync<ArgumentException>(() => ItemsBenchmarkOutput.WriteAsync(
                first, Path.Combine(directory, "items.txt")));
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task A_quick_run_measures_every_budget_and_coalesces_the_crafts_into_ONE_commit()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"items-quick-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string database = Path.Combine(directory, "items.db");
        string output = Path.Combine(directory, "items-sqlite-v1-seed915.json");
        try
        {
            ItemsBenchmarkConfig config = ItemsBenchmarkConfig.Parse(new[]
            {
                "--items", "--quick", "--players", "2", "--generations", "1000", "--crafts", "20",
                "--seed", "915", "--database", database, "--output", output,
            });
            var console = new StringWriter(CultureInfo.InvariantCulture);

            ItemsBenchmarkResult result = await ItemsBenchmarkRunner.RunAsync(config, console);
            await ItemsBenchmarkOutput.WriteAsync(result, output);

            // Budget 4, the one structural NUMBER here: twenty crafts in one held action are ONE commit, and
            // the same twenty as separate commits are the comparison the saving is read against.
            Assert.Equal(1, result.Budget4CoalescedCommitCount);
            Assert.Equal(config.Crafts, result.Budget4UncoalescedCommitCount);
            Assert.True(
                result.Budget4CoalescedOwnedBytes < result.Budget4UncoalescedOwnedBytes,
                $"coalesced {result.Budget4CoalescedOwnedBytes} must be under uncoalesced {result.Budget4UncoalescedOwnedBytes}.");
            Assert.Equal("Applied", result.Budget4StoreStatus);

            // Everything else is STRUCTURE: present, a real number, and above zero where zero would mean the
            // leg never ran.
            AssertEveryNumberIsReal(result);
            AssertMeasured(result);

            Assert.Equal("quick", result.Mode);
            Assert.Equal("sqlite", result.Provider);
            Assert.Equal("items-sqlite-v1", result.Workload);
            Assert.Equal(915, result.Seed);
            Assert.Equal(2, result.Budget12Players);
            Assert.Equal(2, result.Budget13Players);
            Assert.Equal(config.ThroughputSeconds, result.Budget13Seconds);
            Assert.Equal(config.ScaleInstances, result.ScaleInstances);
            Assert.Equal(config.BankPagesPerPlayer, result.Budget12PagesPerPlayer);
            Assert.Equal(config.ModCount, result.ContentModCount);

            // The two counters the shipped generator and the shipped table build own. Budget 5's is the
            // properties a roll must hold (no mod twice, no group twice, no legacy mod, no tier outside its
            // gate, the per kind caps) and budget 9's is the table build's own live count and live weight
            // self check, so neither is a timing and both are exactly zero or something is wrong.
            Assert.Equal(0, result.Budget5InvariantViolations);
            Assert.Equal(0, result.Budget9ConsistencyFailures);
            Assert.Equal(0, result.Budget13FailureCount);

            // The report beside the numbers names every budget of section 16, which is what makes the console
            // run readable without the JSON.
            string printed = console.ToString();
            for (int budget = 1; budget <= 13; budget++)
                Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"{budget,3} "), printed, StringComparison.Ordinal);

            string written = await File.ReadAllTextAsync(output, Encoding.UTF8);
            Assert.Equal(result.ToJson() + Environment.NewLine, written);
            using JsonDocument reread = JsonDocument.Parse(written);
            Assert.Equal(1, reread.RootElement.GetProperty("budget4CoalescedCommitCount").GetInt32());
        }
        finally
        {
            Delete(directory);
        }
    }

    static void AssertEveryNumberIsReal(ItemsBenchmarkResult result)
    {
        foreach (PropertyInfo property in typeof(ItemsBenchmarkResult).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            object? value = property.GetValue(result);

            // A GetTotalMemory delta is a DIFFERENCE either side of a measurement, so a collection landing
            // inside one makes it legitimately negative. It goes red on whichever run the GC chose, which is
            // the flakiness this whole file exists to avoid, so the sign rule does not apply to it.
            bool signed = property.Name.Contains("TotalMemoryDelta", StringComparison.Ordinal);
            switch (value)
            {
                case double number:
                    Assert.True(double.IsFinite(number), $"{property.Name} is {number}.");
                    Assert.True(signed || number >= 0, $"{property.Name} is {number}.");
                    break;
                case int number:
                    Assert.True(signed || number >= 0, $"{property.Name} is {number}.");
                    break;
                case long number:
                    Assert.True(signed || number >= 0, $"{property.Name} is {number}.");
                    break;
                case string text:
                    Assert.False(string.IsNullOrEmpty(text), $"{property.Name} is empty.");
                    break;
                default:
                    Assert.Fail($"{property.Name} is a {property.PropertyType.Name}, which this sweep does not read.");
                    break;
            }
        }
    }

    static void AssertMeasured(ItemsBenchmarkResult result)
    {
        var missing = new List<string>();
        foreach (string name in MeasuredBudgets)
        {
            PropertyInfo property = typeof(ItemsBenchmarkResult).GetProperty(name)
                ?? throw new InvalidOperationException($"{name} is not a property of the result.");
            double value = Convert.ToDouble(property.GetValue(result), CultureInfo.InvariantCulture);
            if (value <= 0) missing.Add(name);
        }

        Assert.Equal(Array.Empty<string>(), missing.ToArray());
    }

    static void Delete(string directory)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                return;
            }
            catch (IOException)
            {
                System.Threading.Thread.Sleep(50);
            }
        }
    }
}
