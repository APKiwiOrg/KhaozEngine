using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Physics;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

/// <summary>Persisted identities pinned to literals derived outside the engine, from their documented canonical forms
/// and the fixture definitions. A test that takes an input from the engine says so. A change here is an identity
/// migration.</summary>
public class MapPersistedIdentityTests
{
    static readonly MapWorldGrids Grids = new(64f, 1, 4, Vector2.Zero);
    static readonly MapNavTileOptions Options = new(2f, "profile-a", "legacy-stepper");

    [Fact]
    public void DoorwayPlacementDigest_IsPinned()
    {
        // Over the doorway's id, numeric id 1, asset id, the SHA-256 of its collider bytes, no selection, the float bits
        // of (0.23, 0, 0.17), yaw 0.371, scale 1.137 and raise 0, and the interaction policy hash.
        var g = MapPlacementShapes.Resolve(NativeWorldFixtures.Doorway(0.371f, 1.137f).Resolved)
            .Single(p => p.PlacementId == "doorway");
        Assert.Equal("217765858ec4d9176bdb2704cf5a744802fa2ad2b9c8ef0f93ae2359295534a1", g.Digest);
    }

    [Fact]
    public void StackedCaveUpperCeilingChunkDigest_IsPinned()
    {
        // 72 faces of 36 flat cells at y 6.5, split south-west to north-east, emitted as (A, C, B) about anchor
        // (3, 7, 3), each owned by its slot cell and parent triangle with the ceiling role.
        var set = MapTerrainPhysics.Compile(NativeWorldFixtures.StackedCave().View, new MapTerrainChunkPolicy());
        Assert.Equal("1414db9235eb9409c416ce533d4b2041e41350006d0ff83e49cc60d2fae7237f",
            set.Chunks.Single(c => c.ChunkId == "cave-upper-ceiling/0,0/0,0,64").Digest);
    }

    [Fact]
    public void LegacyTerrainIdentity_IsPinned()
    {
        // Composed from the terrain block and sculpt tile digests MapLegacyTerrainDigestTests pins.
        Assert.Equal("0cad1729a54d81cdcd40981d6217a4f3c8973dfa3f79f7f9db8cd5ef6048d403",
            NativeWorldFixtures.BuildLegacyPinnedTerrain().LegacyTerrainIdentity);
    }

    [Fact]
    public void NavGeometryDigestOverAStaticAndASculptTile_AndItsSeam_ArePinned()
    {
        var world = NativeWorldFixtures.BuildLegacyPinnedTerrain();
        var tiles = MapNavTiling.Partition(world, Grids, Options);
        Assert.Equal(new[] { new MapNavTileCoord(0, -1), new MapNavTileCoord(1, -1) }, tiles.Select(t => t.Coord));
        // Tile (0, -1) holds the crate's placement digest, the pinned terrain block and sculpt tile (1, -1).
        Assert.Equal("4f3c9ed5ebac482552989d05ed2bd63204514bde73c03d2f3d29f1daf06ad311", tiles[0].GeometryDigest);
        // Tile (1, -1) holds the terrain block alone. Their seam is the edge x 64 from z -64 to 0, with a 2 m margin.
        Assert.Equal("e253ae086cdc8ba1d83694e1b073a25ff831af4a0b4293320e7225abb149e601", tiles[1].GeometryDigest);
        var seam = Assert.Single(MapNavTiling.Seams(tiles, world, Options));
        Assert.Equal("52f9a46adcd544b1ddb62b6375c8bb2c22f0ba4014435bb589dfc275142746b3", seam.Digest);
    }

    [Fact]
    public void StackedCaveBuildHash_IsPinned()
    {
        // The canonical composition, the crate's placement digest and the upper ceiling chunk digest were derived outside
        // the engine. The authored hash and the other seven chunk digests are inputs read from the engine once, so this
        // pin guards the composition and catches drift in every input.
        var world = NativeWorldFixtures.BuildStackedCave();
        Assert.Equal("2da51aa2076566e84e80dcf1b7b5edfa67a572b576d8fa7f0e3b66f9626e7ccd", world.AuthoredHash);
        Assert.Equal("dd60fcdfec6b014115f46c6d7766b0ab1d8c22fc1e0f51545d8d12f3c574c057", world.BuildHash);
    }
}
