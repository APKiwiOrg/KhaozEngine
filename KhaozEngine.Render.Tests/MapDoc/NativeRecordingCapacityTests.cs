using System.Linq;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("NativeDeviceLifecycle")]
public sealed class NativeRecordingCapacityTests
{
    [Fact]
    public void NativeLoadThenEmptyPublication_ReturnsToSourceOnlyAfterDeviceRetirement()
    {
        using var device = new ReadbackRingFakeDevice();
        using var host = new NativeUploadTestScene(device);
        using var fixture = ResidencyFixture.Placement();
        var state = fixture.State(ResidencyFixture.Window());
        var scheduler = new ResidencyScheduler();
        using var residency = new MapRenderResidency(state, new MapScene3DSink(host.Scene),
            new(MaxAppliesPerUpdate: 16, MaxDisposalsPerUpdate: 16), scheduler);
        scheduler.RunAll(); residency.Update();
        foreach (var fence in device.SubmittedFences) fence.AsManual().Signal();
        residency.Update();
        Assert.Equal(MapSceneStatus.CompleteForScope, residency.Update().Transition.Status);
        Assert.NotEmpty(ResidencyFixture.Draw(residency).Resources);
        residency.SetWindows([]);
        ResidencyFixture.Complete(residency, scheduler);
        host.Scene.Begin();
        Assert.True(residency.Lifetime.LiveHandles > 0);
        foreach (var fence in device.SubmittedFences) fence.AsManual().Signal();
        host.Scene.Begin(); residency.Update();
        long sourceOnly = state.Source.Data.CaptureWork.RetainedBytes + state.Extents.MetadataBytes +
            state.Closure.CapturedResources.Sum(r => (long)r.ByteLength);
        Assert.Equal(sourceOnly, residency.Lifetime.CpuBytes);
        Assert.Equal(0, residency.Lifetime.GpuBytes);
        Assert.Equal(0, residency.Lifetime.LiveHandles);
        Assert.Equal(residency.Lifetime.Created, residency.Lifetime.Disposed);
    }

    [Fact]
    public void EmptyPublication_ReplacesRecordingStorageAndReturnsToSourceOnlyCpu()
    {
        using var fixture = ResidencyFixture.Placement();
        var state = fixture.State(ResidencyFixture.Window());
        var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice();
        using var residency = new MapRenderResidency(state, device, new(MaxDisposalsPerUpdate: 16), scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        Assert.NotEmpty(ResidencyFixture.Draw(residency).Resources);
        var before = Containers(residency);
        residency.SetWindows([]);
        ResidencyFixture.Complete(residency, scheduler);
        long sourceOnly = state.Source.Data.CaptureWork.RetainedBytes + state.Extents.MetadataBytes +
            state.Closure.CapturedResources.Sum(r => (long)r.ByteLength);
        Assert.Equal(sourceOnly, residency.Lifetime.CpuBytes);
        Assert.Equal(0, residency.Lifetime.GpuBytes);
        Assert.Equal(0, residency.Lifetime.LiveHandles);
        Assert.Empty(device.Live);
        var after = Containers(residency);
        for (int i = 0; i < before.Length; i++)
        {
            Assert.NotSame(before[i].Identity, after[i].Identity);
            Assert.Equal(0, after[i].Capacity);
        }
        Assert.Empty(ResidencyFixture.Draw(residency).Resources);
        residency.Dispose();
        Assert.Equal(0, residency.Lifetime.CpuBytes);
    }

    [Fact]
    public void PublicationBelowHalfCapacity_ReplacesAllThreeContainersAndSettlesTheirCharge()
    {
        using var fixture = ResidencyFixture.Placement();
        var one = ResidencyFixture.Window();
        var scheduler = new ResidencyScheduler();
        using var residency = new MapRenderResidency(fixture.State(one, ResidencyFixture.Window(-3, 3),
            ResidencyFixture.Window(-2, 2), ResidencyFixture.Window(-1, 1)), new ResidencyDevice(), null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        Assert.Equal(28, residency.AcceptedPlans.Sum(p => p.Resources.Count));
        ResidencyFixture.Draw(residency);
        var before = Containers(residency);
        residency.SetWindows([one, ResidencyFixture.Window(-3, 3)]);
        ResidencyFixture.Complete(residency, scheduler);
        var atHalf = Containers(residency);
        for (int i = 0; i < before.Length; i++) Assert.Same(before[i].Identity, atHalf[i].Identity);
        residency.SetWindows([one]);
        ResidencyFixture.Complete(residency, scheduler);
        Assert.Equal(7, Assert.Single(residency.AcceptedPlans).Resources.Count);
        var after = Containers(residency);
        for (int i = 0; i < before.Length; i++)
        {
            Assert.NotSame(before[i].Identity, after[i].Identity);
            Assert.Equal(7, after[i].Capacity);
        }
        Assert.Equal(4096, residency.RecordingChargeBytesForTests);
        Assert.NotEmpty(ResidencyFixture.Draw(residency).Resources);
    }

    [Fact]
    public void Disposal_DropsRecordingBackingStorageBeforeReturningCredit()
    {
        using var fixture = ResidencyFixture.Placement();
        var scheduler = new ResidencyScheduler();
        using var residency = new MapRenderResidency(fixture.State(ResidencyFixture.Window()), new ResidencyDevice(), null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        ResidencyFixture.Draw(residency);
        residency.Dispose();
        foreach (var container in Containers(residency)) Assert.Equal(0, container.Capacity);
        Assert.Equal(0, residency.Lifetime.CpuBytes);
        Assert.Equal(0, residency.Lifetime.LiveHandles);
    }

    static (object Identity, int Capacity)[] Containers(MapRenderResidency residency) => residency.RecordingStorageForTests;
}
