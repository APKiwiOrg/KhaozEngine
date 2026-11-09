using System.Numerics;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Interiors;

public sealed class MapMembershipCapacityTests
{
    [Theory]
    [InlineData(37167, false)]
    [InlineData(37168, true)]
    public void HighCoefficient_ChargesNormalizedOutputAndOneLiveArithmeticWorkspace(long allowance, bool accepted)
    {
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: allowance));
        using var product = admission.BeginPreparation();
        var counts = new MapRoofPreparationCounts();
        using var build = new MapMembershipBuild(admission, product, counts);
        if (!accepted)
        {
            var refusal = Assert.Throws<MapSceneCapacityException>(() => new MapMembershipAffine(BigInteger.One << 180, 0, 1, 1));
            Assert.Equal(new MapSceneCapacity("MaxPreparedBytes", 37168, allowance), refusal.Capacity);
            return;
        }
        var affine = new MapMembershipAffine(BigInteger.One << 180, 0, 1, 1);
        // Store 128, token directory 32 and page 88, magnitude directory 32 and page 4120.
        Assert.Equal(4400, affine.Store.InventoryBytes);
        Assert.Equal(4400, admission.WorkspaceBytes);
        Assert.Equal(37168, admission.PeakBytes);
        Assert.Equal(26, counts.NormalizedCoefficientBytes);
    }

    [Theory]
    [InlineData(0, 1, true)]
    [InlineData(0, 0.0000001, false)]
    [InlineData(1000000000000000, 1, false)]
    public void TransformedDiscovery_RefusesAnUncertifiedInverseRange(double translation, double scale, bool certified)
    {
        var transform = new MapResourceTransform(new(translation, 0, 0, .4, scale));
        if (certified) MapMembershipDemandPlan.CertifyRange(MapLatticeFrame.ImportedMetreCentimetre, transform);
        else Assert.Throws<MapExactOverflowException>(() => MapMembershipDemandPlan.CertifyRange(MapLatticeFrame.ImportedMetreCentimetre, transform));
    }

    [Theory]
    [InlineData(247, false)]
    [InlineData(248, true)]
    public void Coefficient_AdmitsItsTokenPageBeforeAllocating(long allowance, bool accepted)
    {
        int entered = 0;
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: allowance));
        admission.AllocationAdmitted = increment => { if (increment == 88) entered++; };
        using var active = admission.Enter();
        using var product = admission.BeginPreparation();
        var counts = new MapRoofPreparationCounts();
        using var build = new MapMembershipBuild(admission, product, counts);
        var store = MapMembershipCoefficientStore.Create(4);
        if (!accepted)
        {
            var refusal = Assert.Throws<MapSceneCapacityException>(() => store.Append(new BigInteger(256)));
            Assert.Equal(new MapSceneCapacity("MaxPreparedBytes", 248, allowance), refusal.Capacity);
            Assert.Equal(0, entered);
            Assert.Equal(160, admission.WorkspaceBytes);
            Assert.Equal(0, counts.NormalizedCoefficientBytes);
            return;
        }
        int coefficient = store.Append(new BigInteger(256));
        Assert.Equal(0, store[coefficient].Kind);
        Assert.Equal(248, store.InventoryBytes);
        Assert.Equal(248, admission.WorkspaceBytes);
        Assert.Equal(2, counts.NormalizedCoefficientBytes);
        product.Complete(248).Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }
}
