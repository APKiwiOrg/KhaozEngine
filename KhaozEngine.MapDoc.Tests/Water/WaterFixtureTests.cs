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
    [InlineData("sea-connected-cave")]
    [InlineData("sea-connected-cave-dry-portal")]
    [InlineData("sea-closed-cave")]
    [InlineData("sea-connected-cave-alias-endpoints")]
    [InlineData("sinkhole-below-surface")]
    [InlineData("sinkhole-at-surface")]
    [InlineData("sinkhole-above-surface")]
    public void SharedFixtures_PassR2Coverage(string world) =>
        Assert.Empty(MapSpaceCoverageValidator.Validate(MapScopedSurfaces.CompleteView(SharedWorld(world).Surfaces)));

    static MapDocument SharedWorld(string world) => world switch
    {
        "ocean-over-caves" => WaterFixtures.OceanOverCaves(),
        "sea-connected-cave" => WaterFixtures.SeaConnectedCave(portalBottomY: -5f),
        "sea-connected-cave-dry-portal" => WaterFixtures.SeaConnectedCave(portalBottomY: 1f),
        "sea-closed-cave" => WaterFixtures.SeaConnectedCave(portalBottomY: null),
        "sea-connected-cave-alias-endpoints" => WaterFixtures.SeaConnectedCaveThroughAliases("sea", "grotto"),
        "sinkhole-below-surface" => WaterFixtures.SinkholeCave(openingY: -10f),
        "sinkhole-at-surface" => WaterFixtures.SinkholeCave(openingY: 0f),
        "sinkhole-above-surface" => WaterFixtures.SinkholeCave(openingY: 1f),
        _ => throw new ArgumentException("unknown shared water world", nameof(world)),
    };
}
