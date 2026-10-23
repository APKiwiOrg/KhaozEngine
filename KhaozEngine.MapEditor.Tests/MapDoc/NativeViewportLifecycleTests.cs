using System;
using System.IO;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Game;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapEditor;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("AllocSensitive")]
public sealed class NativeViewportLifecycleTests
{
    [Fact]
    public void SceneReload_RebuildsNativeViewportWithTheInitialHostConfiguration()
    {
        string directory = Path.Combine(Path.GetTempPath(), "khaozengine-native-reload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var fixture = CaptureDependencyFixtures.Create(directory);
            string path = Path.Combine(directory, "invented.map.json");
            MapDocumentFile.Save(fixture.Document, path);
            using var device = new NativeUploadTestScene();
            var request = CaptureDependencyFixtures.Request();
            var host = new Host(Context(request));
            Func<MapFramePoint> observer = () => request.Observer;
            Func<string?> space = () => "invented-room";
            var options = new MapEditorOptions
            {
                DocumentPath = path,
                NativeRenderHost = host,
                NativeStyleId = "windy",
                NativeObserver = observer,
                NativeObserverSpaceId = space
            };
            var scene = new MapEditorScene().Init(device.Scene, null!, null!, options);
            var manager = new SceneManager();
            manager.Push(scene);
            try
            {
                var original = scene.SceneViewport;
                Assert.True(original.IsNative);
                Assert.True(scene.ReloadDocument(), scene.StatusText);
                var reloaded = scene.SceneViewport;
                Assert.NotSame(original, reloaded);
                Assert.True(reloaded.IsNative);
                Assert.Same(host, reloaded.NativeRenderHost);
                Assert.Equal(path, reloaded.NativeDocumentPath);
                Assert.Equal("windy", reloaded.NativeStyleId);
                Assert.Same(observer, reloaded.NativeObserver);
                Assert.Same(space, reloaded.NativeObserverSpaceId);
                Assert.Equal(2, host.Resolves);
                Assert.Equal((path, "windy"), host.LastInput);
            }
            finally { manager.Pop(); }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void WarmViewportUpdate_WithinAcceptedWindowAllocatesZero(float offset)
    {
        var request = CaptureDependencyFixtures.Request();
        using var device = new NativeUploadTestScene();
        using var world = new ViewportWorld(device.Scene, [])
        {
            NativeRenderHost = new Host(Context(request)),
            NativeObserver = () => request.Observer,
            NativeStyleId = "windy"
        };
        world.Build(CaptureDependencyFixtures.Create().Document, MapDocRegistry.CreateDefault());
        var view = (NativeMapView)typeof(ViewportWorld).GetField("_nativeView", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!;
        Assert.Equal(0, view.SettleForCapture(1024).Pending);
        void Update() => world.Update(new Vector3(offset, 0, offset), 1f / 60);
        Update(); Update();
        AllocationMeasurement.NoPerCallAllocation("settled native ViewportWorld.Update", Update);
    }

    static MapNativeRenderContext Context(MapSnapshotRequest request) => new(request.Closure, request.Source,
        MapRenderExtentValidator.Validate(request.Closure, request.Source.RequiredAssetIds, new(new())).Proofs!,
        request.Policy.Render, request.Policy.Lighting, "windy");

    sealed class Host(MapNativeRenderContext context) : IMapNativeRenderHost
    {
        internal int Resolves;
        internal (string Path, string Style) LastInput;
        public MapNativeRenderContext Resolve(MapDocument document, string path, string style)
        {
            Assert.NotNull(document.ResolverIdentity);
            Resolves++; LastInput = (path, style);
            return context;
        }
    }
}
