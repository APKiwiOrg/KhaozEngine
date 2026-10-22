using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("NativeDeviceLifecycle")]
public sealed class NativeMapSnapshotGpuTests
{
    [GpuFact]
    public void LiteralShadowRequest_RequiresBothOffCameraCastersAndDerivedPointKey()
    {
        // This is the identical Task 12 literal request, including Quality, windy, time 3.25 and wind seed 23.
        var request = CaptureDependencyFixtures.Request();
        var positive = NativeMapSnapshot.Capture(request, scene =>
        {
            Assert.Equal(TemporalUpscale.Quality, scene.Post.Temporal.Upscale);
            var lights = (System.Collections.IEnumerable)typeof(Scene3D).GetField("_lights", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(scene)!;
            var point = Assert.Single(lights.Cast<KhaozEngine.Render3D.Rendering.ModelRenderer.PointLightData>());
            Assert.Equal(3068690713473075190, point.LocalShadow.Key);
        });
        Assert.Equal(MapSceneStatus.CompleteForScope, positive.Status); Assert.NotNull(positive.Image);
        Assert.Equal("windy", positive.Echo.StyleId); Assert.Equal(4, positive.Echo.Policy.WarmupFrames);
        foreach (string owner in new[] { "sun-caster", "point-caster" })
        {
            var omitted = CaptureOmittingCaster(request, owner);
            NativeMapGoldenAssertions.FeatureVisible(positive.Image!.Value, omitted);
            Assert.Equal(0, NativeMapGoldenAssertions.Changed(omitted, omitted, NativeMapGoldenAssertions.FeatureRoi));
        }
    }

    [GpuFact]
    public void QualityWindyPreview_KeepsItsCappedReadinessAndExactEcho()
    {
        // The actual tool wire counterpart lives in MapEditor.Tests.NativeMapCaptureWireTests.
        // It is required in that project's existing backend shard, with no capability skip.
        var request = CaptureDependencyFixtures.Request(1) with { Requirement = MapCaptureRequirement.Preview };
        var result = NativeMapSnapshot.Capture(request);
        Assert.NotNull(result.Image); Assert.Equal(MapSceneStatus.Incomplete, result.Status);
        Assert.True(result.Witness.Capped); Assert.False(result.Witness.Scene.CompleteForScope); Assert.False(result.Witness.Scene.WholeWorldComplete);
        Assert.Equal(0, result.Readiness.Pending); Assert.Equal(1, result.Readiness.Ready);
        Assert.Equal(MapCaptureAa.Quality, result.Echo.Policy.Aa); Assert.Equal("windy", result.Echo.StyleId);
        Assert.Equal(3.25, result.Echo.TimeSeconds); Assert.Equal(23, result.Echo.WindSeed); Assert.Equal(4, result.Echo.Policy.WarmupFrames);
        Assert.Equal(new Vector3(2, 1, 1), result.Echo.Observer.Local); Assert.Equal(new Vector3(5, 0, 0), result.Echo.Focus!.Value.Local);
        Assert.Contains("scope.capped", result.Findings);
    }

    [GpuFact]
    public void TexturedPaint_ActualSinkReusesItsLeaseAndRefusesUnadmittedReplacement()
    {
        var closure = NativeRenderAssetFixtures.Create(); var oversized = NativeRenderAssetFixtures.Create("resident");
        using var native = NativeSceneFixtures.Create("terrain-paint");
        native.Document.NativeAssets = closure.Roots.ToList();
        using var fixture = new ResidencyFixture(native.Document, closure);
        using var changedNative = NativeSceneFixtures.Create("terrain-paint"); changedNative.Document.NativeAssets = oversized.Roots.ToList();
        using var changed = new ResidencyFixture(changedNative.Document, oversized);
        var scope = ResidencyFixture.Window() with { Channels = MapSceneChannels.Terrain };
        var policy = ResidencyFixture.Policy(materials: ["paint"], terrain: new Dictionary<ushort, string> { [10] = "paint", [11] = "paint", [12] = "paint" });
        var state = fixture.State(scope) with { Policy = policy };
        using var gpu = GpuDeviceContext.CreateHeadless(); using var host = new NativeUploadTestScene(gpu.GpuDevice);
        host.Scene.Camera.Frame(new(.5f, 1.25f, -.5f), new Vector3(2));
        var scheduler = new ResidencyScheduler();
        using var residency = new MapRenderResidency(state, new MapScene3DSink(host.Scene), new(MaxResidentCpuBytes: 2_000_000, MaxPlanBytes: 500_000), scheduler);
        scheduler.RunAll(); using var view = new NativeMapView(host.Scene, residency); Assert.Equal(0, view.SettleForCapture(1024).Pending);
        var plan = Assert.Single(residency.AcceptedPlans); var lease = plan.Input;
        Assert.Contains(plan.Inventory.Terrain.Partitions, p => p.PaintId == "paint-a"); Assert.Equal(220, lease.Materials["paint"].Maps.Albedo!.Value.Rgba[0]);
        var frame = NativePresentationFixtures.Frame(mode: MapRoofMode.AlwaysVisible);
        host.Scene.Begin(); view.Draw(frame); byte[] first = host.Capture();
        int decodes = KhaozEngine.Render2D.ImageDecodeAdmission.PixelDecodeEntries;
        long uploaded = residency.Lifetime.UploadBytes, created = residency.Lifetime.Created;
        var accepted = residency.CurrentTransition.AcceptedWitness;
        var proposed = changed.State(scope) with { Policy = policy };
        view.Replace(proposed); scheduler.RunAll(); view.SettleForCapture(1024);
        Assert.Equal(MapSceneStatus.CapacityExceeded, residency.CurrentTransition.Status);
        Assert.Equal(accepted, residency.CurrentTransition.AcceptedWitness); Assert.Same(plan, Assert.Single(residency.AcceptedPlans));
        Assert.Same(lease, plan.Input); Assert.Equal(decodes, KhaozEngine.Render2D.ImageDecodeAdmission.PixelDecodeEntries);
        Assert.Equal(uploaded, residency.Lifetime.UploadBytes); Assert.Equal(created, residency.Lifetime.Created);
        host.Scene.Begin(); view.Draw(frame); byte[] second = host.Capture(); Assert.Equal(first, second);
        Assert.Equal(uploaded, residency.Lifetime.UploadBytes);
    }

    [GpuFact]
    public void TwoFocuses_ReusePublishedFoliageAndChangeCutoffAndLodPixels()
    {
        using var scene = new NativeMapGoldenScene(); scene.CaptureFocusPair();
    }

    [GpuFact]
    public void ClosedWaterDomain_ActualSinkQueuesNoWater()
    {
        using var scene = new NativeMapGoldenScene(); scene.Capture("water-closed");
        var evidence = scene.LastEvidence;
        Assert.Equal(0, evidence.Ready.Pending); Assert.True(evidence.Ready.Transition.AcceptedWitness!.CompleteForScope);
        Assert.DoesNotContain(evidence.Submitted, d => d.Water.HasValue);
        Assert.DoesNotContain(evidence.Inventory.Resources, r => r.Payload is MapWaterResource);
    }

    [GpuFact]
    public void UnresolvedWaterDomain_RefusesBeforeAnySceneSubmission()
    {
        using var setup = NativeMapGoldenSetup.Create("water-missing");
        var request = new MapSnapshotRequest(setup.State.Closure, setup.State.Source, setup.State.Windows[0], setup.Framing,
            setup.Frame.Observer, null, setup.Frame.RoofMode, 12, 17, "native",
            new(setup.State.Policy, CaptureDependencyFixtures.Lighting(), MapCaptureAa.Native), MapCaptureRequirement.CompleteScope,
            Focus: setup.Frame.Focus);
        bool submitted = false;
        var result = NativeMapSnapshot.Capture(request, _ => submitted = true);
        Assert.NotEqual(MapSceneStatus.CompleteForScope, result.Status); Assert.False(submitted);
        Assert.Null(result.Image); Assert.Null(result.Backend);
    }

    static Render3DCapture CaptureOmittingCaster(MapSnapshotRequest request, string omitted)
    {
        var prepared = MapCapturePreparation.Prepare(request); NativeMapView? view = null;
        MapRenderResidency? residency = null; MapScene3DSink? sink = null;
        Render3DCapture result = default;
        try
        {
            Render3DSnapshot.CaptureSequence(640, 480, scene =>
            {
                NativeMapSnapshot.Configure(scene, request);
                var state = new MapRenderSourceState(request.Source, request.Closure, request.Source.RenderExtents!, request.Policy.Render, prepared.Witness.AcquiredScopes);
                sink = new MapScene3DSink(scene) { Restriction = new(request.Scope.DrawBounds) };
                residency = new(state, sink); view = new(scene, residency);
                Assert.Equal(0, view.SettleForCapture(1024).Pending);
            }, (scene, _) =>
            {
                sink!.BeginFrame(); scene.EffectTimeSeconds = (float)request.TimeSeconds;
                var frame = new MapFrameState(request.Scope.Surfaces.Frame, request.Observer, request.RoofMode,
                    request.TimeSeconds, request.WindSeed, request.Focus!.Value);
                residency!.Record(frame, new CasterOmission(sink, omitted));
            }, request.Policy.WarmupFrames + 1, (_, image) => result = image, request.Policy.WarmupFrames, request.Policy.Lighting.Shadows);
        }
        finally { view?.Dispose(); }
        return result;
    }
    sealed class CasterOmission(IMapDrawSink sink, string owner) : IMapDrawSink
    {
        public void Register(MapPreparedResource resource) => sink.Register(resource);
        public void Write(in MapDrawRecord draw) { if (draw.Resource.Key.OwnerId != owner) sink.Write(draw); }
    }
}
