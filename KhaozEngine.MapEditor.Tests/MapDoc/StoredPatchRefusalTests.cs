using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class StoredPatchRefusalTests
{
    [Fact]
    public void CorruptPayloadByte_PreservesThePinnedDigestFailureThroughIdentity() => TiledDocFixture.InDirectory(directory =>
    {
        // W6-D1b review regression: this producer remained untyped after its identity adapter closed.
        var doc = SurfaceStorageFixtures.FlatPatches(1);
        var key = new MapPatchKey("ground", 0, 0);
        MapDocumentFile.SaveTiled(doc, directory);
        var source = MapStoredSurfaceSource.Open(directory);
        byte[] original = SurfaceStorageFixtures.PayloadBytes(directory, key);
        string expectedHash = Convert.ToHexStringLower(SHA256.HashData(original));
        SurfaceStorageFixtures.FlipPayloadByte(directory, key);
        string actualHash = Convert.ToHexStringLower(SHA256.HashData(SurfaceStorageFixtures.PayloadBytes(directory, key)));
        var read = source.ReadPatch(key);
        Assert.Equal(MapPatchStatus.Corrupt, read.Status);
        Assert.Null(read.Patch);
        var finding = Assert.IsType<MapDocumentFinding>(read.Finding);
        Assert.Equal(MapDocumentRefusal.DigestMismatch, finding.Reason);
        Assert.Equal(key, finding.Patch);
        Assert.Equal(expectedHash, finding.ExpectedHash);
        Assert.Equal(actualHash, finding.ActualHash);
        var options = new MapResolveOptions("headless", 1, "options", ResolverVersion: 2);
        var assets = SurfaceStorageFixtures.Assets();
        var error = Assert.Throws<MapDocumentException>(() => MapAuthoredIdentityV2.Compute(source, assets, options));
        Assert.Equal(MapDocumentRefusal.DigestMismatch, error.Reason);
        Assert.Equal(key, error.Patch);
        Assert.Equal(expectedHash, error.ExpectedHash);
        Assert.Equal(actualHash, error.ActualHash);
        Assert.Equal(MapPatchStatus.Corrupt, error.PatchStatus);
        File.WriteAllBytes(MapSurfaceStorageLayout.PathOf(directory, 'p', expectedHash), original);
        Assert.Equal(MapAuthoredIdentityV2.Compute(doc, assets, options), MapAuthoredIdentityV2.Compute(source, assets, options));
    });

    [Fact]
    public void MissingPayloadMember_PreservesKnownPatchThroughReadAndIdentity() => TiledDocFixture.InDirectory(directory =>
    {
        // W6-D1b review regression: schema refusals lost the payload's known patch at both adapters.
        var doc = SurfaceStorageFixtures.FlatPatches(1);
        var key = new MapPatchKey("ground", 0, 0);
        MapDocumentFile.SaveTiled(doc, directory);
        byte[] original = SurfaceStorageFixtures.PayloadBytes(directory, key);
        string originalHash = MapSurfaceStorageLayout.Digest(original);
        JsonObject payload = JsonNode.Parse(original)!.AsObject();
        payload.Remove("heights");
        byte[] invalid = Encoding.UTF8.GetBytes(payload.ToJsonString());
        MapSurfaceStorageLayout.Write(directory, 'p', invalid, new());
        SurfaceStorageFixtures.RewriteIndexEntry(directory, key, entry => entry["payloadSha256"] = MapSurfaceStorageLayout.Digest(invalid));
        var source = MapStoredSurfaceSource.Open(directory);

        var read = source.ReadPatch(key);
        Assert.Equal(MapPatchStatus.Corrupt, read.Status);
        Assert.Null(read.Patch);
        var finding = Assert.IsType<MapDocumentFinding>(read.Finding);
        Assert.Equal(MapDocumentRefusal.SchemaMemberMissing, finding.Reason);
        Assert.Equal("heights", finding.FieldPath);
        Assert.Equal(key, finding.Patch);
        var options = new MapResolveOptions("headless", 1, "options", ResolverVersion: 2);
        var assets = SurfaceStorageFixtures.Assets();
        var error = Assert.Throws<MapDocumentException>(() => MapAuthoredIdentityV2.Compute(source, assets, options));
        Assert.Equal(finding.Reason, error.Reason);
        Assert.Equal(finding.FieldPath, error.FieldPath);
        Assert.Equal(key, error.Patch);
        Assert.Equal(MapPatchStatus.Corrupt, error.PatchStatus);

        SurfaceStorageFixtures.RewriteIndexEntry(directory, key, entry => entry["payloadSha256"] = originalHash);
        Assert.Equal(MapAuthoredIdentityV2.Compute(doc, assets, options),
            MapAuthoredIdentityV2.Compute(MapStoredSurfaceSource.Open(directory), assets, options));
    });
}
