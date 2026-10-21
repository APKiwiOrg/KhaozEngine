using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapEdit;
using KhaozEngine.Render2D;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.Tests.MapDoc;
using ModelContextProtocol.Protocol;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

public sealed partial class NativeMapCaptureWireTests
{
    [GpuFact]
    public async Task NativeRenderWire_TransportsExactRequest()
    {
        string dir = Temp();
        try
        {
            var (doc, _) = CaptureDependencyFixtures.Create(dir); string path = Path.Combine(dir, "world.map.json"); MapDocumentFile.Save(doc, path);
            string policyPath = Path.Combine(dir, "policy.json"); File.WriteAllText(policyPath, NativeRenderMaterialSchemaTests.PolicyJson());
            await using var harness = await McpWire.StartAsync(policyPath);
            var opened = await harness.Client.CallAsync("map_open", new Dictionary<string, object?> { ["path"] = path }); Assert.False(opened.IsError == true);
            var output = await harness.Client.CallAsync("render_view", Arguments(NativeRenderRequestTests.Json)); Assert.False(output.IsError == true);
            var text = Assert.IsType<TextContentBlock>(output.Content[0]); var image = Assert.IsType<ImageContentBlock>(output.Content[1]);
            using var json = JsonDocument.Parse(text.Text); var root = json.RootElement;
            Assert.Equal("Incomplete", root.GetProperty("status").GetString()); Assert.True(root.GetProperty("capped").GetBoolean());
            Assert.False(root.GetProperty("completeForScope").GetBoolean()); Assert.False(root.GetProperty("wholeWorldComplete").GetBoolean());
            Assert.Equal("Quality", root.GetProperty("aa").GetString()); Assert.Equal("windy", root.GetProperty("styleId").GetString());
            Assert.Equal("room-b", root.GetProperty("observerSpaceId").GetString()); Assert.Equal("AlwaysHidden", root.GetProperty("roofMode").GetString());
            Assert.Equal(2, root.GetProperty("observer").GetProperty("local").GetProperty("x").GetDouble());
            Assert.Equal(1, root.GetProperty("observer").GetProperty("local").GetProperty("y").GetDouble());
            Assert.Equal(3.25, root.GetProperty("timeSeconds").GetDouble()); Assert.Equal(23, root.GetProperty("windSeed").GetInt32()); Assert.Equal(4, root.GetProperty("warmupFrames").GetInt32());
            Assert.Equal(0, root.GetProperty("readiness").GetProperty("pending").GetInt32()); Assert.Equal(1, root.GetProperty("readiness").GetProperty("ready").GetInt32());
            Assert.True(root.GetProperty("readiness").GetProperty("cpuBytes").GetInt64() <= 268435456);
            Assert.True(root.GetProperty("readiness").GetProperty("gpuBytes").GetInt64() <= 536870912);
            Assert.Contains(root.GetProperty("findings").EnumerateArray(), f => f.GetString() == "scope.capped");
            Assert.False(string.IsNullOrEmpty(root.GetProperty("backend").GetString()));
            var decoded = ImageRgba.Decode(image.Data.ToArray()); Assert.Equal(640, decoded.Width); Assert.Equal(480, decoded.Height);
            var actual = harness.Services.GetService(typeof(RenderService)) as RenderService;
            var direct = actual!.RenderNative(NativeRenderRequestTests.Json);
            Assert.Equal(direct.Image!.Value.Backend.ToString(), root.GetProperty("backend").GetString());
            Assert.Equal(direct.Readiness.CpuBytes, root.GetProperty("readiness").GetProperty("cpuBytes").GetInt64());
            Assert.Equal(direct.Readiness.GpuBytes, root.GetProperty("readiness").GetProperty("gpuBytes").GetInt64());
            Assert.Equal(direct.Readiness.QueuedBytes, root.GetProperty("readiness").GetProperty("queuedBytes").GetInt64());
        }
        finally { Directory.Delete(dir, true); }
    }
    [GpuFact]
    public async Task NativeWire_ExplicitFocusChangesSubmittedCoverage()
    {
        string dir = Temp();
        try
        {
            var (doc, _) = CaptureDependencyFixtures.Create(dir);
            doc.Placements.RemoveAll(p => p.Id == "unrelated-far");
            doc.Placements[0].X = -3; doc.Placements[0].Y = .5f; doc.Placements[1].X = 3; doc.Placements[1].Y = .5f;
            string path = Path.Combine(dir, "world.map.json"); MapDocumentFile.Save(doc, path);
            var file = NativeRenderMaterialSchemaTests.PolicyFile(); var row = file.Styles[0];
            file = file with { Styles = [row with { Render = row.Render with { Placements = new([], 4) } }] };
            string policy = Path.Combine(dir, "policy.json"); File.WriteAllText(policy, JsonSerializer.Serialize(file, NativePolicyJson.Options));
            await using var harness = await McpWire.StartAsync(policy);
            Assert.False((await harness.Client.CallAsync("map_open", new Dictionary<string, object?> { ["path"] = path })).IsError == true);
            var first = await harness.Client.CallAsync("render_view", Arguments(NativeRenderRequestTests.Json)); Assert.False(first.IsError == true);
            var changed = JsonNode.Parse(NativeRenderRequestTests.Json)!; changed["focus"]!["local"]!["x"] = 0;
            var second = await harness.Client.CallAsync("render_view", Arguments(changed.ToJsonString())); Assert.False(second.IsError == true);
            using var a = JsonDocument.Parse(((TextContentBlock)first.Content[0]).Text);
            using var b = JsonDocument.Parse(((TextContentBlock)second.Content[0]).Text);
            Assert.Equal("Explicit", a.RootElement.GetProperty("focusSource").GetString()); Assert.Equal("Explicit", b.RootElement.GetProperty("focusSource").GetString());
            Assert.Equal(5, a.RootElement.GetProperty("focus").GetProperty("local").GetProperty("x").GetDouble());
            Assert.Equal(0, b.RootElement.GetProperty("focus").GetProperty("local").GetProperty("x").GetDouble());
            var left = ImageRgba.Decode(((ImageContentBlock)first.Content[1]).Data.ToArray()).Pixels;
            var right = ImageRgba.Decode(((ImageContentBlock)second.Content[1]).Data.ToArray()).Pixels;
            int pixels = 0;
            for (int y = 0; y < 480; y++) for (int x = 320; x < 640; x++)
            { int offset = (y * 640 + x) * 4; if (Math.Abs(left[offset] - right[offset]) >= 8 || Math.Abs(left[offset + 1] - right[offset + 1]) >= 8) pixels++; }
            Assert.True(pixels >= 64, "Explicit focus must change at least 64 pixels in the right prop region.");
        }
        finally { Directory.Delete(dir, true); }
    }
    static Dictionary<string, object?> Arguments(string json) => new()
    {
        ["eyeX"] = 999,
        ["eyeY"] = 999,
        ["eyeZ"] = 999,
        ["targetX"] = 999,
        ["targetY"] = 999,
        ["targetZ"] = 999,
        ["width"] = 1,
        ["height"] = 1,
        ["nativeRequestJson"] = json
    };
    static string Temp() { string dir = Path.Combine(Path.GetTempPath(), "khaozengine-native-wire-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); return dir; }
}
