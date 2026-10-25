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
    [InlineData(49, false)]
    [InlineData(50, true)]
    public void Coefficient_AdmitsItsActualMagnitudeBeforeAllocating(long allowance, bool accepted)
    {
        int entered = 0;
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: allowance), _ => entered++);
        using var active = admission.Enter();
        using var product = admission.BeginPreparation();
        var counts = new MapRoofPreparationCounts();
        using var build = new MapMembershipBuild(admission, product, counts);
        if (!accepted)
        {
            var refusal = Assert.Throws<MapSceneCapacityException>(() => new MapMembershipCoefficient(new BigInteger(256)));
            Assert.Equal(new MapSceneCapacity("MaxPreparedBytes", 50, allowance), refusal.Capacity);
            Assert.Equal(0, entered);
            Assert.Equal(0, admission.WorkspaceBytes);
            Assert.Equal(0, counts.NormalizedCoefficientBytes);
            return;
        }
        var coefficient = new MapMembershipCoefficient(new BigInteger(256));
        Assert.Equal(50, coefficient.InventoryBytes);
        Assert.Equal(50, admission.WorkspaceBytes);
        Assert.Equal(2, counts.NormalizedCoefficientBytes);
        product.Complete(50).Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }
}
