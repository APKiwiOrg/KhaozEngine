using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfGpuClockManifestTests
{
    [Theory]
    [MemberData(nameof(RendererPerfColdLimitPolicyTests.Classes), MemberType = typeof(RendererPerfColdLimitPolicyTests))]
    public void Every_class_requires_its_explicit_clock_policy(string preset, string leg)
    {
        JsonObject json = RendererPerfColdLimitPolicyTests.ManifestJson(preset, leg);
        bool rehearsal = preset.StartsWith("rehearsal", StringComparison.Ordinal);
        json["execution"]!["lockedGraphicsClockMHz"] = rehearsal ? null : JsonValue.Create(1200);
        var manifest = RendererPerfRehearsalManifestTests.Parse(json);
        using var encoded = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(manifest, RendererPerfManifest.JsonOptions));
        JsonElement clock = encoded.RootElement.GetProperty("execution").GetProperty("lockedGraphicsClockMHz");
        if (rehearsal) Assert.Equal(JsonValueKind.Null, clock.ValueKind);
        else Assert.Equal(1200, clock.GetInt32());
        json["execution"]!.AsObject().Remove("lockedGraphicsClockMHz");
        Assert.Equal("Json", Assert.Throws<RendererPerfProtocolException>(() => RendererPerfRehearsalManifestTests.Parse(json)).Code);
    }

    [Theory]
    [InlineData("text", "Json")]
    [InlineData("fractional", "Json")]
    [InlineData("overflow", "Json")]
    [InlineData("null", "Preset")]
    [InlineData("zero", "Preset")]
    [InlineData("other-clock", "Preset")]
    [InlineData("duplicate", "Json")]
    public void Paid_clock_refuses_missing_mistyped_or_nonpolicy_values(string fault, string code)
    {
        JsonObject json = RendererPerfProtocolTests.ManifestJson();
        json["execution"]!["lockedGraphicsClockMHz"] = fault switch
        {
            "text" => JsonValue.Create("1200"),
            "fractional" => JsonValue.Create(1200.5),
            "overflow" => JsonValue.Create(long.MaxValue),
            "null" => null,
            "zero" => JsonValue.Create(0),
            "other-clock" => JsonValue.Create(1215),
            _ => JsonValue.Create(1200)
        };
        string text = json.ToJsonString();
        if (fault == "duplicate") text = text.Replace("\"lockedGraphicsClockMHz\":1200", "\"lockedGraphicsClockMHz\":1200,\"lockedGraphicsClockMHz\":1200", StringComparison.Ordinal);
        Assert.Equal(code, Assert.Throws<RendererPerfProtocolException>(() => RendererPerfManifest.Parse(Encoding.UTF8.GetBytes(text))).Code);
    }

    [Theory]
    [InlineData("rehearsal")]
    [InlineData("rehearsal-full")]
    [InlineData("rehearsal-controls")]
    public void Software_classes_refuse_a_hardware_clock(string preset)
    {
        JsonObject json = RendererPerfColdLimitPolicyTests.ManifestJson(preset, "linux-vulkan");
        json["execution"]!["lockedGraphicsClockMHz"] = 1200;
        Assert.Equal("Preset", Assert.Throws<RendererPerfProtocolException>(() => RendererPerfRehearsalManifestTests.Parse(json)).Code);
    }
}
