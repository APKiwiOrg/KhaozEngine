using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class StoredWholeIdentityTests
{
    static readonly MapResolveOptions V2 = new("headless", 1, "options", ResolverVersion: 2);

    [Theory]
    [InlineData("resolver")]
    [InlineData("recipe")]
    [InlineData("options")]
    public void StoredIdentity_LegacyMetadataRefusesBeforePayloadWork(string invalid) => TiledDocFixture.InDirectory(directory =>
    {
        MapDocument doc = SurfaceStorageFixtures.ThreeSurfaces();
        if (invalid == "resolver")
        {
            doc.ResolverIdentity = new(1, 1);
            doc.SupportRecipe = MapSupportRecipe.LegacyXzCallbackV1;
            doc.Surfaces = new MapSurfaceSet();
        }
        MapTiledFile.Save(doc, directory, MapDocRegistry.CreateDefault(), null,
            new MapSurfacePacking(IndexBlockSlots: 1, DirectoryBlockPages: 2));

        if (invalid == "recipe")
        {
            string manifestPath = Path.Combine(directory, "map.json");
            JsonObject manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!.AsObject();
            manifest["supportRecipe"] = nameof(MapSupportRecipe.LegacyXzCallbackV1);
            File.WriteAllText(manifestPath, manifest.ToJsonString());
            string[] payloadFiles = Directory.GetFiles(Path.Combine(directory, "tiles", "surfaces", "p"), "*.json", SearchOption.AllDirectories);
            Assert.NotEmpty(payloadFiles);
            foreach (string payloadFile in payloadFiles) File.Delete(payloadFile);
            Assert.All(payloadFiles, payloadFile => Assert.False(File.Exists(payloadFile)));
            MapDocumentException error = Assert.Throws<MapDocumentException>(() => MapStoredSurfaceSource.Open(directory));
            Assert.Contains("Resolver version 2 requires supportRecipe AuthoredBindingsV2.", error.Message);
            return;
        }

        MapStoredSurfaceSource source = MapStoredSurfaceSource.Open(directory);
        MapResolveOptions options = invalid == "options" ? V2 with { ResolverVersion = 1 } : V2;
        var work = new MapWholeIdentityWork();
        Assert.Throws<MapDocumentException>(() => MapAuthoredIdentityV2.Compute(source, SurfaceStorageFixtures.Assets(), options, work));
        Assert.Equal(0, work.PayloadReads);
    });

    [Fact]
    public void StoredIdentity_LazyClosureStreamsOnePayloadAtATime() => TiledDocFixture.InDirectory(directory =>
    {
        MapDocument doc = SurfaceStorageFixtures.FlatPatches(257);
        MapAssetClosure assets = SurfaceStorageFixtures.Assets();
        Assert.Equal(257, doc.Surfaces.Patches.Count);
        MapTiledFile.Save(doc, directory, MapDocRegistry.CreateDefault(), null,
            new MapSurfacePacking(IndexBlockSlots: 1, DirectoryBlockPages: 2));
        MapStoredSurfaceSource source = MapStoredSurfaceSource.Open(directory);
        Assert.NotEmpty(source.Index.Directory);
        Assert.Empty(source.Index.ReadDirectoryPages);
        Assert.Empty(source.Index.ReadIndexPages);
        Assert.Empty(source.Index.Entries);

        string expected = MapAuthoredIdentityV2.Compute(doc, assets, V2);
        var work = new MapWholeIdentityWork();
        Assert.Equal(expected, MapAuthoredIdentityV2.Compute(source, assets, V2, work));
        Assert.Equal(257, work.PayloadReads);
        Assert.Equal(1, work.MaximumPayloadsHeld);

        SurfaceStorageFixtures.DeletePayload(directory, new("ground", 0, 0));
        MapDocumentException error = Assert.Throws<MapDocumentException>(() => MapAuthoredIdentityV2.Compute(source, assets, V2));
        Assert.Contains("Missing", error.Message);
    });

    [Fact]
    public void StoredIdentity_UsesOriginalPinnedClosureAfterManifestReplacement() => TiledDocFixture.InDirectory(directory =>
    {
        MapDocument doc = SurfaceStorageFixtures.ThreeSurfaces();
        MapAssetClosure assets = SurfaceStorageFixtures.Assets();
        MapTiledFile.Save(doc, directory, MapDocRegistry.CreateDefault(), null,
            new MapSurfacePacking(IndexBlockSlots: 1, DirectoryBlockPages: 2));
        MapStoredSurfaceSource original = MapStoredSurfaceSource.Open(directory);
        string root = original.RootSha256;
        MapResolverIdentityDoc? resolver = original.ResolverIdentity;
        MapSupportRecipe recipe = original.SupportRecipe;
        Assert.Equal(new MapResolverIdentityDoc(1, 2), resolver);
        Assert.Equal(MapSupportRecipe.AuthoredBindingsV2, recipe);
        string expected = MapAuthoredIdentityV2.Compute(doc, assets, V2);

        string manifestPath = Path.Combine(directory, "map.json");
        JsonObject manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!.AsObject();
        manifest["nativeAssets"]![0]!["sha256"] = new string('0', 64);
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var retainedRootsWork = new MapWholeIdentityWork();
        Assert.Equal(expected, MapAuthoredIdentityV2.Compute(original, assets, V2, retainedRootsWork));
        Assert.Equal(5, retainedRootsWork.PayloadReads);
        MapStoredSurfaceSource changedRoots = MapStoredSurfaceSource.Open(directory);
        var rejectedRootsWork = new MapWholeIdentityWork();
        Assert.Throws<MapDocumentException>(() => MapAuthoredIdentityV2.Compute(changedRoots, assets, V2, rejectedRootsWork));
        Assert.Equal(0, rejectedRootsWork.PayloadReads);

        string[] originalPayloads = SurfaceStorageFixtures.SurfaceFiles(directory)
            .Where(path => path.Replace(Path.DirectorySeparatorChar, '/').Contains("/p/", StringComparison.Ordinal)).ToArray();
        doc.Surfaces.Patches[new("ground", 0, 0)].Heights[5]++;
        MapTiledFile.Save(doc, directory, MapDocRegistry.CreateDefault(), null,
            new MapSurfacePacking(IndexBlockSlots: 1, DirectoryBlockPages: 2));
        Assert.Contains(originalPayloads, path => !File.Exists(Path.Combine(directory, path)));
        Assert.Equal(root, original.RootSha256);
        Assert.Equal(resolver, original.ResolverIdentity);
        Assert.Equal(recipe, original.SupportRecipe);

        MapDocumentException missing = Assert.Throws<MapDocumentException>(() => MapAuthoredIdentityV2.Compute(original, assets, V2));
        Assert.Contains("Missing", missing.Message);
        MapStoredSurfaceSource replacement = MapStoredSurfaceSource.Open(directory);
        string replacementToken = MapAuthoredIdentityV2.Compute(doc, assets, V2);
        Assert.NotEqual(root, replacement.RootSha256);
        Assert.NotEqual(expected, replacementToken);
        Assert.Equal(replacementToken, MapAuthoredIdentityV2.Compute(replacement, assets, V2));
        Assert.Equal(root, original.RootSha256);
        Assert.Equal(resolver, original.ResolverIdentity);
        Assert.Equal(recipe, original.SupportRecipe);
    });
}
