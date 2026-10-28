using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfColdLimitPolicyTests
{
    public static IEnumerable<object[]> Classes()
    {
        foreach (string preset in RendererPerfSchedules.Presets)
        {
            var schedule = RendererPerfSchedules.For(preset);
            foreach (string leg in schedule.Legs)
                if (schedule.ExecutionClass == RendererPerfExecutionClass.Performance || leg != "windows-vulkan")
                    yield return [preset, leg];
        }
    }

    public static IEnumerable<object[]> SoftwareClasses() => Classes().Where(entry =>
        RendererPerfSchedules.For((string)entry[0]).ExecutionClass == RendererPerfExecutionClass.Rehearsal);

    internal static JsonObject ManifestJson(string preset, string leg) => preset switch
    {
        "rehearsal-controls" => RendererPerfRehearsalControlsTests.ManifestJson(leg),
        "rehearsal" or "rehearsal-full" => RendererPerfRehearsalClassesTests.ManifestJson(preset, leg),
        "water-optics-v2" or "rehearsal-water-optics-v2" or "rehearsal-water-optics-v2-full"
            => RendererPerfWaterOpticsPresetTests.ManifestJson(preset, leg),
        _ => RendererPerfProtocolTests.ManifestJson(preset, leg)
    };

    [Theory]
    [MemberData(nameof(Classes))]
    public void Every_preset_requires_the_same_bounded_cold_limit(string preset, string leg)
    {
        JsonObject json = ManifestJson(preset, leg);
        json["limits"]!["coldFrameMilliseconds"] = 120000;
        RendererPerfRehearsalManifestTests.Parse(json);
        foreach (int limit in new[] { -1, 0, 10000, 60000, 119999, 120001, int.MaxValue })
        {
            json["limits"]!["coldFrameMilliseconds"] = limit;
            Assert.Throws<RendererPerfProtocolException>(() => RendererPerfRehearsalManifestTests.Parse(json));
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("mistyped")]
    [InlineData("fractional")]
    [InlineData("null")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    public void Cold_limit_uses_strict_required_typed_json(string fault)
    {
        JsonObject json = RendererPerfProtocolTests.ManifestJson();
        json["limits"]!["coldFrameMilliseconds"] = 120000;
        switch (fault)
        {
            case "missing": json["limits"]!.AsObject().Remove("coldFrameMilliseconds"); break;
            case "mistyped": json["limits"]!["coldFrameMilliseconds"] = "120000"; break;
            case "fractional": json["limits"]!["coldFrameMilliseconds"] = 120000.5; break;
            case "null": json["limits"]!["coldFrameMilliseconds"] = null; break;
            case "unknown": json["limits"]!["coldFrameOverride"] = true; break;
        }
        string text = json.ToJsonString();
        if (fault == "duplicate") text = text.Replace("\"coldFrameMilliseconds\":120000", "\"coldFrameMilliseconds\":120000,\"coldFrameMilliseconds\":120000", System.StringComparison.Ordinal);
        Assert.Equal("Json", Assert.Throws<RendererPerfProtocolException>(() =>
            RendererPerfManifest.Parse(Encoding.UTF8.GetBytes(text))).Code);
    }
}
