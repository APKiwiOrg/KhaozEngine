using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.Locomotion.Contacts;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

/// <summary>Re-evaluates finalized hosted artifacts with the current verdict and writes the Markdown tables
/// for the hosted record. The record runs only when <c>KHAOZ_SUPPORT_COST_HOSTED_RUNS</c> names the artifact
/// directory. It reads data only and never measures a locomotion operation.</summary>
public sealed class SupportCostHostedRecordTests
{
    sealed record Row(string Scene, string Operation, bool Dense, long Lifetime, SupportCostSample[] Samples,
        SupportCostSample?[] Legacy, SupportCostGate[] Gates, double[] Scales);

    [Fact]
    public void MedianVerdictRejectsALaterIterationBeyondTheStructuralCap()
    {
        SupportCostSample tick = SupportGateCostTests.Synthetic("DryTick", 1) with
        { ColdGeometryPassed = true, GeometryMatchesOracle = true, Comparable = false };
        SupportCostSample legacy = tick with { Operation = "LegacyTick" };
        SupportCostSample over = tick with { Counts = tick.Counts with { Find = SupportCostTickBound.For(false).Finds + 1 } };
        Row Make(SupportCostSample last) => new(tick.Scene, tick.Operation, false, 624, [tick, tick, last],
            [legacy, legacy, legacy], [SupportCostGate.Pass, SupportCostGate.Pass, SupportCostGate.Pass], [1, 1, 1]);
        Assert.True(MedianVerdict(Make(tick)).Passed);
        Verdict verdict = MedianVerdict(Make(over));
        Assert.False(verdict.Passed);
        Assert.Equal("E", verdict.Codes);
    }

    [Fact]
    public void HostedRecordWithCorrectedCalibration()
    {
        string? directory = Environment.GetEnvironmentVariable("KHAOZ_SUPPORT_COST_HOSTED_RUNS");
        if (string.IsNullOrEmpty(directory)) return;
        string commit = Environment.GetEnvironmentVariable("KHAOZ_SUPPORT_COST_REFERENCE_COMMIT") ?? "";
        string reportPath = Environment.GetEnvironmentVariable("KHAOZ_SUPPORT_COST_HOSTED_REPORT") ?? "";
        Assert.False(string.IsNullOrEmpty(reportPath), "KHAOZ_SUPPORT_COST_HOSTED_REPORT names the Markdown output.");
        string? root = Directory.GetCurrentDirectory();
        while (root is not null && !File.Exists(Path.Combine(root, "KhaozEngine.slnx"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        JsonObject? reference = SupportCostArtifact.ReadReference(
            Path.Combine(root, "docs", "verification", "2026-10-11-support-gates-cost.json"), commit);
        Assert.True(reference is not null, $"No finalized {SupportCostArtifact.ReferenceSection(commit)} section.");

        string[] files = Directory.GetFiles(directory, "run-*.json")
            .OrderBy(file => int.Parse(Path.GetFileNameWithoutExtension(file)[4..], CultureInfo.InvariantCulture)).ToArray();
        Assert.NotEmpty(files);
        var scales = new HostedScale[files.Length];
        var rows = new Dictionary<(string, string), Row>();
        for (int i = 0; i < files.Length; i++)
        {
            JsonObject hosted = JsonNode.Parse(File.ReadAllText(files[i]))!["baseline"]!.AsObject();
            Assert.StartsWith(commit, hosted["CandidateSha"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.True(SupportCostVerdict.TryJudgeHosted(reference, hosted, out scales[i], out var judged), $"{files[i]} does not calibrate.");
            long lifetime = hosted["LifetimeInventory"]!["AcquiredExplicitTickBytes"]!.GetValue<long>();
            foreach (var (sample, (legacy, gate)) in judged)
            {
                if (!rows.TryGetValue((sample.Scene, sample.Operation), out Row? row))
                    rows[(sample.Scene, sample.Operation)] = row = new(sample.Scene, sample.Operation, sample.Dense, lifetime,
                        new SupportCostSample[files.Length], new SupportCostSample?[files.Length], new SupportCostGate[files.Length], new double[files.Length]);
                row.Samples[i] = sample;
                row.Legacy[i] = legacy;
                row.Gates[i] = gate;
                row.Scales[i] = scales[i].K;
            }
        }
        Assert.All(rows.Values, row => Assert.All(row.Samples, sample => Assert.NotNull(sample)));
        Assert.All(rows.Values, row => Assert.All(row.Legacy, legacy => Assert.NotNull(legacy)));
        // Allocation and eligibility fields are deterministic, so the median row reuses the first iteration's.
        Assert.All(rows.Values, row => Assert.All(row.Samples, sample => Assert.Equal(row.Samples[0].WarmBytes, sample.WarmBytes)));
        // Envelope classification uses deterministic fields only, so every iteration must agree.
        Assert.All(rows.Values, row => Assert.Equal(row.Gates.Contains(SupportCostGate.Envelope), row.Gates.All(gate => gate == SupportCostGate.Envelope)));
        CultureInfo culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            File.WriteAllText(reportPath, Report(scales, rows.Values.ToArray(), files.Length));
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    static string Report(HostedScale[] scales, Row[] rows, int iterations)
    {
        var text = new StringBuilder();
        Row[] ordinary = rows.Where(row => !row.Dense && row.Gates[0] != SupportCostGate.Envelope).ToArray();
        Row[] envelope = rows.Where(row => row.Gates[0] == SupportCostGate.Envelope).ToArray();
        Row[] dense = rows.Where(row => row.Dense && row.Gates[0] != SupportCostGate.Envelope).ToArray();
        text.AppendLine("| Iteration | k | Rows | Lower quartile | Upper quartile | Ordinary pass | Ordinary reject | Envelope |");
        text.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        for (int i = 0; i < iterations; i++)
            text.AppendLine(($"| {i + 1} | {scales[i].K:F6} | {scales[i].Rows} | {scales[i].LowerQuartile:F6} | {scales[i].UpperQuartile:F6} | " +
                $"{ordinary.Count(row => row.Gates[i] == SupportCostGate.Pass)} | {ordinary.Count(row => row.Gates[i] == SupportCostGate.Reject)} | " +
                $"{envelope.Length} |"));
        text.AppendLine();
        var verdicts = ordinary.Select(row => (Row: row, Median: MedianVerdict(row))).ToArray();
        text.AppendLine(($"Median rows: {verdicts.Count(v => v.Median.Passed)} ordinary pass, {verdicts.Count(v => !v.Median.Passed)} ordinary reject, " +
            $"{envelope.Length} envelope, {dense.Length} dense without a time verdict."));
        text.AppendLine();
        text.AppendLine("| Family | Rows | Pass | Reject | Median C / limit |");
        text.AppendLine("| --- | ---: | ---: | ---: | ---: |");
        foreach (string family in new[] { "Find", "Substep", "Whole tick" })
        {
            var members = verdicts.Where(v => Family(v.Row.Operation) == family).ToArray();
            text.AppendLine(($"| {family} | {members.Length} | {members.Count(v => v.Median.Passed)} | {members.Count(v => !v.Median.Passed)} | " +
                $"{Median(members.Select(v => v.Median.Candidate / v.Median.Limit)):F3} |"));
        }
        text.AppendLine();
        text.AppendLine("| Scene | Operation | Median C | Median L | Limit | C / limit | Verdict | Pass/" + iterations + " |");
        text.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | --- | ---: |");
        foreach (var (row, median) in verdicts.Where(v => ComparisonBasis(v.Row).Length == 0))
            text.AppendLine(($"| {row.Scene} | {Short(row.Operation)} | {median.Candidate:F3} | {median.Legacy:F3} | {median.Limit:F3} | " +
                $"{median.Candidate / median.Limit:F3} | {(median.Passed ? "PASS" : "FAIL " + median.Codes)} | {row.Gates.Count(g => g == SupportCostGate.Pass)} |"));
        text.AppendLine();
        text.AppendLine("| E/L scene | Operation | Basis | Median C | Absolute limit | Substeps / cap | Find / cap | Sweeps / cap | Penetrations / cap | Bound | Verdict | Pass/" + iterations + " |");
        text.AppendLine("| --- | --- | --- | ---: | ---: | --- | --- | --- | --- | --- | --- | ---: |");
        foreach (var (row, median) in verdicts.Where(v => ComparisonBasis(v.Row).Length > 0))
        {
            bool bounded = SupportCostVerdict.OutsideComparableEnvelope(row.Samples[0]);
            SupportCostTickBound bound = SupportCostTickBound.For(row.Operation == "ExplicitTick");
            string Counts(Func<SupportCostSample, long> select, int cap) =>
                $"{row.Samples.Min(select)} to {row.Samples.Max(select)} / {(bounded ? cap.ToString(CultureInfo.InvariantCulture) : "n/a")}";
            text.AppendLine(($"| {row.Scene} | {Short(row.Operation)} | {ComparisonBasis(row)} | {median.Candidate:F3} | {median.Limit:F3} | " +
                $"{Counts(s => s.Substeps, bound.Substeps)} | {Counts(s => s.Counts.Find, bound.Finds)} | " +
                $"{Counts(s => s.Queries.Sweeps, bound.Sweeps)} | {Counts(s => s.Queries.Penetrations, bound.Penetrations)} | " +
                $"{(bounded ? SupportCostVerdict.EveryIterationBounded(row.Samples) ? "PASS" : "FAIL" : "not required")} | " +
                $"{(median.Passed ? "PASS" : "FAIL " + median.Codes)} | {row.Gates.Count(g => g == SupportCostGate.Pass)} |"));
        }
        text.AppendLine();
        text.AppendLine("| Envelope scene | Operation | Outcome | Oracle | Reads | Median C |");
        text.AppendLine("| --- | --- | --- | --- | --- | ---: |");
        foreach (Row row in envelope)
        {
            SupportCostSample first = row.Samples[0];
            string reads = string.Join(" ", first.SupportReads.Select(read => ((SupportStatus)read.Status).ToString()));
            text.AppendLine(($"| {row.Scene} | {Short(row.Operation)} | {first.Outcome} | {(first.OracleOutcome.Length == 0 ? "reads" : first.OracleOutcome)} | " +
                $"{(reads.Length == 0 ? "none" : reads)} | {Median(Equivalent(row, false)):F3} |"));
        }
        text.AppendLine();
        text.AppendLine("| Dense scene | Operation | Median C | Median L | Warm bytes | Allocation |");
        text.AppendLine("| --- | --- | ---: | ---: | ---: | --- |");
        foreach (Row row in dense)
        {
            SupportCostSample first = row.Samples[0];
            bool allocation = Enumerable.Range(0, iterations).All(i => !SupportCostVerdict.Check(row.Samples[i], row.Legacy[i]!,
                true, row.Scales[i], row.Lifetime, hosted: true).Allocation);
            text.AppendLine(($"| {row.Scene} | {Short(row.Operation)} | {Median(Equivalent(row, false)):F3} | {Median(Equivalent(row, true)):F3} | " +
                $"{first.WarmBytes} | {(allocation ? "PASS" : "FAIL")} |"));
        }
        return text.ToString();
    }

    readonly record struct Verdict(double Candidate, double Legacy, double Limit, bool Passed, string Codes);

    static string ComparisonBasis(Row row) => row.Operation is "DryTick" or "ExplicitTick"
        ? string.Join(",", new[] { (SupportCostVerdict.OutsideComparableEnvelope(row.Samples[0]), "E"),
            (!SupportCostVerdict.OutsideComparableEnvelope(row.Samples[0]) && SupportCostVerdict.LegacyHasNoSupport(row.Legacy[0]!), "L") }
            .Where(reason => reason.Item1).Select(reason => reason.Item2)) : "";

    /// <summary>Median of each candidate and legacy time divided by its own iteration's k, judged at k = 1.</summary>
    static Verdict MedianVerdict(Row row)
    {
        double candidate = Median(Equivalent(row, false)), legacy = Median(Equivalent(row, true));
        SupportCostSample sample = row.Samples[0] with { WarmMicroseconds = candidate };
        SupportCostSample pair = row.Legacy[0]! with { WarmMicroseconds = legacy };
        SupportCostGate gate = SupportCostVerdict.JudgeRow(sample, [sample, pair], 1, row.Lifetime, hosted: true)!.Value.Gate;
        SupportCostCheck check = SupportCostVerdict.Check(sample, pair, sample.Dense, 1, row.Lifetime, hosted: true);
        Assert.Equal(check.Passed, gate == SupportCostGate.Pass);
        check = check with { Bound = check.Bound || !SupportCostVerdict.EveryIterationBounded(row.Samples) };
        return new(candidate, legacy, check.TimeLimit, check.Passed, check.Codes);
    }

    static IEnumerable<double> Equivalent(Row row, bool legacy) => Enumerable.Range(0, row.Samples.Length)
        .Select(i => (legacy ? row.Legacy[i]?.WarmMicroseconds ?? double.NaN : row.Samples[i].WarmMicroseconds) / row.Scales[i]);

    static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.OrderBy(value => value).ToArray();
        if (sorted.Length == 0) return double.NaN;
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    static string Family(string operation) => operation == "Find" ? "Find"
        : operation.StartsWith("GroundSubstep", StringComparison.Ordinal) ? "Substep" : "Whole tick";

    static string Short(string operation) => operation switch
    {
        "GroundSubstep/GroundTickBorrowed/0" => "B0",
        "GroundSubstep/GroundTickBorrowed/1" => "B1",
        "GroundSubstep/DryTick/0" => "D0",
        "GroundSubstep/DryTick/1" => "D1",
        "GroundSubstep/ExplicitTick/0" => "E0",
        "GroundSubstep/ExplicitTick/1" => "E1",
        "DryTick" => "Dry",
        "ExplicitTick" => "Explicit",
        _ => operation,
    };
}
