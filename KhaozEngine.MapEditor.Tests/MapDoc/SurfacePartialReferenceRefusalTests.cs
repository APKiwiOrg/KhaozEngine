using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfacePartialReferenceRefusalTests
{
    [Fact]
    public void NewOutgoingReference_IdentifiesAKnownMissingPatchWithoutWriting() => TiledDocFixture.InDirectory(dir =>
    {
        MapDocumentFile.SaveTiled(SurfaceStorageFixtures.ThreeSurfaces(), dir);
        MapDocument window = SurfaceStorageFixtures.LoadSlotWindow(dir, 0);
        var key = new MapPatchKey("ground", 0, 0);
        var missing = new MapPatchKey("ground", 40, 0);
        var dependencies = window.Surfaces.Patches[key].CornerDependencies;
        var saved = AssertFixtures.Files(dir);
        dependencies.Add(new(0, 0, new(missing, MapLatticeAddress.Corner(60, 0))));

        var error = Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveTiled(window, dir));
        Assert.Equal(MapDocumentRefusal.ReferenceMissing, error.Reason);
        Assert.Equal(missing, error.Patch);
        Assert.Equal(key, error.ExpectedPatch);
        Assert.Equal(saved, AssertFixtures.Files(dir));
        dependencies.Clear();
        MapDocumentFile.SaveTiled(window, dir);
        Assert.Empty(MapDocumentFile.VerifyTiled(dir));
    });

    [Fact]
    public void UnloadedIncidentRecord_IdentifiesItsAnchorAndChangedPatchWithoutWriting() => TiledDocFixture.InDirectory(dir =>
    {
        MapDocument doc = SurfaceStorageFixtures.ThreeSurfaces();
        var key = new MapPatchKey("ground", 0, 0);
        var anchor = new MapPatchKey("far", 300, 0);
        doc.Surfaces.Patches[anchor].Records.Add(new MapBoundaryChain("remote-rim", MapChainKind.Authored, null, new[]
        {
            new MapChainVertex(new("ground", MapLatticeAddress.Corner(60, 0)), 1000),
            new MapChainVertex(new("ground", MapLatticeAddress.Corner(64, 0)), 1000),
        }));
        MapDocumentFile.SaveTiled(doc, dir);
        MapDocument window = SurfaceStorageFixtures.LoadSlotWindow(dir, 0);
        Assert.False(window.Surfaces.Patches.ContainsKey(anchor));
        var saved = AssertFixtures.Files(dir);
        window.Surfaces.Patches[key].Heights[6]++;

        var error = Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveTiled(window, dir));
        Assert.Equal(MapDocumentRefusal.StorageReverseDependantUnloaded, error.Reason);
        Assert.Equal(anchor, error.Patch);
        Assert.Equal(key, error.ExpectedPatch);
        Assert.Equal("remote-rim", error.RecordId);
        Assert.Equal(saved, AssertFixtures.Files(dir));
        window.Surfaces.Patches[key].Heights[6]--;
        MapDocumentFile.SaveTiled(window, dir);
        Assert.Empty(MapDocumentFile.VerifyTiled(dir));
    });
}
