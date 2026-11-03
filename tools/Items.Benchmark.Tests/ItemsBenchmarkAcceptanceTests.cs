using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.Benchmarks.Items;
using Xunit;

namespace KhaozEngine.Tests.Benchmarks;

/// <summary>
/// Explicit end-to-end acceptance for the Items instrument and workload structure before publishing evidence.
/// Budget 12 reads process-wide resident memory and forces collections, so this fact runs alone in its own
/// test process under the assembly-local AllocSensitive definition. It keeps 200 resident players above
/// the instrument's 4 MiB noise band and two players in the busy-work leg. See engine issue #1030.
/// It validates the benchmark structure, not a performance result or authorization for a full campaign.
/// </summary>
[Collection("AllocSensitive")]
public sealed class ItemsBenchmarkAcceptanceTests
{
    [Fact]
    public async Task A_quick_run_measures_every_budget_and_coalesces_the_crafts_into_ONE_commit()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"items-quick-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string database = Path.Combine(directory, "items.db");
        string output = Path.Combine(directory, "items-sqlite-v1-seed915.json");
        try
        {
            // Budget 12 is a live heap difference, and two idle readings of that instrument may disagree by
            // ResidentMemoryTests.NoiseBytes, 4 MiB. Two players hold about 113 KB of pages, so their sign was
            // noise: hosted Metal read the admitted layer as -28,032 bytes (#1380). The quick default of 200
            // holds about 11 MB of pages and 12 MB more in the admitted layer, clear of that band, while budget
            // 13 keeps two players so its busy loop stays cheap.
            ItemsBenchmarkConfig config = ItemsBenchmarkConfig.Parse(new[]
            {
                "--items", "--quick", "--players", "2", "--resident-players", "200", "--generations", "1000",
                "--crafts", "20", "--seed", "915", "--database", database, "--output", output,
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
            Assert.Equal(200, result.Budget12Players);
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
            ItemsBenchmarkFixtureDirectory.Delete(directory);
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
        foreach (string name in ItemsBenchmarkFields.MeasuredBudgets)
        {
            PropertyInfo property = typeof(ItemsBenchmarkResult).GetProperty(name)
                ?? throw new InvalidOperationException($"{name} is not a property of the result.");
            double value = Convert.ToDouble(property.GetValue(result), CultureInfo.InvariantCulture);
            if (value <= 0) missing.Add(name);
        }

        Assert.Equal(Array.Empty<string>(), missing.ToArray());
    }
}
