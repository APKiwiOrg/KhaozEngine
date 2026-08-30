using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;
using Xunit;
using TiledFixture = KhaozEngine.Tests.MapDoc.TiledDocFixture;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>What actually happens when <see cref="MapEditorOptions.RenderDistance"/> reaches past the slice of a
    /// tiled document <see cref="MapEditorOptions.EditorWindowRadius"/> loaded (#363). The two defaults fit today with
    /// room to spare, and the issue was raised because nothing pins what the boundary does, so this is the boundary
    /// pinned: the streamer neither clamps its ring to the loaded window nor throws at its edge. It streams the far
    /// field from the composed <see cref="TerrainField"/>, which outside the window is the pure analytic base, because
    /// the authored sculpt tiles that would have modified it live in document tiles this window never read. The cost
    /// of over-reaching is therefore unauthored ground in the far ring, not a void and not a crash.</summary>
    public class MapEditorWindowReachTests
    {
        // Records loads so the ring can be measured with no GPU. ReLod and Unload are no-ops: this test pumps a
        // stationary viewer, so neither fires.
        sealed class RecordingSink : IChunkSink
        {
            public readonly List<ChunkCoord> Loads = new();
            public object Load(ChunkCoord coord, int lod, ChunkRing ring) { Loads.Add(coord); return new object(); }
            public void ReLod(ChunkCoord coord, object handle, int lod, ChunkRing ring) { }
            public void Unload(ChunkCoord coord, object handle) { }
        }

        // Every chunk coordinate inside the Euclidean disk of this radius around the origin, which is the ring the
        // streamer is meant to end up holding.
        static HashSet<ChunkCoord> ExpectedDisk(int radius)
        {
            var disk = new HashSet<ChunkCoord>();
            for (int x = -radius; x <= radius; x++)
                for (int z = -radius; z <= radius; z++)
                    if (x * x + z * z <= radius * radius) disk.Add(new ChunkCoord(x, z));
            return disk;
        }

        [Fact]
        public void RenderDistancePastTheLoadedWindow_StreamsUnauthoredGround_NeitherClampedNorThrown()
        {
            TiledFixture.InDirectory(dir =>
            {
                MapDocumentFile.SaveTiled(TiledFixture.SampleDoc(), dir);

                // Windowed at radius 0: the loaded slice is the single document tile holding the bounds centre.
                var options = new MapDocumentLoadOptions { Registry = MapDocRegistry.CreateDefault() };
                MapDocument windowedDoc = MapDocumentWindowing.Load(dir, options,
                    wholeWorldTileLimit: 1, windowRadius: 0, out bool windowed, out MapTileRect? window);
                Assert.True(windowed);
                Assert.NotNull(window);
                Assert.Equal(1, window!.Value.Count);

                var registry = MapDocRegistry.CreateDefault();
                TerrainField windowedField = MapRuntime.BuildField(windowedDoc, registry);
                TerrainField wholeField = MapRuntime.BuildField(MapDocumentFile.LoadTiled(dir, options), registry);

                // The window's own sculpt tile came with it, so inside the window the two fields agree exactly.
                Assert.Equal(wholeField.SampleHeight(8f, 12f), windowedField.SampleHeight(8f, 12f), 4);

                // The sculpt tile owned by document tile (-1, 0) did not, so outside the window the windowed field
                // is the analytic base with the authored delta missing. Quiet degradation, not an exception, and
                // exactly the fixture's 1.25 m delta at that sculpt cell's centre.
                Assert.Equal(1.25f, wholeField.SampleHeight(-80f, 20f) - windowedField.SampleHeight(-80f, 20f), 4);

                // The editor's own streamer wiring: the default render distance, synchronous like ViewportWorld's.
                StreamerConfig config = RenderDistanceProfile.Default.ToStreamerConfig().Synchronous();
                var sink = new RecordingSink();
                using var streamer = new TerrainStreamer(config, sink);
                HashSet<ChunkCoord> disk = ExpectedDisk(config.OuterRadius);
                for (int frame = 0; frame < 1000 && sink.Loads.Count < disk.Count; frame++)
                    streamer.Update(Vector3.Zero, 1f / 60f);

                // No clamp: the ring is exactly the disk the profile asked for, with nothing dropped at the window
                // edge. No throw either, or the pump above would never have finished.
                Assert.Equal(disk, new HashSet<ChunkCoord>(sink.Loads));

                // And it genuinely over-reaches: chunks land outside the loaded window's world rect.
                RectArea loaded = MapTileGrid.AreaOf(window.Value.Min, windowedDoc.TileSize);
                int outside = 0;
                foreach (ChunkCoord coord in sink.Loads)
                {
                    Vector2 centre = ChunkGrid.CenterOf(coord, config.ChunkSize);
                    if (centre.X < loaded.MinX || centre.X >= loaded.MaxX || centre.Y < loaded.MinZ || centre.Y >= loaded.MaxZ)
                        outside++;
                }
                Assert.True(outside > 0, $"expected chunks past the loaded window, got {outside} of {sink.Loads.Count}");
            });
        }
    }
}
