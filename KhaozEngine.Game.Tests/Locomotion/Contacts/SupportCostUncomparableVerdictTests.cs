using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

/// <summary>SC-18 absolute ceilings and controller bounds for ticks without a comparable legacy result.</summary>
public sealed class SupportCostUncomparableVerdictTests
{
    [Theory]
    [InlineData(0.5)]
    [InlineData(1)]
    [InlineData(2)]
    public void FailedLegacyGeometryUsesOnlyTheScaledAbsoluteCeiling(double scale)
    {
        SupportCostSample tick = Tick("DryTick", 1000 * scale);
        SupportCostSample legacy = tick with { Operation = "LegacyTick", GeometryPassed = false, WarmMicroseconds = scale };
        Assert.Equal(SupportCostGate.Pass, Judge(tick, legacy, scale));
        Assert.Equal(SupportCostGate.Reject, Judge(tick with { WarmMicroseconds = Math.BitIncrement(tick.WarmMicroseconds) }, legacy, scale));
        Assert.Equal("T", SupportCostVerdict.Check(tick with { WarmMicroseconds = 1001 * scale }, legacy, false, scale, 624, true).Codes);
    }

    [Theory]
    [InlineData("DryTick", 0.5)]
    [InlineData("DryTick", 2)]
    [InlineData("ExplicitTick", 0.5)]
    [InlineData("ExplicitTick", 2)]
    public void OutsideComparableEnvelopeUsesTheScaledAbsoluteCeiling(string operation, double scale)
    {
        SupportCostSample tick = Tick(operation, 1000 * scale) with { Comparable = false };
        SupportCostSample legacy = Tick("LegacyTick", scale);
        Assert.Equal(SupportCostGate.Pass, Judge(tick, legacy, scale));
        Assert.Equal(SupportCostGate.Reject, Judge(tick with { WarmMicroseconds = Math.BitIncrement(tick.WarmMicroseconds) }, legacy, scale));
    }

    [Theory]
    [InlineData("DryTick", "substeps", 16384)]
    [InlineData("DryTick", "find", 147495)]
    [InlineData("DryTick", "sweep", 745568)]
    [InlineData("DryTick", "penetration", 598138)]
    [InlineData("ExplicitTick", "substeps", 16384)]
    [InlineData("ExplicitTick", "find", 1196071)]
    [InlineData("ExplicitTick", "sweep", 66904178)]
    [InlineData("ExplicitTick", "penetration", 71172462)]
    public void UncomparableTickRejectsCountsBeyondTheStructuralCap(string operation, string field, int cap)
    {
        SupportCostSample tick = Tick(operation, 1) with { Comparable = false };
        SupportCostSample legacy = Tick("LegacyTick", 1);
        Assert.Equal(SupportCostGate.Pass, Judge(WithCount(tick, field, cap), legacy));
        foreach (int invalid in new[] { -1, cap + 1 })
        {
            SupportCostSample exceeded = WithCount(tick, field, invalid);
            Assert.Equal(SupportCostGate.Reject, Judge(exceeded, legacy));
            Assert.Equal("E", SupportCostVerdict.Check(exceeded, legacy, false, 1, 624, true).Codes);
        }
    }

    [Theory]
    [InlineData("stationary")]
    [InlineData("substeps")]
    [InlineData("segments")]
    [InlineData("mixed")]
    public void EveryOutsideEnvelopeReasonReceivesTheAbsoluteVerdict(string reason)
    {
        SupportCostSample tick = Tick("DryTick", 1000);
        tick = reason switch
        {
            "stationary" => tick with { AchievedMotion = Vector3.Zero },
            "substeps" => tick with { Substeps = 4 },
            "segments" => tick with { Counts = tick.Counts with { ModeSegments = 2 } },
            _ => tick with { Comparable = false },
        };
        Assert.Equal(SupportCostGate.Pass, Judge(tick, Tick("LegacyTick", 1)));
    }

    [Theory]
    [InlineData("geometry")]
    [InlineData("cold geometry")]
    [InlineData("oracle")]
    [InlineData("refusal")]
    [InlineData("allocation")]
    [InlineData("missing pair")]
    [InlineData("legacy time")]
    public void UncomparableRowsKeepEveryOtherPrecondition(string fault)
    {
        SupportCostSample tick = Tick("DryTick", 1) with { Comparable = false };
        SupportCostSample legacy = Tick("LegacyTick", 1) with { GeometryPassed = false };
        tick = fault switch
        {
            "geometry" => tick with { GeometryPassed = false },
            "cold geometry" => tick with { ColdGeometryPassed = false },
            "oracle" => tick with { GeometryMatchesOracle = false },
            "refusal" => tick with { Refusal = true },
            "allocation" => tick with { WarmBytes = 209 },
            _ => tick,
        };
        if (fault == "legacy time") legacy = legacy with { WarmMicroseconds = double.NaN };
        SupportCostSample[] samples = fault == "missing pair" ? [tick] : [tick, legacy];
        Assert.Equal(SupportCostGate.Reject, SupportCostVerdict.JudgeRow(tick, samples, 1, 624, true)!.Value.Gate);
    }

    [Theory]
    [InlineData(0.5, 90, 40)]
    [InlineData(2, 280, 80)]
    [InlineData(1, 625, 500)]
    public void ComparableTickRetainsTheLegacyRelativeLimit(double scale, double limit, double legacyTime)
    {
        SupportCostSample tick = Tick("DryTick", limit), legacy = Tick("LegacyTick", legacyTime);
        Assert.Equal(SupportCostGate.Pass, Judge(tick, legacy, scale));
        Assert.Equal(SupportCostGate.Reject, Judge(tick with { WarmMicroseconds = Math.BitIncrement(limit) }, legacy, scale));
    }

    [Theory]
    [InlineData("376971dda", "2026-10-14-support-cost-hosted-1334.md", 108, 31)]
    [InlineData("5c373abe9", "2026-10-17-support-cost-hosted-1334-option-c.md", 125, 14)]
    public void CorrectedHostedRecordsChangeOnlyPreviouslyEOrLRows(string commit, string record, int passes, int rejects)
    {
        string? root = Directory.GetCurrentDirectory();
        while (root is not null && !File.Exists(Path.Combine(root, "KhaozEngine.slnx"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        string directory = Path.Combine(root, "docs", "verification");
        JsonObject reference = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "2026-10-11-support-gates-cost.json")))!
            ["reference-" + commit]!.AsObject();
        var options = new JsonSerializerOptions { IncludeFields = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        SupportCostSample[] samples = reference["Samples"]!.Deserialize<SupportCostSample[]>(options)!;
        string text = File.ReadAllText(Path.Combine(directory, record));
        if (commit == "376971dda") text = text[text.IndexOf("## Corrected calibration", StringComparison.Ordinal)..];
        int passed = 0, rejected = 0, judged = 0, changed = 0;
        foreach (string line in text.Split('\n').Where(line => line.StartsWith("| ", StringComparison.Ordinal)))
        {
            string[] cells = line.Split('|').Skip(1).SkipLast(1).Select(cell => cell.Trim()).ToArray();
            if (cells.Length < 8 || !double.TryParse(cells[2], CultureInfo.InvariantCulture, out double candidate)) continue;
            string operation = LongOperation(cells[1]);
            if (!SupportCostVerdict.IsGated(operation)) continue;
            int verdictColumn = commit == "376971dda" ? 6 : 7;
            string original = cells[verdictColumn];
            if (original != "PASS" && !original.StartsWith("FAIL ", StringComparison.Ordinal)) continue;
            SupportCostSample sample = samples.Single(sample => sample.Scene == cells[0] && sample.Operation == operation)
                with { WarmMicroseconds = candidate };
            string pairedOperation = operation == "Find" ? "LegacyDownPass" : "LegacyTick";
            SupportCostSample legacy = samples.Single(sample => sample.Scene == cells[0] && sample.Operation == pairedOperation);
            legacy = legacy with { WarmMicroseconds = double.Parse(cells[3], CultureInfo.InvariantCulture) };
            SupportCostCheck check = SupportCostVerdict.Check(sample, legacy, false, 1, 624, true);
            string current = check.Passed ? "PASS" : "FAIL " + check.Codes;
            string[] originalCodes = original.StartsWith("FAIL ", StringComparison.Ordinal) ? original[5..].Split(',') : [];
            bool exception = originalCodes.Contains("E") || originalCodes.Contains("L");
            if (!exception) Assert.Equal(original, current);
            if (original != current) changed++;
            if (check.Passed) passed++; else rejected++;
            judged++;
        }
        Assert.Equal(139, judged);
        Assert.True(changed > 0);
        Assert.Equal(passes, passed);
        Assert.Equal(rejects, rejected);
    }

    static SupportCostSample Tick(string operation, double time) => SupportGateCostTests.Synthetic(operation, time) with
    { ColdGeometryPassed = true, GeometryMatchesOracle = true };

    static SupportCostGate Judge(SupportCostSample tick, SupportCostSample legacy, double scale = 1) =>
        SupportCostVerdict.JudgeRow(tick, [tick, legacy], scale, 624, true)!.Value.Gate;

    static SupportCostSample WithCount(SupportCostSample tick, string field, int value) => field switch
    {
        "substeps" => tick with { Substeps = value },
        "find" => tick with { Counts = tick.Counts with { Find = value } },
        "sweep" => tick with { Queries = tick.Queries with { Sweeps = value } },
        _ => tick with { Queries = tick.Queries with { Penetrations = value } },
    };

    static string LongOperation(string operation) => operation switch
    {
        "B0" => "GroundSubstep/GroundTickBorrowed/0", "B1" => "GroundSubstep/GroundTickBorrowed/1",
        "D0" => "GroundSubstep/DryTick/0", "D1" => "GroundSubstep/DryTick/1",
        "E0" => "GroundSubstep/ExplicitTick/0", "E1" => "GroundSubstep/ExplicitTick/1",
        "Dry" => "DryTick", "Explicit" => "ExplicitTick", _ => operation,
    };
}
