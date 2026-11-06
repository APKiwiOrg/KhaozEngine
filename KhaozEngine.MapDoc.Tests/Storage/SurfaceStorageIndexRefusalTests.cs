using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfaceStorageIndexRefusalTests
{
    static readonly string Digest = new('a', 64), OtherDigest = new('b', 64);

    [Theory]
    [InlineData(false, MapDocumentRefusal.ReferenceMissing)]
    [InlineData(true, MapDocumentRefusal.StorageDirectoryPagesOverlap)]
    public void DirectoryAdmission_IdentifiesTheUnknownSurfaceOrOverlappingPair(bool overlap, MapDocumentRefusal reason)
    {
        var refs = SurfaceStorageFixtures.FlatPatches(1).Surfaces.Refs;
        var first = new MapDirectoryPageRef(overlap ? "ground" : "missing", new(0, 0, 1, 1), Digest);
        var second = new MapDirectoryPageRef("ground", new(0, 0, 1, 1), OtherDigest);
        var error = Assert.Throws<MapDocumentException>(() => new MapSurfaceStorageIndex(overlap ? [first, second] : [first], refs));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(overlap ? "ground" : "missing", error.SurfaceId);
        Assert.Equal("surfaceStorage", error.FieldPath);
        if (overlap) Assert.Equal((0, 1), (error.Index, error.OtherIndex));
        else Assert.Equal(Digest, error.ExpectedHash);
        var control = new MapSurfaceStorageIndex(overlap
            ? [first, second with { Covers = new(1, 0, 2, 1) }] : [first with { SurfaceId = "ground" }], refs);
        Assert.Equal(overlap ? 2 : 1, control.Directory.Count);
    }

    [Fact]
    public void IndexAdmission_IdentifiesADuplicatePatchAcrossPages()
    {
        var set = SurfaceStorageFixtures.FlatPatches(2).Surfaces;
        MapSurfaceIndexEntry first = MapSurfaceTiledStore.Entry(set.Patches.Values.First(), [], []);
        MapSurfaceIndexEntry second = MapSurfaceTiledStore.Entry(set.Patches.Values.Last(), [], []);
        var page = new MapIndexPageRef(new(0, 0, 2, 1), Digest, 1);
        var otherPage = page with { Sha256 = OtherDigest };
        var index = new MapSurfaceStorageIndex([], set.Refs);
        index.AddIndex(page, [first]);
        var error = Assert.Throws<MapDocumentException>(() => index.AddIndex(otherPage, [first]));
        Assert.Equal(MapDocumentRefusal.IdentityDuplicate, error.Reason);
        Assert.Equal(first.Key, error.Patch);
        Assert.Equal(OtherDigest, error.ExpectedHash);
        var control = new MapSurfaceStorageIndex([], set.Refs);
        control.AddIndex(page, [first]);
        control.AddIndex(otherPage, [second]);
        Assert.Equal(new[] { first.Key, second.Key }, control.Entries.Select(e => e.Key));
    }
}
