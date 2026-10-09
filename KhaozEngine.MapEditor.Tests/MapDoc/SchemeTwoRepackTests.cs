using System;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Storage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SchemeTwoRepackTests
{
    [Fact]
    public void SchemeTwo_RepackedPagesKeepTheTokenWhileFilesDiffer() => TiledDocFixture.InDirectory(a => TiledDocFixture.InDirectory(b =>
    {
        MapDocument doc = SurfaceStorageFixtures.ThreeSurfaces();
        MapDocumentFile.SaveTiled(doc, a);
        MapTiledFile.Save(doc, b, MapDocRegistry.CreateDefault(), null, new MapSurfacePacking(IndexBlockSlots: 1, DirectoryBlockPages: 2));
        var options = new MapResolveOptions("headless", 1, "options", ResolverVersion: 2);
        Assert.Equal(MapAuthoredIdentityV2.Compute(MapStoredSurfaceSource.Open(a), SurfaceStorageFixtures.Assets(), options),
            MapAuthoredIdentityV2.Compute(MapStoredSurfaceSource.Open(b), SurfaceStorageFixtures.Assets(), options));
        string[] first = SurfaceStorageFixtures.SurfaceFiles(a).Select(f => f.Replace(Path.DirectorySeparatorChar, '/'))
            .Where(f => f.Contains("/i/", StringComparison.Ordinal)).ToArray();
        string[] second = SurfaceStorageFixtures.SurfaceFiles(b).Select(f => f.Replace(Path.DirectorySeparatorChar, '/'))
            .Where(f => f.Contains("/i/", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(first);
        Assert.NotEmpty(second);
        Assert.False(first.SequenceEqual(second, StringComparer.Ordinal));
    }));
}
