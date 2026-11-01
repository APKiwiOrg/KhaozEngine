using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfConstructionTests
{
    [Fact]
    public void Shared_sources_and_descriptors_round_trip_once_with_every_factory_reference()
    {
        const string vertex = "vertex\r\n// exact \"caf\u00e9\"";
        using var native = new RendererPerfConstructionFixture();
        var capture = new RendererPerfConstructionCapture(OneOperation() with { Blocks = [new("S0", RendererPerfLayout.TotalOnly, 0, 0, 1)] });
        for (int op = 0; op < 2; op++)
        {
            capture.BeginOperation(op); var device = RendererPerfDevice.Wrap(native, Recorder(), capture);
            using var layout = device.Factory.CreateResourceLayout(new([new("Params", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex)]));
            foreach (string fragment in new[] { "fragment A", "fragment B" })
            {
                using var shader = device.Factory.CreateShadersFromSpirv(new(vertex.AsSpan()), fragment);
                using var pipeline = device.Factory.CreateGraphicsPipeline(new()
                { ShaderSet = shader, ResourceLayouts = [layout], VertexLayouts = [], BlendAttachments = [], Outputs = new(null) });
            }
            capture.EndOperation();
        }
        var i = capture.Freeze(); Assert.True(i.Complete, i.Reason);
        Assert.Equal(10, i.FactoryEvents.Length); Assert.Equal(4, i.Occurrences.Length);
        Assert.Equal(2, i.Shaders.Length); Assert.Single(i.Layouts); Assert.Equal(2, i.Recipes.Length);
        Assert.Equal(new[] { 0, 1, 0, 1 }, i.Occurrences.Select(o => o.RecipeId));
        byte[] bytes = RendererPerfCompilerJson.Serialize(i, 1048576);
        var restored = RendererPerfCompilerJson.Parse(bytes, 1048576);
        Assert.Equal(vertex, restored.Shaders[0].Stages[0].Source);
        Assert.Same(restored.Shaders[0].Stages[0].Source, restored.Shaders[1].Stages[0].Source);
        Assert.Equal(bytes, RendererPerfCompilerJson.Serialize(restored, 1048576));
        using var json = JsonDocument.Parse(bytes);
        Assert.Equal(3, json.RootElement.GetProperty("sources").GetArrayLength());
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("foreign")]
    [InlineData("duplicate")]
    [InlineData("unused")]
    [InlineData("order")]
    [InlineData("old-schema")]
    [InlineData("shader")]
    [InlineData("layout")]
    public void Strict_content_tables_refuse_changed_foreign_unused_duplicate_or_unordered_values(string fault)
    {
        using var native = new RendererPerfConstructionFixture();
        var capture = new RendererPerfConstructionCapture(OneOperation()); capture.BeginOperation(0);
        var device = RendererPerfDevice.Wrap(native, Recorder(), capture);
        using var layout = device.Factory.CreateResourceLayout(new([]));
        using var first = Compute(device); using var second = Compute(device, ComputeSource + "\n// second");
        capture.EndOperation(); var inventory = capture.Freeze();
        var node = JsonNode.Parse(RendererPerfCompilerJson.Serialize(inventory, 1048576))!;
        var sources = node["sources"]!.AsArray();
        switch (fault)
        {
            case "hash": sources[0]!["source"] = "changed exact bytes"; break;
            case "foreign": node["shaders"]![0]!["stages"]![0]!["sourceSha256"] = new string('f', 64); break;
            case "duplicate": sources.Add(sources[0]!.DeepClone()); break;
            case "unused": sources.Add(new JsonObject { ["sha256"] = RendererPerfConstructionContent.SourceHash("unused"), ["source"] = "unused" }); break;
            case "order": var source = sources[0]!; sources.RemoveAt(0); sources.Add(source); break;
            case "old-schema": node["schema"] = "khaozengine.renderer-compiler-inventory/1"; break;
            case "shader":
            case "layout":
                var rows = node[fault == "shader" ? "shaders" : "layouts"]!.AsArray();
                var duplicate = rows[0]!.DeepClone(); duplicate["id"] = rows.Count; rows.Add(duplicate);
                var events = node["factoryEvents"]!.AsArray();
                events.Add(new JsonObject
                {
                    ["operationOrdinal"] = 0,
                    ["ordinal"] = events.Count,
                    ["kind"] = fault,
                    ["inputId"] = rows.Count - 1,
                    ["outputId"] = rows.Count - 1,
                    ["failure"] = null
                });
                break;
        }
        var error = Assert.Throws<JsonException>(() => RendererPerfCompilerJson.Parse(Encoding.UTF8.GetBytes(node.ToJsonString()), 1048576));
        Assert.Contains(fault is "shader" or "layout" ? "Duplicate" : fault == "old-schema" ? "schema" : "ource", error.Message, StringComparison.Ordinal);
    }
}
