using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfRehearsalClassesTests
{
    internal static JsonObject ManifestJson(string preset, string leg)
    {
        bool full = preset == "rehearsal-full";
        JsonObject json = RendererPerfProtocolTests.ManifestJson(full ? "full" : "pilot", leg);
        json["preset"] = preset;
        json["execution"]!["runner"] = leg == "linux-vulkan" ? "ubuntu-24.04" : "windows-2025-vs2026";
        json["execution"]!["adapterPolicy"] = leg == "linux-vulkan" ? "mesa-lavapipe-software" : "microsoft-warp-software";
        json["limits"]!["unitSeconds"] = full ? 19800 : 5400;
        json["limits"]!["frameMilliseconds"] = 60000;
        json["limits"]!["jobMinutes"] = full ? 350 : 120;
        json["budget"]!["maximumBilledMinutes"] = full ? 350 : 120;
        json["budget"]!["ratePerMinute"] = 0;
        json["budget"]!["maximumCostUsd"] = 0;
        json["approval"]!["campaignMaximumUsd"] = 0;
        return json;
    }

    public static IEnumerable<object[]> Classes()
    {
        foreach (string preset in new[] { "rehearsal", "rehearsal-full" })
            foreach (string leg in new[] { "linux-vulkan", "windows-d3d11" })
                yield return [preset, leg];
    }

    [Theory]
    [MemberData(nameof(Classes))]
    public void Each_software_class_keeps_its_exact_schedule_and_limits(string preset, string leg)
    {
        var manifest = RendererPerfRehearsalManifestTests.Parse(ManifestJson(preset, leg));
        bool full = preset == "rehearsal-full";
        var paid = RendererPerfRehearsalManifestTests.Parse(RendererPerfProtocolTests.ManifestJson(full ? "full" : "pilot", leg));
        Assert.Equal(RendererPerfExecutionClass.Rehearsal, manifest.ExecutionClass);
        Assert.Equal(full ? 16944 : 1260, manifest.TotalFrameCount);
        Assert.Equal(paid.ConditioningFrames, manifest.ConditioningFrames);
        Assert.Equal(paid.Blocks, manifest.Blocks, EqualityComparer<RendererPerfBlock>.Default);
        Assert.Equal(paid.Controls, manifest.Controls, EqualityComparer<RendererPerfControl>.Default);
        Assert.Equal(paid.Recipes, manifest.Recipes, EqualityComparer<RendererPerfRecipeIdentity>.Default);
        Assert.Equal(new RendererPerfLimits(full ? 19800 : 5400, 60000, 120000, full ? 350 : 120), manifest.Limits);
        Assert.Equal(new RendererPerfBudget(full ? 350 : 120, 0, "USD", 0), manifest.Budget);
    }

    [Theory]
    [MemberData(nameof(Classes))]
    public void Cross_leg_schedule_and_performance_values_are_refused(string preset, string leg)
    {
        JsonObject original = ManifestJson(preset, leg);
        foreach ((string section, string key, JsonNode? value) in new (string, string, JsonNode?)[]
        {
            ("execution", "runner", JsonValue.Create(leg == "linux-vulkan" ? "windows-2025-vs2026" : "ubuntu-24.04")),
            ("execution", "runner", JsonValue.Create("windows-latest")),
            ("execution", "runner", JsonValue.Create("gpu-t4-linux")),
            ("execution", "runner", JsonValue.Create("gpu-t4-windows")),
            ("execution", "adapterPolicy", JsonValue.Create(leg == "linux-vulkan" ? "microsoft-warp-software" : "mesa-lavapipe-software")),
            ("execution", "adapterPolicy", JsonValue.Create("nvidia-t4-hardware")),
            ("execution", "backend", JsonValue.Create(leg == "linux-vulkan" ? "d3d11" : "vulkan")),
            ("execution", "leg", JsonValue.Create("windows-vulkan")),
            ("limits", "unitSeconds", JsonValue.Create(preset == "rehearsal" ? 19800 : 5400)),
            ("limits", "jobMinutes", JsonValue.Create(preset == "rehearsal" ? 350 : 120)),
            ("limits", "frameMilliseconds", JsonValue.Create(10000)),
            ("budget", "maximumBilledMinutes", JsonValue.Create(preset == "rehearsal" ? 350 : 120)),
            ("budget", "ratePerMinute", JsonValue.Create(0.052m)),
            ("budget", "maximumCostUsd", JsonValue.Create(1)),
            ("approval", "campaignMaximumUsd", JsonValue.Create(1))
        })
        {
            JsonObject json = original.DeepClone().AsObject();
            json[section]![key] = value;
            Assert.Throws<RendererPerfProtocolException>(() => RendererPerfRehearsalManifestTests.Parse(json));
        }
        foreach (string paidPreset in new[] { "pilot", "full" })
        {
            JsonObject json = original.DeepClone().AsObject();
            json["preset"] = paidPreset;
            Assert.Throws<RendererPerfProtocolException>(() => RendererPerfRehearsalManifestTests.Parse(json));
        }
        JsonObject otherSchedule = original.DeepClone().AsObject();
        otherSchedule["preset"] = preset == "rehearsal" ? "rehearsal-full" : "rehearsal";
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfRehearsalManifestTests.Parse(otherSchedule));
    }

    [Theory]
    [InlineData("rehearsal-FULL")]
    [InlineData("rehearsal-full-windows")]
    [InlineData("Rehearsal")]
    public void Preset_is_explicit_and_case_sensitive(string preset)
    {
        var json = ManifestJson("rehearsal", "linux-vulkan");
        json["preset"] = preset;
        json["unitId"] = "rehearsal-full";
        json["approval"]!["purpose"] = "rehearsal-full";
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfRehearsalManifestTests.Parse(json));
    }
}
