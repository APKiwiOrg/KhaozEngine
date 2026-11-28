using System;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

[Collection("NativeDeviceLifecycle")]
public sealed class RendererPerfPaidBudgetTests
{
    [Theory]
    [InlineData("pilot", "linux-vulkan", 600, 12, 15, 0.052, 0.780)]
    [InlineData("pilot", "windows-d3d11", 600, 20, 23, 0.102, 2.346)]
    [InlineData("full", "linux-vulkan", 1500, 35, 38, 0.052, 1.976)]
    [InlineData("full", "windows-d3d11", 1500, 50, 53, 0.102, 5.406)]
    [InlineData("full", "windows-vulkan", 1500, 50, 53, 0.102, 5.406)]
    public void Paid_caps_include_cleanup_without_extending_unit_or_frame_deadlines(string preset, string leg,
        int seconds, int minutes, int billed, double rate, double cost)
    {
        JsonObject json = ManifestJson(preset, leg, minutes, billed, (decimal)cost);
        var manifest = RendererPerfRehearsalManifestTests.Parse(json);
        Assert.Equal(new RendererPerfLimits(seconds, 10000, 120000, minutes), manifest.Limits);
        Assert.Equal(new RendererPerfBudget(billed, (decimal)rate, "USD", (decimal)cost), manifest.Budget);
        Assert.Equal(preset == "pilot" ? 1260 : 16944, manifest.TotalFrameCount);
        Assert.Equal(22m, manifest.Approval.CampaignMaximumUsd);
        using var fixture = new RendererPerfWorkflowContractTests.WorkflowFixture();
        fixture.Manifest = json;
        fixture.Manifest["workloadSha"] = fixture.Workload;
        fixture.Environment["LEGS"] = leg;
        fixture.SaveManifest();
        var result = fixture.Run("Select");
        Assert.True(result.Code == 0, result.Output);
        var line = result.Output.Split('\n').Single(s => s.StartsWith("matrix=", StringComparison.Ordinal));
        var row = Assert.Single(JsonNode.Parse(line[7..])!["include"]!.AsArray())!;
        Assert.Equal(minutes, row["jobMinutes"]!.GetValue<int>());
        Assert.Equal(leg == "linux-vulkan" ? "gpu-t4-linux" : "gpu-t4-windows", row["runner"]!.GetValue<string>());
        Assert.Equal("performance", row["executionClass"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("pilot", "linux-vulkan", 12, 15, 0.780)]
    [InlineData("pilot", "windows-d3d11", 20, 23, 2.346)]
    [InlineData("full", "linux-vulkan", 35, 38, 1.976)]
    [InlineData("full", "windows-d3d11", 50, 53, 5.406)]
    [InlineData("full", "windows-vulkan", 50, 53, 5.406)]
    public void Paid_validator_refuses_changed_caps_costs_and_approval(string preset, string leg, int minutes, int billed, double cost)
    {
        JsonObject original = ManifestJson(preset, leg, minutes, billed, (decimal)cost);
        foreach ((string section, string key, JsonNode? value) in new (string, string, JsonNode?)[]
        {
            ("limits", "unitSeconds", JsonValue.Create(601)),
            ("limits", "frameMilliseconds", JsonValue.Create(10001)),
            ("limits", "jobMinutes", JsonValue.Create(minutes + 1)),
            ("budget", "maximumBilledMinutes", JsonValue.Create(billed - 1)),
            ("budget", "maximumBilledMinutes", JsonValue.Create(billed + 1)),
            ("budget", "ratePerMinute", JsonValue.Create(0.053m)),
            ("budget", "currency", JsonValue.Create("NZD")),
            ("budget", "maximumCostUsd", JsonValue.Create((decimal)cost - 0.001m)),
            ("budget", "maximumCostUsd", JsonValue.Create((decimal)cost + 0.001m)),
            ("approval", "campaignMaximumUsd", JsonValue.Create(22.01m)),
            ("approval", "campaignMaximumUsd", JsonValue.Create((decimal)cost - 0.001m)),
            ("approval", "reference", JsonValue.Create(" ")),
            ("approval", "purpose", JsonValue.Create(" ")),
            ("approval", "campaignReference", JsonValue.Create(" "))
        })
        {
            JsonObject json = original.DeepClone().AsObject();
            json[section]![key] = value;
            Assert.Throws<RendererPerfProtocolException>(() => RendererPerfRehearsalManifestTests.Parse(json));
        }
        using var fixture = new RendererPerfWorkflowContractTests.WorkflowFixture();
        fixture.Manifest = original;
        fixture.Manifest["workloadSha"] = fixture.Workload;
        fixture.Environment["LEGS"] = leg;
        fixture.Manifest["budget"]!["maximumBilledMinutes"] = minutes;
        fixture.SaveManifest();
        Assert.NotEqual(0, fixture.Run("Select").Code);
        fixture.Manifest["budget"]!["maximumBilledMinutes"] = billed;
        fixture.Manifest["limits"]!["jobMinutes"] = minutes + 1;
        fixture.SaveManifest();
        Assert.NotEqual(0, fixture.Run("Select").Code);
    }

    [Theory]
    [InlineData("windows-d3d11")]
    [InlineData("windows-vulkan")]
    public void Windows_full_refuses_the_former_35_minute_cap(string leg)
    {
        JsonObject json = ManifestJson("full", leg, 35, 38, 3.876m);
        Assert.Equal("Preset", Assert.Throws<RendererPerfProtocolException>(() => RendererPerfRehearsalManifestTests.Parse(json)).Code);
        using var fixture = new RendererPerfWorkflowContractTests.WorkflowFixture();
        fixture.Manifest = json;
        fixture.Manifest["workloadSha"] = fixture.Workload;
        fixture.Environment["LEGS"] = leg;
        fixture.SaveManifest();
        Assert.NotEqual(0, fixture.Run("Select").Code);
    }

    static JsonObject ManifestJson(string preset, string leg, int minutes, int billed, decimal cost)
    {
        var json = RendererPerfProtocolTests.ManifestJson(preset, leg);
        json["limits"]!["jobMinutes"] = minutes;
        json["budget"]!["maximumBilledMinutes"] = billed;
        json["budget"]!["maximumCostUsd"] = cost;
        json["approval"]!["campaignMaximumUsd"] = 22;
        return json;
    }
}
