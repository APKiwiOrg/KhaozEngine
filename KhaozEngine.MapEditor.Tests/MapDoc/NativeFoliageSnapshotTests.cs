using System;
using System.IO;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
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
