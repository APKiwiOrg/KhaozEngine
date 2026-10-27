using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeUploadSettlementTests
{
    [Fact]
    public void ReleasedSourceColumns_LeaveRendererSnapshotsChargedUntilRetirement()
    {
        using var device = new ReadbackRingFakeDevice();
        using var host = new NativeUploadTestScene(device);
        var probe = DetachedColumnUpload(host.Scene);
        using var upload = probe.Upload;
        upload.Advance(long.MaxValue);
        device.SubmittedFences[^1].AsManual().Signal();
        Assert.True(upload.Ready);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        Assert.All(probe.Arrays, reference => Assert.False(reference.IsAlive));
        Assert.Equal(RetainedCpu(2), probe.Admission.CpuBytes);
        var handle = upload.Publish();
        host.Scene.ReleaseResource(handle); upload.Dispose(); host.Scene.Begin();
        Assert.Equal(RetainedCpu(2), probe.Admission.CpuBytes);
        foreach (var fence in device.SubmittedFences) fence.AsManual().Signal();
        host.Scene.Begin(); probe.Reservation.Released();
        Assert.Equal(0, probe.Admission.CpuBytes);
        Assert.Equal(0, probe.Admission.GpuBytes);
        GC.KeepAlive(probe);
    }
    sealed record ColumnUploadProbe(MapRenderAdmission Admission, MapUploadReservation Reservation, SceneResourceUpload Upload, WeakReference[] Arrays);
    [MethodImpl(MethodImplOptions.NoInlining)]
    static ColumnUploadProbe DetachedColumnUpload(Scene3D scene)
    {
        var original = Data(scene, 2).Foliage!;
        var matrices = original.WorldTransforms.ToArray();
        var positions = matrices.Select(m => m.Translation).ToArray();
        var ranks = original.Ranks.ToArray(); var indices = original.ModelIndices.ToArray();
        var ids = original.Models.Select(m => m.ModelId).ToArray();
        var columns = GroundCoverInstanceColumns.TakeOwnership(matrices, positions, ranks, indices, ids);
        var detached = new SceneFoliageSource(original.Anchor, columns.Transforms, columns.Ranks, columns.ModelIndices, original.Models, original.TransformSpace);
        var data = new SceneUploadData(null, null, detached, default);
        var admission = new MapRenderAdmission(new());
        var reservation = admission.TryReserve(scene.MeasureResourceUpload(data))!;
        var upload = scene.PrepareResourceUpload(data, reservation);
        return new(admission, reservation, upload, [new(matrices), new(positions), new(ranks), new(indices), new(ids)]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Completion_ChargesOnlyTheIndependentSurvivorInventory(int kind)
    {
        using var host = new NativeUploadTestScene();
        var data = Data(host.Scene, kind);
        var admission = new MapRenderAdmission(new());
        var demand = host.Scene.MeasureResourceUpload(data);
        using var reservation = admission.TryReserve(demand)!;
        using var upload = host.Scene.PrepareResourceUpload(data, reservation);
        Assert.Equal(demand.PeakCpuBytes, admission.CpuBytes);
        upload.Advance(long.MaxValue);
        Assert.True(upload.Ready);
        Assert.Equal(RetainedCpu(kind), admission.CpuBytes);
        var handle = upload.Publish();
        Assert.Equal(RetainedCpu(kind), admission.CpuBytes);
        Assert.Null(typeof(SceneResourceUpload).GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(upload));
        var transfers = typeof(SceneResourceUpload).GetField("_transfers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(upload)!;
        Assert.Equal(0, transfers.GetType().GetProperty("Capacity")!.GetValue(transfers));
        Assert.Null(typeof(SceneResourceUpload).GetField("_barrier", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(upload));
        Assert.Null(typeof(SceneResourceUpload).GetField("_completion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(upload));
        Assert.Equal(demand.OwnedGpuBytes, admission.GpuBytes);
        host.Scene.ReleaseResource(handle);
        host.Scene.Dispose();
        Assert.Equal(0, admission.CpuBytes);
        Assert.Equal(0, admission.GpuBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void GpuStaging_RemainsChargedUntilTheUploadFenceSignals(int kind)
    {
        using var device = new ReadbackRingFakeDevice();
        using var host = new NativeUploadTestScene(device);
        var data = Data(host.Scene, kind);
        var demand = host.Scene.MeasureResourceUpload(data);
        var admission = new MapRenderAdmission(new());
        using var reservation = admission.TryReserve(demand)!;
        using var upload = host.Scene.PrepareResourceUpload(data, reservation);
        upload.Advance(long.MaxValue);
        Assert.False(upload.Ready);
        Assert.Equal(demand.OwnedGpuBytes + demand.PeakStagingGpuBytes, admission.GpuBytes);
        Assert.Throws<InvalidOperationException>(() => upload.Publish());
        device.SubmittedFences[^1].AsManual().Signal();
        Assert.True(upload.Ready);
        Assert.Equal(demand.OwnedGpuBytes, admission.GpuBytes);
        Assert.True(device.SubmittedFences[^1].AsManual().Disposed);
        Assert.True(upload.Ready);
        Assert.Equal(RetainedCpu(kind), admission.CpuBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Retirement_AfterSettlementReleasesExactlyTheRetainedRemainder(int kind)
    {
        using var device = new ReadbackRingFakeDevice();
        using var host = new NativeUploadTestScene(device);
        var data = Data(host.Scene, kind);
        var admission = new MapRenderAdmission(new());
        using var reservation = admission.TryReserve(host.Scene.MeasureResourceUpload(data))!;
        using var upload = host.Scene.PrepareResourceUpload(data, reservation);
        upload.Advance(long.MaxValue);
        device.SubmittedFences[^1].AsManual().Signal();
        var handle = upload.Publish();
        host.Scene.ReleaseResource(handle);
        upload.Dispose();
        host.Scene.Begin();
        Assert.Equal(RetainedCpu(kind), admission.CpuBytes);
        Assert.Equal(upload.Demand.OwnedGpuBytes, admission.GpuBytes);
        Assert.Equal(1, admission.Lifetime.LiveHandles);
        foreach (var fence in device.SubmittedFences) fence.AsManual().Signal();
        host.Scene.Begin();
        reservation.Released();
        Assert.Equal(0, admission.CpuBytes);
        Assert.Equal(0, admission.GpuBytes);
        Assert.Equal(0, admission.Lifetime.LiveHandles);
        Assert.Equal(1, admission.Lifetime.Disposed);
        var owned = typeof(SceneResourceUpload).GetField("_owned", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(upload)!;
        Assert.Equal(0, owned.GetType().GetProperty("Capacity")!.GetValue(owned));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Cancellation_BeforePublicationReturnsTheFullReservationOnce(int kind)
    {
        using var device = new ReadbackRingFakeDevice();
        using var host = new NativeUploadTestScene(device);
        var data = Data(host.Scene, kind);
        var admission = new MapRenderAdmission(new());
        var demand = host.Scene.MeasureResourceUpload(data);
        using var accepted = host.Scene.PrepareResourceUpload(data, admission.TryReserve(demand)!);
        accepted.Advance(long.MaxValue);
        device.SubmittedFences[^1].AsManual().Signal();
        var handle = accepted.Publish();
        using var reservation = admission.TryReserve(demand)!;
        var cancelled = host.Scene.PrepareResourceUpload(data, reservation);
        cancelled.Advance(4);
        cancelled.Dispose(); cancelled.Dispose();
        host.Scene.Begin();
        Assert.Equal(RetainedCpu(kind) + demand.PeakCpuBytes, admission.CpuBytes);
        Assert.Equal(demand.OwnedGpuBytes * 2 + demand.PeakStagingGpuBytes, admission.GpuBytes);
        foreach (var fence in device.SubmittedFences) fence.AsManual().Signal();
        host.Scene.Begin();
        reservation.Released();
        Assert.Equal(RetainedCpu(kind), admission.CpuBytes);
        Assert.Equal(demand.OwnedGpuBytes, admission.GpuBytes);
        Assert.Equal(1, admission.Lifetime.LiveHandles);
        Assert.Equal(1, admission.Lifetime.Disposed);
        host.Scene.ReleaseResource(handle);
        host.Scene.Dispose();
        Assert.Equal(0, admission.CpuBytes);
        Assert.Equal(0, admission.GpuBytes);
        Assert.Equal(2, admission.Lifetime.Disposed);
    }

    static SceneUploadData Data(Scene3D scene, int kind)
    {
        if (kind != 2) return new(NativeResourceUploadTests.Triangle(),
            kind == 1 ? new(new(new byte[64], 4, 4), null, null, .5f) : null, null, default);
        var part = scene.LoadMesh(NativeResourceUploadTests.Triangle());
        var foliage = new SceneFoliageSource(default, new[] { Matrix4x4.Identity }, new[] { .2f }, new[] { 0 },
            [new("one", [part], default, true)]);
        return new(null, null, foliage, default);
    }

    // Independent one-row inventory. Meshes retain no vertex/index/image inputs. The foliage product owns
    // nine arrays, including both patch representations and the point-shadow local-transform stream.
    // Its published batch also embeds two 288-byte temporal uniform snapshots, beside 512 bytes of metadata.
    static long RetainedCpu(int kind)
    {
        if (kind == 0) return 2072;
        if (kind == 1) return 3128;
        return 2328 + 2 * 288 + 9 * 24 +
            8 + 24 + 40 + 96 + Aligned(Unsafe.SizeOf<FoliageInstance>()) +
            Aligned(Unsafe.SizeOf<FoliagePatch>()) + 8 + 96 + 64;
    }
    static long Aligned(int bytes) => (bytes + 7L) / 8 * 8;
}

internal static class SettlementFenceExtensions
{
    internal static ManualFence AsManual(this KhaozEngine.Gpu.IGpuFence fence) => Assert.IsType<ManualFence>(fence);
}
