using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("AllocSensitive")]
public sealed class NativeMapAllocationTests
{
    [Fact]
    public void WarmIdleUpdateDrawPick_AllocateZero() => MeasureView(false);

    [GpuFact]
    public void WarmGpuQueueUpdateDraw_AllocateZero() => MeasureView(true);

    static void MeasureView(bool gpu)
    {
        using var context = gpu ? GpuDeviceContext.CreateHeadless() : null;
        using var host = new NativeUploadTestScene(context?.GpuDevice);
        using var fixture = NativePresentationFixtures.Create();
        using var lodFixture = NativePresentationFixtures.Create(cluster: true);
        var realization = NativeOriginRealizationFixtures.Create(); using var originFixture = realization.Scene;
        var originScheduler = new ResidencyScheduler();
        using var originResidency = new MapRenderResidency(NativeOriginRealizationFixtures.State(realization), new MapScene3DSink(host.Scene), null, originScheduler);
        var scheduler = new ResidencyScheduler(); var lodScheduler = new ResidencyScheduler();
        using var residency = new MapRenderResidency(fixture.State(NativePresentationFixtures.Scope), new MapScene3DSink(host.Scene), null, scheduler);
        var lodState = lodFixture.State(NativePresentationFixtures.Scope);
        lodState = lodState with
        {
            Policy = new(NativeMapPlacementTests.Policy(true), lodState.Policy.AnimatedAssets,
            lodState.Policy.Foliage, lodState.Policy.Water, [], lodState.Policy.TerrainMaterials)
        };
        using var lod = new MapRenderResidency(lodState, new MapScene3DSink(host.Scene), null, lodScheduler);
        scheduler.RunAll(); lodScheduler.RunAll(); originScheduler.RunAll();
        using var view = new NativeMapView(host.Scene, residency); using var lodView = new NativeMapView(host.Scene, lod);
        using var originView = new NativeMapView(host.Scene, originResidency); Assert.Equal(0, originView.SettleForCapture(1024).Pending);
        var originFrame = new MapFrameState(realization.PickOrigin.Frame, realization.PickOrigin, MapRoofMode.AlwaysVisible, 12, 17, realization.PickOrigin);
        Assert.Equal(0, view.SettleForCapture(1024).Pending); Assert.Equal(0, lodView.SettleForCapture(1024).Pending);
        var modes = new[] { MapRoofMode.Auto, MapRoofMode.Auto, MapRoofMode.AlwaysHidden, MapRoofMode.AlwaysVisible };
        var focuses = new[] { 0f, 12f, 22f, 35f };
        var ray = new MapPresentationRay(new(WorldFrame.Origin, new(0, .5f, 0)), Vector3.UnitZ, 10);
        MapPresentationHit? hidden = null; int queued = 0;
        void Idle() { _ = view.Update(NativePresentationFixtures.Frame()); _ = lodView.Update(NativePresentationFixtures.Frame()); _ = originView.Update(originFrame); }
        void Update()
        {
            for (int i = 0; i < 4; i++)
            { _ = view.Update(NativePresentationFixtures.Frame(i % 2 == 0 ? 1 : 9, modes[i])); _ = lodView.Update(LodFrame(i)); _ = originView.Update(originFrame); }
        }
        MapFrameState LodFrame(int i) => NativePresentationFixtures.Frame() with { Focus = new(WorldFrame.Origin, new(focuses[i], 0, 0)) };
        void DrawPick()
        {
            for (int i = 0; i < 4; i++)
            {
                host.Scene.Begin(); var frame = NativePresentationFixtures.Frame(i % 2 == 0 ? 1 : 9, modes[i]);
                view.Draw(frame); lodView.Draw(LodFrame(i)); originView.Draw(originFrame);
                var hit = view.Pick(ray, MapPresentationChannel.Selection, frame);
                if (i == 0) hidden = hit;
                _ = view.Pick(ray, MapPresentationChannel.Camera, frame);
                _ = view.Pick(ray, MapPresentationChannel.Physical, frame);
                queued = host.Scene.QueuedInstancesForTests.Count;
            }
        }
        Idle(); Update(); DrawPick(); Idle(); Update(); DrawPick();
        long created = residency.Lifetime.Created + lod.Lifetime.Created;
        long uploads = residency.Lifetime.UploadBytes + lod.Lifetime.UploadBytes;
        long warmedIdleAllocatedBytes = AllocationMeasurement.Measure("native idle update", Idle);
        long warmedUpdateAllocatedBytes = AllocationMeasurement.Measure("native moving observer update", Update);
        long warmedRecordAndPickAllocatedBytes = AllocationMeasurement.Measure("native queue draw and hidden-hit pick", DrawPick);
        Assert.True(warmedIdleAllocatedBytes == 0 && warmedUpdateAllocatedBytes == 0 && warmedRecordAndPickAllocatedBytes == 0,
            $"idle={warmedIdleAllocatedBytes}, update={warmedUpdateAllocatedBytes}, record/pick={warmedRecordAndPickAllocatedBytes}");
        Assert.Equal(0L, warmedIdleAllocatedBytes);
        Assert.Equal(0L, warmedUpdateAllocatedBytes);
        Assert.Equal(0L, warmedRecordAndPickAllocatedBytes);
        Assert.Equal("object-behind", hidden!.Value.OwnerId); Assert.True(queued > 0);
        Assert.Equal(created, residency.Lifetime.Created + lod.Lifetime.Created);
        Assert.Equal(uploads, residency.Lifetime.UploadBytes + lod.Lifetime.UploadBytes);
        if (gpu) host.Capture();
    }

    [Fact]
    public void LifetimeCounters_ReturnToBaselineAfterRecenterAndDispose()
    {
        using var host = new NativeUploadTestScene(); using var fixture = ResidencyFixture.Foliage();
        var scheduler = new ResidencyScheduler();
        var state = fixture.FoliageState(); var residency = new MapRenderResidency(state, new MapScene3DSink(host.Scene), null, scheduler);
        var lifetime = residency.Lifetime; long baselineLiveHandles = lifetime.LiveHandles;
        scheduler.RunAll(); using var view = new NativeMapView(host.Scene, residency);
        Assert.Equal(0, view.SettleForCapture(1024).Pending); Assert.True(lifetime.Created > 0); Assert.True(lifetime.CpuBytes > 0);
        residency.SetWindows([ResidencyFixture.Window(2, 8) with { Channels = state.Windows[0].Channels }]);
        scheduler.RunAll(); Assert.Equal(0, view.SettleForCapture(1024).Pending);
        Assert.True(lifetime.GpuBytes > 0); Assert.True(lifetime.UploadBytes > 0);
        residency.SetWindows([]); scheduler.RunAll(); Assert.Equal(0, view.SettleForCapture(1024).Pending);
        view.Dispose();
        // The scene holds GPU retirement until its completion boundary, including after residency disposal.
        Assert.True(lifetime.LiveHandles > 0); host.Scene.Dispose();
        long disposedLiveHandles = lifetime.LiveHandles;
        long disposedChargedBytes = lifetime.CpuBytes + lifetime.GpuBytes + lifetime.QueuedBytes;
        Assert.Equal(baselineLiveHandles, disposedLiveHandles);
        Assert.Equal(lifetime.Created, lifetime.Disposed);
        Assert.Equal(0L, disposedChargedBytes); Assert.Equal(0, lifetime.WorkerCount);
    }
}
