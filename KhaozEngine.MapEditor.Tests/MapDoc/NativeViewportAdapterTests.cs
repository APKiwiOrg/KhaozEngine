using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapEditor;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeViewportAdapterTests
{
    [Fact]
    public void ViewportAndSnapshot_ShareNativeInventoryAndStyle()
    {
        var request = CaptureDependencyFixtures.Request();
        var prepared = MapCapturePreparation.Prepare(request);
        Assert.Equal(KhaozEngine.MapDoc.Scene.MapSceneStatus.CompleteForScope, prepared.Status);
        var context = new MapNativeRenderContext(request.Closure, request.Source,
            MapRenderExtentValidator.Validate(request.Closure, request.Source.RequiredAssetIds, new(new())).Proofs!,
            request.Policy.Render, request.Policy.Lighting, "windy");
        var options = new MapEditorOptions { NativeRenderHost = new Host(context), NativeObserver = () => request.Observer, NativeStyleId = "windy" };
        var resolved = options.NativeRenderHost.Resolve(CaptureDependencyFixtures.Create().Document, "invented.map.json", options.NativeStyleId);
        Assert.Same(request.Policy.Render, resolved.RenderPolicy); Assert.Equal(.7f, resolved.RenderPolicy.Foliage.Options.WindStrength);
        Assert.Equal(request.Observer, options.NativeObserver());
        Assert.Equal(request.Source.SourceGeneration, prepared.Witness.Scene.SourceGeneration);
        using var device = new NativeUploadTestScene();
        using var world = new ViewportWorld(device.Scene, [])
        {
            NativeRenderHost = options.NativeRenderHost,
            NativeObserver = options.NativeObserver,
            NativeStyleId = "windy"
        };
        world.Build(CaptureDependencyFixtures.Create().Document, MapDocRegistry.CreateDefault());
        var view = (NativeMapView)typeof(ViewportWorld).GetField("_nativeView", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(world)!;
        Assert.Equal(0, view.SettleForCapture(1024).Pending);
        var residency = (MapRenderResidency)typeof(NativeMapView).GetField("_residency", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(view)!;
        var recorder = new MapDrawRecorder();
        residency.Record(new(KhaozEngine.Primitives.WorldFrame.Origin, request.Observer, request.RoofMode, request.TimeSeconds, request.WindSeed, request.Focus!.Value), recorder);
        var keys = recorder.Snapshot().Resources.Select(r => r.Reference.Key.OwnerId).Distinct().ToArray();
        Assert.Contains("capture-floor", keys); Assert.Contains("sun-caster", keys); Assert.Contains("point-caster", keys); Assert.Contains("point-a", keys);
        Assert.DoesNotContain("unrelated-far", keys);
        Assert.Equal(request.Policy.Render.Digest, residency.DesiredState.Policy.Digest);
        device.Scene.Begin(); world.Update(System.Numerics.Vector3.Zero, 0); world.Draw(System.Numerics.Vector3.Zero, null, default, new());
        Assert.True(device.Scene.LightCount > 0); Assert.NotEmpty(device.Scene.QueuedInstancesForTests);
        Assert.Equal(request.Observer, options.NativeObserver());
    }
    [Fact]
    public void MissingHost_RefusesBeforeTheAnalyticTerrainPath()
    {
        using var world = new ViewportWorld(null!, []);
        var error = Assert.Throws<MapDocumentException>(() => world.Build(CaptureDependencyFixtures.Create().Document, MapDocRegistry.CreateDefault()));
        Assert.Equal("NativeRenderHostRequired", error.Message); Assert.False(world.IsBuilt);
    }
    sealed class Host(MapNativeRenderContext context) : IMapNativeRenderHost
    { public MapNativeRenderContext Resolve(MapDocument document, string path, string style) => context; }
}
