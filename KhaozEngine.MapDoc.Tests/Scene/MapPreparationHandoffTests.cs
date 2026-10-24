using KhaozEngine.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapPreparationHandoffTests
{
    static MapPreparationReservation Retain(MapSceneAdmission admission, long bytes)
    {
        using var preparation = admission.BeginPreparation();
        preparation.Reserve(bytes);
        return preparation.Complete(bytes);
    }

    [Fact]
    public void ForeignAttemptHandoff_AdmitsDestinationBeforeReleasingSource()
    {
        var source = new MapSceneAdmission(new(MaxPreparedBytes: 100));
        var destination = new MapSceneAdmission(new(MaxPreparedBytes: 100));
        var product = Retain(source, 40);
        MapPreparationReservation result;
        using (var preparation = destination.BeginPreparation())
        {
            preparation.Reserve(50);
            preparation.WorkspaceOwner.Absorb(product);
            Assert.Equal(0, source.RetainedBytes);
            Assert.Equal(90, destination.WorkspaceBytes);
            Assert.Equal(90, destination.PeakBytes);
            result = preparation.Complete(40);
        }
        Assert.Equal(0, destination.WorkspaceBytes);
        Assert.Equal(40, destination.RetainedBytes);
        product.Dispose(); result.Dispose();
        Assert.Equal(0, destination.RetainedBytes);
    }

    [Fact]
    public void ForeignAttemptRefusal_PreservesTheSourceAndDestinationCounters()
    {
        var source = new MapSceneAdmission(new(MaxPreparedBytes: 100));
        var destination = new MapSceneAdmission(new(MaxPreparedBytes: 100));
        using var product = Retain(source, 40);
        using var preparation = destination.BeginPreparation();
        preparation.Reserve(61);
        var refusal = Assert.Throws<MapSceneCapacityException>(() => preparation.WorkspaceOwner.Absorb(product));
        Assert.Equal(101, refusal.Capacity.Requested);
        Assert.Equal(100, refusal.Capacity.Allowance);
        Assert.Equal(40, source.RetainedBytes);
        Assert.Equal(40, product.Bytes);
        Assert.Equal(61, destination.WorkspaceBytes);
        Assert.Equal(61, destination.PeakBytes);
    }

    [Fact]
    public void ChildViewHandoff_ChargesTheChildWithoutDuplicatingSharedRootInventory()
    {
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 100));
        using var parent = admission.LimitPreparation(90, "parent");
        using var product = Retain(admission, 40);
        using var parentWork = admission.Workspace(); parentWork.Reserve(20);
        MapPreparationReservation result;
        using (admission.LimitPreparation(60, "child"))
        using (var preparation = admission.BeginPreparation())
        {
            preparation.Reserve(20);
            preparation.WorkspaceOwner.Absorb(product);
            Assert.Equal(0, admission.RetainedBytes);
            Assert.Equal(80, admission.WorkspaceBytes);
            Assert.Equal(80, admission.PeakBytes);
            var refusal = Assert.Throws<MapSceneCapacityException>(() => preparation.Reserve(1));
            Assert.Equal("child", refusal.LimitName);
            Assert.Equal(61, refusal.Capacity.Requested);
            Assert.Equal(60, refusal.Capacity.Allowance);
            result = preparation.Complete(40);
        }
        Assert.Equal(40, admission.RetainedBytes);
        Assert.Equal(20, admission.WorkspaceBytes);
        result.Dispose(); parentWork.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
    }

    [Fact]
    public void ChildViewRefusal_PreservesTheEarlierProductAndBindingLimit()
    {
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 100));
        using var parent = admission.LimitPreparation(90, "parent");
        using var product = Retain(admission, 41);
        using var parentWork = admission.Workspace(); parentWork.Reserve(20);
        using var child = admission.LimitPreparation(60, "child");
        using var preparation = admission.BeginPreparation(); preparation.Reserve(20);
        var refusal = Assert.Throws<MapSceneCapacityException>(() => preparation.WorkspaceOwner.Absorb(product));
        Assert.Equal("child", refusal.LimitName);
        Assert.Equal(61, refusal.Capacity.Requested);
        Assert.Equal(60, refusal.Capacity.Allowance);
        Assert.Equal(41, product.Bytes);
        Assert.Equal(41, admission.RetainedBytes);
        Assert.Equal(40, admission.WorkspaceBytes);
        Assert.Equal(81, admission.PeakBytes);
    }
}
