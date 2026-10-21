using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapEdit;
using KhaozEngine.Content;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.MapEditTool;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeRenderMaterialSchemaTests
{
    internal static NativeRenderPolicyFile PolicyFile()
    {
        var options = new GroundCoverRenderOptions
        {
            UseGpuBatches = true,
            FadeMode = GroundCoverFadeMode.HeightScale,
            WindStrength = .7f,
            MotionProfiles = new Dictionary<string, FoliageMotionProfile>
            {
                ["shrub-a"] = new(.3f, .15f, .4f, .035f),
                ["shrub-b"] = new(.6f, .2f, .1f, .07f),
                ["shrub-c"] = new(.4f, .1f, .2f, .02f),
                ["shrub-d"] = new(.2f, .1f, .3f, .01f)
            }
        };
        var render = new NativeRenderPolicy(new([], 200), [], new(options, new()), new WaterLook { SwellAmplitude = 0, WaveSource = WaterWaveSource.Procedural },
            ["floor-material"], new() { [10] = "floor-material" });
        return new(1, "windy", [new("windy", render)], new(Vector3.Normalize(new(-1, 1, 0)), Color.White, 1,
            CaptureDependencyFixtures.Lighting().Shadows, 16), 1, "RenderStreamPlan");
    }
    internal static string PolicyJson() => JsonSerializer.Serialize(PolicyFile(), NativePolicyJson.Options);
    internal static string Schema(string name)
    {
        var assembly = typeof(NativeRenderRequest).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.Ordinal)))!;
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    [Fact]
    public void ClosedRequestAndPolicySchemas_AgreeWithTheEvaluators()
    {
        string input = NativeRenderRequestTests.Json;
        Assert.True(JsonSchemaValidator.Validate(input, Schema("native-render-request.schema.json")).IsValid);
        NativeRenderRequest.Read(input);
        string policy = PolicyJson();
        var validated = JsonSchemaValidator.Validate(policy, Schema("native-render-policy.schema.json"));
        Assert.True(validated.IsValid, string.Join("\n", validated.Errors));
        var file = NativeRenderPolicyFile.Read(policy);
        Assert.Equal(.7f, file.Styles[0].Render.Freeze().Foliage.Options.WindStrength);
        Assert.Equal(4, file.Styles[0].Render.Freeze().Foliage.Options.MotionProfiles.Count);
        foreach (string path in new[] { "", "styles.0", "styles.0.render", "lighting", "lighting.shadows", "lighting.shadows.pointShadows", "styles.0.render.foliage.options", "styles.0.render.water" })
        {
            var node = JsonNode.Parse(policy)!;
            foreach (string segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries)) node = int.TryParse(segment, out int index) ? node[index]! : node[segment]!;
            node["Unknown"] = 1;
            string changed = node.Root.ToJsonString();
            Assert.False(JsonSchemaValidator.Validate(changed, Schema("native-render-policy.schema.json")).IsValid);
            Assert.Throws<ArgumentException>(() => NativeRenderPolicyFile.Read(changed));
        }
    }
    [Fact]
    public void Policy_RejectsDuplicateCaseVariantAndInvalidStylesBeforeHostResolution()
    {
        string json = PolicyJson();
        Assert.Throws<ArgumentException>(() => NativeRenderPolicyFile.Read(json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1")));
        Assert.Throws<ArgumentException>(() => NativeRenderPolicyFile.Read(json.Replace("\"windStrength\":", "\"WindStrength\":")));
        var doc = JsonNode.Parse(json)!; doc["styles"]!.AsArray().Add(doc["styles"]![0]!.DeepClone());
        Assert.Throws<ArgumentException>(() => NativeRenderPolicyFile.Read(doc.ToJsonString()));
        doc = JsonNode.Parse(json)!; doc["focusDefault"] = "Camera";
        Assert.Throws<ArgumentException>(() => NativeRenderPolicyFile.Read(doc.ToJsonString()));
    }
    [Fact]
    public void ConfigurationRanges_AgreeBeforeHostResolution()
    {
        foreach (var (path, value) in new[] { ("styles.0.render.water.opacity", "-1"), ("styles.0.render.water.swellComponents", "9"),
            ("styles.0.render.foliage.options.windStrength", "2"), ("lighting.shadows.shadowCascadeCount", "5"),
            ("lighting.shadows.pointShadows.faceResolution", "65"), ("previewWindowLimit", "0") })
        {
            var root = JsonNode.Parse(PolicyJson())!; var node = root; var pieces = path.Split('.');
            foreach (string segment in pieces[..^1]) node = int.TryParse(segment, out int index) ? node[index]! : node[segment]!;
            node[pieces[^1]] = JsonNode.Parse(value); string json = root.ToJsonString();
            Assert.False(JsonSchemaValidator.Validate(json, Schema("native-render-policy.schema.json")).IsValid);
            Assert.ThrowsAny<ArgumentException>(() => NativeRenderPolicyFile.Read(json));
        }
    }
    [Fact]
    public void MaterialSchema_UsesTheTaskOneClosedContract()
    {
        string material = NativeRenderAssetFixtures.Material();
        MapRenderMaterialJson.Read(System.Text.Encoding.UTF8.GetBytes(material), "paint");
        var assembly = typeof(MapRenderMaterialJson).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith("map-render-material.schema.json", StringComparison.Ordinal)))!;
        using var reader = new StreamReader(stream);
        string schema = reader.ReadToEnd();
        Assert.True(JsonSchemaValidator.Validate(material, schema).IsValid);
        string bad = material.Replace("\"metallic\":0.1", "\"metallic\":2", StringComparison.Ordinal);
        Assert.False(JsonSchemaValidator.Validate(bad, schema).IsValid);
        Assert.Throws<MapRenderAssetRefusal>(() => MapRenderMaterialJson.Read(System.Text.Encoding.UTF8.GetBytes(bad), "paint"));
    }
}
