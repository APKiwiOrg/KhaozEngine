using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("NativeDeviceLifecycle")]
public sealed class NativeUploadBatchResidencyTests
{
    [Fact]
    public void ManyResources_BoundPeakAndPreserveAtomicDrawsAndDigests()
    {
        using var fixture = Many();
        var state = fixture.State(ResidencyFixture.Window(-4, 20));
        var scheduler = new ResidencyScheduler(); var device = new UploadBatchDevice();
        using var residency = new MapRenderResidency(state, device, NativeUploadBatchAdmissionTests.Budgets(), scheduler);
        long peak = 0;
        device.Began = () =>
        {
            peak = Math.Max(peak, device.UploadCpuBytes);
            Assert.Empty(ResidencyFixture.Draw(residency).Draws);
        };
        ResidencyFixture.Complete(residency, scheduler);
        var bounded = ResidencyFixture.Draw(residency);
        Assert.True(device.Begins > 4);
        Assert.True(peak <= 56 * 20 + 100);
        using var control = new MapRenderResidency(state, new UploadBatchDevice(), null, scheduler);
        ResidencyFixture.Complete(control, scheduler);
        var expected = ResidencyFixture.Draw(control);
        Assert.Equal(expected.Draws, bounded.Draws);
        Assert.Equal(expected.Resources.Select(r => r.PayloadDigest), bounded.Resources.Select(r => r.PayloadDigest));
        Assert.Equal(control.CurrentTransition.AcceptedWitness!.PolicyDigest, residency.CurrentTransition.AcceptedWitness!.PolicyDigest);
    }

    [Theory]
    [InlineData(false, "MaxInFlightUploadCpuBytes")]
    [InlineData(true, "MaxInFlightUploadStagingGpuBytes")]
    public void OversizedResource_RefusesBeforeAnyUpload(bool staging, string limit)
    {
        using var fixture = Many();
        var scheduler = new ResidencyScheduler(); var device = new UploadBatchDevice();
        var budgets = NativeUploadBatchAdmissionTests.Budgets(staging ? 100 : 99, staging ? 9 : 10);
        using var residency = new MapRenderResidency(fixture.State(ResidencyFixture.Window(-4, 20)), device, budgets, scheduler);
        scheduler.RunAll();
        var result = residency.Update();
        Assert.Equal(MapSceneStatus.CapacityExceeded, result.Transition.Status);
        Assert.Equal(limit, result.Transition.Capacity!.LimitName);
        Assert.Equal(0, device.Begins);
    }

    [Fact]
    public void RetainedWindowThatCannotFit_RefusesBeforeTheFirstUpload()
    {
        using var fixture = Many();
        var scheduler = new ResidencyScheduler(); var device = new UploadBatchDevice();
        // Each of eight placements has six LOD mesh parts and one light. One staging batch needs 10 bytes.
        var budgets = NativeUploadBatchAdmissionTests.Budgets() with { MaxResidentGpuBytes = 2249 };
        using var residency = new MapRenderResidency(fixture.State(ResidencyFixture.Window(-4, 20)), device, budgets, scheduler);
        scheduler.RunAll();
        var result = residency.Update();
        Assert.Equal(MapSceneStatus.CapacityExceeded, result.Transition.Status);
        Assert.Equal(new MapSceneCapacity("MaxResidentGpuBytes", 2250, 2249), result.Transition.Capacity);
        Assert.Equal(0, device.Begins);
    }

    [Fact]
    public void RetainedCpuThatCannotFit_RefusesBeforeTheFirstUpload()
    {
        using var fixture = Many();
        var scheduler = new ResidencyScheduler();
        var device = new UploadBatchDevice { PeakCpu = 10_000_100, RetainedCpu = 10_000_000 };
        var budgets = NativeUploadBatchAdmissionTests.Budgets(10_000_100) with { MaxResidentCpuBytes = 100_000_000 };
        using var residency = new MapRenderResidency(fixture.State(ResidencyFixture.Window(-4, 20)), device, budgets, scheduler);
        scheduler.RunAll();
        var result = residency.Update();
        Assert.Equal(MapSceneStatus.CapacityExceeded, result.Transition.Status);
        Assert.Equal("MaxResidentCpuBytes", result.Transition.Capacity!.LimitName);
        Assert.True(result.Transition.Capacity.Requested >= 56 * 10_000_000L);
        Assert.Equal(100_000_000, result.Transition.Capacity.Allowance);
        Assert.Equal(0, device.Begins);
    }

    [Fact]
    public void Cancellation_ReleasesCompletedAndInFlightOwnersExactlyOnce()
    {
        using var fixture = Many();
        var scheduler = new ResidencyScheduler(); var device = new UploadBatchDevice { CompleteFirstOnly = true };
        using var residency = new MapRenderResidency(fixture.State(ResidencyFixture.Window(-4, 20)), device,
            NativeUploadBatchAdmissionTests.Budgets(), scheduler);
        scheduler.RunAll(); residency.Update(); residency.Update();
        Assert.Equal(2, device.Begins);
        Assert.Empty(ResidencyFixture.Draw(residency).Draws);
        residency.SetWindows([]);
        ResidencyFixture.Complete(residency, scheduler);
        Assert.Equal(2, device.Cancellations);
        Assert.Equal(0, residency.Lifetime.LiveHandles);
        Assert.Equal(0, residency.Lifetime.GpuBytes);
        long source = residency.Lifetime.CpuBytes;
        residency.Dispose(); residency.Dispose();
        Assert.True(source > 0);
        Assert.Equal(0, residency.Lifetime.CpuBytes);
    }

    [Fact]
    public void SecondWindow_PeakIncludesOldAndNewRetainedOwnersAndOneBatch()
    {
        using var fixture = Many();
        var a = ResidencyFixture.Window(-4, 7); var b = ResidencyFixture.Window(7, 20);
        var scheduler = new ResidencyScheduler(); var device = new UploadBatchDevice();
        using var residency = new MapRenderResidency(fixture.State(a), device, NativeUploadBatchAdmissionTests.Budgets(), scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        int old = device.Begins;
        long peak = 0;
        device.Began = () =>
        {
            peak = Math.Max(peak, device.UploadCpuBytes);
        };
        residency.SetWindows([a, b]);
        ResidencyFixture.Complete(residency, scheduler);
        Assert.True(device.Begins > old);
        Assert.True(peak <= 56 * 20 + 100);
        Assert.Equal(8, ResidencyFixture.Draw(residency).Draws.Where(d => d.Rigid.HasValue).Select(d => d.Resource.Key.OwnerId).Distinct().Count());
    }

    static ResidencyFixture Many()
    {
        var fixture = ResidencyFixture.Placement();
        fixture.Document.Placements = Enumerable.Range(0, 8).Select(i => new MapPlacement
        { Id = "invented-" + i, AssetId = "prop-a", Kind = "cosmetic", NumericId = i + 1, X = i * 2, Y = 0 }).ToList();
        return fixture;
    }
}

internal sealed class UploadBatchDevice : IMapRenderDevice
{
    readonly Dictionary<MapRenderHandle, ISceneUploadReservation> _live = new();
    readonly List<ISceneUploadReservation> _reservations = [];
    // Observe actual charged leases only. The expected peak is pinned to the fixture's 56 resources.
    internal long UploadCpuBytes => _reservations.Sum(r => (long)typeof(MapUploadReservation)
        .GetField("_cpu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(r)!);
    long _next;
    internal int Begins, Cancellations;
    internal long PeakCpu = 100, RetainedCpu = 20;
    internal bool CompleteFirstOnly;
    internal Action? Began;
    public SceneUploadDemand MeasureUpload(MapPreparedResource resource)
    {
        return new(resource.PayloadDigest, 40, 10, PeakCpu, 40, 4, RetainedCpu);
    }
    public IMapRenderUpload BeginUpload(MapPreparedResource resource, ISceneUploadReservation reservation)
    {
        reservation.Claim(MeasureUpload(resource));
        _reservations.Add(reservation); Begins++; Began?.Invoke();
        return new Upload(this, reservation, !CompleteFirstOnly || Begins == 1);
    }
    public void Release(MapRenderHandle handle)
    { var lease = _live[handle]; _live.Remove(handle); lease.Retiring(); lease.Released(); }
    sealed class Upload(UploadBatchDevice owner, ISceneUploadReservation lease, bool completes) : IMapRenderUpload
    {
        bool _ready, _published, _disposed;
        public bool Ready => _ready;
        public long Advance(long transferBytes)
        {
            if (_ready || !completes || transferBytes < 4) return 0;
            lease.TransferCompleted(); lease.InputsReleased(owner.RetainedCpu); _ready = true;
            return 4;
        }
        public MapRenderHandle Publish()
        {
            Assert.True(_ready); Assert.False(_published); Assert.False(_disposed);
            var handle = new MapRenderHandle(++owner._next);
            lease.Published(new(handle.Value)); _published = true; owner._live.Add(handle, lease); return handle;
        }
        public void Dispose()
        {
            if (_published || _disposed) return;
            _disposed = true; owner.Cancellations++; lease.Retiring(); lease.Released();
        }
    }
}
