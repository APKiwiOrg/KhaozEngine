using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc
{
    /// <summary>Crash consistency and the memory claim. Every crash case fakes the kill by aborting the
    /// writer at a named step, so it runs headless and deterministically with no process kill, and each
    /// asserts that the document still LOADS and equals the expected whole version, which is the invariant,
    /// rather than asserting on files.</summary>
    public class MapTiledDurabilityTests
    {
        static MapDocumentSaveOptions ThrowAt(MapTiledSaveStep step) => new()
        {
            OnStep = s => { if (s == step) throw new InvalidOperationException($"simulated crash at {s}."); },
        };

        /// <summary>Saves the fixture, loads it whole, and edits one placement so exactly one tile's content
        /// hash changes. Returns the edited document, which is the "new version" every crash case races.</summary>
        static MapDocument SaveThenEdit(string directory)
        {
            MapDocumentFile.SaveTiled(TiledDocFixture.SampleDoc(), directory);
            MapDocument edited = MapDocumentFile.LoadTiled(directory);
            edited.Placements.Single(p => p.Id == "p-a").Yaw = 2.75f;
            return edited;
        }

        /// <summary>The world identity of the edited version, computed from a MONOLITHIC document so it comes
        /// from content rather than from a stored index. That is the honest expectation for "the new version
        /// landed", and it cross-checks that the two forms agree on the edited world too.</summary>
        static string EditedWorldHash()
        {
            MapDocument doc = TiledDocFixture.SampleDoc();
            doc.Placements.Single(p => p.Id == "p-a").Yaw = 2.75f;
            return MapDocumentHash.OfWorld(doc);
        }

        [Fact]
        public void CrashBeforeFirstTileWrite_LeavesPreviousVersionIntact()
        {
            TiledDocFixture.InDirectory(directory =>
            {
                MapDocument edited = SaveThenEdit(directory);
                Assert.Throws<InvalidOperationException>(
                    () => MapDocumentFile.SaveTiled(edited, directory, null, ThrowAt(MapTiledSaveStep.BeforeTileWrite)));

                MapDocument back = MapDocumentFile.LoadTiled(directory);
                Assert.Equal(0.5f, back.Placements.Single(p => p.Id == "p-a").Yaw);
                Assert.Equal(MapDocumentHash.OfWorld(TiledDocFixture.SampleDoc()), MapDocumentHash.OfWorld(back));
            });
        }

        [Fact]
        public void CrashBeforeManifestRename_LeavesPreviousVersionIntact()
        {
            TiledDocFixture.InDirectory(directory =>
            {
                MapDocument edited = SaveThenEdit(directory);
                Assert.Throws<InvalidOperationException>(
                    () => MapDocumentFile.SaveTiled(edited, directory, null, ThrowAt(MapTiledSaveStep.BeforeManifestRename)));

                // New bytes are on disk at names no live manifest references, and map.json is untouched.
                MapDocument back = MapDocumentFile.LoadTiled(directory);
                Assert.Equal(0.5f, back.Placements.Single(p => p.Id == "p-a").Yaw);
                Assert.Equal(MapDocumentHash.OfWorld(TiledDocFixture.SampleDoc()), MapDocumentHash.OfWorld(back));
                Assert.True(File.Exists(Path.Combine(directory, "map.json.tmp")));
            });
        }

        [Fact]
        public void CrashDuringSweep_LeavesNewVersionIntact()
        {
            TiledDocFixture.InDirectory(directory =>
            {
                MapDocument edited = SaveThenEdit(directory);
                Assert.Throws<InvalidOperationException>(
                    () => MapDocumentFile.SaveTiled(edited, directory, null, ThrowAt(MapTiledSaveStep.DuringSweep)));

                MapDocument back = MapDocumentFile.LoadTiled(directory);
                Assert.Equal(2.75f, back.Placements.Single(p => p.Id == "p-a").Yaw);
                Assert.Equal(EditedWorldHash(), MapDocumentHash.OfWorld(back));

                // The new version is live and correct, and some superseded files linger.
                Assert.Contains(MapDocumentFile.VerifyTiled(directory), e => e.Contains("orphan", StringComparison.Ordinal));
            });
        }

        [Fact]
        public void ResaveAfterCrash_SweepsOrphansAndTmpFiles()
        {
            TiledDocFixture.InDirectory(directory =>
            {
                MapDocument edited = SaveThenEdit(directory);
                Assert.Throws<InvalidOperationException>(
                    () => MapDocumentFile.SaveTiled(edited, directory, null, ThrowAt(MapTiledSaveStep.BeforeManifestRename)));

                IReadOnlyList<string> dirty = MapDocumentFile.VerifyTiled(directory);
                Assert.Contains(dirty, e => e.Contains("map.json.tmp", StringComparison.Ordinal));
                Assert.Contains(dirty, e => e.Contains("orphan", StringComparison.Ordinal));

                MapDocumentFile.SaveTiled(edited, directory);
                Assert.Empty(MapDocumentFile.VerifyTiled(directory));
                Assert.Equal(2.75f, MapDocumentFile.LoadTiled(directory).Placements.Single(p => p.Id == "p-a").Yaw);
            });
        }

        [Fact]
        public void ResaveWithUnreadableManifest_RewritesEverythingAndSkipsTheSweep()
        {
            TiledDocFixture.InDirectory(directory =>
            {
                MapDocument edited = SaveThenEdit(directory);
                IReadOnlyList<string> before = TiledDocFixture.TileFiles(directory);
                File.WriteAllText(Path.Combine(directory, "map.json"), "{ not json at all");

                MapDocumentFile.SaveTiled(edited, directory);

                // The directory is correct...
                MapDocument back = MapDocumentFile.LoadTiled(directory);
                Assert.Equal(2.75f, back.Placements.Single(p => p.Id == "p-a").Yaw);
                Assert.Equal(EditedWorldHash(), MapDocumentHash.OfWorld(back));

                // ...and still carries what the unreadable manifest could not account for, deliberately:
                // deleting files on the authority of a manifest that failed to parse is how a bad save turns
                // into a lost world.
                IReadOnlyList<string> after = TiledDocFixture.TileFiles(directory);
                Assert.All(before, f => Assert.Contains(f, after));
                Assert.Contains(MapDocumentFile.VerifyTiled(directory), e => e.Contains("orphan", StringComparison.Ordinal));
            });
        }

        [Fact]
        public void UnchangedTile_IsNotRewritten()
        {
            TiledDocFixture.InDirectory(directory =>
            {
                MapDocument edited = SaveThenEdit(directory);
                var stamps = TiledDocFixture.TileFiles(directory)
                    .ToDictionary(f => f, f => File.GetLastWriteTimeUtc(Path.Combine(directory, f)));

                MapDocumentFile.SaveTiled(edited, directory);

                IReadOnlyList<string> after = TiledDocFixture.TileFiles(directory);
                Assert.Equal(stamps.Count, after.Count);

                // Exactly one tile changed, so exactly one file name changed and every survivor is untouched.
                string[] survivors = after.Where(stamps.ContainsKey).ToArray();
                Assert.Equal(stamps.Count - 1, survivors.Length);
                foreach (string file in survivors)
                    Assert.Equal(stamps[file], File.GetLastWriteTimeUtc(Path.Combine(directory, file)));
            });
        }

        [Fact]
        public void VerifyTiled_ReportsAHandEditedTile()
        {
            TiledDocFixture.InDirectory(directory =>
            {
                MapDocumentFile.SaveTiled(TiledDocFixture.SampleDoc(), directory);
                Assert.Empty(MapDocumentFile.VerifyTiled(directory));

                string tile = Path.Combine(directory, TiledDocFixture.TileFiles(directory)
                    .Single(f => f.Contains("t_0_0.", StringComparison.Ordinal)));
                JsonNode node = JsonNode.Parse(File.ReadAllText(tile))!;
                node["placements"]![0]!["yaw"] = 9.5f;
                File.WriteAllText(tile, node.ToJsonString());

                Assert.Contains(MapDocumentFile.VerifyTiled(directory),
                                e => e.Contains("hashes to", StringComparison.Ordinal));

                // Load is trusting by default and strict on request.
                Assert.Equal(9.5f, MapDocumentFile.LoadTiled(directory).Placements.Single(p => p.Id == "p-a").Yaw);
                Assert.Throws<MapDocumentException>(() =>
                    MapDocumentFile.LoadTiled(directory, new MapDocumentLoadOptions { VerifyTileHashes = true }));
            });
        }

        [Fact]
        public void VerifyTiled_ReportsOrphansAndTmpFiles()
        {
            TiledDocFixture.InDirectory(directory =>
            {
                MapDocumentFile.SaveTiled(TiledDocFixture.SampleDoc(), directory);
                string shard = Path.Combine(directory, "tiles", "s_0_0");
                File.WriteAllText(Path.Combine(shard, "t_0_0.deadbeef.json"), "{}");
                File.WriteAllText(Path.Combine(shard, "t_0_0.deadbeef.json.tmp"), "{}");
                File.WriteAllText(Path.Combine(directory, "map.json.tmp"), "{}");

                IReadOnlyList<string> report = MapDocumentFile.VerifyTiled(directory);
                Assert.Contains(report, e => e.Contains("orphan", StringComparison.Ordinal));
                Assert.Equal(2, report.Count(e => e.Contains("stray temp file", StringComparison.Ordinal)));
            });
        }

        [Fact]
        public void VerifyTiled_ReportsAnUnreadableManifestInsteadOfThrowing()
        {
            TiledDocFixture.InDirectory(directory =>
            {
                File.WriteAllText(Path.Combine(directory, "map.json"), "{ not json");
                Assert.NotEmpty(MapDocumentFile.VerifyTiled(directory));
            });
        }

        [Fact]
        public void PowerFailDurability_WritesTheSameDocument()
        {
            TiledDocFixture.InDirectory(directory =>
            {
                MapDocument doc = TiledDocFixture.SampleDoc();
                MapDocumentFile.SaveTiled(doc, directory, null,
                                          new MapDocumentSaveOptions { Durability = MapSaveDurability.PowerFail });
                Assert.Equal(MapDocumentHash.OfWorld(doc), MapDocumentHash.OfWorld(MapDocumentFile.LoadTiled(directory)));
                Assert.Empty(MapDocumentFile.VerifyTiled(directory));
            });
        }

        /// <summary>Locks the fix for the durability claim that used to be an unconditional silent no-op: on
        /// Unix, <see cref="UnixDirectorySync.Flush"/> must actually fsync, not merely fail to throw. A bare
        /// "did not throw" assertion would have passed on the old, permanently-broken implementation too
        /// (every failure there was swallowed), so this checks the return value that says whether the syscall
        /// genuinely ran.</summary>
        [Fact]
        public void UnixDirectorySync_FlushSucceedsOnARealDirectory()
        {
            if (OperatingSystem.IsWindows()) return;   // no primitive there, every call site already gates on it
            TiledDocFixture.InDirectory(directory => Assert.True(UnixDirectorySync.Flush(directory)));
        }

        [Fact]
        public void SaveTo_DoesNotBufferTheDocument()
        {
            // The measured failure was a contiguous-buffer element count, not a memory wall: Serialize wrote
            // UTF-8 into one pooled array that grew by doubling and then transcoded the whole thing to a
            // string. This writes a document far past any sane buffer straight through the writer.
            MapDocument doc = BigSculptDoc(tileCount: 18_000);

            var sink = new CountingStream();
            long before = GC.GetTotalMemory(forceFullCollection: true);
            MapDocumentFile.SaveTo(doc, sink);
            long after = GC.GetTotalMemory(forceFullCollection: true);

            Assert.True(sink.Written > 200L * 1024 * 1024,
                        $"the fixture must exceed 200 MB serialized to be a real test, wrote {sink.Written} bytes.");
            Assert.True(after - before < 32L * 1024 * 1024,
                        $"the write retained {after - before} bytes for a {sink.Written}-byte document.");
            GC.KeepAlive(doc);
        }

        [Fact]
        public void SaveTiled_SerializationBufferStaysFlatAsTileCountDoubles()
        {
            // What is flat is the SERIALIZATION buffer, one tile at a time, not the process: step 1 validates
            // and step 2 buckets the whole document, both of which hold everything. So this measures the heap
            // DELTA across the tile-writing phase itself, and asserts it does not grow with tile count while
            // total allocation does.
            (long delta, long allocated, long bytes) small = MeasureTiledWrite(4096);
            (long delta, long allocated, long bytes) large = MeasureTiledWrite(8192);

            const long Bound = 32L * 1024 * 1024;
            // The bound only discriminates if a buffering writer would blow it, so the fixture has to be
            // several times the bound. At 8,192 tiles it is roughly 90 MB against a 32 MB bound.
            Assert.True(large.bytes > 2 * Bound,
                        $"the fixture must be far bigger than the bound to discriminate, wrote {large.bytes} bytes.");
            Assert.True(Math.Abs(small.delta) < Bound, $"4096 tiles moved the heap by {small.delta} bytes.");
            Assert.True(Math.Abs(large.delta) < Bound, $"8192 tiles moved the heap by {large.delta} bytes.");
            Assert.True(large.allocated > small.allocated * 1.5,
                        $"allocation should scale with tile count: {small.allocated} then {large.allocated}.");
        }

        static (long Delta, long Allocated, long Bytes) MeasureTiledWrite(int tileCount)
        {
            MapDocument doc = ManyTileDoc(tileCount);
            long first = -1;
            var options = new MapDocumentSaveOptions
            {
                // Sampled once, at the first tile file: after validation and bucketing, both of which hold
                // the whole document by construction and are not what this measures. A forced collection,
                // because the interesting quantity is LIVE bytes and the suite runs in parallel, so an
                // uncollected sample would mostly report another test's gen0 churn.
                OnStep = step =>
                {
                    if (step == MapTiledSaveStep.BeforeTileWrite && first < 0)
                        first = GC.GetTotalMemory(forceFullCollection: true);
                },
            };

            // Thread-local, so a parallel test's allocations cannot bleed into the scaling assertion.
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long delta = 0, bytes = 0;
            TiledDocFixture.InDirectory(directory =>
            {
                MapDocumentFile.SaveTiled(doc, directory, null, options);
                delta = GC.GetTotalMemory(forceFullCollection: true) - first;
                bytes = TiledDocFixture.TileFiles(directory).Sum(f => new FileInfo(Path.Combine(directory, f)).Length);
            });
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            GC.KeepAlive(doc);
            return (delta, allocated, bytes);
        }

        /// <summary>A document whose serialized form is hundreds of megabytes: one sculpt tile per 16 m span
        /// in a long row, every delta a three-character number so the line count is the size.</summary>
        static MapDocument BigSculptDoc(int tileCount)
        {
            float span = TerrainSculpt.TileSize * MapTerrainOverrides.DefaultCellSize;
            var doc = new MapDocument
            {
                Id = "big-zone",
                Bounds = new MapBounds { MinX = -1f, MinZ = -1f, MaxX = (tileCount + 1) * span, MaxZ = span },
            };
            var overrides = new MapTerrainOverrides();
            for (int i = 0; i < tileCount; i++)
            {
                var tile = new MapSculptTile(i, 0);
                Array.Fill(tile.Deltas, 0.5f);
                overrides.PutTile(tile);
            }
            doc.TerrainOverrides = overrides;
            return doc;
        }

        /// <summary>A document occupying <paramref name="tileCount"/> document tiles, one placement and one
        /// sculpt tile each, so the tile COUNT is the variable and each tile file is about 14 KB. The sculpt
        /// tile is what makes the measurement discriminating: at 8,192 tiles the document serializes to well
        /// over 100 MB, so a writer that buffered the whole thing could not hide inside the bound.</summary>
        static MapDocument ManyTileDoc(int tileCount)
        {
            int side = (int)Math.Ceiling(Math.Sqrt(tileCount));
            float tile = MapDocumentFile.DefaultTileSize;
            float extent = (side + 1) * tile;
            var doc = new MapDocument
            {
                Id = "many-tiles",
                Bounds = new MapBounds { MinX = -extent, MinZ = -extent, MaxX = extent, MaxZ = extent },
            };
            var overrides = new MapTerrainOverrides();
            int sculptPerTile = (int)(tile / (TerrainSculpt.TileSize * MapTerrainOverrides.DefaultCellSize));
            for (int i = 0; i < tileCount; i++)
            {
                int tx = i % side, tz = i / side;
                doc.Placements.Add(new MapPlacement { Id = $"p-{i}", Kind = "rock", X = tx * tile + 1f, Z = tz * tile + 1f });
                overrides.PutTile(new MapSculptTile(tx * sculptPerTile, tz * sculptPerTile));
            }
            doc.TerrainOverrides = overrides;
            return doc;
        }
    }
}
