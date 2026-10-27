using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("NativeDeviceLifecycle")]
public sealed class NativeUploadReadinessTests
{
    [Theory]
    [InlineData(100, 70, false)]
    [InlineData(700, 10, false)]
    [InlineData(100, 10, true)]
    public void ReadyWithoutCallbacks_ReleasesInFlightCreditAndRetainsCpuThroughReplacement(long cpu, long staging, bool lateInputRelease)
    {
        using var fixture = ResidencyFixture.Placement();
        var scheduler = new ResidencyScheduler(); var device = new ObservedDevice();
        using var residency = new MapRenderResidency(fixture.State(ResidencyFixture.Window()), device,
            new(MaxAppliesPerUpdate: 32, MaxUploadBytesPerUpdate: 16, MaxResidentGpuBytes: 1470,
                MaxInFlightUploadCpuBytes: cpu, MaxInFlightUploadStagingGpuBytes: staging), scheduler);
        UploadWindow(residency, scheduler, device, 0, 0);
        var accepted = residency.CurrentTransition.AcceptedWitness;
        Assert.Equal(7, residency.Lifetime.LiveHandles);
        Assert.Equal(3, ResidencyFixture.Draw(residency).Draws.Count);
        AssertCharges(residency, device, 700, 700, 0, 0);

        if (lateInputRelease)
        {
            foreach (var reservation in device.Reservations)
            {
                reservation.TransferCompleted(); reservation.TransferCompleted();
                reservation.InputsReleased(20); reservation.InputsReleased(20);
            }
            AssertCharges(residency, device, 140, 700, 0, 0);
        }

        fixture.Document.Placements[0].X = .1f;
        residency.RequestReplacement(fixture.State(ResidencyFixture.Window()));
        UploadWindow(residency, scheduler, device, 7, lateInputRelease ? 140 : 700);
        Assert.NotEqual(accepted, residency.CurrentTransition.AcceptedWitness);
        Assert.Equal(7, residency.Lifetime.LiveHandles);
        Assert.Equal(3, ResidencyFixture.Draw(residency).Draws.Count);
        AssertCharges(residency, device, 700, 700, 0, 0);
        residency.Dispose(); residency.Dispose();
        AssertCharges(residency, device, 0, 0, 0, 0);
        Assert.Equal(0, residency.Lifetime.CpuBytes);
    }

    static void UploadWindow(MapRenderResidency residency, ResidencyScheduler scheduler, ObservedDevice device, int previous, long retainedCpu)
    {
        scheduler.RunAll();
        var accepted = residency.CurrentTransition.AcceptedWitness;
        var draws = ResidencyFixture.Draw(residency).Draws;
        // One placement has six LOD meshes and one light. Each existing-device demand omits the retained
        // bound, owns 100 GPU bytes and retains its complete 100 CPU bytes. Only 10 GPU bytes are staging.
        for (int completed = 0; completed < 7; completed++)
        {
            Assert.Equal(MapSceneStatus.Incomplete, residency.Update().Transition.Status);
            Assert.Equal(previous + completed + 1, device.Reservations.Count);
            AssertCharges(residency, device, retainedCpu + (completed + 1) * 100, (previous + completed) * 100 + 110, 100, 10);
            Assert.Equal(accepted, residency.CurrentTransition.AcceptedWitness);
            Assert.Equal(draws, ResidencyFixture.Draw(residency).Draws);

            Assert.Equal(MapSceneStatus.Incomplete, residency.Update().Transition.Status);
            Assert.Equal(previous + completed + 1, device.Reservations.Count);
            AssertCharges(residency, device, retainedCpu + (completed + 1) * 100, (previous + completed + 1) * 100, 0, 0);
            Assert.Equal(accepted, residency.CurrentTransition.AcceptedWitness);
            Assert.Equal(draws, ResidencyFixture.Draw(residency).Draws);
        }
        Assert.Equal(MapSceneStatus.CompleteForScope, residency.Update().Transition.Status);
    }

    static void AssertCharges(MapRenderResidency residency, ObservedDevice device, long cpu, long gpu, long inFlightCpu, long inFlightStaging)
    {
        long Charged(string name) => device.Reservations.Where(r => !(bool)typeof(MapUploadReservation)
            .GetField("_released", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(r)!).Sum(r => (long)typeof(MapUploadReservation)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(r)!);
        Assert.Equal(cpu, Charged("_cpu"));
        Assert.Equal(gpu, Charged("_gpu"));
        Assert.Equal(gpu, residency.Lifetime.GpuBytes);
        Assert.Equal(inFlightCpu, Charged("_inFlightCpu"));
        Assert.Equal(inFlightStaging, Charged("_inFlightStaging"));
    }

    sealed class ObservedDevice : IMapRenderDevice
    {
        readonly ResidencyDevice _device = new();
        internal List<ISceneUploadReservation> Reservations { get; } = [];
        public SceneUploadDemand MeasureUpload(MapPreparedResource resource) => _device.MeasureUpload(resource);
        public IMapRenderUpload BeginUpload(MapPreparedResource resource, ISceneUploadReservation reservation)
        {
            Reservations.Add(reservation);
            return _device.BeginUpload(resource, reservation);
        }
        public void Release(MapRenderHandle handle) => _device.Release(handle);
    }
}
