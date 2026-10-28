using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

[Collection("GpuBackendProvidersSerial")]
public sealed class RendererPerfProfilePublicationTests : IClassFixture<RendererPerfBaselineFixture>
{
    readonly RendererPerfBaselineFixture _fixture;
    public RendererPerfProfilePublicationTests(RendererPerfBaselineFixture fixture) => _fixture = fixture;

    RendererPerfProfileRequest Request() => new("khaozengine.renderer-profile-request/1", _fixture.Output(), _fixture.ProfileLegs());

    [Fact]
    public void Deltas_pair_profiles_inside_one_unit()
    {
        var summary = RendererPerfAnalysis.Analyze(RendererPerfProfileFixture.Report());
        var rows = RendererPerfProfileComparison.Compare(summary);
        Assert.Equal(32, rows.Length);
        var pairs = new[] { ("S0", "S1", "village OFF to M2 ON", 116d), ("S1", "S2", "village M2 ON to M5 ON", 115d),
            ("S3", "S4", "outdoors OFF to M2 ON", 116d), ("S4", "S5", "outdoors M2 ON to M5 ON", 116d) };
        foreach (var (baseline, candidate, label, combinedP95) in pairs)
            foreach (var layout in new[] { RendererPerfLayout.TotalOnly, RendererPerfLayout.PerPass })
                foreach (int? iteration in new int?[] { 0, 1, 2, null })
                {
                    var row = Assert.Single(rows, r => r.BaselineSceneId == baseline && r.CandidateSceneId == candidate
                        && r.Layout == layout && r.Iteration == iteration);
                    Assert.Equal(label, row.Label);
                    var delta = row.Deltas["gpu.frame"];
                    Assert.Equal(108.5, delta.Mean.Value);
                    Assert.Equal(108.5, delta.Median.Value);
                    Assert.Equal(iteration is null ? combinedP95 : 116, delta.P95.Value);
                    Assert.Equal(116, delta.P99.Value);
                    int scene = int.Parse(baseline.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture);
                    double before = 108.5 * (scene + 1) + 2 * (iteration ?? 1) + (layout == RendererPerfLayout.PerPass ? 5 : 0);
                    Assert.Equal(10850 / before, delta.Percent.Value!.Value, 10);
                    Assert.Equal(0, row.Deltas["cpu.frame"].Mean.Value);
                    if (layout == RendererPerfLayout.PerPass)
                    {
                        var pass = row.Deltas["gpu.pass.opaque.0"];
                        Assert.Equal(10, pass.Mean.Value);
                        Assert.Equal(10, pass.Median.Value);
                        Assert.Equal(10, pass.P95.Value);
                        Assert.Equal(10, pass.P99.Value);
                        Assert.Equal(1000 / (10 * (scene + 1) + 2 * (iteration ?? 1) + 7.5), pass.Percent.Value!.Value, 10);
                    }
                }
    }

    [Fact]
    public void Comparison_inspects_rehearsal_and_intersects_metrics_without_inventing_available_values()
    {
        var summary = RendererPerfAnalysis.Analyze(RendererPerfProfileFixture.Report());
        var zero = RendererPerfAnalysis.Distribution([RendererPerfAnalysisFixture.Value(0)], 1);
        var unsupported = RendererPerfAnalysis.Distribution([new(null, "ns", "synthetic outer pair",
            RendererPerfMetricStatus.Unsupported, "synthetic counter unavailable")], 1);
        summary = summary with
        {
            PromotionEligible = false,
            Blocks = [.. summary.Blocks.Select(b => b with { Metrics = b.Metrics.Add("zero", zero)
                .Add("unavailable", b.Block.SceneId == "S1" ? unsupported : zero)
                .Add(b.Block.SceneId == "S0" ? "only-baseline" : "only-candidate", zero) })]
        };
        var row = RendererPerfProfileComparison.Compare(summary).First(r => r.BaselineSceneId == "S0" && r.Iteration == 0);
        Assert.False(row.Deltas.ContainsKey("only-baseline"));
        Assert.False(row.Deltas.ContainsKey("only-candidate"));
        Assert.Null(row.Deltas["zero"].Percent.Value);
        Assert.Null(row.Deltas["unavailable"].Mean.Value);
        Assert.Contains("synthetic counter unavailable", row.Deltas["unavailable"].Mean.Reason);
        var rehearsal = _fixture.Create("linux-vulkan", "profile-comparison-rehearsal", "rehearsal-water-optics-v2", 1);
        var descriptive = RendererPerfAnalysis.ReadDirectory(rehearsal.Directory, rehearsal.ReportName);
        Assert.False(descriptive.PromotionEligible);
        Assert.Equal(32, RendererPerfProfileComparison.Compare(descriptive).Length);
        Assert.All(RendererPerfProfileComparison.Compare(descriptive), r => Assert.Equal(108.5, r.Deltas["gpu.frame"].Mean.Value));
        Assert.Empty(RendererPerfProfileComparison.Compare(RendererPerfAnalysis.Analyze(RendererPerfAnalysisFixture.Report(full: true))));
    }

    [Fact]
    public void Publication_matches_the_golden()
    {
        var request = Request();
        var files = RendererPerfProfilePublisher.Publish(request);
        Assert.Equal(new[] { "linux-vulkan-profiles-linux.md", "windows-d3d11-profiles-windows.md",
            "profiles-profiles-linux-profiles-windows.md", "index-profiles-linux-profiles-windows.md" }, files);
        foreach (string name in files)
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(request.OutputDirectory, name));
            string text = Encoding.UTF8.GetString(bytes);
            Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
            Assert.DoesNotContain('\r', text);
            Assert.DoesNotContain(_fixture.Root, text);
            if (name.StartsWith("profiles-", StringComparison.Ordinal) || name.StartsWith("index-", StringComparison.Ordinal))
                Assert.Equal(Encoding.UTF8.GetBytes(Expected(name.StartsWith("profiles-", StringComparison.Ordinal) ? "profiles" : "index")), bytes);
            else
            {
                var leg = request.Legs.Single(l => name.StartsWith(l.Leg, StringComparison.Ordinal));
                Assert.Contains("Actual billed minutes " + leg.BilledMinutes + ".", text);
                Assert.Contains("\"preset\":\"water-optics-v2\"", text);
                Assert.Contains("\"reportSha256\":\"" + RendererPerfRecipe.Hash(File.ReadAllBytes(Path.Combine(leg.Directory, leg.ReportName))) + "\"", text);
            }
        }
        var reversed = request with { OutputDirectory = _fixture.Output(), Legs = [.. request.Legs.Reverse()] };
        Assert.Equal(files.ToArray(), RendererPerfProfilePublisher.Publish(reversed).ToArray());
        foreach (string name in files)
            Assert.Equal(File.ReadAllBytes(Path.Combine(request.OutputDirectory, name)), File.ReadAllBytes(Path.Combine(reversed.OutputDirectory, name)));
    }

    static string Expected(string name, [System.Runtime.CompilerServices.CallerFilePath] string source = "")
        => File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!, "../../Fixtures/renderer-profiles", name + ".expected.txt"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    [Theory]
    [InlineData("full", "NotProfile")]
    [InlineData("rehearsal", "Rehearsal")]
    [InlineData("workload", "Pair")]
    [InlineData("baseline-source", "Pair")]
    [InlineData("failed-promotion", "Promotion")]
    [InlineData("failed-publication", "EvidenceInvalid")]
    [InlineData("missing-sidecar", "EvidenceInvalid")]
    [InlineData("foreign", "Identity")]
    [InlineData("native-compiler", "Identity")]
    [InlineData("run-url", "Identity")]
    [InlineData("leg", "Identity")]
    [InlineData("linux-billing", "Billing")]
    [InlineData("windows-billing", "Billing")]
    [InlineData("zero-billing", "Billing")]
    [InlineData("negative-billing", "Billing")]
    [InlineData("pair", "Pair")]
    [InlineData("duplicate", "Pair")]
    [InlineData("schema", "Request")]
    [InlineData("output", "Request")]
    public void Publication_refuses_what_a_baseline_refuses(string fault, string code)
    {
        var request = Request();
        var leg = request.Legs[0];
        if (fault is "workload" or "baseline-source" or "failed-promotion" or "failed-publication" or "missing-sidecar" or "native-compiler")
            leg = _fixture.Create("linux-vulkan", "profile-fault-" + fault, "water-optics-v2", 38, fault);
        if (fault == "full") leg = _fixture.Request().Legs[0];
        if (fault == "rehearsal") leg = _fixture.Create("linux-vulkan", "profile-refused-rehearsal", "rehearsal-water-optics-v2", 1);
        if (fault == "foreign") leg = leg with { ManifestSha256 = new('f', 64) };
        if (fault == "run-url") leg = leg with { RunUrl = "https://github.com/foreign/repo/actions/runs/123/attempts/1" };
        if (fault == "leg") leg = leg with { Leg = "windows-d3d11" };
        if (fault == "linux-billing") leg = leg with { BilledMinutes = 39 };
        if (fault == "zero-billing") leg = leg with { BilledMinutes = 0 };
        if (fault == "negative-billing") leg = leg with { BilledMinutes = -1 };
        var windows = request.Legs[1] with { BilledMinutes = fault == "windows-billing" ? 54 : 53 };
        request = request with { Legs = fault == "pair" ? [leg] : fault == "duplicate" ? [leg, leg] : [leg, windows] };
        if (fault == "schema") request = request with { Schema = "khaozengine.renderer-baseline-request/1" };
        if (fault == "output") request = request with { OutputDirectory = " " };
        Assert.Equal(code, Assert.Throws<RendererPerfBaselineException>(() => RendererPerfProfilePublisher.Publish(request)).Code);
        Assert.False(Directory.Exists(request.OutputDirectory));
    }

    [Theory]
    [InlineData("linux-vulkan-profiles-linux.md")]
    [InlineData("windows-d3d11-profiles-windows.md")]
    [InlineData("profiles-profiles-linux-profiles-windows.md")]
    [InlineData("index-profiles-linux-profiles-windows.md")]
    public void Existing_output_refuses_before_any_file_is_written(string existingName)
    {
        var request = Request();
        Directory.CreateDirectory(request.OutputDirectory);
        string existing = Path.Combine(request.OutputDirectory, existingName);
        File.WriteAllText(existing, "immutable prior evidence");
        Assert.Equal("Exists", Assert.Throws<RendererPerfBaselineException>(() => RendererPerfProfilePublisher.Publish(request)).Code);
        Assert.Equal(new[] { existing }, Directory.GetFiles(request.OutputDirectory));
        Assert.Equal("immutable prior evidence", File.ReadAllText(existing));
    }

    [Fact]
    public void Baseline_publication_is_unchanged()
    {
        var request = _fixture.Request();
        var names = RendererPerfBaselinePublisher.Publish(request);
        foreach (var leg in request.Legs)
            RendererPerfBaselinePublisherTests.AssertGolden(leg,
                File.ReadAllBytes(Path.Combine(request.OutputDirectory, names.Single(n => n.StartsWith(leg.Leg, StringComparison.Ordinal)))));
    }

    [Fact]
    public async System.Threading.Tasks.Task Command_publishes_profiles_without_registering_GPU_providers()
    {
        var request = Request();
        string requestPath = Path.Combine(_fixture.Root, "profiles-command.json");
        File.WriteAllText(requestPath, JsonSerializer.Serialize(request, RendererPerfManifest.JsonOptions));
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(RendererPerfCompilerPublicationTests.Verifier());
        start.ArgumentList.Add("publish-profiles");
        start.ArgumentList.Add("--request");
        start.ArgumentList.Add(requestPath);
        start.Environment.Remove("KE_GPU_TESTS");
        start.Environment.Remove("KE_RENDERER_PERF_UNIT");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string output = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, output);
        Assert.Contains("no GPU providers registered", output);
        Assert.Equal(4, Directory.GetFiles(request.OutputDirectory).Length);
    }
}
