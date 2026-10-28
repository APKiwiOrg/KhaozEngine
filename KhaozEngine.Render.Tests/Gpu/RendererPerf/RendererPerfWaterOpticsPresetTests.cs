using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfWaterOpticsPresetTests
{
    internal static readonly string[] BlockOrder =
    [
        "S0/0/TotalOnly", "S0/0/PerPass", "S1/0/PerPass", "S1/0/TotalOnly", "S2/0/TotalOnly", "S2/0/PerPass",
        "S3/0/PerPass", "S3/0/TotalOnly", "S4/0/TotalOnly", "S4/0/PerPass", "S5/0/PerPass", "S5/0/TotalOnly",
        "S1/1/TotalOnly", "S1/1/PerPass", "S2/1/PerPass", "S2/1/TotalOnly", "S3/1/TotalOnly", "S3/1/PerPass",
        "S4/1/PerPass", "S4/1/TotalOnly", "S5/1/TotalOnly", "S5/1/PerPass", "S0/1/PerPass", "S0/1/TotalOnly",
        "S2/2/TotalOnly", "S2/2/PerPass", "S3/2/PerPass", "S3/2/TotalOnly", "S4/2/TotalOnly", "S4/2/PerPass",
        "S5/2/PerPass", "S5/2/TotalOnly", "S0/2/TotalOnly", "S0/2/PerPass", "S1/2/PerPass", "S1/2/TotalOnly"
    ];

    // Independent fixture values let admission tests catch a changed schedule table.
    internal static JsonObject ManifestJson(string preset = "water-optics-v2", string leg = "linux-vulkan")
    {
        JsonObject json = RendererPerfProtocolTests.ManifestJson("full", leg);
        bool rehearsal = preset.StartsWith("rehearsal-", StringComparison.Ordinal);
        bool single = preset == "rehearsal-water-optics-v2-single";
        bool shortened = preset == "rehearsal-water-optics-v2" || single;
        json["preset"] = preset;
        json["conditioningFrames"] = shortened ? 300 : 1200;
        var recipes = new JsonArray();
        for (int scene = 0; scene < 6; scene++)
        {
            JsonNode recipe = json["recipes"]![1]!.DeepClone();
            recipe["sceneId"] = "S" + scene;
            recipe["version"] = RendererPerfWaterOptics.RecipeIds[scene] + "/1";
            recipes.Add(recipe);
        }
        json["recipes"] = recipes;
        var blocks = new JsonArray();
        foreach (string entry in single ? BlockOrder.Take(12) : BlockOrder)
        {
            string[] fields = entry.Split('/');
            blocks.Add(new JsonObject
            {
                ["sceneId"] = fields[0], ["iteration"] = int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture),
                ["layout"] = fields[2] == "TotalOnly" ? "total-only" : "per-pass",
                ["warmupFrames"] = single ? 32 : shortened ? 64 : 192, ["measuredFrames"] = single ? 64 : shortened ? 128 : 256
            });
        }
        json["blocks"] = blocks;
        foreach (JsonNode? control in json["controls"]!.AsArray())
        {
            control!["warmupFrames"] = shortened ? 32 : 64;
            control["measuredFrames"] = shortened ? 64 : 128;
        }
        json["approval"]!["campaignMaximumUsd"] = rehearsal ? 0 : 7.382m;
        if (rehearsal)
        {
            json["execution"]!["runner"] = leg == "linux-vulkan" ? "ubuntu-24.04" : "windows-2025-vs2026";
            json["execution"]!["adapterPolicy"] = leg == "linux-vulkan" ? "mesa-lavapipe-software" : "microsoft-warp-software";
            json["execution"]!["lockedGraphicsClockMHz"] = null;
            json["limits"]!["unitSeconds"] = 19800;
            json["limits"]!["frameMilliseconds"] = 60000;
            json["limits"]!["jobMinutes"] = 350;
            json["budget"]!["maximumBilledMinutes"] = 350;
            json["budget"]!["ratePerMinute"] = 0;
            json["budget"]!["maximumCostUsd"] = 0;
        }
        return json;
    }

    [Fact]
    public void Paid_schedule_reconciles()
    {
        var manifest = RendererPerfRehearsalManifestTests.Parse(ManifestJson());
        var schedule = manifest.Schedule;
        Assert.Equal(RendererPerfExecutionClass.Performance, manifest.ExecutionClass);
        Assert.Equal(19632, manifest.TotalFrameCount);
        Assert.Equal(19632, schedule.TotalFrames);
        Assert.Equal(BlockOrder, manifest.Blocks.Select(b => $"{b.SceneId}/{b.Iteration}/{b.Layout}"));
        Assert.All(manifest.Blocks, b => Assert.Equal((192, 256), (b.WarmupFrames, b.MeasuredFrames)));
        Assert.Equal(RendererPerfWaterOptics.RecipeIds.Select(id => id + "/1"), schedule.Recipes.Select(r => r.Version));
        Assert.Equal(Enumerable.Range(0, 6), schedule.Recipes.Select(r => r.Scene));
        Assert.All(schedule.Recipes, r => Assert.Equal("renderer-full-native-1080/1", r.Profile));
        Assert.Equal(1200, manifest.ConditioningFrames);
        Assert.Equal("conditioning:S5", schedule.ConditioningBlockId);
        Assert.Equal("conditioning:S5/TotalOnly/0", schedule.ConditioningOperation);
        Assert.Equal(new[] { "linux-vulkan", "windows-d3d11" }, schedule.Legs);
    }

    [Theory]
    [InlineData("rehearsal-water-optics-v2", "linux-vulkan", 8364, 3, 64, 128)]
    [InlineData("rehearsal-water-optics-v2-single", "windows-d3d11", 2604, 1, 32, 64)]
    public void Rehearsal_schedules_reconcile(string preset, string leg, int total, int iterations, int warmup, int measured)
    {
        var manifest = RendererPerfRehearsalManifestTests.Parse(ManifestJson(preset, leg));
        Assert.Equal(total, manifest.TotalFrameCount);
        Assert.Equal(300, manifest.ConditioningFrames);
        Assert.Equal(total, manifest.Schedule.TotalFrames);
        Assert.Equal(iterations, manifest.Schedule.Iterations);
        Assert.Equal(BlockOrder.Take(iterations * 12), manifest.Blocks.Select(b => $"{b.SceneId}/{b.Iteration}/{b.Layout}"));
        Assert.All(manifest.Blocks, b => Assert.Equal((warmup, measured), (b.WarmupFrames, b.MeasuredFrames)));
        Assert.Equal(Enumerable.Range(1, 12), manifest.Controls.Select(c => c.Id));
        Assert.All(manifest.Controls, c => Assert.Equal((32, 64), (c.WarmupFrames, c.MeasuredFrames)));
        Assert.Equal(iterations == 1 ? 25 : 49, manifest.Schedule.Operations);
        Assert.Equal("conditioning:S5/TotalOnly/0", manifest.Schedule.ConditioningOperation);
        Assert.Equal(RendererPerfWaterOptics.RecipeIds.Select(id => id + "/1"), manifest.Recipes.Select(r => r.Version));
        Assert.Equal(Enumerable.Range(0, 6), manifest.Schedule.Recipes.Select(r => r.Scene));
        Assert.All(manifest.Recipes, r => Assert.Equal("renderer-full-native-1080/1", r.Profile));
    }

    [Theory]
    [InlineData("rehearsal-water-optics-v2", "linux-vulkan", "windows-d3d11")]
    [InlineData("rehearsal-water-optics-v2-single", "windows-d3d11", "linux-vulkan")]
    public void Rehearsal_classes_are_leg_specific(string preset, string leg, string refusedLeg)
    {
        var manifest = RendererPerfRehearsalManifestTests.Parse(ManifestJson(preset, leg));
        Assert.Equal(RendererPerfExecutionClass.Rehearsal, manifest.ExecutionClass);
        Assert.Equal(new RendererPerfLimits(19800, 60000, 120000, 350), manifest.Limits);
        Assert.Equal(new RendererPerfBudget(350, 0, "USD", 0), manifest.Budget);
        Assert.Equal(0, manifest.Approval.CampaignMaximumUsd);
        Assert.Equal(leg == "linux-vulkan" ? "ubuntu-24.04" : "windows-2025-vs2026", manifest.Execution.Runner);
        Assert.Equal(leg == "linux-vulkan" ? "mesa-lavapipe-software" : "microsoft-warp-software", manifest.Execution.AdapterPolicy);
        Assert.Null(manifest.Execution.LockedGraphicsClockMHz);
        Assert.Equal(new[] { leg }, manifest.Schedule.Legs);
        Assert.Equal("Preset", Assert.Throws<RendererPerfProtocolException>(() =>
            RendererPerfRehearsalManifestTests.Parse(ManifestJson(preset, refusedLeg))).Code);
        var json = ManifestJson(preset, leg);
        json["approval"]!["campaignMaximumUsd"] = .001m;
        Assert.Equal("Approval", Assert.Throws<RendererPerfProtocolException>(() => RendererPerfRehearsalManifestTests.Parse(json)).Code);
    }

    [Fact]
    public void Retired_full_water_optics_rehearsal_is_unknown()
    {
        string retired = "rehearsal-water-optics-v2-" + "full";
        var json = ManifestJson("rehearsal-water-optics-v2-single", "windows-d3d11");
        json["preset"] = retired;
        var error = Assert.Throws<RendererPerfProtocolException>(() => RendererPerfRehearsalManifestTests.Parse(json));
        Assert.Equal("Preset", error.Code);
        Assert.Equal("Unknown renderer preset.", error.Message);
    }

    [Theory]
    [InlineData("linux-vulkan", 35, 38, "1.976")]
    [InlineData("windows-d3d11", 50, 53, "5.406")]
    public void Paid_caps_are_the_full_caps(string leg, int minutes, int billed, string cost)
    {
        var manifest = RendererPerfRehearsalManifestTests.Parse(ManifestJson(leg: leg));
        Assert.Equal(new RendererPerfLimits(1500, 10000, 120000, minutes), manifest.Limits);
        Assert.Equal(billed, manifest.Budget.MaximumBilledMinutes);
        Assert.Equal(decimal.Parse(cost, System.Globalization.CultureInfo.InvariantCulture), manifest.Budget.MaximumCostUsd);
        Assert.Equal(1095, manifest.Execution.LockedGraphicsClockMHz);
    }

    [Theory]
    [InlineData("linux-vulkan")]
    [InlineData("windows-d3d11")]
    public void Campaign_maximums_are_separate(string leg)
    {
        var json = ManifestJson(leg: leg);
        RendererPerfRehearsalManifestTests.Parse(json);
        json["approval"]!["campaignMaximumUsd"] = 7.383m;
        Assert.Equal("Approval", Assert.Throws<RendererPerfProtocolException>(() => RendererPerfRehearsalManifestTests.Parse(json)).Code);
        json["approval"]!["campaignMaximumUsd"] = json["budget"]!["maximumCostUsd"]!.GetValue<decimal>() - .001m;
        Assert.Equal("Approval", Assert.Throws<RendererPerfProtocolException>(() => RendererPerfRehearsalManifestTests.Parse(json)).Code);
        json = RendererPerfProtocolTests.ManifestJson("full", leg);
        json["approval"]!["campaignMaximumUsd"] = 22m;
        RendererPerfRehearsalManifestTests.Parse(json);
        json["approval"]!["campaignMaximumUsd"] = 22.001m;
        Assert.Equal("Approval", Assert.Throws<RendererPerfProtocolException>(() => RendererPerfRehearsalManifestTests.Parse(json)).Code);
    }

    [Theory]
    [InlineData("linux-vulkan", "adapterPolicy", "mesa-lavapipe-software")]
    [InlineData("windows-d3d11", "adapterPolicy", "microsoft-warp-software")]
    [InlineData("linux-vulkan", "runner", "ubuntu-24.04")]
    [InlineData("windows-d3d11", "runner", "windows-2025-vs2026")]
    public void V2_refuses_software_adapters_and_runners(string leg, string key, string value)
    {
        var json = ManifestJson(leg: leg);
        json["execution"]![key] = value;
        Assert.Equal("Preset", Assert.Throws<RendererPerfProtocolException>(() => RendererPerfRehearsalManifestTests.Parse(json)).Code);
    }

    [Fact]
    public void V2_refuses_windows_vulkan_with_a_campaign_specific_message()
    {
        var error = Assert.Throws<RendererPerfProtocolException>(() =>
            RendererPerfRehearsalManifestTests.Parse(ManifestJson(leg: "windows-vulkan")));
        Assert.Equal("Preset", error.Code);
        Assert.Equal("Windows Vulkan is not an approved leg for the water-optics-v2 campaign.", error.Message);
        var pilot = RendererPerfProtocolTests.ManifestJson("pilot", "windows-vulkan");
        Assert.Equal("Windows Vulkan has no initial pilot preset.", Assert.Throws<RendererPerfProtocolException>(() =>
            RendererPerfRehearsalManifestTests.Parse(pilot)).Message);
    }

    [Theory]
    [InlineData("water-optics-v2", "linux-vulkan", 49)]
    [InlineData("rehearsal-water-optics-v2", "linux-vulkan", 49)]
    [InlineData("rehearsal-water-optics-v2-single", "windows-d3d11", 25)]
    public void Capacity_follows_the_admitted_schedule(string preset, string leg, int operations)
    {
        var manifest = RendererPerfRehearsalManifestTests.Parse(ManifestJson(preset, leg));
        var capture = new RendererPerfConstructionCapture(manifest);
        for (int operation = 0; operation < operations; operation++)
        {
            capture.BeginOperation(operation);
            capture.EndOperation();
        }
        Assert.Throws<InvalidOperationException>(() => capture.BeginOperation(operations));
        var inventory = capture.Freeze();
        Assert.True(inventory.Complete, inventory.Reason);
        Assert.Equal(operations, inventory.Operations.Length);
        Assert.Equal("conditioning:S5/TotalOnly/0", inventory.Operations[0]);
        Assert.Equal("control:12/TotalOnly/0", inventory.Operations[^1]);
        byte[] encoded = RendererPerfCompilerJson.Serialize(inventory, RendererPerfReportJson.MaximumBytes);
        Assert.Equal(operations, RendererPerfCompilerJson.Parse(encoded, RendererPerfReportJson.MaximumBytes).Operations.Length);
        Assert.ThrowsAny<ArgumentException>(() => new RendererPerfConstructionCapture(manifest with { Blocks = manifest.Blocks.Add(manifest.Blocks[0]) }));
        var oversized = inventory with { Operations = inventory.Operations.Add("foreign/TotalOnly/0") };
        var file = new RendererPerfCompilerFile(RendererPerfCompilerJson.Schema, manifest.UnitId,
            new string('a', 64), manifest.WorkloadSha, inventory);
        byte[] fileBytes = JsonSerializer.SerializeToUtf8Bytes(file, RendererPerfCompilerJson.Options);
        Assert.Equal(operations, RendererPerfCompilerJson.ReadFile(fileBytes, RendererPerfReportJson.MaximumBytes,
            manifest.Schedule.Operations).Inventory.Operations.Length);
        byte[] oversizedBytes = JsonSerializer.SerializeToUtf8Bytes(file with { Inventory = oversized }, RendererPerfCompilerJson.Options);
        Assert.Throws<JsonException>(() => RendererPerfCompilerJson.ReadFile(oversizedBytes, RendererPerfReportJson.MaximumBytes,
            manifest.Schedule.Operations));
    }

    [Theory]
    [InlineData("rehearsal-water-optics-v2", "linux-vulkan", "gpu-t4-linux")]
    [InlineData("rehearsal-water-optics-v2-single", "windows-d3d11", "gpu-t4-windows")]
    public void V2_rehearsal_refuses_hardware_and_nonzero_cost(string preset, string leg, string runner)
    {
        var original = ManifestJson(preset, leg);
        foreach ((string section, string key, JsonNode? value) in new (string, string, JsonNode?)[]
        {
            ("execution", "runner", JsonValue.Create(runner)),
            ("execution", "adapterPolicy", JsonValue.Create("nvidia-t4-hardware")),
            ("budget", "ratePerMinute", JsonValue.Create(.001m)),
            ("budget", "maximumCostUsd", JsonValue.Create(.001m))
        })
        {
            var json = original.DeepClone().AsObject();
            json[section]![key] = value;
            Assert.Equal("Preset", Assert.Throws<RendererPerfProtocolException>(() => RendererPerfRehearsalManifestTests.Parse(json)).Code);
        }
    }

    [Fact]
    public void V2_factory_storage_and_codec_follow_all_49_operations()
    {
        var manifest = RendererPerfRehearsalManifestTests.Parse(ManifestJson());
        var capture = new RendererPerfConstructionCapture(manifest);
        using var native = new RendererPerfConstructionFixture();
        for (int operation = 0; operation < 49; operation++)
        {
            capture.BeginOperation(operation);
            var device = RendererPerfDevice.Wrap(native, new RendererPerfRecorder(1, RendererPerfLayout.TotalOnly, [], () => 0, 1000), capture);
            for (int factory = 0; factory < 1024; factory++)
                device.Factory.CreateResourceLayout(new([])).Dispose();
            capture.EndOperation();
        }
        var inventory = capture.Freeze();
        Assert.True(inventory.Complete, inventory.Reason);
        Assert.Equal(50176, inventory.FactoryEvents.Length);
        Assert.Equal(0, inventory.Counts.OmittedFactoryEvents);
        byte[] encoded = RendererPerfCompilerJson.Serialize(inventory, RendererPerfReportJson.MaximumBytes);
        Assert.Equal(50176, RendererPerfCompilerJson.Parse(encoded, RendererPerfReportJson.MaximumBytes).FactoryEvents.Length);
    }
}
