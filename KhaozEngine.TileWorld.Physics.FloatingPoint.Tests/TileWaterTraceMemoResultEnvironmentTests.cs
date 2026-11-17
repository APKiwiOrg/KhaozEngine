using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Collection("AllocSensitive")]
[Trait("Category", "WaterKernelContract")]
public sealed class TileWaterTraceMemoResultEnvironmentTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("up")]
    [InlineData("down")]
    [InlineData("zero")]
    [InlineData("flush-output")]
    [InlineData("flush-input")]
    public void NativeControlChangesMatchTheIndependentFlatAndPrismEvaluators(string mode)
    {
        using var environment = WaterFloatingPointEnvironment.Capture();
        if (environment is null || !TileMediumSampler.TryReadTraceControls(out _)) return;
        Assert.True(environment.Set("nearest"));
        foreach (bool flat in new[] { false, true })
        {
            var field = TileWaterTraceMemoResultTests.Field(flat);
            var q = TileWaterTraceMemoResultTests.Query();
            TileWaterTraceMemoResultTests.Check(field, q);
            Assert.Equal(0, TileWaterTraceMemoResultTests.Work(TileWaterTraceMemoResultTests.Check(field, q)));
            if (!environment.Set(mode)) { output.WriteLine($"Unavailable independent mode {mode}"); continue; }
            Assert.True(TileWaterTraceMemoResultTests.Work(TileWaterTraceMemoResultTests.Check(field, q)) > 0);
            Assert.Equal(0, TileWaterTraceMemoResultTests.Work(TileWaterTraceMemoResultTests.Check(field, q)));
            Assert.True(environment.Set("nearest"));
        }
    }
}
