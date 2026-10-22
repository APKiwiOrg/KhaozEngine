using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using KhaozEngine.MapDoc;
using System.Linq;
using System.Text.Json;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapEdit;
using KhaozEngine.Tests.MapDoc;
using Xunit;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render2D;
using ModelContextProtocol.Protocol;

namespace KhaozEngine.Tests.MapEditTool;

[Collection(McpWireCollection.Name)]
public sealed partial class NativeMapCaptureWireTests
{
    [Fact]
    public void NativeResult_ImageRoundTripsThroughMcpContentBlockDeserialization()
    {
        var result = MapCapturePreparation.Prepare(CaptureDependencyFixtures.Request());
        byte[] rgba = [255, 0, 0, 255, 0, 255, 0, 255];
        result = result with { Image = new Render3DCapture(rgba, 2, 1, GpuBackendKind.Vulkan) };
        var wire = new CallToolResult { Content = RenderService.NativeResultBlocks(result).ToList() };
        var decoded = JsonSerializer.Deserialize<CallToolResult>(JsonSerializer.Serialize(wire))!;
        Assert.IsType<TextContentBlock>(decoded.Content[0]);
        var image = Assert.IsType<ImageContentBlock>(decoded.Content[1]);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(rgba, ImageRgba.Decode(image.DecodedData.ToArray()).Pixels);
        var noImage = JsonSerializer.Deserialize<CallToolResult>(JsonSerializer.Serialize(new CallToolResult
        { Content = RenderService.NativeResultBlocks(result with { Image = null }).ToList() }))!;
        Assert.IsType<TextContentBlock>(Assert.Single(noImage.Content));
    }
    [Fact]
    public async Task NativeRequest_InvalidDomainRefusesBeforeHost()
    {
        string directory = Temp();
        try
        {
            var (document, _) = CaptureDependencyFixtures.Create(directory);
            string path = Path.Combine(directory, "world.map.json"); MapDocumentFile.Save(document, path);
            await using var harness = await McpWire.StartAsync();
            Assert.False((await harness.Client.CallAsync("map_open", new Dictionary<string, object?> { ["path"] = path })).IsError == true);
            var session = harness.Services.GetRequiredService<MapEditSession>(); var host = new WireHost(); session.NativeRenderHost = host;
            foreach (string invalid in NativeRenderRequestTests.InvalidRequests())
                Assert.True((await harness.Client.CallAsync("render_view", Arguments(invalid))).IsError == true);
            var missing = Arguments(NativeRenderRequestTests.Json); missing.Remove("nativeRequestJson");
            Assert.True((await harness.Client.CallAsync("render_view", missing)).IsError == true);
            Assert.Equal(0, host.Entries);
            session.Create(Path.Combine(directory, "analytic.map.json"), "analytic", "Analytic", -32, -32, 32, 32);
            Assert.True((await harness.Client.CallAsync("render_view", Arguments(NativeRenderRequestTests.Json))).IsError == true);
            Assert.Equal(0, host.Entries);
        }
        finally { Directory.Delete(directory, true); }
    }
    sealed class WireHost : IMapNativeRenderHost
    {
        internal int Entries;
        public MapNativeRenderContext Resolve(MapDocument document, string path, string style)
        { Entries++; throw new InvalidOperationException("Unexpected host entry."); }
    }
    [Fact]
    public void NativeWire_UsesExactPrimitivePropertySets()
    {
        var request = CaptureDependencyFixtures.Request(1) with { Requirement = MapCaptureRequirement.Preview };
        var result = MapCapturePreparation.Prepare(request);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(NativeRenderWire.ToWire(result), NativeJson.Options));
        var r = json.RootElement;
        Keys(r, "schemaVersion status backend requestedScope requiredScopes acquiredScopes framing observer observerSpaceId focus focusSource roofMode timeSeconds windSeed aa styleId requirement warmupFrames route completeForScope wholeWorldComplete capped readiness findings");
        Assert.Equal(1, r.GetProperty("schemaVersion").GetInt32()); Assert.Equal("Incomplete", r.GetProperty("status").GetString());
        Assert.True(r.GetProperty("capped").GetBoolean()); Assert.False(r.GetProperty("completeForScope").GetBoolean()); Assert.False(r.GetProperty("wholeWorldComplete").GetBoolean());
        Assert.Equal("Explicit", r.GetProperty("focusSource").GetString()); Assert.Equal(5, r.GetProperty("focus").GetProperty("local").GetProperty("x").GetDouble());
        Assert.Equal("Quality", r.GetProperty("aa").GetString()); Assert.Equal(3.25, r.GetProperty("timeSeconds").GetDouble()); Assert.Equal(23, r.GetProperty("windSeed").GetInt32());
        Assert.Equal("windy", r.GetProperty("styleId").GetString()); Assert.Equal(4, r.GetProperty("warmupFrames").GetInt32());
        Scope(r.GetProperty("requestedScope")); foreach (var s in r.GetProperty("requiredScopes").EnumerateArray()) Scope(s);
        foreach (var s in r.GetProperty("acquiredScopes").EnumerateArray()) Scope(s);
        Keys(r.GetProperty("framing"), "eye target orthographic verticalSizeOrFovDegrees width height nearPlane farPlane");
        Point(r.GetProperty("framing").GetProperty("eye")); Point(r.GetProperty("framing").GetProperty("target")); Point(r.GetProperty("observer")); Point(r.GetProperty("focus"));
        Keys(r.GetProperty("readiness"), "pending ready cpuBytes gpuBytes queuedBytes");
        foreach (var p in r.GetProperty("readiness").EnumerateObject()) Assert.True(p.Value.GetInt64() >= 0);
        Assert.All(r.GetProperty("findings").EnumerateArray(), f => Assert.Equal(JsonValueKind.String, f.ValueKind));
        var wrong = KhaozEngine.Tests.MapDoc.Scene.NativeSceneFixtures.Create("empty").Assets;
        var refusal = NativeRenderWire.ToWire(MapCapturePreparation.Prepare(request with { Closure = wrong, Requirement = MapCaptureRequirement.CompleteScope }));
        using var refused = JsonDocument.Parse(JsonSerializer.Serialize(refusal, NativeJson.Options));
        Assert.Equal(JsonValueKind.Null, refused.RootElement.GetProperty("focus").ValueKind); Assert.Equal("Unresolved", refusal.FocusSource);
    }
    static void Keys(JsonElement value, string names) => Assert.Equal(names.Split(' ').Order(StringComparer.Ordinal), value.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    static void Point(JsonElement p) { Keys(p, "frame local"); Keys(p.GetProperty("frame"), "x z"); Keys(p.GetProperty("local"), "x y z"); }
    static void Scope(JsonElement s) { Keys(s, "frame min max spaceIds channels motionPaddingMetres"); Keys(s.GetProperty("frame"), "x z"); Keys(s.GetProperty("min"), "x y z"); Keys(s.GetProperty("max"), "x y z"); }
}
