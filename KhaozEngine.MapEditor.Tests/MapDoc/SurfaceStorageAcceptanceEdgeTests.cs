using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfaceStorageAcceptanceEdgeTests
{
    static readonly MapPatchKey Crossed = new("ground", 0, 0), Anchor = new("ridge", 0, 0),
        Missed = new("miss", 0, 0);

    [Fact]
    public void IndexEncoding_Refuses257EntriesBeforeReadingAnyEntry()
    {
        // Moving the cardinality check after traversal must trip the count-only sentinel.
        var entries = new CountOnlyEntries(257);
        Assert.Throws<MapDocumentException>(() => MapSurfacePages.EncodeIndex("ground", new(0, 0, 16, 16), entries));
        Assert.Equal(0, entries.EnumerationRequests);
        Assert.Equal(0, entries.IndexerRequests);

        // The legal upper count reaches traversal, without allocating 256 entry objects.
        var atLimit = new CountOnlyEntries(256);
        Assert.Throws<InvalidOperationException>(() => MapSurfacePages.EncodeIndex("ground", new(0, 0, 16, 16), atLimit));
        Assert.Equal(1, atLimit.EnumerationRequests);
        Assert.Equal(0, atLimit.IndexerRequests);
    }

    [Fact]
    public void SurfaceFileRead_RefusesAnOversizedSparseFileByItsLength()
    {
        TiledDocFixture.InDirectory(dir =>
        {
            // The fixture sets a logical length. It never constructs a page-sized byte array.
            string digest = new('0', 64);
            string path = MapSurfaceStorageLayout.PathOf(dir, 'i', digest);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.SetLength(1_048_577L);

            // A tiny valid file keeps an unconditional read refusal from satisfying this case.
            byte[] tiny = { (byte)'{', (byte)'}' };
            string tinyDigest = Convert.ToHexStringLower(SHA256.HashData(tiny));
            File.WriteAllBytes(MapSurfaceStorageLayout.PathOf(dir, 'i', tinyDigest), tiny);
            Assert.Equal(tiny, MapSurfaceStorageLayout.Read(dir, 'i', tinyDigest));

            MapDocumentException? refusal = null;
            long before = GC.GetAllocatedBytesForCurrentThread();
            try { _ = MapSurfaceStorageLayout.Read(dir, 'i', digest); }
            catch (MapDocumentException ex) { refusal = ex; }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.NotNull(refusal);
            Assert.Contains("byte limit", refusal!.Message);
            // The entire measured call must allocate less than the oversized file's byte count.
            Assert.InRange(allocated, 0L, 1_048_576L);
            Assert.Equal(1_048_577L, new FileInfo(path).Length);
        });
    }

    [Fact]
    public void SurfaceWriter_IndexesACrossingSegmentButNotItsBoundingBoxMiss()
    {
        TiledDocFixture.InDirectory(dir =>
        {
            MapDocumentFile.SaveTiled(CrossingWorld(), dir);
            MapSurfaceStorageIndex index = MapDocumentFile.LoadTiled(dir).Tiles!.Surfaces!;
            // (0,0) to (8,8) crosses [3,5] x [3,5], with both endpoints outside.
            Assert.Equal(new[] { new MapRecordRef("crossing-edge", Anchor) },
                index.Entries.Single(e => e.Key == Crossed).IncidentRecords);
            // [3,5] x [6,8] is inside the bounding box but cannot meet x = z.
            Assert.Empty(index.Entries.Single(e => e.Key == Missed).IncidentRecords);
            Assert.Empty(index.Entries.Single(e => e.Key == Anchor).IncidentRecords);
            Assert.Empty(MapDocumentFile.VerifyTiled(dir));
        });
    }

    [Fact]
    public void VerifyTiled_ReportsAStaleIncidentListAfterValidEnvelopeRehashing()
    {
        TiledDocFixture.InDirectory(dir =>
        {
            MapDocumentFile.SaveTiled(CrossingWorld(), dir);
            MapDocument before = MapDocumentFile.LoadTiled(dir);
            string semantics = SurfaceStorageFixtures.SemanticSnapshot(before);
            byte[] payload = SurfaceStorageFixtures.PayloadBytes(dir, Crossed);
            Assert.Equal(new[] { new MapRecordRef("crossing-edge", Anchor) },
                before.Tiles!.Surfaces!.Entries.Single(e => e.Key == Crossed).IncidentRecords);
            Assert.Empty(MapDocumentFile.VerifyTiled(dir));

            ClearCrossedIncidentList(dir);

            // Decode and semantic identity still succeed. Only writer bookkeeping is stale.
            MapDocument after = MapDocumentFile.LoadTiled(dir);
            Assert.Empty(after.Tiles!.Surfaces!.Entries.Single(e => e.Key == Crossed).IncidentRecords);
            Assert.Equal(semantics, SurfaceStorageFixtures.SemanticSnapshot(after));
            Assert.Equal(payload, SurfaceStorageFixtures.PayloadBytes(dir, Crossed));
            string finding = Assert.Single(MapDocumentFile.VerifyTiled(dir));
            Assert.True(finding.StartsWith("incident", StringComparison.Ordinal), finding);
            Assert.Contains("ground", finding);
        });
    }

    static MapDocument CrossingWorld()
    {
        MapDocument doc = SurfaceStorageFixtures.FlatPatches(0);
        MapSurfaceRef ground = doc.Surfaces.Refs[0];
        doc.Surfaces.Refs.Add(ground with { Id = "ridge" });
        doc.Surfaces.Refs.Add(ground with { Id = "miss" });
        MapSurfacePatch anchor = SurfaceAcceptancePatchFixture.Patch(Anchor, 0, 0, 8, 8);
        anchor.Records.Add(new MapBoundaryChain("crossing-edge", MapChainKind.Authored, null, new[]
        {
            new MapChainVertex(new("ridge", MapLatticeAddress.Corner(0, 0)), 1000),
            new MapChainVertex(new("ridge", MapLatticeAddress.Corner(8, 8)), 1000),
        }));
        doc.Surfaces.Patches.Add(Anchor, anchor);
        doc.Surfaces.Patches.Add(Crossed, SurfaceAcceptancePatchFixture.Patch(Crossed, 3, 3, 2, 2));
        doc.Surfaces.Patches.Add(Missed, SurfaceAcceptancePatchFixture.Patch(Missed, 3, 6, 2, 2));
        return doc;
    }

    static void ClearCrossedIncidentList(string root)
    {
        // This fixture has exactly one ground directory, index page and entry.
        string manifestPath = Path.Combine(root, "map.json");
        JsonObject manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!.AsObject();
        JsonObject directoryRef = manifest["surfaceStorage"]!.AsArray().Select(n => n!.AsObject())
            .Single(r => r["surfaceId"]!.GetValue<string>() == "ground");
        string oldDirectoryPath = PagePath(root, 'd', directoryRef["sha256"]!.GetValue<string>());
        JsonObject directory = JsonNode.Parse(File.ReadAllBytes(oldDirectoryPath))!.AsObject();
        JsonObject indexRef = directory["pages"]!.AsArray().Single()!.AsObject();
        string oldIndexPath = PagePath(root, 'i', indexRef["sha256"]!.GetValue<string>());
        JsonObject index = JsonNode.Parse(File.ReadAllBytes(oldIndexPath))!.AsObject();
        JsonObject entry = index["entries"]!.AsArray().Single()!.AsObject();
        JsonObject key = entry["key"]!.AsObject();
        Assert.Equal("ground", key["surfaceId"]!.GetValue<string>());
        Assert.Equal("0", key["slotX"]!.GetValue<string>());
        Assert.Equal("0", key["slotZ"]!.GetValue<string>());
        JsonArray incidents = entry["incidentRecords"]!.AsArray();
        Assert.Single(incidents);
        incidents.Clear();

        indexRef["sha256"] = WritePage(root, 'i', index);
        directoryRef["sha256"] = WritePage(root, 'd', directory);
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        // Remove only superseded fixture pages, so an orphan cannot satisfy the finding assertion.
        File.Delete(oldIndexPath);
        File.Delete(oldDirectoryPath);
    }

    static string PagePath(string root, char kind, string digest) =>
        Path.Combine(root, "tiles", "surfaces", kind.ToString(), digest + ".json");

    static string WritePage(string root, char kind, JsonObject page)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(page.ToJsonString());
        string digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        File.WriteAllBytes(PagePath(root, kind, digest), bytes);
        return digest;
    }

    sealed class CountOnlyEntries(int count) : IReadOnlyList<MapSurfaceIndexEntry>
    {
        public int Count => count;
        internal int EnumerationRequests { get; private set; }
        internal int IndexerRequests { get; private set; }
        public MapSurfaceIndexEntry this[int index]
        {
            get
            {
                IndexerRequests++;
                throw new InvalidOperationException("count-only entries must not be indexed");
            }
        }
        public IEnumerator<MapSurfaceIndexEntry> GetEnumerator()
        {
            EnumerationRequests++;
            throw new InvalidOperationException("count-only entries must not be enumerated");
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

internal static class SurfaceAcceptancePatchFixture
{
    // All callers use fixed buffers of at most 64 cells and 81 heights.
    internal static MapSurfacePatch Patch(MapPatchKey key, int minX, int minZ, int width, int depth)
    {
        int count = width * depth;
        if (count is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(width));
        return new MapSurfacePatch
        {
            Key = key,
            CellMinX = minX,
            CellMinZ = minZ,
            Width = width,
            Depth = depth,
            Heights = Enumerable.Repeat(1000, (width + 1) * (depth + 1)).ToArray(),
            Cells = Enumerable.Repeat(new MapSurfaceCell(1, 0, MapOverlayCut.Full, 0,
                MapCellFlags.None, MapCellTopology.Auto), count).ToArray(),
            Presence = new[] { count == 64 ? ulong.MaxValue : (1UL << count) - 1 },
        };
    }
}
