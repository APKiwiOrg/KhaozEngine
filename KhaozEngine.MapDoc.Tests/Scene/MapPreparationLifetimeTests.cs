using System;
using KhaozEngine.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapPreparationLifetimeTests
{
    [Fact]
    public void SequentialWorkspace_UsesPeakRatherThanCumulativeDemand()
    {
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 100));
        admission.ReserveBytes(40);
        for (int i = 0; i < 3; i++)
        {
            using var workspace = admission.Workspace();
            workspace.Reserve(50);
            Assert.Equal(50, admission.WorkspaceBytes);
        }
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.Equal(40, admission.RetainedBytes);
        Assert.Equal(90, admission.PeakBytes);
        using var boundary = admission.Workspace();
        boundary.Reserve(60);
        var refused = Assert.Throws<MapSceneCapacityException>(() => boundary.Reserve(1));
        Assert.Equal(new MapSceneCapacity("MaxPreparedBytes", 101, 100), refused.Capacity);
        Assert.Equal(100, admission.PeakBytes);
        Assert.Equal(60, admission.WorkspaceBytes);
    }

    [Fact]
    public void TransferAndDoubleDisposal_PreserveLiveOwnership()
    {
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 100));
        var workspace = admission.Workspace();
        workspace.Reserve(70);
        var retained = workspace.Transfer(40);
        Assert.Equal(30, admission.WorkspaceBytes);
        Assert.Equal(40, admission.RetainedBytes);
        workspace.Dispose(); workspace.Dispose();
        Assert.Equal(60, admission.RemainingPreparedBytes);
        using (var growth = admission.Workspace())
        {
            growth.Reserve(60);
            Assert.Throws<MapSceneCapacityException>(() => growth.Reserve(1));
        }
        retained.Dispose(); retained.Dispose();
        Assert.Equal(100, admission.RemainingPreparedBytes);
    }

    [Fact]
    public void ChildBudget_ParentReleaseNeverCreatesNegativeDemand()
    {
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 100));
        var parent = admission.Workspace(); parent.Reserve(40);
        using var child = admission.LimitPreparation(30, "MaxPlanBytes", 10);
        parent.Dispose();
        using var workspace = admission.Workspace(); workspace.Reserve(30);
        var refused = Assert.Throws<MapSceneCapacityException>(() => workspace.Reserve(1));
        Assert.Equal(new MapSceneCapacity("MaxPlanBytes", 41, 40), refused.Capacity);
        Assert.Equal(30, admission.WorkspaceBytes);
    }

    [Fact]
    public void ReturnedOwnership_ConstrainsLaterWindowsAfterItsWindowCloses()
    {
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 100));
        MapPreparationReservation retained;
        using (admission.LimitPreparation(60, "MaxPlanBytes"))
        {
            using var workspace = admission.Workspace(); workspace.Reserve(60);
            retained = workspace.Transfer(60);
        }
        using (retained)
        using (admission.LimitPreparation(80, "MaxPlanBytes"))
        using (var workspace = admission.Workspace())
        {
            var refused = Assert.Throws<MapSceneCapacityException>(() => workspace.Reserve(41));
            Assert.Equal(new MapSceneCapacity("MaxPreparedBytes", 101, 100), refused.Capacity);
        }
        Assert.Equal(0, admission.RetainedBytes);
    }
}
