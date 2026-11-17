using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Collection("AllocSensitive")]
[Trait("Category", "WaterKernelContract")]
public class TileWaterTraceMemoEnvironmentTests(ITestOutputHelper output)
{
    // The changed-control theory runs in KhaozEngine.TileWorld.Physics.FloatingPoint.Tests.
    [Fact]
    public void WarmHitsAndWarmTableMissesAllocateNothing()
    {
        var field = MovementWaterFieldTests.Field(GrazingWaterBank.Document());
        var query = GrazingWaterBank.Lift(110.5f);
        var miss = TileWaterTraceMemoLifetimeTests.Query(1);
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        MovementCoverageResult result = default;
        field.TraceWater(query, Vector3.Zero, MovementWaterFieldTests.Identity, spans, contacts);
        field.TraceWater(query, Vector3.Zero, MovementWaterFieldTests.Identity, spans, contacts);
        AllocationMeasurement.NoPerCallAllocation("an exact water trace memo hit", () =>
            result = field.TraceWater(query, Vector3.Zero, MovementWaterFieldTests.Identity, spans, contacts));
        Assert.Equal(MovementAvailability.Known, result.Availability);
        AllocationMeasurement.NoPerCallAllocation("a warm water trace memo table miss", () =>
            result = field.TraceWater(miss, Vector3.Zero, MovementWaterFieldTests.Identity, spans, contacts));
        Assert.Equal(MovementAvailability.Known, result.Availability);
        output.WriteLine($"memo-layout-bytes key={Unsafe.SizeOf<TileWaterTraceMemo.Key>()}, span={Unsafe.SizeOf<MovementCoverageSpan>()}, contact={Unsafe.SizeOf<MovementDomainContact>()}");
    }
}
