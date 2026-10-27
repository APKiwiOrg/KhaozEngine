using System.Linq;
using System.Globalization;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("NativeDeviceLifecycle")]
public sealed class NativeMapCapacityDiagnosticsTests
{
    [Theory]
    [InlineData(true, "MaxPlanBytes")]
    [InlineData(false, "MaxResidentCpuBytes")]
    public void ResidencyRefusal_NamesTheDeclaredPreparationBudget(bool plan, string name)
    {
        using var fixture = ResidencyFixture.Placement();
        var scheduler = new ResidencyScheduler();
        var budgets = plan ? new MapRenderBudgets(MaxPlanBytes: 1) : new MapRenderBudgets(MaxResidentCpuBytes: 1);
        using var residency = new MapRenderResidency(fixture.State(ResidencyFixture.Window()), new ResidencyDevice(), budgets, scheduler);
        scheduler.RunAll();
        var transition = residency.Update().Transition;
        Assert.Equal(MapSceneStatus.CapacityExceeded, transition.Status);
        Assert.Equal(name, transition.Capacity!.LimitName);
        Assert.Equal(1, transition.Capacity.Allowance);
        Assert.True(transition.Capacity.Requested > 1);
        Assert.Contains(name, Assert.Single(transition.Findings));
    }

    [Fact]
    public void Admission_ReportsCombinedDemandBeyondInt64WithoutOverflowing()
    {
        var admission = new MapRenderAdmission(new(MaxResidentGpuBytes: long.MaxValue));
        using var reservation = admission.TryReserve(new("invented", long.MaxValue, 1, 0, 0, 0), out var capacity);
        Assert.Null(reservation);
        Assert.NotNull(capacity);
        Assert.Equal("MaxResidentGpuBytes", capacity.LimitName);
        Assert.Equal("9223372036854775808", capacity.Requested.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(long.MaxValue, capacity.Allowance);
        Assert.Equal(0, admission.GpuBytes);
    }

    [Fact]
    public void UploadPreflight_NamesWholeWindowGpuDemandAndAllowance()
    {
        using var fixture = ResidencyFixture.Placement();
        var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice();
        using var residency = new MapRenderResidency(fixture.State(ResidencyFixture.Window()), device, new(MaxResidentGpuBytes: 0), scheduler);
        scheduler.RunAll();
        var transition = residency.Update().Transition;
        Assert.Equal(MapSceneStatus.CapacityExceeded, transition.Status);
        // Six LOD mesh parts and one light each own 100 bytes and need 10 staging bytes.
        Assert.Equal(new MapSceneCapacity("MaxResidentGpuBytes", 770, 0), transition.Capacity);
        Assert.Contains("requested 770", Assert.Single(transition.Findings));
        Assert.Equal(0, device.Begins);
    }

    [Fact]
    public void AcquisitionRefusal_PreservesTypedDetailAcrossWorkerConversion()
    {
        using var fixture = ResidencyFixture.Placement();
        var state = fixture.State(ResidencyFixture.Window());
        using var document = MapDocumentSource.FromDocument(fixture.Document);
        var source = MapSceneSource.Capture(document, fixture.Closure, new(MaxSelectedResources: 0)).Source!;
        state = state with { Source = source.WithRenderExtents(state.Extents).Source! };
        var scheduler = new ResidencyScheduler();
        using var residency = new MapRenderResidency(state, new ResidencyDevice(), null, scheduler);
        scheduler.RunAll();
        var transition = residency.Update().Transition;
        Assert.Equal(MapSceneStatus.CapacityExceeded, transition.Status);
        Assert.Equal(new MapSceneCapacity("MaxSelectedResources", 1, 0), transition.Capacity);
        Assert.Contains("allowance 0", Assert.Single(transition.Findings));
    }
}
