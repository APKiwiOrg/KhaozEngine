using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc
{
    /// <summary>Covers the two seams residency hands the rest of the engine: the
    /// <see cref="IChunkBuildGate"/> that keeps a chunk from building against a tile that has not arrived, and
    /// the <see cref="IPlacementSource"/> that carries a streamed tile's props to the render sink. Plus the
    /// teleport contract, which is the case the ordering rule alone does not cover.</summary>
    public class MapResidencySeamTests
    {
        const float Chunk = 60f;
        const float SculptCell = 2f;   // a 64 m sculpt span

        static MapResidencyConfig Sync(int load, int unload, int budget = 64) =>
            new MapResidencyConfig(load, unload, budget).Synchronous();

        /// <summary>A chunk coordinate whose whole sculpt-expanded footprint lies inside one document tile,
        /// so a gate assertion is about that tile and nothing else.</summary>
        static ChunkCoord ChunkDeepInside(int tileX, int tileZ)
        {
            float x = tileX * ResidencyFixture.Tile + 300f;
            float z = tileZ * ResidencyFixture.Tile + 300f;
            return ChunkGrid.CoordOf(x, z, Chunk);
        }

        [Fact]
        public void BuildGate_DefersAChunkOverANonResidentTile()
        {
            using MapDocumentSource source = ResidencyFixture.Source(ResidencyFixture.Square(4));
            var sink = new RecordingTileSink();
            using var residency = new MapTileResidency(source, Sync(1, 2), sink);
            IChunkBuildGate gate = residency.GateFor(Chunk, SculptCell);

            residency.Update(ResidencyFixture.At(0, 0));

            Assert.True(gate.CanBuild(ChunkDeepInside(0, 0)));     // resident
            Assert.True(gate.CanBuild(ChunkDeepInside(1, 1)));     // resident (diagonal)
            Assert.False(gate.CanBuild(ChunkDeepInside(4, 0)));    // occupied, not resident: DEFER

            residency.Update(ResidencyFixture.At(3, 0));           // walk over so tile 4 comes into range
            Assert.True(gate.CanBuild(ChunkDeepInside(4, 0)));
        }

        [Fact]
        public void BuildGate_AllowsAChunkOverAnAbsentTile()
        {
            // The one that matters most: gating on ABSENCE would deadlock the streamer over empty world, and a
            // sparse 100 km world is mostly empty. An absent tile is buildable, full stop.
            using MapDocumentSource source = ResidencyFixture.Source((0, 0));
            var sink = new RecordingTileSink();
            using var residency = new MapTileResidency(source, Sync(1, 2), sink);
            IChunkBuildGate gate = residency.GateFor(Chunk, SculptCell);

            residency.Update(ResidencyFixture.At(0, 0));

            Assert.True(gate.CanBuild(ChunkDeepInside(9, 9)));      // nothing authored out there, ever
            Assert.True(gate.CanBuild(ChunkDeepInside(-7, 3)));
            Assert.True(gate.CanBuild(ChunkDeepInside(0, 0)));
        }

        [Fact]
        public void BuildGate_WaitsForTheSculptOwningNeighbour()
        {
            // A sculpt tile belongs to the document tile containing its ORIGIN corner, so ground on a chunk's
            // low-X or low-Z edge can carry deltas owned by the neighbour on that side. The footprint is
            // expanded one sculpt span on those two sides before it is mapped to tiles, so the chunk waits.
            using MapDocumentSource source = ResidencyFixture.Source((0, 0), (-1, 0), (0, -1));
            var sink = new RecordingTileSink();
            using var residency = new MapTileResidency(source, Sync(load: 0, unload: 1), sink);
            IChunkBuildGate gate = residency.GateFor(Chunk, SculptCell);

            residency.Update(ResidencyFixture.At(0, 0));
            MapTileCoord only = Assert.Single(residency.Resident);
            Assert.Equal(new MapTileCoord(0, 0), only);

            // Chunk (0, 0) sits on tile (0, 0)'s low corner, so its expanded footprint reaches the two occupied
            // neighbours that own the sculpt there. Neither is resident: defer.
            Assert.False(gate.CanBuild(new ChunkCoord(0, 0)));
            // A chunk one sculpt span further in touches only the resident tile.
            Assert.True(gate.CanBuild(ChunkDeepInside(0, 0)));

            using var wide = new MapTileResidency(source, Sync(1, 2), new RecordingTileSink());
            wide.Update(ResidencyFixture.At(0, 0));
            Assert.True(wide.GateFor(Chunk, SculptCell).CanBuild(new ChunkCoord(0, 0)));
        }

        [Fact]
        public void BuildGate_DoesNotWaitOnATileTheChunkOnlyTouchesAtItsExclusiveMaxEdge()
        {
            // A chunk's max edge is EXCLUSIVE. A chunk whose edge lands exactly on a tile boundary does not
            // cover that next tile, and waiting on it would be a defer that clears only if that tile happens to
            // be in the ring. 512 / 60 is not an integer, so this needs a chunk size that divides the tile.
            using MapDocumentSource source = ResidencyFixture.Source((0, 0), (1, 0));
            var sink = new RecordingTileSink();
            using var residency = new MapTileResidency(source, Sync(load: 0, unload: 1), sink);
            IChunkBuildGate gate = residency.GateFor(chunkSize: 64f, SculptCell);

            residency.Update(ResidencyFixture.At(0, 0));

            // Chunk 7 spans [448, 512): its max edge is exactly tile (1, 0)'s origin, and tile (1, 0) is
            // occupied but not resident. The chunk still builds, because it does not reach into it.
            Assert.True(gate.CanBuild(new ChunkCoord(7, 4)));
            // Chunk 8 spans [512, 576), genuinely inside the non-resident tile: deferred.
            Assert.False(gate.CanBuild(new ChunkCoord(8, 4)));
        }

        [Fact]
        public void GateFor_TurnsPermissiveOnceTheResidencyIsDisposed()
        {
            // F6 regression. A disposed residency reports nothing resident, so the occupied-but-not-resident
            // test would otherwise refuse every occupied tile forever. Disposal turns the gate permissive
            // instead - the cleaner shutdown path is clearing TerrainStreamer.BuildGate, this is the safety net
            // for a caller that does not do that.
            using MapDocumentSource source = ResidencyFixture.Source(ResidencyFixture.Square(4));
            var sink = new RecordingTileSink();
            var residency = new MapTileResidency(source, Sync(1, 2), sink);
            IChunkBuildGate gate = residency.GateFor(Chunk, SculptCell);

            residency.Update(ResidencyFixture.At(0, 0));
            Assert.False(gate.CanBuild(ChunkDeepInside(4, 0)));   // occupied, never resident: deferred as usual

            residency.Dispose();

            Assert.True(gate.CanBuild(ChunkDeepInside(4, 0)));    // permissive once disposed
            Assert.True(gate.CanBuild(ChunkDeepInside(0, 0)));    // even the tile that WAS resident
        }

        [Fact]
        public void Teleport_PrimeAroundFillsTheRingBeforeAnyChunkAsks()
        {
            // Ordering residency before the streamer is necessary and NOT sufficient: async residency leaves a
            // discontinuous focus move asking for chunks whose tiles are many frames away, and those chunks
            // would build with no sculpt and no placements, which is a fall-through hazard. PrimeAround is the
            // half of the teleport contract that closes it.
            (int, int)[] tiles = ResidencyFixture.Square(1).Concat(ResidencyFixture.Square(1, 30, 30)).ToArray();
            using MapDocumentSource source = ResidencyFixture.Source(tiles);
            var sink = new RecordingTileSink();
            var dispatcher = new ManualTileDispatcher();
            using var residency = new MapTileResidency(source, new MapResidencyConfig(1, 2, 2), sink, dispatcher);
            IChunkBuildGate gate = residency.GateFor(Chunk, SculptCell);
            ChunkCoord arrival = ChunkDeepInside(30, 30);

            residency.PrimeAround(ResidencyFixture.At(0, 0));
            Assert.False(gate.CanBuild(arrival));

            // A plain Update at the destination requests the reads and returns. Nothing has landed, so the gate
            // holds the arrival chunk rather than letting it build on bare analytic terrain.
            residency.Update(ResidencyFixture.At(30, 30));
            Assert.False(gate.CanBuild(arrival));

            residency.PrimeAround(ResidencyFixture.At(30, 30));
            Assert.True(gate.CanBuild(arrival));
            Assert.Equal(9, residency.Resident.Count);                       // and the old ring is gone
            Assert.DoesNotContain(new MapTileCoord(0, 0), residency.Resident);
        }

        [Fact]
        public void PlacementsIn_ServesOnlyResidentTiles()
        {
            using MapDocumentSource source = ResidencyFixture.Source(ResidencyFixture.Square(2));
            var sink = new RecordingTileSink();
            using var residency = new MapTileResidency(source, Sync(1, 2), sink);
            residency.Update(ResidencyFixture.At(0, 0));

            var into = new List<PropPlacement>();
            residency.PlacementsIn(MapTileGrid.AreaOf(new MapTileCoord(0, 0), ResidencyFixture.Tile), into);
            PropPlacement one = Assert.Single(into);
            Assert.Equal(ResidencyFixture.Name(0, 0), one.Id);

            // Occupied, in the document, but two tiles out and therefore not resident: it serves nothing, which
            // is what makes the source a view of RESIDENCY rather than of the world.
            into.Clear();
            residency.PlacementsIn(MapTileGrid.AreaOf(new MapTileCoord(2, 0), ResidencyFixture.Tile), into);
            Assert.Empty(into);

            // A rect spanning the whole 5x5 authored block returns exactly the 9 resident tiles' placements.
            into.Clear();
            residency.PlacementsIn(new RectArea(-3f * ResidencyFixture.Tile, -3f * ResidencyFixture.Tile,
                                                3f * ResidencyFixture.Tile, 3f * ResidencyFixture.Tile), into);
            Assert.Equal(9, into.Count);
            Assert.Equal(9, into.Select(p => p.Id).Distinct().Count());
        }

        [Fact]
        public void PlacementsIn_IsHalfOpenSoAPartitionReproducesTheWhole()
        {
            MapDocument doc = ResidencyFixture.Doc((0, 0));
            doc.Placements.Add(new MapPlacement { Id = "edge", Kind = "edge", X = 100f, Z = 100f, Y = 0f });
            using MapDocumentSource source = MapDocumentSource.FromDocument(doc);
            var sink = new RecordingTileSink();
            using var residency = new MapTileResidency(source, Sync(0, 1), sink);
            residency.Update(ResidencyFixture.At(0, 0));

            var low = new List<PropPlacement>();
            var high = new List<PropPlacement>();
            residency.PlacementsIn(new RectArea(0f, 0f, 100f, 512f), low);     // max edge exactly on the prop
            residency.PlacementsIn(new RectArea(100f, 0f, 512f, 512f), high);

            Assert.DoesNotContain(low, p => p.Id == "edge");                   // exclusive max
            Assert.Contains(high, p => p.Id == "edge");                        // inclusive min
            Assert.Equal(2, low.Count + high.Count);                           // nothing lost, nothing doubled
        }

        [Fact]
        public void PlacementsIn_GroundSnapsThroughTheField()
        {
            MapDocument doc = ResidencyFixture.Doc();
            doc.Placements.Add(new MapPlacement { Id = "floating", Kind = "rock", X = 100f, Z = 100f, Y = null });
            using MapDocumentSource source = MapDocumentSource.FromDocument(doc);
            var field = new TerrainField(new TerrainConfig
            {
                GentleAmplitude = 0f,
                Biomes = new[]
                {
                    new BiomeBand
                    {
                        Start = float.NegativeInfinity, End = float.PositiveInfinity,
                        Biome = BiomeId.Meadow, BaseHeight = 12f, HillAmplitude = 0f,
                    },
                },
            });

            using var snapping = new MapTileResidency(source, Sync(0, 1), new RecordingTileSink(), dispatcher: null, field);
            snapping.Update(ResidencyFixture.At(0, 0));
            var into = new List<PropPlacement>();
            snapping.PlacementsIn(new RectArea(0f, 0f, 512f, 512f), into);
            Assert.Equal(12f, Assert.Single(into).Y, 3);

            // With no field there is no honest answer, so it says so rather than inventing a height.
            using var fieldless = new MapTileResidency(source, Sync(0, 1), new RecordingTileSink());
            fieldless.Update(ResidencyFixture.At(0, 0));
            Assert.Throws<InvalidOperationException>(() =>
                fieldless.PlacementsIn(new RectArea(0f, 0f, 512f, 512f), new List<PropPlacement>()));
        }

        [Fact]
        public void PlacementsIn_FollowsTheResidentSetAsItMoves()
        {
            // The staleness the whole seam exists to kill: what the source serves must track arrivals and
            // departures, not a set frozen when it was constructed.
            using MapDocumentSource source = ResidencyFixture.Source(ResidencyFixture.Square(4));
            var sink = new RecordingTileSink();
            using var residency = new MapTileResidency(source, Sync(0, 1), sink);
            RectArea far = MapTileGrid.AreaOf(new MapTileCoord(3, 0), ResidencyFixture.Tile);

            residency.Update(ResidencyFixture.At(0, 0));
            var into = new List<PropPlacement>();
            residency.PlacementsIn(far, into);
            Assert.Empty(into);

            residency.Update(ResidencyFixture.At(3, 0));
            residency.PlacementsIn(far, into);
            Assert.Single(into);

            residency.Update(ResidencyFixture.At(20, 20));
            into.Clear();
            residency.PlacementsIn(far, into);
            Assert.Empty(into);
        }

        [Fact]
        public void FromDocument_ReadTile_ClonesPlacementsAndSculptDeltasAwayFromTheCallersDocument()
        {
            // F8 regression. FromDocument's spatial index buckets the CALLER's own live document objects once.
            // Handing those straight out would break the immutability MapTileContent promises, since a
            // placement is a mutable class and a sculpt tile's Deltas is a mutable float[] that something like
            // TerrainSculpt.With stores by reference. OpenTiled never has this problem - every read parses fresh
            // objects off disk - so this is specific to the in-memory path.
            var doc = new MapDocument
            {
                Id = "clone-check", DisplayName = "Clone Check",
                Bounds = new MapBounds { MinX = -512f, MinZ = -512f, MaxX = 512f, MaxZ = 512f },
                TileSize = ResidencyFixture.Tile,
            };
            var placement = new MapPlacement { Id = "p", Kind = "rock", X = 10f, Z = 20f, Y = 0f };
            doc.Placements.Add(placement);
            var overrides = new MapTerrainOverrides(2f);
            overrides.SetDelta(4, 6, 1.5f);   // sculpt tile (0, 0) -> document tile (0, 0)
            doc.TerrainOverrides = overrides;

            using MapDocumentSource source = MapDocumentSource.FromDocument(doc);
            MapTileContent content = source.ReadTile(new MapTileCoord(0, 0));

            MapPlacement servedPlacement = Assert.Single(content.Placements);
            Assert.NotSame(placement, servedPlacement);
            Assert.Equal(placement.X, servedPlacement.X);

            MapSculptTile originalTile = doc.TerrainOverrides.Tiles[0];
            MapSculptTile servedTile = Assert.Single(content.SculptTiles);
            Assert.NotSame(originalTile, servedTile);
            Assert.NotSame(originalTile.Deltas, servedTile.Deltas);
            Assert.Equal(originalTile.Deltas, servedTile.Deltas);   // same VALUES, distinct array

            // Mutating the original document after the fact must not reach back into content already served.
            placement.X = 999f;
            originalTile.Deltas[0] = 42f;

            Assert.Equal(10f, servedPlacement.X);
            Assert.NotEqual(42f, servedTile.Deltas[0]);
        }

        [Fact]
        public void Invalidate_RereadsAResidentTileThroughTheFullLifecycle()
        {
            // Re-reading fires unload then load, because the bodies and sculpt a consumer built from the OLD
            // content have to go before the new content replaces it - and it has to see GENUINELY new content.
            // A re-saved tile is content-addressed (F2), so its file changes name on every edit: this needs a
            // real tiled directory rather than the in-memory fixture the rest of the suite uses, or Invalidate
            // would just re-derive the identical frozen snapshot and this test would not tell a re-read from a
            // no-op.
            TiledDocFixture.InDirectory(directory =>
            {
                MapDocument doc = TiledDocFixture.SampleDoc();
                MapDocumentFile.SaveTiled(doc, directory);

                using MapDocumentSource source = MapDocumentSource.OpenTiled(directory);
                var sink = new RecordingTileSink();
                using var residency = new MapTileResidency(source, Sync(1, 2), sink);
                var home = new MapTileCoord(0, 0);

                residency.Update(new Vector3(10f, 0f, 20f));   // sits inside document tile (0, 0)
                Assert.True(residency.TryGetContent(home, out MapTileContent before));
                Assert.Equal(2, before.Placements.Count);      // p-a, p-b
                sink.Reset();

                // The editor re-saves the SAME tile with a genuinely different placement set, which lands under
                // a new content hash - the exact case a frozen index cannot re-read without
                // MapDocumentSource.Refresh() (F2).
                doc.Placements.Add(new MapPlacement { Id = "p-new", Kind = "rock", X = 50f, Z = 60f });
                MapDocumentFile.SaveTiled(doc, directory);

                residency.Invalidate(home);

                Assert.Equal(home, Assert.Single(sink.Unloaded));
                Assert.Equal(home, Assert.Single(sink.LoadedCoords()));
                Assert.True(residency.TryGetContent(home, out MapTileContent after));
                Assert.NotSame(before, after);
                Assert.Equal(3, after.Placements.Count);
                Assert.Contains(after.Placements, p => p.Id == "p-new");

                // A tile that is not resident has nothing to re-read: it picks the new content up when it
                // arrives.
                sink.Reset();
                residency.Invalidate(new MapTileCoord(40, 40));
                Assert.Empty(sink.Unloaded);
                Assert.Empty(sink.Loaded);
            });
        }

        [Fact]
        public void UnloadAllAndDispose_DrainEverything()
        {
            using MapDocumentSource source = ResidencyFixture.Source(ResidencyFixture.Square(1));
            var sink = new RecordingTileSink();
            var residency = new MapTileResidency(source, Sync(1, 2), sink);

            residency.Update(ResidencyFixture.At(0, 0));
            Assert.Equal(9, residency.Resident.Count);

            residency.UnloadAll();
            Assert.Empty(residency.Resident);
            Assert.Equal(9, sink.Unloaded.Count);
            // Departures fire in ascending (Z, then X) order, so a teardown is reproducible.
            Assert.Equal(sink.Unloaded.OrderBy(c => c.Z).ThenBy(c => c.X).ToArray(), sink.Unloaded.ToArray());

            var into = new List<PropPlacement>();
            residency.PlacementsIn(new RectArea(-2048f, -2048f, 2048f, 2048f), into);
            Assert.Empty(into);   // the published snapshot went with it

            sink.Reset();
            residency.Update(ResidencyFixture.At(0, 0));
            Assert.Equal(9, sink.Loaded.Count);   // and it refills cleanly afterwards

            sink.Reset();
            residency.Dispose();
            Assert.Equal(9, sink.Unloaded.Count);   // Dispose drains through the sink too, not just UnloadAll
            Assert.Empty(residency.Resident);

            residency.Dispose();                  // idempotent
            Assert.Throws<ObjectDisposedException>(() => residency.Update(ResidencyFixture.At(0, 0)));
        }

        [Fact]
        public void ArrivingTileProps_ReachAnAlreadyBuiltChunk_ThroughARealStreamerInvalidate()
        {
            // F1 regression. The composition contract's third leg in practice: a REAL residency wired to a REAL
            // TerrainStreamer through a sink whose TileLoaded calls streamer.Invalidate on arrival - the pattern
            // the class doc names, and the one PlacementSourceLayerTests.StreamedPlacements_ReachTheSink (a fake
            // IPlacementSource, no MapTileResidency involved) cannot exercise. BuildGate is left null (the
            // un-gated default): the streamer already built the chunk once, against nothing, BEFORE the
            // document tile lands - the exact configuration the reviewer reproduced the failure in. If Publish()
            // ran after TileLoaded, the streamer's synchronous rebuild inside TileLoaded would read the snapshot
            // from BEFORE this tile arrived and the props would never draw.
            const float chunkSize = 64f;
            var doc = new MapDocument
            {
                Id = "streamer-wiring", DisplayName = "Streamer Wiring",
                Bounds = new MapBounds { MinX = -512f, MinZ = -512f, MaxX = 512f, MaxZ = 512f },
                TileSize = ResidencyFixture.Tile,
            };
            // An explicit Y: this residency is built with no TerrainField, and PlacementsIn throws for a
            // null-Y placement without one - orthogonal to what F1 is about. Kind (not Id) is what
            // MapTileResidency.PlacementsIn carries into PropPlacement.Id, so Kind is what the assertion below
            // checks.
            doc.Placements.Add(new MapPlacement { Id = "p-a", Kind = "p-a", X = 10f, Z = 20f, Y = 0f });
            using MapDocumentSource source = MapDocumentSource.FromDocument(doc);
            var dispatcher = new ManualTileDispatcher();
            var tileSink = new InvalidatingMapTileSink(source.Tiles.TileSize);
            // LoadRadius 0: the desired ring is exactly the focus tile, so exactly one read is in flight below.
            using var residency = new MapTileResidency(source, new MapResidencyConfig(0, 1, 8), tileSink, dispatcher);

            var chunkSink = new PropCapturingChunkSink(residency, chunkSize);
            var streamerConfig = new StreamerConfig(LoadRadius: 2, UnloadRadius: 3, MaxLoadsPerFrame: 8, ChunkSize: chunkSize);
            using var streamer = new TerrainStreamer(streamerConfig, chunkSink);
            tileSink.Streamer = streamer;   // BuildGate stays null: the un-gated case the finding calls out

            var focus = new Vector3(10f, 0f, 20f);   // exactly where placement p-a sits
            ChunkCoord homeChunk = ChunkGrid.CoordOf(focus.X, focus.Z, chunkSize);

            // The streamer runs once BEFORE the tile arrives: the chunk builds now, against nothing.
            streamer.Update(focus, 1f / 60f);
            Assert.True(chunkSink.Props.TryGetValue(homeChunk, out List<PropPlacement>? before));
            Assert.Empty(before!);

            // Residency requests the tile asynchronously and it is still in flight.
            residency.Update(focus);
            Assert.Equal(1, dispatcher.PendingCount);

            // Complete the read: Pump -> ApplyReady fires TileLoaded, which (through the glue sink) calls
            // streamer.Invalidate SYNCHRONOUSLY, rebuilding the already-loaded chunk and re-querying
            // residency.PlacementsIn right then, inside the same call.
            dispatcher.RunAll();
            residency.Update(focus);

            Assert.True(chunkSink.Props.TryGetValue(homeChunk, out List<PropPlacement>? after));
            Assert.Equal("p-a", Assert.Single(after!).Id);
        }
    }

    /// <summary>An <see cref="IMapTileSink"/> that glues residency arrivals to a real
    /// <see cref="TerrainStreamer"/>, exactly as the class doc's composition contract describes:
    /// <c>TileLoaded</c> re-invalidates the document tile's chunk footprint. <see cref="Streamer"/> is settable
    /// because the streamer's own sink needs residency (as an <see cref="IPlacementSource"/>) to exist first, so
    /// the two cannot be constructed in one line each.</summary>
    sealed class InvalidatingMapTileSink : IMapTileSink
    {
        readonly float _tileSize;
        public TerrainStreamer? Streamer;

        public InvalidatingMapTileSink(float tileSize) => _tileSize = tileSize;

        public void TileLoaded(MapTileCoord coord, MapTileContent content, ChunkRing ring) =>
            Streamer?.Invalidate(MapTileGrid.AreaOf(coord, _tileSize));

        public void TileRingChanged(MapTileCoord coord, MapTileContent content, ChunkRing ring) { }
        public void TileUnloaded(MapTileCoord coord) { }
    }

    /// <summary>An <see cref="IChunkSink"/> that builds by querying a live <see cref="IPlacementSource"/> (the
    /// production wiring an <see cref="IPlacementSource"/>-backed <c>PropLayer</c> uses) and records what each
    /// chunk got, so a test can see whether an arrival actually reached an already-loaded chunk.</summary>
    sealed class PropCapturingChunkSink : IChunkSink
    {
        readonly IPlacementSource _placements;
        readonly float _chunkSize;
        public readonly Dictionary<ChunkCoord, List<PropPlacement>> Props = new();

        public PropCapturingChunkSink(IPlacementSource placements, float chunkSize)
        {
            _placements = placements;
            _chunkSize = chunkSize;
        }

        public object Load(ChunkCoord coord, int lod, ChunkRing ring) => Build(coord);
        public void ReLod(ChunkCoord coord, object handle, int lod, ChunkRing ring) => Build(coord);
        public void Unload(ChunkCoord coord, object handle) => Props.Remove(coord);

        object Build(ChunkCoord coord)
        {
            var into = new List<PropPlacement>();
            _placements.PlacementsIn(ChunkGrid.AreaOf(coord, _chunkSize), into);
            Props[coord] = into;
            return coord;
        }
    }
}
