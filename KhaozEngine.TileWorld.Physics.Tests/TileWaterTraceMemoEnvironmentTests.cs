using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Collection("AllocSensitive")]
public class TileWaterTraceMemoEnvironmentTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("up")]
    [InlineData("down")]
    [InlineData("zero")]
    [InlineData("flush-output")]
    [InlineData("flush-input")]
    public void ChangingFloatingPointControlsMissesThenMatchesRecomputation(string mode)
    {
        using var environment = WaterFloatingPointEnvironment.Capture();
        if (environment is null || !TileWaterFloatingPointState.TryRead(out _))
        {
            output.WriteLine("No supported native control reader. Memo execution is bypassed on this platform.");
            return;
        }
        Assert.True(environment.Set("nearest"));
        Assert.True(TileWaterFloatingPointState.TryRead(out ulong baseline));
        var field = MovementWaterFieldTests.Field(GrazingWaterBank.Document());
        var query = GrazingWaterBank.Lift(110.5f);
        WaterTraceMemoComparison.Check(field, query);
        WaterTraceMemoComparison.Memo(WaterTraceMemoComparison.Check(field, query), hit: true);
        if (!environment.Set(mode))
        {
            output.WriteLine($"No independent {mode} control on this architecture.");
            return;
        }
        Assert.True(TileWaterFloatingPointState.TryRead(out ulong changed));
        Assert.NotEqual(baseline, changed);
        WaterTraceMemoComparison.Memo(WaterTraceMemoComparison.Check(field, query), hit: false);
        WaterTraceMemoComparison.Memo(WaterTraceMemoComparison.Check(field, query), hit: true);
        Assert.True(TileWaterFloatingPointState.TryRead(out ulong after));
        Assert.Equal(changed, after);
        Assert.True(environment.Set("nearest"));
        WaterTraceMemoComparison.Memo(WaterTraceMemoComparison.Check(field, query), hit: true);
    }

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
