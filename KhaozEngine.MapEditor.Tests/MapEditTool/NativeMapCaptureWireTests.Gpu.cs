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
using KhaozEngine.Render3D;
using Microsoft.Extensions.DependencyInjection;

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
            var output = await harness.Client.CallAsync("render_view", Arguments(NativeRenderRequestTests.Json)); Assert.False(output.IsError == true, WireText(output));
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
            var decoded = ImageRgba.Decode(image.DecodedData.ToArray()); Assert.Equal(640, decoded.Width); Assert.Equal(480, decoded.Height);
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
            var fixture = FocusFixture(dir);
            await using var harness = await McpWire.StartAsync(fixture.Policy);
            Assert.False((await harness.Client.CallAsync("map_open", new Dictionary<string, object?> { ["path"] = fixture.World })).IsError == true);
            int submitted = 0;
            harness.Services.GetRequiredService<RenderService>().NativeCapture = request => NativeMapSnapshot.Capture(request, scene =>
            {
                if (request.StyleId == "windy") { AssertSubmittedFocus(scene, request); submitted++; }
                else Assert.Empty(scene.FoliageUniformsForTests.ToArray());
            });
            var first = await harness.Client.CallAsync("render_view", Arguments(FocusRequest(5))); Assert.False(first.IsError == true, WireText(first));
            var second = await harness.Client.CallAsync("render_view", Arguments(FocusRequest(0))); Assert.False(second.IsError == true, WireText(second));
            Assert.True(submitted >= 2);
            using var a = JsonDocument.Parse(((TextContentBlock)first.Content[0]).Text);
            using var b = JsonDocument.Parse(((TextContentBlock)second.Content[0]).Text);
            Assert.Equal("Explicit", a.RootElement.GetProperty("focusSource").GetString()); Assert.Equal("Explicit", b.RootElement.GetProperty("focusSource").GetString());
            Assert.Equal(5, a.RootElement.GetProperty("focus").GetProperty("local").GetProperty("x").GetDouble());
            Assert.Equal(0, b.RootElement.GetProperty("focus").GetProperty("local").GetProperty("x").GetDouble());
            var left = ImageRgba.Decode(((ImageContentBlock)first.Content[1]).DecodedData.ToArray()).Pixels;
            var right = ImageRgba.Decode(((ImageContentBlock)second.Content[1]).DecodedData.ToArray()).Pixels;
            var bareFirst = await harness.Client.CallAsync("render_view", Arguments(FocusRequest(5, "bare"))); Assert.False(bareFirst.IsError == true, WireText(bareFirst));
            var bareSecond = await harness.Client.CallAsync("render_view", Arguments(FocusRequest(0, "bare"))); Assert.False(bareSecond.IsError == true, WireText(bareSecond));
            var bareLeft = ImageRgba.Decode(((ImageContentBlock)bareFirst.Content[1]).DecodedData.ToArray()).Pixels;
            var bareRight = ImageRgba.Decode(((ImageContentBlock)bareSecond.Content[1]).DecodedData.ToArray()).Pixels;
            int foliagePixels = 0, propPixels = 0;
            for (int y = 0; y < 480; y++) for (int x = 0; x < 640; x++)
            {
                int offset = (y * 640 + x) * 4;
                bool propChanged = Math.Abs(bareLeft[offset] - bareRight[offset]) >= 8 || Math.Abs(bareLeft[offset + 1] - bareRight[offset + 1]) >= 8;
                if (x >= 320 && propChanged) propPixels++;
                // The top foliage ROI is clear of both props. Subtract the no-foliage focus control's pixel change.
                if (y is >= 48 and < 160 &&
                    (Math.Abs((left[offset] - right[offset]) - (bareLeft[offset] - bareRight[offset])) >= 8 ||
                     Math.Abs((left[offset + 1] - right[offset + 1]) - (bareLeft[offset + 1] - bareRight[offset + 1])) >= 8)) foliagePixels++;
            }
            Assert.True(foliagePixels >= 64, $"Explicit focus changed {foliagePixels} foliage ROI pixels after the bare control, expected at least 64.");
            Assert.True(propPixels >= 64, $"Explicit focus changed {propPixels} right prop pixels in the bare control, expected at least 64.");
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
