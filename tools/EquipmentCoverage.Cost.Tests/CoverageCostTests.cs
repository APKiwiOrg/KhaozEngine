using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using KhaozEngine.Tests.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.CoverageCost;

/// <summary>
/// The only caller of <see cref="CoverageCostHarness.MeasureUnit"/>. Each row is one owner-authorized file committed
/// under authorized/ and named after its case or paired unit. A paired unit runs its two conditions in order in this
/// process. With no committed file the theory is skipped, so this project measures nothing until approval exists. A
/// process measures exactly one selected unit, chosen by an exact filter.
/// </summary>
public sealed class CoverageCostTests(ITestOutputHelper output)
{
    static int started;

    public static IEnumerable<object[]> Authorized() => CoverageCostTheoryAttribute.Cases().Select(name => new object[] { name });

    [CoverageCostTheory]
    [MemberData(nameof(Authorized))]
    public void Measure_authorized_case(string name)
    {
        if (Interlocked.Increment(ref started) > 1)
            throw new InvalidOperationException("Select exactly one authorized unit per run with an exact test filter.");
        string folder = Environment.GetEnvironmentVariable("KE_COVERAGE_COST_REPORTS") is { Length: > 0 } configured
            ? configured : Path.Combine(AppContext.BaseDirectory, "coverage-cost-reports");
        Directory.CreateDirectory(folder);
        string stamp = $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}";
        int condition = 0;
        // Every finished condition is written, flushed and printed before the next starts.
        void Publish(string label, string json)
        {
            using (var file = new FileStream(Path.Combine(folder, $"{name}-{stamp}-{label}.json"), FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(file))
            {
                writer.Write(json);
                writer.Flush();
                file.Flush(true);
            }
            output.WriteLine(json);
            Console.Out.WriteLine(json);
            Console.Out.Flush();
        }
        var (unit, completed, reason) = CoverageCostHarness.MeasureUnit(
            File.ReadAllBytes(Path.Combine(CoverageCostTheoryAttribute.Folder, name + ".json")), name,
            json => Publish($"condition-{++condition}", json));
        Publish("unit", unit);
        Assert.True(completed, reason);
    }
}

/// <summary>Skips the theory when no authorized manifest is present in the build output.</summary>
public sealed class CoverageCostTheoryAttribute : TheoryAttribute
{
    internal static string Folder => Path.Combine(AppContext.BaseDirectory, "authorized");

    internal static IEnumerable<string> Cases() => Directory.Exists(Folder)
        ? Directory.EnumerateFiles(Folder, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.Ordinal)
        : [];

    public CoverageCostTheoryAttribute()
    {
        if (!Cases().Any()) Skip = "No owner-authorized coverage cost manifest is committed under authorized/.";
    }
}
