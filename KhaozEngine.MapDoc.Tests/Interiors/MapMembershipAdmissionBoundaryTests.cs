using System.Numerics;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Interiors;

public sealed class MapMembershipAdmissionBoundaryTests
{
    [Fact]
    public void Compilation_WhenBothLimitsBind_RefusesBytesBeforeFacesAndBeforeEntry()
    {
        int entered = 0;
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 0, MaxPreparedFaces: 0), _ => entered++);
        var error = Assert.Throws<MapSceneCapacityException>(() => admission.ReserveCompilation(1));
        Assert.Equal(new MapSceneCapacity("MaxPreparedBytes", 1280, 0), error.Capacity);
        Assert.Equal(0, entered);
        Assert.Equal(0, admission.PreparedFaces);
        Assert.Equal(0, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
    }

    [Theory]
    [InlineData(247, 0)]
    [InlineData(248, 1)]
    public void TokenPage_EntersTheAllocationOnlyAfterItsCapacityIsAdmitted(long allowance, int expectedEntries)
    {
        int entered = 0;
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: allowance));
        admission.AllocationAdmitted = increment => { if (increment == 88) entered++; };
        using var product = admission.BeginPreparation();
        using var build = new MapMembershipBuild(admission, product, new MapRoofPreparationCounts());
        var store = MapMembershipCoefficientStore.Create(4);
        if (allowance == 247) Assert.Throws<MapSceneCapacityException>(() => store.Append(new BigInteger(256)));
        else _ = store.Append(new BigInteger(256));
        Assert.Equal(expectedEntries, entered);
    }
}
