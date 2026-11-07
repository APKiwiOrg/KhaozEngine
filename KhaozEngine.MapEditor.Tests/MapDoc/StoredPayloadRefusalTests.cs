using System;
using System.IO;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class StoredPayloadRefusalTests
{
    [Theory]
    [InlineData("Cells")]
    [InlineData("MinHeightUnits")]
    [InlineData("MaxHeightUnits")]
    [InlineData("Dependencies")]
    [InlineData("RecordIds")]
    public void IndexMetadataMismatch_KeepsItsFieldThroughTheStoredReadAndIdentityAdapter(string field) =>
        TiledDocFixture.InDirectory(directory =>
    {
        var document = SurfaceStorageFixtures.ThreeSurfaces();
        var key = new MapPatchKey(field == "RecordIds" ? "ridge" : "ground", field == "Dependencies" ? 1 : 0, 0);
        MapDocumentFile.SaveTiled(document, directory);
        SurfaceStorageFixtures.RewriteIndexEntry(directory, key, entry =>
        {
            switch (field)
            {
                case "Cells": entry["cells"]!["minX"] = entry["cells"]!["minX"]!.GetValue<long>() + 1; break;
                case "MinHeightUnits": entry["minHeightUnits"] = entry["minHeightUnits"]!.GetValue<int>() - 1; break;
                case "MaxHeightUnits": entry["maxHeightUnits"] = entry["maxHeightUnits"]!.GetValue<int>() + 1; break;
                case "Dependencies": entry["dependencies"] = new JsonArray(); break;
                default: entry["recordIds"] = new JsonArray(); break;
            }
        });
        byte[] manifest = File.ReadAllBytes(Path.Combine(directory, MapTiledFile.ManifestName));
        var source = MapStoredSurfaceSource.Open(directory);
        var read = source.ReadPatch(key);
        Assert.Equal(MapPatchStatus.Corrupt, read.Status);
        Assert.Null(read.Patch);
        Assert.Equal(MapDocumentRefusal.AuthoredValueMismatch, read.Finding!.Reason);
        Assert.Equal(field, read.Finding.FieldPath);
        Assert.Equal(key, read.Finding.Patch);
        var error = Assert.Throws<MapDocumentException>(() => MapAuthoredIdentityV2.Compute(source,
            SurfaceStorageFixtures.Assets(), new("headless", 1, "options", ResolverVersion: 2)));
        Assert.Equal(read.Finding.Reason, error.Reason);
        Assert.Equal(field, error.FieldPath);
        Assert.Equal(key, error.Patch);
        Assert.Equal(MapPatchStatus.Corrupt, error.PatchStatus);
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(directory, MapTiledFile.ManifestName)));
        MapDocumentFile.SaveTiled(document, directory);
        Assert.Equal(MapPatchStatus.Present, MapStoredSurfaceSource.Open(directory).ReadPatch(key).Status);
    });
}
