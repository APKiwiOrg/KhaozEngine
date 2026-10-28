using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfColdLimitPolicyTests
{
    public static IEnumerable<object[]> Classes()
    {
        foreach (string leg in new[] { "linux-vulkan", "windows-d3d11", "windows-vulkan" })
        {
            if (leg != "windows-vulkan") yield return ["pilot", leg];
            yield return ["full", leg];
            if (leg == "windows-vulkan") continue;
            foreach (string preset in new[] { "rehearsal", "rehearsal-full", "rehearsal-controls" })
                yield return [preset, leg];
        }
    }

    // Classes is also the v1 selector fixture inventory. C# admission is extended before that selector.
    public static IEnumerable<object[]> AdmittedClasses()
    {
        foreach (var entry in Classes()) yield return entry;
        yield return ["water-optics-v2", "linux-vulkan"];
        yield return ["water-optics-v2", "windows-d3d11"];
        yield return ["rehearsal-water-optics-v2", "linux-vulkan"];
        yield return ["rehearsal-water-optics-v2-full", "windows-d3d11"];
    }

    internal static JsonObject ManifestJson(string preset, string leg) => preset switch
    {
        "rehearsal-controls" => RendererPerfRehearsalControlsTests.ManifestJson(leg),
        "rehearsal" or "rehearsal-full" => RendererPerfRehearsalClassesTests.ManifestJson(preset, leg),
        "water-optics-v2" or "rehearsal-water-optics-v2" or "rehearsal-water-optics-v2-full"
            => RendererPerfWaterOpticsPresetTests.ManifestJson(preset, leg),
        _ => RendererPerfProtocolTests.ManifestJson(preset, leg)
    };

    [Theory]
    [MemberData(nameof(AdmittedClasses))]
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
