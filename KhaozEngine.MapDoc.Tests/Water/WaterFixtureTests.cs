using System;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Water;

public class WaterFixtureTests
{
    // Editor publication runs R2 coverage over the complete view, so every shared world must pass it to take edits.
    [Theory]
    [InlineData("ocean-over-caves")]
    public void SharedFixtures_PassR2Coverage(string world) =>
        Assert.Empty(MapSpaceCoverageValidator.Validate(MapScopedSurfaces.CompleteView(SharedWorld(world).Surfaces)));

    static MapDocument SharedWorld(string world) => world switch
    {
        "ocean-over-caves" => WaterFixtures.OceanOverCaves(),
        _ => throw new ArgumentException("unknown shared water world", nameof(world)),
    };
}
