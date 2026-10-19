using System;
using System.IO;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc.Foliage;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeFoliageSnapshotTests
{
    [Fact]
    public void EveryNativeCopyPath_RetainsIndependentFoliage()
    {
        var fixture = FoliageFixtures.Create();
        MapDocument source = fixture.Document;
        MapDocRegistry registry = MapDocRegistry.CreateDefault();
        MapDocument globals = MapTiledFile.GlobalsOnly(source);
        MapDocument candidate = NativeDocumentSnapshot.Clone(source, registry);
        foreach (var copy in new[] { globals, candidate })
        {
            Assert.NotNull(copy.Foliage);
            Assert.Equal(FoliageStorageFixtures.Json(source.Foliage!), FoliageStorageFixtures.Json(copy.Foliage!));
            Assert.NotSame(source.Foliage, copy.Foliage);
            Assert.NotSame(source.Foliage!.Layers[0], copy.Foliage!.Layers[0]);
        }
        var target = FoliageFixtures.Create().Document;
        candidate.Foliage = new(1, [FoliageStorageFixtures.Change(candidate.Foliage!.Layers[0], l => l["seed"] = 42)]);
        NativeDocumentSnapshot.PublishWriteSet(target, candidate, MapNativeWriteSet.PlacementsOnly with { Placements = false });
        Assert.Equal(17, target.Foliage!.Layers[0].Seed);
        NativeDocumentSnapshot.PublishWriteSet(target, candidate, MapNativeWriteSet.PlacementsOnly with { Placements = false, Foliage = true });
        Assert.Equal(42, target.Foliage.Layers[0].Seed);
        Assert.NotSame(candidate.Foliage, target.Foliage);
        NativeDocumentSnapshot.Publish(target, source);
        Assert.Equal(17, target.Foliage.Layers[0].Seed);
        Assert.NotSame(source.Foliage, target.Foliage);
        source.Foliage = null;
        Assert.NotNull(target.Foliage);
        candidate.Foliage = null;
        NativeDocumentSnapshot.PublishWriteSet(target, candidate, MapNativeWriteSet.PlacementsOnly with { Placements = false, Foliage = true });
        Assert.Null(target.Foliage);
    }

    [Fact]
    public void ValidationCandidate_SeesFoliageAndRefusesInvalidProposal()
    {
        MapDocument source = FoliageFixtures.Create().Document;
        MapDocument candidate = NativeDocumentSnapshot.Clone(source, MapDocRegistry.CreateDefault());
        Assert.NotNull(candidate.Foliage);
        candidate.ResolverIdentity = new(1, 1);
        Assert.Contains(MapDocumentValidator.Validate(candidate, MapDocRegistry.CreateDefault()),
            e => e.StartsWith("foliage: requires resolver identity", StringComparison.Ordinal));
        Assert.Equal(2, source.ResolverIdentity!.ResolverVersion);
    }

    [Fact]
    public void Snapshot_AttachesAndValidatesFoliageWithoutInlineDensityWrites()
    {
        var fixture = FoliageFixtures.Create();
        using var work = MapFoliageStorageWork.Capture();
        var candidate = NativeDocumentSnapshot.Clone(fixture.Document, MapDocRegistry.CreateDefault());
        Assert.NotNull(candidate.Foliage);
        Assert.NotSame(fixture.Document.Foliage, candidate.Foliage);
        Assert.Equal(0, work.InlineWrites);
        Assert.Equal(0, work.ChunkHashes);
        fixture.Document.Foliage = new(1, [FoliageStorageFixtures.Change(fixture.Document.Foliage!.Layers[0], l => l["cellSize"] = -1)]);
        Assert.Contains("cellSize", Assert.Throws<MapDocumentException>(() =>
            NativeDocumentSnapshot.Clone(fixture.Document, MapDocRegistry.CreateDefault())).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorTransaction_ExpandsWithoutWholeFoliageWrites()
    {
        // The released hut prefab with a foliage layer bound to its exterior floor, so publication expands both sides.
        PrefabFixture fixture = PrefabFixtures.Create();
        MapDocument document = fixture.Document;
        document.Foliage = new(1, [FoliageStorageFixtures.Change(FoliageFixtures.BasicLayer(), l =>
        {
            l["support"] = new JsonObject { ["kind"] = "Surface", ["surfaceId"] = "exterior" };
            l["compatibilityOrdinals"] = new JsonArray(new JsonObject { ["surfaceId"] = "exterior", ["ordinal"] = 0 });
            l["models"] = new JsonArray(new JsonObject { ["assetId"] = "item", ["weight"] = 1 });
            l["allowedVisibleMaterials"] = new JsonArray(10);
            l["materials"] = new JsonArray(new JsonObject { ["id"] = 10, ["resourceId"] = "material", ["kind"] = "Ordinary" });
        })]);
        Assert.Empty(MapFoliageValidation.ValidateBound(document, fixture.Assets));
        string projection = MapFoliageProjection.Digest(document.Foliage);
        long writes = MapFoliageJson.ThreadPayloadWrites, expansions = MapPrefabResolver.ThreadExpansions;
        NativeDocumentTransaction.Run(document, new AddPlacementCommand(new MapPlacement
        { Id = "item-new", Kind = "scenery", AssetId = "item", X = 1, Y = -2, Z = 1 }), false, fixture.Assets);
        Assert.Contains(document.Placements, p => p.Id == "item-new");
        Assert.True(MapPrefabResolver.ThreadExpansions - expansions > 0);
        Assert.Equal(0, MapFoliageJson.ThreadPayloadWrites - writes);
        Assert.NotNull(document.Foliage);
        Assert.Equal(projection, MapFoliageProjection.Digest(document.Foliage));
    }

    [Fact]
    public void LocalHistoryAndSemanticHash_ObserveFoliageOnlyChanges()
    {
        var fixture = FoliageFixtures.Create();
        var session = PrefabEditorFixtures.Session(new PrefabFixture(fixture.Document, fixture.Assets));
        string directory = Path.GetDirectoryName(session.DocumentPath)!;
        try
        {
            session.Mutate((doc, _) =>
            {
                Assert.NotNull(doc.Foliage);
                doc.Foliage = new(1, [FoliageStorageFixtures.Change(doc.Foliage!.Layers[0], l => l["seed"] = 42)]);
                return 0;
            }, false);
            Assert.True(session.IsDirty);
            Assert.Equal((1, 1, 1L), session.NativeHistoryState);
            Assert.Equal(42, PrefabEditorFixtures.Document(session).Foliage!.Layers[0].Seed);
            session.UndoNative();
            Assert.Equal(17, PrefabEditorFixtures.Document(session).Foliage!.Layers[0].Seed);
            session.RedoNative();
            Assert.Equal(42, PrefabEditorFixtures.Document(session).Foliage!.Layers[0].Seed);
        }
        finally { Directory.Delete(directory, true); }
    }
}
