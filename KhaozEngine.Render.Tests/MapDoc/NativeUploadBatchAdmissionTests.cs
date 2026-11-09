using System;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeUploadBatchAdmissionTests
{
    [Fact]
    public void CompletedUnpublishedUpload_ReleasesInFlightAllowance()
    {
        var admission = new MapRenderAdmission(Budgets(100, 10));
        var demand = new SceneUploadDemand("invented", 40, 10, 100, 40, 4);
        using var first = admission.TryReserve(demand)!;
        first.Claim(demand);
        Assert.Null(admission.TryReserve(demand, out var blocked));
        Assert.Equal("MaxInFlightUploadCpuBytes", blocked!.LimitName);
        first.TransferCompleted(); first.InputsReleased(20);
        first.TransferCompleted(); first.InputsReleased(20);
        using var second = admission.TryReserve(demand)!;
        Assert.NotNull(second);
        Assert.Equal(120, admission.CpuBytes);
        Assert.Equal(90, admission.GpuBytes);
        first.Retiring(); first.Released(); first.Released();
        second.Dispose();
        Assert.Equal(0, admission.CpuBytes);
        Assert.Equal(0, admission.GpuBytes);
    }

    [Theory]
    [InlineData(99, 10, "MaxInFlightUploadCpuBytes", 100, 99)]
    [InlineData(100, 9, "MaxInFlightUploadStagingGpuBytes", 10, 9)]
    public void SingleResource_RefusesNamedInFlightLimit(long cpu, long staging, string name, long requested, long allowance)
    {
        var admission = new MapRenderAdmission(Budgets(cpu, staging));
        using var reservation = admission.TryReserve(new("invented", 40, 10, 100, 40, 4), out var capacity);
        Assert.Null(reservation);
        Assert.Equal(name, capacity!.LimitName);
        Assert.Equal(requested, capacity.Requested);
        Assert.Equal(allowance, capacity.Allowance);
        Assert.Equal(0, admission.CpuBytes);
        Assert.Equal(0, admission.GpuBytes);
    }

    [Fact]
    public void StagingAllowance_WaitsForCompletionIndependentlyOfCpu()
    {
        var admission = new MapRenderAdmission(Budgets(200, 10));
        var demand = new SceneUploadDemand("invented", 40, 10, 100, 40, 4);
        using var first = admission.TryReserve(demand)!;
        first.Claim(demand);
        Assert.Null(admission.TryReserve(demand, out var blocked));
        Assert.Equal("MaxInFlightUploadStagingGpuBytes", blocked!.LimitName);
        first.TransferCompleted();
        using var second = admission.TryReserve(demand);
        Assert.NotNull(second);
        Assert.Equal(200, admission.CpuBytes);
        Assert.Equal(90, admission.GpuBytes);
        first.InputsReleased(20); first.Retiring(); first.Released();
    }

    [Fact]
    public void CancelledUpload_HoldsInFlightAllowanceUntilDestruction()
    {
        var admission = new MapRenderAdmission(Budgets(100, 10));
        var demand = new SceneUploadDemand("invented", 40, 10, 100, 40, 4);
        using var first = admission.TryReserve(demand)!;
        first.Claim(demand); first.Retiring(); first.Dispose();
        Assert.Null(admission.TryReserve(demand));
        first.Released(); first.Released();
        using var next = admission.TryReserve(demand);
        Assert.NotNull(next);
        Assert.Equal(100, admission.CpuBytes);
        Assert.Equal(50, admission.GpuBytes);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(100, -1)]
    public void NegativeInFlightAllowances_AreRejected(long cpu, long staging) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MapRenderAdmission(Budgets(cpu, staging)));

    internal static MapRenderBudgets Budgets(long cpu = 100, long staging = 10) =>
        new(MaxAppliesPerUpdate: 32, MaxInFlightUploadCpuBytes: cpu, MaxInFlightUploadStagingGpuBytes: staging);
}
