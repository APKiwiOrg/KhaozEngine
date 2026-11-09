using System;
using System.IO;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapEdit;
using KhaozEngine.Tests.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

public sealed class NativeMapCaptureTests
{
    [Fact]
    public void CappedPreview_CannotClaimComplete()
    {
        var request = CaptureDependencyFixtures.Request(1) with { Requirement = MapCaptureRequirement.Preview };
        var result = MapCapturePreparation.Prepare(request);
        Assert.Equal(MapSceneStatus.Incomplete, result.Status); Assert.True(result.Witness.Capped);
        Assert.False(result.Witness.Scene.CompleteForScope); Assert.False(result.Witness.Scene.WholeWorldComplete);
        Assert.Single(result.Witness.AcquiredScopes); Assert.Contains("scope.capped", result.Findings);
        Assert.Equal(MapCaptureFocusSource.Explicit, result.FocusSource); Assert.Null(result.Image); Assert.Null(result.Backend);
    }
    [Fact]
    public void NativeCapture_MissingClosureNeverReturnsTerrainOnlyImage()
    {
        var request = CaptureDependencyFixtures.Request();
        var other = KhaozEngine.Tests.MapDoc.Scene.NativeSceneFixtures.Create("empty").Assets;
        var result = NativeMapSnapshot.Capture(request with { Closure = other });
        Assert.Equal(MapSceneStatus.Stale, result.Status); Assert.Null(result.Image); Assert.Null(result.Backend);
        Assert.Equal(MapCaptureFocusSource.Unresolved, result.FocusSource);
    }
    [Fact]
    public void FocusDefaults_ResolveOnceAndRefuseAnUnsupportedCentre()
    {
        var request = CaptureDependencyFixtures.Request() with { Focus = null };
        var view = MapCapturePreparation.Prepare(request);
        Assert.Equal(request.Framing.Eye, view.Echo.Focus); Assert.Equal(MapCaptureFocusSource.ViewEye, view.FocusSource);
        var top = MapCapturePreparation.Prepare(request with { Route = MapCaptureRoute.TopDown });
        Assert.Equal(MapCaptureFocusSource.TopDownSupport, top.FocusSource); Assert.Equal(new Vector3(0, 2, 0), top.Echo.Focus!.Value.Local);
        var b = request.Scope.DrawBounds;
        var outside = request with
        {
            Route = MapCaptureRoute.TopDown,
            Scope = request.Scope with
            {
                DrawBounds = b with { MinX = 40, MaxX = 48 },
                Surfaces = request.Scope.Surfaces with { LocalMin = new(40, -4), LocalMax = new(48, 4) }
            }
        };
        var refused = MapCapturePreparation.Prepare(outside);
        Assert.Equal(MapCaptureFocusSource.Unresolved, refused.FocusSource); Assert.Contains("FocusUnavailable", refused.Findings);
    }
    [Fact]
    public void InvalidRequest_AndAnalyticRequest_EnterNoHost()
    {
        string dir = Path.Combine(Path.GetTempPath(), "khaozengine-native-request-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            var (doc, _) = CaptureDependencyFixtures.Create(dir); string path = Path.Combine(dir, "world.map.json"); MapDocumentFile.Save(doc, path);
            var session = new MapEditSession(); session.Open(path);
            var host = new RefusingHost(); var service = new RenderService(session, host);
            Assert.Throws<ArgumentException>(() => service.RenderNative("{}")); Assert.Equal(0, host.Entries);
            foreach (string invalid in NativeRenderRequestTests.InvalidRequests()) Assert.Throws<ArgumentException>(() => service.RenderNative(invalid));
            Assert.Equal(0, host.Entries);
            Assert.Throws<ArgumentException>(() => service.NativeBlocks(null, MapCaptureRoute.View)); Assert.Equal(0, host.Entries);
            session.Create(Path.Combine(dir, "analytic.map.json"), "analytic", "Analytic", -32, -32, 32, 32);
            Assert.Throws<ArgumentException>(() => service.RenderNative(NativeRenderRequestTests.Json)); Assert.Equal(0, host.Entries);
        }
        finally { Directory.Delete(dir, true); }
    }
    [Fact]
    public void AnalyticCapture_KeepsReleasedRouting()
    {
        string dir = Path.Combine(Path.GetTempPath(), "khaozengine-analytic-render-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            var session = new MapEditSession(); session.Create(Path.Combine(dir, "world.map.json"), "analytic", "Analytic", -32, -32, 32, 32);
            var render = new RenderService(session);
            Assert.Null(render.NativeBlocks(null, MapCaptureRoute.View));
            Assert.Throws<ArgumentException>(() => render.NativeBlocks(NativeRenderRequestTests.Json, MapCaptureRoute.TopDown));
            Assert.Equal(new Vector3(2, 3, 4), RenderStreamPlan.ForView(new(2, 3, 4)).Focus);
        }
        finally { Directory.Delete(dir, true); }
    }
    sealed class RefusingHost : IMapNativeRenderHost
    {
        public int Entries;
        public MapNativeRenderContext Resolve(MapDocument document, string path, string style) { Entries++; throw new InvalidOperationException(); }
    }
}
