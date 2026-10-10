using System.Reflection;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Identity;

/// <summary>The legacy terrain digests are persisted identities. The golden values were derived independently by
/// hashing the canonical JSON each helper documents, so a change here is an identity migration.</summary>
public sealed class MapLegacyTerrainDigestTests
{
    [Fact]
    public void TerrainBlock_IsPinned()
    {
        var document = new MapDocument();
        document.Terrain.Seed = 7;
        document.Terrain.WaterLevel = -0.5f;
        document.Terrain.Biomes.Add(new MapBiomeBand { Biome = BiomeId.Meadow, BaseHeight = 1.5f });
        Assert.Equal("1a0d054ecfeed7fbcdbe3fb756862b44d59a67cb5d828139add5506e85c1ffef",
            MapLegacyTerrainDigest.TerrainBlock(document));
    }

    [Fact]
    public void SculptTile_IsPinned()
    {
        Assert.Equal("77dbce03857a630ce0e2eca6f66d973fd3987fba6813f8f717389ab554f9906f",
            MapLegacyTerrainDigest.SculptTile(Tile()));
    }

    [Fact]
    public void SculptTile_EqualsTheContentDigestSculptEntry()
    {
        // The content digest's entry digest is internal to MapDoc, so it is reached by name.
        MethodInfo entry = typeof(MapLegacyTerrainDigest).Assembly
            .GetType("KhaozEngine.MapDoc.Identity.MapAuthoredContentDigest", throwOnError: true)!
            .GetMethod("SculptDigest", BindingFlags.NonPublic | BindingFlags.Static)!;
        MapSculptTile tile = Tile();
        Assert.Equal((string)entry.Invoke(null, new object[] { tile })!, MapLegacyTerrainDigest.SculptTile(tile));
    }

    // Tile (1, -1) with two nonzero deltas.
    static MapSculptTile Tile()
    {
        var tile = new MapSculptTile(1, -1);
        tile[0, 0] = 1.5f;
        tile[1, 1] = -0.25f;
        return tile;
    }
}
