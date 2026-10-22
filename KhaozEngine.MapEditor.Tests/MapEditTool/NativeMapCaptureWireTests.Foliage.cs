using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapEdit;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.MapDoc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ModelContextProtocol.Protocol;

namespace KhaozEngine.Tests.MapEditTool;

public sealed partial class NativeMapCaptureWireTests
{
    [Fact]
    public async Task NativeWire_FoliageFocusReachesActualSinkBeforeAnyGpuCapture()
    {
        string dir = Temp();
        try
        {
            var fixture = FocusFixture(dir);
            await using var harness = await McpWire.StartAsync(fixture.Policy);
            Assert.False((await harness.Client.CallAsync("map_open", new Dictionary<string, object?> { ["path"] = fixture.World })).IsError == true);
            int submissions = 0;
            harness.Services.GetRequiredService<RenderService>().NativeCapture = request =>
            {
                var prepared = MapCapturePreparation.Prepare(request);
                Assert.NotEqual(MapCaptureFocusSource.Unresolved, prepared.FocusSource);
                var proofs = MapRenderExtentValidator.Validate(request.Closure, request.Source.RequiredAssetIds, new(new())).Proofs!;
                var source = request.Source.WithRenderExtents(proofs).Source!;
                using var host = new NativeUploadTestScene();
                using var residency = new MapRenderResidency(new(source, request.Closure, proofs, request.Policy.Render, prepared.Witness.AcquiredScopes), new MapScene3DSink(host.Scene));
                using var view = new NativeMapView(host.Scene, residency);
                var ready = view.SettleForCapture(1024);
                Assert.Equal(0, ready.Pending);
                Assert.Equal(KhaozEngine.MapDoc.Scene.MapSceneStatus.CompleteForScope, ready.Transition.Status);
                host.Scene.Begin();
                var echo = prepared.Echo;
                view.Draw(new(echo.Scope.Surfaces.Frame, echo.Observer, echo.RoofMode, echo.TimeSeconds, echo.WindSeed, echo.Focus!.Value));
                var recorder = new MapDrawRecorder();
                residency.Record(new(echo.Scope.Surfaces.Frame, echo.Observer, echo.RoofMode, echo.TimeSeconds, echo.WindSeed, echo.Focus!.Value), recorder);
                var recorded = recorder.Snapshot();
                Assert.True(recorded.Resources.Any(r => r.Payload is MapFoliageResource), "No generated rows. Required=" +
                    string.Join(",", prepared.Witness.Scene.Required.Select(k => k.Kind + "/" + k.Id)) + " resources=" +
                    string.Join(",", recorded.Resources.Select(r => r.Reference.Key.Kind + "/" + r.Reference.Key.OwnerId)));
                AssertSubmittedFocus(host.Scene, request);
                submissions++;
                return prepared;
            };
            var first = await harness.Client.CallAsync("render_view", Arguments(FocusRequest(5)));
            Assert.True(first.IsError != true, WireText(first));
            var second = await harness.Client.CallAsync("render_view", Arguments(FocusRequest(0)));
            Assert.True(second.IsError != true, WireText(second));
            Assert.Equal(2, submissions);
        }
        finally { Directory.Delete(dir, true); }
    }

    static void AssertSubmittedFocus(Scene3D scene, MapSnapshotRequest request)
    {
        var slots = scene.FoliageUniformsForTests.ToArray(); Assert.NotEmpty(slots);
        var options = request.Policy.Render.Foliage.Options;
        Assert.Equal(4, options.DrawRadius); Assert.Equal(2, options.DensityRadius);
        Assert.Equal(.35f, options.DistantDensity); Assert.Equal(.5f, options.InstanceFadeBandWidth);
        Assert.Equal(2, options.MotionProfiles.Count); Assert.True(options.ModelCastsShadows["A"]); Assert.False(options.ModelCastsShadows["B"]);
        // FocusRadius.xyz is the LocalFocus actually submitted, reduced against the published batch's anchor.
        var batches = (System.Collections.IEnumerable)typeof(Scene3D).GetField("_foliageBatches", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(scene)!;
        var anchor = Assert.Single(batches.Cast<FoliageBatch>()).PreparedAnchor!.Value;
        Assert.Equal(new RenderAnchor(-4, -1, -4), anchor);
        Assert.True(request.Focus!.Value.Local.X is 5 or 0);
        var localFocus = request.Focus.Value.Local.X == 5 ? new Vector3(9, 1, 4) : new Vector3(4, 1, 4);
        Assert.NotEqual(request.Observer.Local, localFocus); Assert.NotEqual(request.Framing.Eye.Local, localFocus);
        Assert.All(slots, slot =>
        {
            Assert.Equal(localFocus, new Vector3(slot.FocusRadius.X, slot.FocusRadius.Y, slot.FocusRadius.Z));
            Assert.Equal(new Vector4(localFocus, 4), slot.FocusRadius);
            Assert.Equal(new Vector4(1, .35f, 2, 1), slot.Density);
        });
    }

    static (string World, string Policy) FocusFixture(string dir)
    {
        var (doc, closure) = CaptureDependencyFixtures.Create(dir, foliage: true);
        doc.Placements.RemoveAll(p => p.Id == "unrelated-far");
        doc.Placements[0].X = -3; doc.Placements[0].Y = .5f; doc.Placements[1].X = 3; doc.Placements[1].Y = .5f;
        string path = Path.Combine(dir, "world.map.json"); MapDocumentFile.Save(doc, path);
        var file = NativeRenderMaterialSchemaTests.PolicyFile(); var row = file.Styles[0];
        var options = CaptureDependencyFixtures.FoliageOptions();
        var extents = closure.Assets.Where(a => a.Id is "A" or "B").ToDictionary(a => a.Id,
            a => new NativeFoliageExtent(a.Id, a.RenderBounds.Min, a.RenderBounds.Max, a.SourceUnitsToMetres, .35f));
        var render = row.Render with { Placements = new([], 4), Foliage = new(options, extents) };
        var bare = CaptureDependencyFixtures.FoliageOptions(); bare.QualityDensity = 0;
        file = file with { Styles = [row with { Render = render }, new("bare", render with { Foliage = new(bare, extents) })] };
        string policy = Path.Combine(dir, "policy.json"); File.WriteAllText(policy, JsonSerializer.Serialize(file, NativePolicyJson.Options));
        return (path, policy);
    }
    static string FocusRequest(float x, string style = "windy")
    {
        var request = JsonNode.Parse(NativeRenderRequestTests.Json)!;
        request["focus"]!["local"]!["x"] = x; request["styleId"] = style;
        request["scope"]!["min"]!["x"] = -4; request["scope"]!["min"]!["z"] = -4;
        request["scope"]!["max"]!["x"] = 4; request["scope"]!["max"]!["z"] = 4;
        return request.ToJsonString();
    }
}
