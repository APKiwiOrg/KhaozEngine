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
/// Default engine CI fences for Items options, limits, declared budgets, stable JSON and atomic output.
/// The four ResidentMemoryTests calibration and regression facts also remain in Server.Tests.
/// The end-to-end quick run lives in the outside-solution Items.Benchmark.Tests project and gates
/// explicit benchmark validation before new evidence is published.
/// </summary>
[Collection("AllocSensitive")]
public sealed class ItemsBenchmarkTests
{
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
        Assert.Equal(2_000, config.ResidentPlayers);
        Assert.Equal(5_000, config.Generations);
        Assert.Equal(7, config.Crafts);
        Assert.Equal(915, config.Seed);
        Assert.Equal(database, config.DatabasePath);
        Assert.Equal(output, config.OutputPath);

        Assert.Throws<ArgumentOutOfRangeException>(() => ItemsBenchmarkConfig.Parse(
            new[] { "--items", "--players", (ItemsBenchmarkConfig.MaximumPlayers + 1).ToString(CultureInfo.InvariantCulture) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ItemsBenchmarkConfig.Parse(new[] { "--items", "--players", "0" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ItemsBenchmarkConfig.Parse(new[] { "--items", "--resident-players", "0" }));
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
        Assert.Equal(3, asked.ResidentPlayers);
        Assert.Equal(quick.Players, quick.ResidentPlayers);
        ItemsBenchmarkConfig apart = ItemsBenchmarkConfig.Parse(new[] { "--items", "--players", "3", "--resident-players", "40" });
        Assert.Equal(3, apart.Players);
        Assert.Equal(40, apart.ResidentPlayers);
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
        // every field of theirs is either non-zero in a clean run, in ItemsBenchmarkFields.MeasuredBudgets,
        // or named here with the reason it may legitimately sit at zero or below it. A field in neither list
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
            if (!ItemsBenchmarkFields.MeasuredBudgets.Contains(property.Name, StringComparer.Ordinal)
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

    static void Delete(string directory) => ItemsBenchmarkFixtureDirectory.Delete(directory);
}
