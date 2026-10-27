using System;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

[Collection("NativeDeviceLifecycle")]
public sealed class RendererPerfGpuClockSelectorTests
{
    [Theory]
    [MemberData(nameof(RendererPerfColdLimitPolicyTests.Classes), MemberType = typeof(RendererPerfColdLimitPolicyTests))]
    public void Selector_emits_only_the_admitted_clock_for_every_class(string preset, string leg)
    {
        using var fixture = new RendererPerfWorkflowContractTests.WorkflowFixture();
        fixture.Manifest = RendererPerfColdLimitPolicyTests.ManifestJson(preset, leg);
        fixture.Manifest["workloadSha"] = fixture.Workload;
        fixture.Environment["LEGS"] = leg;
        fixture.Environment["RENDERER_PERF_LOCKED_GRAPHICS_CLOCK_MHZ"] = "1590";
        bool rehearsal = preset.StartsWith("rehearsal", StringComparison.Ordinal);
        fixture.Manifest["execution"]!["lockedGraphicsClockMHz"] = rehearsal ? null : JsonValue.Create(1200);
        fixture.SaveManifest();
        var result = fixture.Run("Select");
        Assert.True(result.Code == 0, result.Output);
        string matrix = result.Output.Split('\n').Single(s => s.StartsWith("matrix=", StringComparison.Ordinal))[7..];
        JsonNode clock = JsonNode.Parse(matrix)!["include"]![0]!;
        Assert.True(clock.AsObject().ContainsKey("lockedGraphicsClockMHz"));
        Assert.Equal(rehearsal ? null : (int?)1200, clock["lockedGraphicsClockMHz"]?.GetValue<int>());
        fixture.Manifest["execution"]!.AsObject().Remove("lockedGraphicsClockMHz");
        fixture.SaveManifest();
        Assert.NotEqual(0, fixture.Run("Select").Code);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("fractional")]
    [InlineData("null")]
    [InlineData("other-clock")]
    [InlineData("duplicate")]
    [InlineData("wrong-case")]
    public void Selector_refuses_invalid_paid_clocks(string fault)
    {
        using var fixture = new RendererPerfWorkflowContractTests.WorkflowFixture();
        fixture.Manifest["execution"]!["lockedGraphicsClockMHz"] = fault switch
        {
            "text" => JsonValue.Create("1200"),
            "fractional" => JsonValue.Create(1200.5),
            "other-clock" => JsonValue.Create(1215),
            "duplicate" or "wrong-case" => JsonValue.Create(1200),
            _ => null
        };
        fixture.SaveManifest();
        if (fault is "duplicate" or "wrong-case")
        {
            string text = File.ReadAllText(fixture.ManifestPath).Replace("\"lockedGraphicsClockMHz\":1200",
                fault == "duplicate" ? "\"lockedGraphicsClockMHz\":1200,\"lockedGraphicsClockMHz\":1200" : "\"LockedGraphicsClockMHz\":1200", StringComparison.Ordinal);
            File.WriteAllText(fixture.ManifestPath, text);
            fixture.Environment["RENDERER_PERF_MANIFEST_SHA256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fixture.ManifestPath)));
        }
        Assert.NotEqual(0, fixture.Run("Select").Code);
    }
}
