using System;
using System.Collections.Immutable;
using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Linq;
using System.Text;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

[Collection("GpuBackendProvidersSerial")]
public sealed class RendererPerfBaselinePublisherTests : IClassFixture<RendererPerfBaselineFixture>
{
    readonly RendererPerfBaselineFixture _fixture;
    public RendererPerfBaselinePublisherTests(RendererPerfBaselineFixture fixture) => _fixture = fixture;

    [Fact]
    public void Full_pair_produces_exact_immutable_index_and_separate_honest_series()
    {
        var request = _fixture.Request();
        var paths = RendererPerfBaselinePublisher.Publish(request);
        Assert.Equal(new[] { "linux-vulkan-full-linux.md", "windows-d3d11-full-windows.md", "index-full-linux-full-windows.md" }, paths);
        Assert.Equal("# Renderer baseline index\n\nFormat khaozengine.renderer-baseline/1. Separate series, no cross-backend deltas.\n\n"
            + "| Leg | Unit | Summary | Actual billed minutes |\n| --- | --- | --- | --- |\n"
            + "| linux-vulkan | full-linux | [summary](linux-vulkan-full-linux.md) | 17 |\n"
            + "| windows-d3d11 | full-windows | [summary](windows-d3d11-full-windows.md) | 23 |\n",
            File.ReadAllText(Path.Combine(request.OutputDirectory, paths[2])));
        foreach (string name in paths.Take(2))
        {
            var bytes = File.ReadAllBytes(Path.Combine(request.OutputDirectory, name));
            string text = Encoding.UTF8.GetString(bytes);
            Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
            Assert.DoesNotContain('\r', text);
            Assert.DoesNotContain(_fixture.Root, text);
            Assert.Contains("#1547", text);
            Assert.Contains("ModelIncomplete", text);
            Assert.Contains("within-job only", text);
            Assert.Contains("| S0 | gpu.frame | 768/768 | 100 ns |", text);
            Assert.Contains("| S4 | gpu.frame | 768/768 | 100 ns |", text);
            Assert.Contains("| S3 | gpu.unattributed | 768/768 | 60 ns |", text);
            Assert.Contains("gpu.pass.opaque.0", text);
            Assert.Contains("control:12", text);
            Assert.Contains("Largest measured excesses", text);
            Assert.Contains("No available measured excess", text);
            Assert.Contains("manifestSha256", text);
            Assert.Contains("workflowSha", text);
            var leg = request.Legs.Single(l => name.StartsWith(l.Leg, StringComparison.Ordinal));
            AssertGolden(leg, bytes);

            var summary = RendererPerfAnalysis.ReadDirectory(leg.Directory, leg.ReportName);
            var s3 = summary.Combined.Single(row => row.SceneId == "S3" && row.Layout == RendererPerfLayout.PerPass);
            var unattributed = s3.Metrics["gpu.unattributed"];
            Assert.Equal(768, unattributed.Count);
            Assert.Equal(60, unattributed.Mean.Value);
        }
        Assert.Contains("Actual billed minutes 17.", File.ReadAllText(Path.Combine(request.OutputDirectory, paths[0])));
        Assert.Contains("Actual billed minutes 23.", File.ReadAllText(Path.Combine(request.OutputDirectory, paths[1])));
        byte[][] original = paths.Select(p => File.ReadAllBytes(Path.Combine(request.OutputDirectory, p))).ToArray();
        Assert.Equal("Exists", Assert.Throws<RendererPerfBaselineException>(() => RendererPerfBaselinePublisher.Publish(request)).Code);
        for (int i = 0; i < paths.Length; i++) Assert.Equal(original[i], File.ReadAllBytes(Path.Combine(request.OutputDirectory, paths[i])));
        var again = request with { OutputDirectory = _fixture.Output(), Legs = [.. request.Legs.Reverse()] };
        Assert.Equal(paths.ToArray(), RendererPerfBaselinePublisher.Publish(again).ToArray());
        for (int i = 0; i < paths.Length; i++) Assert.Equal(original[i], File.ReadAllBytes(Path.Combine(again.OutputDirectory, paths[i])));
    }

    internal static void AssertGolden(RendererPerfBaselineLeg leg, byte[] bytes)
    {
        string text = Encoding.UTF8.GetString(bytes);
        string rootHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(leg.Directory, leg.ReportName))));
        Assert.Contains("\"reportSha256\":\"" + rootHash + "\"", text);
        // Wire ordering can vary between fixture processes. Hash each input independently.
        string expected = Expected(leg.Leg).Replace("@REPORT_SHA256@", rootHash, StringComparison.Ordinal);
        using var raw = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(leg.Directory, leg.ReportName)));
        foreach (var sidecar in raw.RootElement.GetProperty("sidecars").EnumerateArray())
        {
            string path = sidecar.GetProperty("path").GetString()!;
            string digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(leg.Directory, path))));
            expected = expected.Replace("@SIDECAR_" + path + "@", digest, StringComparison.Ordinal);
        }
        Assert.Equal(Encoding.UTF8.GetBytes(expected), bytes);
    }

    static string Expected(string leg, [System.Runtime.CompilerServices.CallerFilePath] string source = "")
        => File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!, "../../Fixtures/renderer-baseline", leg + ".expected.txt"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public void Invalid_second_leg_and_existing_second_summary_never_write_the_first_summary()
    {
        var request = _fixture.Request();
        var invalid = request with { Legs = [request.Legs[0], request.Legs[1] with { WorkflowSha = new('f', 40) }] };
        Assert.Equal("Identity", Assert.Throws<RendererPerfBaselineException>(() => RendererPerfBaselinePublisher.Publish(invalid)).Code);
        Assert.False(Directory.Exists(request.OutputDirectory));
        Directory.CreateDirectory(request.OutputDirectory);
        string existing = Path.Combine(request.OutputDirectory, "windows-d3d11-full-windows.md");
        File.WriteAllText(existing, "immutable prior evidence");
        Assert.Equal("Exists", Assert.Throws<RendererPerfBaselineException>(() => RendererPerfBaselinePublisher.Publish(request)).Code);
        Assert.Equal(new[] { existing }, Directory.GetFiles(request.OutputDirectory));
        Assert.Equal("immutable prior evidence", File.ReadAllText(existing));
    }

    [Theory]
    [InlineData(1e9, "38")]
    [InlineData(1e6, "-1960")]
    public void Explicit_matched_references_preserve_rates_and_rank_only_available_excesses(double measuredRate, string excess)
    {
        var request = _fixture.Request();
        var rates = new RendererPerfReferenceRates(1e9, null, measuredRate, null, null, 0, "fp32-fma", "synthetic matched reference");
        request = request with
        {
            Legs = [request.Legs[0] with
        {
            References = ImmutableDictionary<string, RendererPerfReferenceRates>.Empty.Add("S3/opaque/0", rates)
        }, request.Legs[1]]
        };
        var paths = RendererPerfBaselinePublisher.Publish(request);
        string text = File.ReadAllText(Path.Combine(request.OutputDirectory, paths[0]));
        Assert.Contains("| S3 | gap.opaque.0.excess | 768/768 | " + excess + " ns |", text);
        Assert.Contains("| S3 | gap.opaque.0.excess | " + excess + " ns |", text);
        Assert.Contains("synthetic matched reference", text);
        Assert.Contains("1000000000", text);
        Assert.Contains("Investigate", text);
        Assert.Contains("ModelIncomplete", text);
    }

    [Fact]
    public async System.Threading.Tasks.Task Command_publishes_received_pair_without_registering_GPU_providers()
    {
        var request = _fixture.Request();
        string requestPath = Path.Combine(_fixture.Root, "command.json");
        File.WriteAllText(requestPath, JsonSerializer.Serialize(request, RendererPerfManifest.JsonOptions));
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(RendererPerfCompilerPublicationTests.Verifier());
        start.ArgumentList.Add("publish");
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
        Assert.Equal(3, Directory.GetFiles(request.OutputDirectory).Length);
    }

    [Fact]
    public async System.Threading.Tasks.Task Command_verifies_received_directory_named_publish_without_writing_summaries()
    {
        var request = _fixture.Request();
        var source = request.Legs[0];
        string received = Path.Combine(_fixture.Root, "publish");
        Directory.CreateDirectory(received);
        foreach (string path in Directory.GetFiles(source.Directory))
            File.Copy(path, Path.Combine(received, Path.GetFileName(path)));
        string[] summaries = Directory.GetFiles(received, "*.md").Order(StringComparer.Ordinal).ToArray();
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _fixture.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(RendererPerfCompilerPublicationTests.Verifier());
        start.ArgumentList.Add("publish");
        start.ArgumentList.Add(source.ReportName);
        start.Environment.Remove("KE_GPU_TESTS");
        start.Environment.Remove("KE_RENDERER_PERF_UNIT");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string output = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, output);
        Assert.Contains("Verified renderer artifacts", output);
        Assert.Equal(summaries, Directory.GetFiles(received, "*.md").Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("pilot", "NotFull")]
    [InlineData("rehearsal", "Rehearsal")]
    [InlineData("failed-promotion", "Promotion")]
    [InlineData("failed-publication", "EvidenceInvalid")]
    [InlineData("missing-sidecar", "EvidenceInvalid")]
    [InlineData("foreign", "Identity")]
    [InlineData("native-compiler", "Identity")]
    [InlineData("run-url", "Identity")]
    [InlineData("leg", "Identity")]
    [InlineData("billing", "Billing")]
    [InlineData("negative-billing", "Billing")]
    [InlineData("pair", "Pair")]
    public void Ineligible_received_evidence_refuses_with_typed_reason_and_no_output(string fault, string code)
    {
        var request = _fixture.Request();
        var leg = request.Legs[0];
        if (fault is "pilot" or "rehearsal" or "failed-promotion" or "failed-publication" or "missing-sidecar" or "native-compiler")
            leg = _fixture.Variant(fault);
        if (fault == "foreign") leg = leg with { ManifestSha256 = new('f', 64) };
        if (fault == "run-url") leg = leg with { RunUrl = "https://github.com/foreign/repo/actions/runs/123/attempts/1" };
        if (fault == "leg") leg = leg with { Leg = "windows-d3d11" };
        if (fault == "billing") leg = leg with { BilledMinutes = 39 };
        if (fault == "negative-billing") leg = leg with { BilledMinutes = -1 };
        request = request with { Legs = fault == "pair" ? [leg] : [leg, request.Legs[1]] };
        Assert.Equal(code, Assert.Throws<RendererPerfBaselineException>(() => RendererPerfBaselinePublisher.Publish(request)).Code);
        Assert.False(Directory.Exists(request.OutputDirectory));
    }
}
