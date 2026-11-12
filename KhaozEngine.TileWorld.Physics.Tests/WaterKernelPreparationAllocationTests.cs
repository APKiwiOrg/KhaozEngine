using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Collection("AllocSensitive")]
[Trait("Category", "WaterKernelContract")]
public sealed class WaterKernelPreparationAllocationTests
{
    [Fact]
    public void WarmPreparedCoverageAllocatesNoPerTraceStorage()
    {
        var geometry = WaterKernelTraceFixture.RepeatedPieces(1);
        var query = WaterKernelTraceFixture.Query();
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        MovementCoverageResult result = default;
        Action trace = () => result = TileWaterPrismCoverage.Trace(geometry.Prisms, geometry.Faces, geometry.Ground,
            geometry.PointIndex, geometry.PrismStarts, geometry.FaceStarts, query, Vector3.Zero,
            MovementWaterFieldTests.Identity, spans, contacts);
        trace();
        Assert.Equal(MovementAvailability.Known, result.Availability);
        long bytes = AllocationMeasurement.Measure("warmed prepared water coverage", trace);
        Assert.Equal(0, bytes);
        Assert.Equal(MovementAvailability.Known, result.Availability);
    }
}
