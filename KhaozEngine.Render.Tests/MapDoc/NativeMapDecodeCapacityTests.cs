using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("NativeDeviceLifecycle")]
public sealed class NativeMapDecodeCapacityTests
{
    [Fact]
    public void CacheDecode_AdmitsActualLiveDemandWithinALargerDeclaredEnvelope()
    {
        using var fixture = ResidencyFixture.Placement();
        var state = fixture.State(ResidencyFixture.Window());
        var scene = new MapSceneAdmission(new(MaxPreparedBytes: 32768));
        using var active = scene.Enter();
        using var cache = new MapRenderAssetCache();
        var result = cache.Acquire(state.Closure, ["prop-a"], [], state.Extents, new(new(MaxPeakBytes: 65536)));
        Assert.True(result.Status == MapRenderAssetStatus.Complete, result.Detail);
        Assert.True(scene.RetainedBytes > 0);
        result.Input!.Dispose();
        Assert.Equal(0, scene.WorkspaceBytes);
        Assert.Equal(0, scene.RetainedBytes);
    }

    [Fact]
    public void CompositionRefusal_RetainsTheCallerPlanWindow()
    {
        using var fixture = ResidencyFixture.Placement();
        var state = fixture.State(ResidencyFixture.Window());
        var scene = new MapSceneAdmission(new());
        scene.ReserveBytes(20);
        using var window = scene.LimitPreparation(1, "MaxPlanBytes", 2);
        var cpuLimits = new MapRenderBudgets(MaxResidentCpuBytes: 4096);
        var cpu = new MapRenderAdmission(cpuLimits);
        var allowance = new MapRenderDecodeAllowance(scene, cpu, cpuLimits);
        using var cache = new MapRenderAssetCache(cpu);
        var result = cache.Acquire(state.Closure, [], [], state.Extents, allowance.Budget);
        Assert.Equal(MapRenderAssetStatus.CapacityExceeded, result.Status);
        Assert.Equal(new MapSceneCapacity("MaxPlanBytes", 1026, 3), allowance.Resolve(result.Capacity!));
        Assert.Equal(0, cpu.CpuBytes);
    }

    [Fact]
    public void CompositionRefusal_RetainsTheCallerCpuAllowance()
    {
        using var fixture = ResidencyFixture.Placement();
        var state = fixture.State(ResidencyFixture.Window());
        var limits = new MapRenderBudgets(MaxResidentCpuBytes: 4096);
        var cpu = new MapRenderAdmission(limits);
        using var retained = cpu.ReserveCpu(4095);
        var allowance = new MapRenderDecodeAllowance(new(new()), cpu, limits);
        using var cache = new MapRenderAssetCache(cpu);
        var result = cache.Acquire(state.Closure, [], [], state.Extents, allowance.Budget);
        Assert.Equal(MapRenderAssetStatus.CapacityExceeded, result.Status);
        Assert.Equal(new MapSceneCapacity("MaxResidentCpuBytes", 5119, 4096), allowance.Resolve(result.Capacity!));
        Assert.Equal(4095, cpu.CpuBytes);
    }

    [Fact]
    public void StrictMeshAdmission_ReportsMeasuredDecoderPeak()
    {
        using var fixture = ResidencyFixture.Placement();
        var asset = fixture.Closure.GetAsset("prop-a");
        var bytes = fixture.Closure.GetResource(asset.MeshResourceId).Bytes;
        var budget = new GltfDecodeBudget(new(MaxPeakBytes: 1));
        var admission = GltfDecodeAdmission.Measure(bytes, budget);
        Assert.False(admission.Allowed);
        Assert.Equal("MaxPeakBytes", admission.Capacity!.LimitName);
        Assert.Equal(1, admission.Capacity.Allowance);
        Assert.Equal(admission.PeakBytes, admission.Capacity.Requested);
        Assert.True(admission.Capacity.Requested > 1);
        var error = Assert.Throws<GltfDecodeRefusal>(() => GltfLoader.LoadPartsWithMaterialsStrict(bytes, "invented", budget));
        Assert.Equal(admission.Capacity, error.Capacity);
    }
    [Fact]
    public void CacheComposition_ReportsInnerPeakAfterCpuAdmissionSucceeds()
    {
        using var fixture = ResidencyFixture.Placement();
        var state = fixture.State(ResidencyFixture.Window());
        var admission = new MapRenderAdmission(new(MaxResidentCpuBytes: 4096));
        using var cache = new MapRenderAssetCache(admission);
        var budget = new GltfDecodeBudget(new(MaxPeakBytes: 1));
        var result = cache.Acquire(state.Closure, [], [], state.Extents, budget);
        Assert.Equal(MapRenderAssetStatus.CapacityExceeded, result.Status);
        Assert.Null(result.Input);
        Assert.Contains("MaxPeakBytes", result.Detail);
        Assert.Equal(new MapSceneCapacity("MaxPeakBytes", 1024, 1), result.Capacity);
        Assert.Equal(0, admission.CpuBytes);
        Assert.Equal(0, budget.OwnedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MeshAdmission_PreservesDecoderLimitThroughPreparedInput(bool prepared)
    {
        using var fixture = ResidencyFixture.Placement();
        var state = fixture.State(ResidencyFixture.Window());
        var budget = new GltfDecodeBudget(new(MaxPeakBytes: 1));
        if (prepared)
        {
            var result = MapPreparedInputLease.LoadSelected(state.Closure, ["prop-a"], [], state.Extents, budget);
            Assert.Equal(MapRenderAssetStatus.CapacityExceeded, result.Status);
            Assert.Contains("MaxPeakBytes", result.Detail);
            Assert.Equal("MaxPeakBytes", result.Capacity!.LimitName);
            Assert.Equal(1, result.Capacity.Allowance);
        }
        else
        {
            var result = MapRenderAssets.LoadSelected(state.Closure, ["prop-a"], state.Extents, budget);
            Assert.Equal(MapRenderAssetStatus.CapacityExceeded, result.Status);
            Assert.Contains("MaxPeakBytes", result.Detail);
            Assert.Equal("MaxPeakBytes", result.Capacity!.LimitName);
            Assert.Equal(1, result.Capacity.Allowance);
        }
        Assert.Equal(0, budget.OwnedBytes);
    }

    [Fact]
    public void MaterialPreparation_PreservesDecoderReservationDetail()
    {
        using var fixture = ResidencyFixture.Placement();
        var budget = new GltfDecodeBudget(new(MaxPeakBytes: 1));
        var error = Assert.Throws<MapRenderAssetRefusal>(() => MapRenderMaterials.Load(fixture.Closure, [], budget));
        Assert.Contains("MaxPeakBytes", error.Message);
        Assert.Equal(new MapSceneCapacity("MaxPeakBytes", 256, 1), error.Capacity);
        Assert.Equal(0, budget.OwnedBytes);
    }
}
