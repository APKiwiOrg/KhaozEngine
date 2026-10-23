using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapWindowFoliageReachTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdjacentGenerationChunk_ReachingWindowMatchesCompleteGeneration(bool tiled)
    {
        var fixture = FoliageFixtures.Create();
        using var saved = WindowFoliageFixtures.Scene(fixture);
        var source = NativeSceneFixtures.Open(saved, tiled);
        var scope = WindowFoliageFixtures.Scope(60.9f, -4, 60.95f, 0);
        var captured = MapFoliageGenerationContext.CaptureWindow(source, scope);
        Assert.Equal(MapFoliageStatus.Complete, captured.Status);
        var expected = MapFoliageDistribution.Generate(FoliageFixtures.Context(fixture), "cover-a", new(1, 0));
        Assert.NotEmpty(expected.Samples);
        var actual = MapFoliageDistribution.Generate(captured.Context!, "cover-a", new(1, 0));
        MapWindowFoliageTests.Same(expected, actual);
    }

    [Fact]
    public void AdjacentGenerationChunks_ConsumeTheDescriptorAllowance()
    {
        using var saved = WindowFoliageFixtures.Scene(FoliageFixtures.Create());
        var source = NativeSceneFixtures.Open(saved, false);
        var result = MapFoliageGenerationContext.CaptureWindow(source,
            WindowFoliageFixtures.Scope(61.1f, -4, 61.6f, 0), new(MaxChunkDescriptors: 1));
        Assert.Equal(MapFoliageStatus.CapacityExceeded, result.Status);
        Assert.Null(result.Context);
    }
}
