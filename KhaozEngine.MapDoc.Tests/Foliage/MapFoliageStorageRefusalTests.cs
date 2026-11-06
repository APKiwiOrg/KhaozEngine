using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageStorageRefusalTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ManifestLayerReferences_RetainTheDeclaredAndSubmittedIdentity(bool chunk)
    {
        var fixture = FoliageStorageFixtures.RegionFixture();
        using var directory = new FoliageStorageFixtures.DirectoryScope();
        var prepared = MapFoliageStorage.Prepare(fixture.Document, directory.Root);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            MapFoliageStorage.WriteManifest(writer, fixture.Document.Foliage!, prepared.Index);
        var node = JsonNode.Parse(stream.ToArray())!;
        var target = chunk ? node["layers"]![0]!["chunks"]![0]! : node["packs"]![0]!;
        target["layerId"] = "missing-layer";
        using var refused = JsonDocument.Parse(node.ToJsonString());
        var error = Assert.Throws<MapDocumentException>(() => MapFoliageStorage.ReadManifest(refused.RootElement));
        Assert.Equal(chunk ? MapDocumentRefusal.AuthoredValueMismatch : MapDocumentRefusal.ReferenceMissing, error.Reason);
        Assert.Equal(chunk ? FoliageFixtures.LayerId : "missing-layer", error.LayerId);
        Assert.Equal("layerId", error.FieldPath);
        if (chunk) Assert.Equal("missing-layer", error.ActualText);
        target["layerId"] = FoliageFixtures.LayerId;
        using var control = JsonDocument.Parse(node.ToJsonString());
        Assert.Equal(3, MapFoliageStorage.ReadManifest(control.RootElement).Index.Chunks.Count);
    }

    [Fact]
    public void SaveSnapshot_ReportsBothHashesWithoutChangingTheProposal()
    {
        var fixture = FoliageFixtures.Create();
        using var directory = new FoliageStorageFixtures.DirectoryScope();
        var original = fixture.Document.Foliage;
        var prepared = MapFoliageStorage.Prepare(fixture.Document, directory.Root);
        fixture.Document.Foliage = null;
        var error = Assert.Throws<MapDocumentException>(() => MapFoliageStorage.RequireUnchanged(fixture.Document, prepared));
        Assert.Equal(MapDocumentRefusal.FoliageSaveSnapshotChanged, error.Reason);
        Assert.Equal(prepared.FoliageSha256, error.ExpectedHash);
        Assert.Equal(MapFoliageProjection.Digest(null), error.ActualHash);
        Assert.NotEqual(error.ExpectedHash, error.ActualHash);
        Assert.Null(fixture.Document.Foliage);
        fixture.Document.Foliage = original;
        MapFoliageStorage.RequireUnchanged(fixture.Document, prepared);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LayoutMembership_IdentifiesTheLayerAndChunk(bool owner)
    {
        var fixture = FoliageStorageFixtures.RegionFixture();
        using var directory = new FoliageStorageFixtures.DirectoryScope();
        var prepared = MapFoliageStorage.Prepare(fixture.Document, directory.Root);
        var rows = prepared.Index.Chunks.ToArray();
        var row = rows[1];
        var wrongRegion = new MapTileCoord(10, 10);
        var packs = prepared.Index.Packs.ToArray();
        if (owner)
        {
            // Move both chunks of this pack together, keeping the index's pack references consistent.
            packs[0] = packs[0] with { Region = wrongRegion };
            rows = rows.Select(r => r.OwnerRegion == row.OwnerRegion ? r with { OwnerRegion = wrongRegion } : r).ToArray();
        }
        else rows[1] = row with { Regions = [row.OwnerRegion] };
        var refused = new MapFoliageStorageIndex(packs, rows);
        var original = fixture.Document.Foliage;
        var error = Assert.Throws<MapDocumentException>(() => MapFoliageStorage.ValidateLayout(fixture.Document, refused));
        Assert.Equal(MapDocumentRefusal.FoliageRegionMembershipMismatch, error.Reason);
        Assert.Equal(row.LayerId, error.LayerId);
        Assert.Equal(owner ? rows[0].Key : row.Key, error.FoliageChunk);
        Assert.Same(original, fixture.Document.Foliage);
        MapFoliageStorage.ValidateLayout(fixture.Document, prepared.Index);
    }

    [Theory]
    [InlineData("coordinate", MapDocumentRefusal.GeometryNotRepresentable)]
    [InlineData("bounds", MapDocumentRefusal.BoundsInvalid)]
    [InlineData("capacity", MapDocumentRefusal.WorkCapacityExceeded)]
    [InlineData("arithmetic", MapDocumentRefusal.GeometryNotRepresentable)]
    public void RegionRange_SelectsTheFailedRuleBeforeEnumerating(string fault, MapDocumentRefusal reason)
    {
        var draft = new FoliageLayerDraft(FoliageFixtures.BasicLayer([]))
        { OriginX = 0, OriginZ = 0, CellSize = 1, Width = 32, Depth = 1 };
        var key = new MapFoliageChunkKey(0, 0);
        float tileSize = 1;
        if (fault == "coordinate") { draft.OriginX = int.MinValue; draft.Width = 1; tileSize = 0.5f; }
        if (fault == "bounds") { draft.Width = 1; key = new(1, 0); }
        // 31 * 4096 / 31 = 4096, so the inclusive range contains 4097 regions.
        if (fault == "capacity") { draft.CellSize = 4096; tileSize = 31; }
        if (fault == "arithmetic") tileSize = 0;
        var layer = draft.Build();
        var error = Assert.Throws<MapDocumentException>(() => MapFoliageStorage.RegionRange(layer, key, tileSize));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(layer.Id, error.LayerId);
        Assert.Equal(key, error.FoliageChunk);
        if (fault == "capacity")
        {
            Assert.Equal("RegionMemberships", error.Budget);
            Assert.Equal(4097L, error.ActualCount);
            Assert.Equal(4096L, error.Limit);
            draft.CellSize = 4095;
        }
        else if (fault == "coordinate")
        {
            Assert.Equal("RegionX", error.FieldPath);
            Assert.Equal(-4294967296L, error.ActualInteger);
            Assert.Equal((long)int.MinValue, error.MinimumInteger);
            Assert.Equal((long)int.MaxValue, error.MaximumInteger);
            tileSize = 1;
        }
        else if (fault == "bounds") key = new(0, 0);
        else tileSize = 1;
        var control = MapFoliageStorage.RegionRange(draft.Build(), key, tileSize);
        if (fault == "capacity") Assert.Equal(4096L, control.Count);
        else if (fault == "coordinate") Assert.Equal((long)int.MinValue, control.MinX);
        else Assert.True(control.Count > 0);
    }

    [Theory]
    [InlineData("missing", MapDocumentRefusal.FoliageChunkUnavailable)]
    [InlineData("key", MapDocumentRefusal.AuthoredValueMismatch)]
    [InlineData("digest", MapDocumentRefusal.DigestMismatch)]
    public void CompleteDensity_RetainsTheFirstMissingOrMismatchedSubject(string fault, MapDocumentRefusal reason)
    {
        var layer = FoliageFixtures.BasicLayer();
        var chunks = layer.Chunks.ToArray();
        if (fault == "missing") chunks = chunks[1..];
        if (fault == "key") chunks[0] = new(new(2, 0), chunks[0].CopyDensity());
        if (fault == "digest")
        {
            var bytes = chunks[0].CopyDensity(); bytes[0] ^= 1;
            chunks[0] = new(chunks[0].Key, bytes);
        }
        var refused = new MapFoliageSet(1, [MapFoliageLayer.FromStorage(layer, layer.ChunkDigests, chunks)]);
        var error = Assert.Throws<MapDocumentException>(() =>
        {
            if (fault == "missing") MapFoliageStorage.RequireComplete(refused);
            else MapFoliageStorage.RequireMatchingDensityForTests(layer.Id, 0, chunks[0], layer.ChunkDigests[0]);
        });
        Assert.Equal(reason, error.Reason);
        Assert.Equal(layer.Id, error.LayerId);
        if (fault == "missing")
        {
            Assert.Equal(1L, error.ActualCount);
            Assert.Equal(2L, error.ExpectedCount);
        }
        else
        {
            Assert.Equal(0, error.Index);
            Assert.Equal(chunks[0].Key, error.FoliageChunk);
            Assert.Equal(layer.ChunkDigests[0].Key, error.ExpectedFoliageChunk);
            if (fault == "digest")
            {
                Assert.Equal(layer.ChunkDigests[0].Sha256, error.ExpectedHash);
                Assert.Equal(FoliageStorageFixtures.Hash(chunks[0].CopyDensity()), error.ActualHash);
            }
        }
        MapFoliageStorage.RequireComplete(new(1, [layer]));
        MapFoliageStorage.RequireMatchingDensityForTests(layer.Id, 0, layer.Chunks[0], layer.ChunkDigests[0]);
    }

    [Fact]
    public void StorageValidation_ForwardsTheCompleteConcreteFindingSnapshot()
    {
        var layer = new FoliageLayerDraft(FoliageFixtures.BasicLayer()) { RootOffset = float.NaN }.Build();
        var error = Assert.Throws<MapDocumentException>(() => MapFoliageStorage.RequireValid(new(1, [layer])));
        Assert.Equal(MapDocumentRefusal.ValueNonfinite, error.Reason);
        Assert.Equal("RootOffset", error.FieldPath);
        Assert.Equal(layer.Id, error.LayerId);
        var finding = Assert.Single(error.Findings!);
        Assert.Equal(error.Reason, finding.Reason);
        Assert.Equal(error.LayerId, finding.LayerId);
        Assert.Equal(error.FieldPath, finding.FieldPath);
        MapFoliageStorage.RequireValid(new(1, [new FoliageLayerDraft(layer) { RootOffset = 0 }.Build()]));
    }

    [Theory]
    [InlineData(MapFoliageReadStatus.Absent)]
    [InlineData(MapFoliageReadStatus.Unavailable)]
    [InlineData(MapFoliageReadStatus.Invalid)]
    [InlineData(MapFoliageReadStatus.CapacityExceeded)]
    public void PackReadRefusal_RetainsItsStatusAndPin(MapFoliageReadStatus status)
    {
        var pack = new MapFoliagePackRef("cover", new(-1, 2), new string('a', 64), 1, MapFoliageRegionPack.ByteLength(1));
        var error = Assert.Throws<MapDocumentException>(() => MapFoliageStorage.Require(status, pack));
        Assert.Equal(MapDocumentRefusal.PinnedContentUnavailable, error.Reason);
        Assert.Equal(status, error.FoliageReadStatus);
        Assert.Equal(pack.LayerId, error.LayerId);
        Assert.Equal(pack.Region, error.Tile);
        Assert.Equal(pack.PayloadSha256, error.ExpectedHash);
        MapFoliageStorage.Require(MapFoliageReadStatus.Found, pack);
    }
}
