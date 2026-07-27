using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc
{
    /// <summary>The document tile grid and the in-memory spatial index: one floor rule shared with
    /// <see cref="ChunkGrid"/>, half-open tiling, and the partition-equals-whole property that makes a
    /// region-scoped query safe to substitute for a whole-document one.</summary>
    public class MapTileGridTests
    {
        [Fact]
        public void MapTileGrid_AgreesWithChunkGrid()
        {
            var rng = new Random(20260727);
            foreach (float size in new[] { 1f, 60f, 512f, 0.75f })
            {
                // Positive, negative, exactly-on-boundary, and fuzzed.
                var probes = new List<(float X, float Z)>
                {
                    (0f, 0f), (size, size), (-size, -size), (size - 0.001f, 0f),
                    (-0.0001f, -0.0001f), (3 * size, -2 * size),
                };
                for (int i = 0; i < 200; i++)
                    probes.Add(((float)(rng.NextDouble() * 8000 - 4000), (float)(rng.NextDouble() * 8000 - 4000)));

                foreach ((float x, float z) in probes)
                {
                    ChunkCoord chunk = ChunkGrid.CoordOf(x, z, size);
                    MapTileCoord tile = MapTileGrid.CoordOf(x, z, size);
                    Assert.Equal(chunk.X, tile.X);
                    Assert.Equal(chunk.Z, tile.Z);

                    RectArea area = MapTileGrid.AreaOf(tile, size);
                    Assert.Equal(ChunkGrid.AreaOf(chunk, size).MinX, area.MinX);
                    Assert.Equal(ChunkGrid.AreaOf(chunk, size).MaxZ, area.MaxZ);
                    Assert.Equal(ChunkGrid.CenterOf(chunk, size), MapTileGrid.CenterOf(tile, size));
                }
            }
        }

        [Fact]
        public void PlacementOnTileBoundary_BelongsToExactlyOneTile()
        {
            MapDocument doc = TiledDocFixture.SampleDoc();
            doc.Placements.Clear();
            doc.Placements.Add(new MapPlacement { Id = "on-max", Kind = "rock", X = 512f, Z = 512f });
            doc.Placements.Add(new MapPlacement { Id = "on-neg", Kind = "rock", X = -512f, Z = -512f });
            doc.Placements.Add(new MapPlacement { Id = "just-under", Kind = "rock", X = 511.9f, Z = 511.9f });

            MapSpatialIndex index = MapSpatialIndex.Build(doc);
            var seen = new List<string>();
            foreach (MapTileCoord tile in index.OccupiedTiles)
                seen.AddRange(index.PlacementsIn(tile).Select(p => p.Id));

            // Each id lands in exactly one bucket, and the half-open rule sends the max-edge one forward.
            Assert.Equal(3, seen.Count);
            Assert.Equal(3, seen.Distinct().Count());
            Assert.Contains(new MapTileCoord(1, 1), index.OccupiedTiles);
            Assert.Contains(new MapTileCoord(-1, -1), index.OccupiedTiles);
            Assert.Contains(new MapTileCoord(0, 0), index.OccupiedTiles);
            Assert.Equal("on-max", index.PlacementsIn(new MapTileCoord(1, 1)).Single().Id);
            Assert.Equal("on-neg", index.PlacementsIn(new MapTileCoord(-1, -1)).Single().Id);
        }

        [Fact]
        public void RegionScopedPlacements_PartitionEqualsWhole()
        {
            MapDocument doc = TiledDocFixture.SampleDoc();
            var registry = MapDocRegistry.CreateDefault();
            TerrainField field = MapRuntime.BuildField(doc, registry);

            IReadOnlyList<PropPlacement> whole = MapRuntime.BuildPlacements(doc, field);

            // A rect grid that deliberately does not divide the bounds evenly, the way the existing chunked
            // determinism test uses 30 m against a 200 m zone.
            const float step = 337f;
            var partitioned = new List<PropPlacement>();
            for (float x = doc.Bounds.MinX; x < doc.Bounds.MaxX; x += step)
                for (float z = doc.Bounds.MinZ; z < doc.Bounds.MaxZ; z += step)
                    partitioned.AddRange(MapRuntime.BuildPlacements(doc, field, new RectArea(x, z, x + step, z + step)));

            Assert.Equal(whole.Count, partitioned.Count);
            Assert.Equal(whole.Select(Key).OrderBy(k => k, StringComparer.Ordinal),
                         partitioned.Select(Key).OrderBy(k => k, StringComparer.Ordinal));
        }

        [Fact]
        public void TileScopedPlacements_UnionEqualsWhole()
        {
            MapDocument doc = TiledDocFixture.SampleDoc();
            var registry = MapDocRegistry.CreateDefault();
            TerrainField field = MapRuntime.BuildField(doc, registry);
            MapSpatialIndex index = MapSpatialIndex.Build(doc);

            var union = new List<PropPlacement>();
            foreach (MapTileCoord tile in index.OccupiedTiles)
                MapRuntime.BuildPlacements(index, field, tile, union);

            Assert.Equal(MapRuntime.BuildPlacements(doc, field).Select(Key).OrderBy(k => k, StringComparer.Ordinal),
                         union.Select(Key).OrderBy(k => k, StringComparer.Ordinal));

            // The allocating tile overload agrees with the appending one.
            MapTileCoord first = index.OccupiedTiles[0];
            Assert.Equal(MapRuntime.BuildPlacements(index, field, first).Select(Key),
                         BuildInto(index, field, first).Select(Key));
        }

        [Fact]
        public void OccupiedTiles_AreAscendingAndIndependentOfAuthoringOrder()
        {
            MapDocument forward = TiledDocFixture.SampleDoc();
            MapDocument reversed = TiledDocFixture.SampleDoc();
            reversed.Placements.Reverse();
            reversed.Spawns.Reverse();

            IReadOnlyList<MapTileCoord> a = MapSpatialIndex.Build(forward).OccupiedTiles;
            IReadOnlyList<MapTileCoord> b = MapSpatialIndex.Build(reversed).OccupiedTiles;
            Assert.Equal(a, b);

            for (int i = 1; i < a.Count; i++)
                Assert.True(a[i].Z > a[i - 1].Z || (a[i].Z == a[i - 1].Z && a[i].X > a[i - 1].X),
                            $"tiles are not ascending in (Z, X) at index {i}.");
        }

        [Fact]
        public void SculptTile_IsOwnedByTheDocumentTileHoldingItsOriginCorner()
        {
            // The 2 m cell size gives a 64 m sculpt span, so sculpt tile (-2, 0) starts at world (-128, 0)
            // and belongs to document tile (-1, 0) even though most of it sits in that tile's interior.
            Assert.Equal(new MapTileCoord(-1, 0), MapTileGrid.OwnerOfSculptTile(-2, 0, 2f, 512f));
            Assert.Equal(new MapTileCoord(0, 0), MapTileGrid.OwnerOfSculptTile(0, 0, 2f, 512f));

            // A cell size where the span does not divide the tile size evenly is still single-owner.
            Assert.Equal(new MapTileCoord(0, 0), MapTileGrid.OwnerOfSculptTile(20, 0, 0.75f, 512f));
            Assert.Equal(new MapTileCoord(1, 0), MapTileGrid.OwnerOfSculptTile(22, 0, 0.75f, 512f));
        }

        [Fact]
        public void RectQueries_AppendIntoACallerOwnedListAndRespectTheHalfOpenRect()
        {
            MapDocument doc = TiledDocFixture.SampleDoc();
            MapSpatialIndex index = MapSpatialIndex.Build(doc);

            var placements = new List<MapPlacement>();
            index.PlacementsIn(new RectArea(0f, 0f, 512f, 512f), placements);
            Assert.Equal(new[] { "p-a", "p-b" }, placements.Select(p => p.Id).OrderBy(i => i, StringComparer.Ordinal));

            var spawns = new List<MapSpawn>();
            index.SpawnsIn(new RectArea(-1024f, 0f, 0f, 512f), spawns);
            Assert.Equal("s-b", Assert.Single(spawns).Id);

            // p-a sits at (10, 20): a rect whose MIN edge is exactly there includes it, one whose MAX edge is
            // exactly there does not.
            var edge = new List<MapPlacement>();
            index.PlacementsIn(new RectArea(10f, 20f, 20f, 30f), edge);
            Assert.Equal("p-a", Assert.Single(edge).Id);
            edge.Clear();
            index.PlacementsIn(new RectArea(0f, 0f, 10f, 20f), edge);
            Assert.Empty(edge);
        }

        static List<PropPlacement> BuildInto(MapSpatialIndex index, TerrainField field, MapTileCoord tile)
        {
            var into = new List<PropPlacement>();
            MapRuntime.BuildPlacements(index, field, tile, into);
            return into;
        }

        static string Key(PropPlacement p) => $"{p.Id}|{p.X:F4}|{p.Y:F4}|{p.Z:F4}|{p.Scale:F4}|{p.Yaw:F4}|{p.Variant}";
    }
}
