using KhaozEngine.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapSelectiveExportTests
{
    [Fact]
    public void Export_MovesCollectorResponsibilityWithoutAnotherDebit()
    {
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 60));
        using var retained = admission.BeginPreparation();
        using (var query = admission.BeginPreparation())
        {
            using var clone = admission.BeginPreparation();
            clone.Reserve(60);
            var receipt = clone.Complete(60);
            query.Export(receipt, retained);
        }
        Assert.Equal(60, admission.RetainedBytes);
        Assert.Equal(60, admission.PeakBytes);
        retained.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }

    [Fact]
    public void RefusedExport_PreservesTheOriginalCollectorAndCapacityDetail()
    {
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 100));
        using var original = admission.BeginPreparation();
        MapPreparationReservation receipt;
        using (var clone = admission.BeginPreparation()) { clone.Reserve(60); receipt = clone.Complete(60); }
        using (admission.LimitPreparation(20, "MaxPlanBytes"))
        using (var destination = admission.BeginPreparation())
        {
            var refused = Assert.Throws<MapSceneCapacityException>(() => original.Export(receipt, destination));
            Assert.Equal(new MapSceneCapacity("MaxPlanBytes", 60, 20), refused.Capacity);
        }
        Assert.Equal(60, receipt.Bytes);
        Assert.Equal(60, admission.RetainedBytes);
        original.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }
}
