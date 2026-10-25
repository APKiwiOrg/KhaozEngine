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
    [InlineData(49, 0)]
    [InlineData(50, 1)]
    public void Magnitude_EntersTheAllocationOnlyAfterItsCapacityIsAdmitted(long allowance, int expectedEntries)
    {
        int entered = 0;
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: allowance), _ => entered++);
        using var product = admission.BeginPreparation();
        using var build = new MapMembershipBuild(admission, product, new MapRoofPreparationCounts());
        if (allowance == 49) Assert.Throws<MapSceneCapacityException>(() => new MapMembershipCoefficient(new BigInteger(256)));
        else _ = new MapMembershipCoefficient(new BigInteger(256));
        Assert.Equal(expectedEntries, entered);
    }
}
