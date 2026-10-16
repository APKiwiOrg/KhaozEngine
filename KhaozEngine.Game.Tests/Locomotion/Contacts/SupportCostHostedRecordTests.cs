using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KhaozEngine.Locomotion.Contacts;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

/// <summary>Re-evaluates finalized hosted artifacts with the current verdict and writes the Markdown tables
/// for the hosted record. Runs only when <c>KHAOZ_SUPPORT_COST_HOSTED_RUNS</c> names the artifact directory.
/// It reads data only and never measures a locomotion operation.</summary>
public sealed class SupportCostHostedRecordTests
{
    static readonly JsonSerializerOptions Options = new()
    { IncludeFields = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    sealed record Row(string Scene, string Operation, bool Dense, long Lifetime, SupportCostSample[] Samples,
        SupportCostSample?[] Legacy, SupportCostGate[] Gates, double[] Scales);

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
            Assert.True(SupportCostVerdict.TryHostedScale(reference, hosted, out scales[i]), $"{files[i]} does not calibrate.");
            long lifetime = hosted["LifetimeInventory"]!["AcquiredExplicitTickBytes"]!.GetValue<long>();
            SupportCostSample[] samples = hosted["Samples"]!.Deserialize<SupportCostSample[]>(Options)!;
            foreach (SupportCostSample sample in samples)
            {
                if (sample.Operation is not ("Find" or "DryTick" or "ExplicitTick") &&
                    !sample.Operation.StartsWith("GroundSubstep", StringComparison.Ordinal)) continue;
                string pair = sample.Operation == "Find" ? "LegacyDownPass" : "LegacyTick";
                SupportCostSample? legacy = samples.FirstOrDefault(s => s.Scene == sample.Scene && s.Operation == pair);
                // The same preconditions SupportCostArtifact.Write applies to a hosted row.
                SupportCostGate gate = legacy is null ? SupportCostGate.Reject
                    : SupportCostVerdict.Evaluate(sample, legacy, sample.Dense, scales[i].K, lifetime);
                if (gate == SupportCostGate.Pass && !(sample.ColdGeometryPassed && sample.GeometryMatchesOracle)) gate = SupportCostGate.Reject;
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
        foreach (var (row, median) in verdicts)
            text.AppendLine(($"| {row.Scene} | {Short(row.Operation)} | {median.Candidate:F3} | {median.Legacy:F3} | {median.Limit:F3} | " +
                $"{median.Candidate / median.Limit:F3} | {(median.Passed ? "PASS" : "FAIL " + median.Codes)} | {row.Gates.Count(g => g == SupportCostGate.Pass)} |"));
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
            long limit = first.Operation switch { "DryTick" => 208, "ExplicitTick" => row.Lifetime, _ => 0 };
            bool allocation = row.Samples.All(sample => sample.WarmBytes <= limit);
            text.AppendLine(($"| {row.Scene} | {Short(row.Operation)} | {Median(Equivalent(row, false)):F3} | {Median(Equivalent(row, true)):F3} | " +
                $"{first.WarmBytes} | {(allocation ? "PASS" : "FAIL")} |"));
        }
        return text.ToString();
    }

    readonly record struct Verdict(double Candidate, double Legacy, double Limit, bool Passed, string Codes);

    /// <summary>Median of each candidate and legacy time divided by its own iteration's k, judged at k = 1.</summary>
    static Verdict MedianVerdict(Row row)
    {
        double candidate = Median(Equivalent(row, false)), legacy = Median(Equivalent(row, true));
        SupportCostSample sample = row.Samples[0] with { WarmMicroseconds = candidate };
        SupportCostSample? pair = row.Legacy[0] is { } l ? l with { WarmMicroseconds = legacy } : null;
        bool passed = pair is not null && sample.ColdGeometryPassed && sample.GeometryMatchesOracle &&
            SupportCostVerdict.Evaluate(sample, pair, false, 1, row.Lifetime) == SupportCostGate.Pass;
        double limit = Family(row.Operation) switch
        {
            "Find" => 50,
            "Substep" => 200,
            _ => Math.Min(1000, legacy + Math.Max(100, 0.25 * legacy)),
        };
        var codes = new List<string>();
        if (candidate > limit) codes.Add("T");
        if (!sample.GeometryPassed || sample.Refusal || !sample.ColdGeometryPassed || !sample.GeometryMatchesOracle) codes.Add("G");
        if (Family(row.Operation) == "Whole tick" && (sample.AchievedMotion.X == 0 && sample.AchievedMotion.Z == 0 ||
            sample.Substeps > 3 || sample.Counts.ModeSegments > 1 || !sample.Comparable)) codes.Add("E");
        if (Family(row.Operation) == "Whole tick" && pair is { GeometryPassed: false }) codes.Add("L");
        return new(candidate, legacy, limit, passed, string.Join(",", codes));
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
