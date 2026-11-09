using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.Primitives;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed partial class NativeMapReadinessTests
{
    [GpuFact]
    public void NativeSink_FocusSequenceReusesItsPublishedBatch()
    {
        using var fixture = ResidencyFixture.Foliage(); var state = fixture.FoliageState();
        var options = new KhaozEngine.Terrain.GroundCoverRenderOptions
        {
            UseGpuBatches = true,
            FadeMode = KhaozEngine.Terrain.GroundCoverFadeMode.HeightScale,
            DrawRadius = 4,
            DensityRadius = 2,
            FadeBandWidth = 1,
            InstanceFadeBandWidth = .5f
        };
        state = state with
        {
            Windows = [ResidencyFixture.Window(-4, 8) with { Channels = MapSceneChannels.Foliage }],
            Policy = ResidencyFixture.Policy(foliage: new(MapFrozenFoliageOptions.Capture(options), state.Policy.Foliage.Extents))
        };
        NativeMapView? view = null; MapRenderResidency? residency = null; Scene3D? saved = null;
        long uploadBytes = 0, created = 0; MapRenderPlan[] plans = []; byte[]? first = null;
        try
        {
            Render3DSnapshot.CaptureSequence(160, 120, scene =>
            {
                saved = scene; scene.Post.Starfield = false;
                scene.Camera.Azimuth = 0; scene.Camera.Elevation = 0; scene.Camera.Distance = 10;
                scene.Camera.Target = new(3.5f, 1.5f, 2); scene.Camera.OrthoSize = 7.5f; scene.Camera.Zoom = 1;
                scene.Camera.AspectRatio = 160f / 120;
                residency = new(state, new MapScene3DSink(scene)); view = new(scene, residency);
                Assert.Equal(0, view.SettleForCapture(1024).Pending);
                uploadBytes = residency.Lifetime.UploadBytes; created = residency.Lifetime.Created; plans = residency.AcceptedPlans.ToArray();
            }, (scene, index) =>
            {
                var point = new MapFramePoint(WorldFrame.Origin, Vector3.Zero);
                view!.Draw(new(WorldFrame.Origin, point, MapRoofMode.AlwaysVisible, 0, 0,
                    new(WorldFrame.Origin, new(index == 0 ? 0 : 5, 0, 0))));
                Assert.All(scene.FoliageUniformsForTests.ToArray(), u => Assert.Equal(index == 0 ? 0 : 5, u.FocusRadius.X));
                Assert.All(scene.FoliageUniformsForTests.ToArray(), u => Assert.Equal(4, u.FocusRadius.W));
            }, 2, (index, capture) =>
            {
                Assert.Equal(uploadBytes, residency!.Lifetime.UploadBytes); Assert.Equal(created, residency.Lifetime.Created);
                Assert.Equal(plans, residency.AcceptedPlans); Assert.Equal(0, saved!.LastFoliageStats.InstanceUploadBytes);
                if (index == 0) first = capture.Rgba;
                else
                {
                    int changed = 0;
                    for (int i = 0; i < capture.Rgba.Length; i += 4)
                        if (Math.Abs(first![i] - capture.Rgba[i]) >= 8 || Math.Abs(first[i + 1] - capture.Rgba[i + 1]) >= 8) changed++;
                    Assert.True(changed >= 64, "Moving only the submitted focus must change at least 64 foliage pixels.");
                }
            });
        }
        finally { view?.Dispose(); }
    }
    // Required on all three backend gates. A local GpuFact skip supplies no rendering evidence.
    [GpuFact]
    public void HiddenRoof_RealSinkSubmitsShadowWithoutAColourDraw()
    {
        using var fixture = NativePresentationFixtures.Create();
        NativeMapView? view = null; MapRenderResidency? residency = null;
        int roofCount = 0, queuedCount = 0; Scene3D? savedScene = null;
        try
        {
            Render3DSnapshot.CaptureSequence(160, 120, scene =>
            {
                savedScene = scene; scene.FrustumCulling = false;
                scene.Camera.Frame(new(4, 1, 3), new Vector3(16));
                residency = new(fixture.State(NativePresentationFixtures.Scope), new MapScene3DSink(scene));
                view = new(scene, residency);
                Assert.Equal(0, view.SettleForCapture(1024).Pending);
            }, (scene, _) =>
            {
                var frame = NativePresentationFixtures.Frame(mode: MapRoofMode.AlwaysHidden);
                var recorder = new MapDrawRecorder(); residency!.Record(frame, recorder);
                var roofs = recorder.Snapshot().Draws.Where(d => d.Resource.Key.Kind == MapRenderKind.Roof).ToArray();
                roofCount = roofs.Length; Assert.True(roofCount > 0);
                Assert.All(roofs, d => { Assert.False(d.Channels.HasFlag(MapDrawChannels.Colour)); Assert.True(d.Channels.HasFlag(MapDrawChannels.Shadow)); });
                var meshes = roofs.Select(d => { Assert.True(residency.TryGetHandle(d.Resource, out var h)); return new SceneResourceAccess(scene).MeshOf(new(h.Value)); }).ToHashSet();
                view!.Draw(frame);
                var queued = scene.QueuedInstancesForTests;
                var hidden = queued.Where(q => meshes.Contains(q.Mesh)).ToArray();
                Assert.Equal(roofCount, hidden.Length); Assert.All(hidden, q => { Assert.True(q.ShadowOnly); Assert.True(q.CastsShadows); });
                queuedCount = queued.Count;
            }, 1, (_, _) =>
            {
                // Counters are populated by the actual colour and shadow passes, after recording and GPU readback.
                var scene = GetScene();
                Assert.Equal(roofCount, scene.ShadowOnlyInstances);
                Assert.Equal(queuedCount - roofCount, scene.DrawnInstances);
                Assert.True(scene.LastShadowPassDiagnostics.RigidDrawCalls > 0);
            }, shadows: new() { Mode = ShadowMode.ShadowMap, ShadowMapResolution = 1024, ShadowCascadeCount = 2 });
        }
        finally { view?.Dispose(); }
        Scene3D GetScene() => savedScene ?? throw new InvalidOperationException();
    }
}
