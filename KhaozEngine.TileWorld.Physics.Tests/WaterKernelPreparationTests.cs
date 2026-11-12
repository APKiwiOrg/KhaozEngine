using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Trait("Category", "WaterKernelContract")]
[Trait("Category", "Diagnostic")]
public sealed class WaterKernelPreparationTests(ITestOutputHelper output)
{
    [Fact]
    public void RealSlabExhaustionUsesTheDirectFallbackWithoutChangingOutput()
    {
        var geometry = WaterKernelTraceFixture.RepeatedFaces(2048, true);
        TileWaterQueryWork.Reset();
        try
        {
            var result = FrozenWaterComparison.CheckPrisms(geometry, WaterKernelTraceFixture.Query());
            Assert.Equal(MovementAvailability.CapacityExceeded, result.Availability);
            var stats = TileWaterPrismCoverage.CapturePreparationForTests();
            Assert.Equal(4 * 1024 * 1024, stats.BudgetBytes);
            Assert.InRange(stats.UsedBytes, 1, stats.BudgetBytes);
            Assert.True(stats.RetainedHits > 0);
            Assert.True(stats.ExhaustedHits > 0);
            Assert.True(stats.FallbackEvaluations > 0);
            output.WriteLine($"PREPARATION_STORAGE {stats}");
        }
        finally { TileWaterQueryWork.Enabled = false; }
    }

    [Fact]
    public void MissesAreDiscardedAndAnotherCaptureCannotReuseRetainedState()
    {
        var misses = WaterKernelTraceFixture.SelectedMisses(1024);
        var busy = WaterKernelTraceFixture.RepeatedPieces(2);
        TileWaterQueryWork.Reset();
        try
        {
            _ = FrozenWaterComparison.CheckPrisms(misses, WaterKernelTraceFixture.Query());
            var first = TileWaterPrismCoverage.CapturePreparationForTests();
            Assert.Equal(0, first.UsedBytes);
            Assert.Equal(0, first.RetainedHits);
            Assert.Equal(1024, first.DiscardedMisses);
            _ = FrozenWaterComparison.CheckPrisms(busy, WaterKernelTraceFixture.Query());
            Assert.True(TileWaterPrismCoverage.CapturePreparationForTests().UsedBytes > 0);
            _ = FrozenWaterComparison.CheckPrisms(misses, WaterKernelTraceFixture.Query());
            var again = TileWaterPrismCoverage.CapturePreparationForTests();
            Assert.Equal(first, again);
            output.WriteLine($"PREPARATION_MISSES {again}");
        }
        finally { TileWaterQueryWork.Enabled = false; }
    }
}
