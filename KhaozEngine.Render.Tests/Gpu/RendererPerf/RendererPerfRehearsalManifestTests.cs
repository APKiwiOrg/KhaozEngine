using System;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfRehearsalManifestTests
{
    internal static JsonObject ManifestJson()
    {
        JsonObject json = RendererPerfProtocolTests.ManifestJson();
        json["preset"] = "rehearsal";
        json["execution"]!["runner"] = "ubuntu-24.04";
        json["execution"]!["adapterPolicy"] = "mesa-lavapipe-software";
        json["limits"]!["unitSeconds"] = 5400;
        json["limits"]!["frameMilliseconds"] = 60000;
        json["limits"]!["jobMinutes"] = 120;
        json["budget"]!["maximumBilledMinutes"] = 120;
        json["budget"]!["ratePerMinute"] = 0;
        json["budget"]!["maximumCostUsd"] = 0;
        json["approval"]!["campaignMaximumUsd"] = 0;
        return json;
    }

    [Fact]
    public void Rehearsal_keeps_the_exact_pilot_schedule_with_separate_limits_and_zero_cost()
    {
        RendererPerfManifest manifest = Parse(ManifestJson());
        Assert.Equal(1260, manifest.TotalFrameCount);
        Assert.Equal(300, manifest.ConditioningFrames);
        Assert.Equal(new RendererPerfLimits(5400, 60000, 120000, 120), manifest.Limits);
        Assert.Equal(new RendererPerfBudget(120, 0, "USD", 0), manifest.Budget);
        Assert.Equal(RendererPerfManifest.Parse(Encoding.UTF8.GetBytes(RendererPerfProtocolTests.ManifestJson().ToJsonString())).Blocks,
            manifest.Blocks, System.Collections.Generic.EqualityComparer<RendererPerfBlock>.Default);
    }

    [Theory]
    [InlineData("execution", "runner", "gpu-t4-linux")]
    [InlineData("execution", "runner", "gpu-t4-windows")]
    [InlineData("execution", "adapterPolicy", "nvidia-t4-hardware")]
    [InlineData("execution", "backend", "d3d11")]
    [InlineData("execution", "leg", "windows-vulkan")]
    [InlineData("execution", "leg", "windows-d3d11")]
    [InlineData("execution", "workflow", "cross-platform-gpu.yml")]
    [InlineData("execution", "completionPolicy", "none")]
    [InlineData("limits", "unitSeconds", 600)]
    [InlineData("limits", "frameMilliseconds", 10000)]
    [InlineData("limits", "jobMinutes", 12)]
    [InlineData("budget", "maximumBilledMinutes", 12)]
    [InlineData("budget", "ratePerMinute", 1)]
    [InlineData("budget", "maximumCostUsd", 1)]
    [InlineData("budget", "currency", "NZD")]
    [InlineData("approval", "reference", " ")]
    [InlineData("approval", "campaignReference", " ")]
    public void Rehearsal_refuses_cross_class_values(string section, string key, object value)
    {
        JsonObject json = ManifestJson();
        json[section]![key] = System.Text.Json.JsonSerializer.SerializeToNode(value);
        Assert.Throws<RendererPerfProtocolException>(() => Parse(json));
    }

    [Theory]
    [InlineData("pilot")]
    [InlineData("full")]
    public void Performance_refuses_rehearsal_values(string preset)
    {
        JsonObject json = ManifestJson();
        json["preset"] = preset;
        Assert.Throws<RendererPerfProtocolException>(() => Parse(json));
        foreach (string section in new[] { "execution", "limits", "budget" })
        {
            JsonObject paid = RendererPerfProtocolTests.ManifestJson(preset);
            paid[section] = ManifestJson()[section]!.DeepClone();
            Assert.Throws<RendererPerfProtocolException>(() => Parse(paid));
        }
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("mistyped")]
    [InlineData("duplicate")]
    public void Rehearsal_uses_the_strict_json_reader(string fault)
    {
        JsonObject json = ManifestJson();
        if (fault == "unknown") json["execution"]!["softwareOverride"] = true;
        if (fault == "missing") json["execution"]!.AsObject().Remove("runner");
        if (fault == "mistyped") json["limits"]!["unitSeconds"] = "5400";
        string text = json.ToJsonString();
        if (fault == "duplicate") text = text.Insert(1, "\"preset\":\"rehearsal\",");
        Assert.Equal("Json", Assert.Throws<RendererPerfProtocolException>(() =>
            RendererPerfManifest.Parse(Encoding.UTF8.GetBytes(text))).Code);
    }

    internal static RendererPerfManifest Parse(JsonObject json) => RendererPerfManifest.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()));
}
