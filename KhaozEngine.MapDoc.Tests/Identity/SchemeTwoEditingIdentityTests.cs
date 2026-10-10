using System.IO;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Identity;

public sealed class SchemeTwoEditingIdentityTests
{
    static readonly MapResolveOptions V2 = new("headless", 1, "options", ResolverVersion: 2);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SchemeTwo_CompleteUnsavedEditsUseCurrentSemantics(bool fromTiled)
    {
        MapDocument doc = SurfaceStorageFixtures.ThreeSurfaces();
        MapAssetClosure assets = SurfaceStorageFixtures.Assets();
        string? directory = null;
        try
        {
            if (fromTiled)
            {
                directory = SurfaceStorageFixtures.SaveToTemp(doc);
                doc = MapDocumentFile.LoadTiled(directory);
                Assert.NotNull(doc.Tiles);
                Assert.False(doc.Tiles.IsPartial);
            }

            var ground = new MapPatchKey("ground", 0, 0);
            string before = MapAuthoredIdentityV2.Compute(doc, assets, V2);
            string originalPatchDigest = MapSurfaceSemantics.PatchDigest(doc.Surfaces.Patches[ground]);
            doc.Surfaces.Patches[ground].Heights[5]++;
            Assert.NotEqual(originalPatchDigest, MapSurfaceSemantics.PatchDigest(doc.Surfaces.Patches[ground]));
            if (fromTiled)
                Assert.Equal(originalPatchDigest, doc.Tiles!.Surfaces!.Entries.Single(e => e.Key == ground).SemanticSha256);

            string edited = MapAuthoredIdentityV2.Compute(doc, assets, V2);
            Assert.NotEqual(before, edited);

            MapDocument detached = SurfaceStorageFixtures.ThreeSurfaces();
            detached.Surfaces = doc.Surfaces.Clone();
            detached.Tiles = null;
            Assert.Null(detached.Tiles);
            Assert.Equal(edited, MapAuthoredIdentityV2.Compute(detached, assets, V2));
        }
        finally
        {
            if (directory is not null) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SchemeTwo_AssetRootMembershipMatchesTheVerifiedClosure()
    {
        MapAssetClosure assets = SurfaceStorageFixtures.Assets();
        MapDocument matching = SurfaceStorageFixtures.ThreeSurfaces();
        Assert.Equal(assets.Roots, matching.NativeAssets);
        string expected = MapAuthoredIdentityV2.Compute(matching, assets, V2);

        MapDocument removed = SurfaceStorageFixtures.ThreeSurfaces();
        removed.NativeAssets.RemoveAt(0);
        Assert.Throws<MapDocumentException>(() => MapAuthoredIdentityV2.Compute(removed, assets, V2));

        MapDocument changed = SurfaceStorageFixtures.ThreeSurfaces();
        changed.NativeAssets[0] = changed.NativeAssets[0] with { Sha256 = new string('0', 64) };
        Assert.NotEqual(assets.Roots[0].Sha256, changed.NativeAssets[0].Sha256);
        Assert.Throws<MapDocumentException>(() => MapAuthoredIdentityV2.Compute(changed, assets, V2));
        Assert.Equal(expected, MapAuthoredIdentityV2.Compute(matching, assets, V2));
    }
}
