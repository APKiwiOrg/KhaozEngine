using System;
using System.Linq;
using KhaozEngine.MapDoc.Scene;
using Xunit;
using static KhaozEngine.Tests.CapacityDemandProbe;

namespace KhaozEngine.Tests.MapDoc.Scene;

[Collection("AllocSensitive")]
public sealed class MapPreparationDemandTests
{
    [Fact]
    public void CompletedScene_ReportsFinalRetainedInventorySeparatelyFromTransientAdmission()
    {
        using var fixture = NativeSceneFixtures.Create("empty");
        var source = NativeSceneFixtures.Open(fixture, false);
        using var probe = new CapacityDemandProbe();
        var result = source.Acquire(MapSceneScopeTests.Window());
        Assert.Equal(MapSceneStatus.CompleteForScope, result.Status);
        var rows = probe.Events.Where(e => e.GetType().GetProperty("Lifetime")!.GetValue(e)!.ToString() == "FinalInventory").ToArray();
        Assert.NotEmpty(rows);
        Assert.Contains(rows, r => Read<decimal>(r, "Requested") >= result.Scene!.InventoryBytes);
        Assert.All(rows, r => Assert.Equal(0, Read<decimal>(r, "ProposedIncrement")));
    }

    [Fact]
    public void LiveLedger_ReportsTheRootAndEveryWindowWithItsOwnAllowance()
    {
        using var probe = new CapacityDemandProbe();
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 100));
        using var window = admission.LimitPreparation(10, "MaxPlanBytes");
        using var owner = admission.Workspace();
        owner.Reserve(7);
        Assert.Throws<MapSceneCapacityException>(() => owner.Reserve(5));
        Assert.Equal(7, admission.WorkspaceBytes);
        Assert.Equal(7, admission.PeakBytes);
        var rows = probe.For("MaxPlanBytes").ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(7, Read<decimal>(rows[0], "Requested"));
        Assert.Equal(12, Read<decimal>(rows[1], "Requested"));
        Assert.Equal(10, Read<decimal>(rows[1], "Allowance"));
        Assert.True(Read<bool>(rows[1], "Refused"));
        Assert.All(probe.For("MaxPreparedBytes"), r => Assert.False(Read<bool>(r, "Refused")));
    }

    [Fact]
    public void BytesProbe_ReportsAcceptedDemandEvenWhenNothingIsReserved()
    {
        using var probe = new CapacityDemandProbe();
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 10));
        Assert.True(admission.CanReservePreparation(7, 1));
        Assert.False(admission.CanReservePreparation(11, 1));
        Assert.Equal(0, admission.RetainedBytes);
        var rows = probe.For("MaxPreparedBytes").ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(7, Read<decimal>(rows[0], "Requested"));
        Assert.Equal(11, Read<decimal>(rows[1], "Requested"));
        Assert.True(Read<bool>(rows[1], "Refused"));
    }
}
