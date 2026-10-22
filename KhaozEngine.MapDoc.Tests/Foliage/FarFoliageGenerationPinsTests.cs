using System;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class FarFoliageGenerationPinsTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(32000, -32000)]
    [InlineData(-32000, 32000)]
    public void FarFoliageGeneration_MatchesItsOwnReleasedLocationPins(int x, int z)
    {
        var fixture = FarFoliageGenerationFixtures.Create(x, z);
        var full = FarFoliageGenerationFixtures.Generate(fixture);
        Assert.Equal(MapFoliageStatus.Complete, full.Status);
        Assert.Equal(FarFoliageGenerationFixtures.Pins(x), FarFoliageGenerationFixtures.Capture(full));
        using var document = MapDocumentSource.FromDocument(fixture.Document);
        var source = MapSceneSource.Capture(document, fixture.Assets).Source!;
        var frame = WorldFrame.Nearest(x, z);
        var scope = new MapSceneScope(new(frame, new(-128), new(128), -64, 64,
            Enum.GetValues<MapSurfaceRole>(), null, new()), new(x - 128, -64, z - 128, x + 128, 64, z + 128), MapSceneChannels.All);
        var window = MapFoliageGenerationContext.CaptureWindow(source, scope);
        Assert.Equal(MapFoliageStatus.Complete, window.Status);
        var actual = MapFoliageDistribution.Generate(window.Context!, FarFoliageGenerationFixtures.Layer, new(0, 0));
        Assert.Equal(MapFoliageStatus.Complete, actual.Status);
        Assert.Equal(FarFoliageGenerationFixtures.Pins(x), FarFoliageGenerationFixtures.Capture(actual));
        Assert.Equal(full.Samples.Count, actual.Samples.Count);
    }
}
