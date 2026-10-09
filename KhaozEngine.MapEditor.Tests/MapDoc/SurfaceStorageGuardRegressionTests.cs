using System;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfaceStorageGuardRegressionTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("type")]
    [InlineData("unloaded")]
    public void PartialSurfaceMetadataCannotPublishAnUnresolvedParent(string variant) => TiledDocFixture.InDirectory(dir =>
    {
        MapDocumentFile.SaveTiled(SurfaceStorageFixtures.ThreeSurfaces(), dir);
        MapDocument window = SurfaceStorageFixtures.LoadSlotWindow(dir, 0);
        Assert.True(window.Tiles!.IsPartial);
        var anchor = variant == "unloaded" ? new MapPatchKey("far", 300, 0) : new MapPatchKey("ridge", 0, 0);
        Assert.Equal(variant != "unloaded", window.Surfaces.Patches.ContainsKey(anchor));
        int ground = window.Surfaces.Refs.FindIndex(s => s.Id == "ground");
        Assert.True(ground >= 0);
        window.Surfaces.Refs[ground] = window.Surfaces.Refs[ground] with
        {
            IndoorSpan = new("new-span", new(variant == "type" ? "ridge-rim" : "missing-space", anchor),
                0, 100, Array.Empty<string>()),
        };
        Assert.Empty(MapDocumentValidator.Validate(window, MapDocRegistry.CreateDefault()));
        byte[] manifest = File.ReadAllBytes(Path.Combine(dir, "map.json"));
        string[] files = SurfaceStorageFixtures.SurfaceFiles(dir).ToArray();
        var error = Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveTiled(window, dir));
        if (variant == "unloaded") Assert.Contains("unloaded", error.Message);
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(dir, "map.json")));
        Assert.Equal(files, SurfaceStorageFixtures.SurfaceFiles(dir));
    });

    [Fact]
    public void RemovedSurfaceStillParticipatesInReverseKnowledgeRefusal() => TiledDocFixture.InDirectory(dir =>
    {
        MapDocumentFile.SaveTiled(SurfaceStorageFixtures.ReverseDependants(withFinePage: true), dir);
        MapDocument window = SurfaceStorageFixtures.LoadSlotWindow(dir, 0);
        Assert.False(window.Surfaces.Patches.ContainsKey(new("fine", 40, 0)));
        window.Surfaces.Patches[SurfaceStorageFixtures.Wide0].Heights[1] = 1100;
        Assert.Equal(1, window.Surfaces.Refs.RemoveAll(s => s.Id == "fine"));
        Assert.Empty(MapDocumentValidator.Validate(window, MapDocRegistry.CreateDefault()));
        byte[] manifest = File.ReadAllBytes(Path.Combine(dir, "map.json"));
        string[] files = SurfaceStorageFixtures.SurfaceFiles(dir).ToArray();
        var error = Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveTiled(window, dir));
        Assert.Contains("unloaded: reverse dependants unknown", error.Message);
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(dir, "map.json")));
        Assert.Equal(files, SurfaceStorageFixtures.SurfaceFiles(dir));
    });

    [Fact]
    public void CachedFilteredPagesStillHaveABoundedTraversal() => TiledDocFixture.InDirectory(dir =>
    {
        MapDocument doc = SurfaceStorageFixtures.FlatPatches(33);
        MapSurfaceRef surface = doc.Surfaces.Refs.Single();
        doc.Surfaces.Refs[0] = surface with { Frame = surface.Frame with { CellUnitMetres = new(1, 1000) } };
        MapDocumentFile.SaveTiled(doc, dir);
        MapStoredSurfaceSource source = MapStoredSurfaceSource.Open(dir);
        foreach (MapPatchKey key in doc.Surfaces.Patches.Keys)
            Assert.Equal(MapPatchStatus.Present, source.ReadPatch(key).Status);
        var scope = new MapSurfaceScope(WorldFrame.Origin, Vector2.Zero, new Vector2(3, 1), 100, 101,
            new[] { MapSurfaceRole.SupportFloor }, null, new MapQueryLimits(MaxCandidatePatches: 1, MaxPageReads: 2));
        MapPatchFindResult result = source.FindPatches(scope);
        Assert.Equal(MapFindStatus.CapacityExceeded, result.Status);
        Assert.Equal(0, result.PagesRead);
        Assert.Empty(result.Patches);
        Assert.Empty(result.KnownEmpty);
        Assert.Empty(result.Unavailable);
    });

    [Fact]
    public void CapturedUnreadPagesCannotAccumulateUnboundedSentinels() => TiledDocFixture.InDirectory(dir =>
    {
        MapDocument doc = SurfaceStorageFixtures.FlatPatches(1);
        MapSurfaceRef surface = doc.Surfaces.Refs.Single();
        doc.Surfaces.Refs[0] = surface with { Frame = surface.Frame with { CellUnitMetres = new(1, 1000) } };
        foreach (long slot in new long[] { 256, 512 })
        {
            var key = new MapPatchKey("ground", slot, 0);
            doc.Surfaces.Patches.Add(key, SurfaceStorageFixtures.FlatPatch(key, 1000));
        }
        MapDocumentFile.SaveTiled(doc, dir);
        MapDocument window = SurfaceStorageFixtures.LoadSlotWindow(dir, -1);
        Assert.True(window.Tiles!.IsPartial);
        Assert.Equal(3, window.Tiles.Surfaces!.Directory.Count);
        Assert.True(window.Tiles.Surfaces.ReadDirectoryPages.Count < 3);
        MapDocumentSurfaceSource source = MapDocumentSurfaceSource.Capture(window);
        var scope = new MapSurfaceScope(WorldFrame.Origin, Vector2.Zero, new Vector2(33, 1), 100, 101,
            new[] { MapSurfaceRole.SupportFloor }, null, new MapQueryLimits(MaxCandidatePatches: 1, MaxPageReads: 1));
        MapPatchFindResult result = source.FindPatches(scope);
        Assert.Equal(MapFindStatus.CapacityExceeded, result.Status);
        Assert.Equal(0, result.PagesRead);
        Assert.Empty(result.Patches);
        Assert.Empty(result.KnownEmpty);
        Assert.Empty(result.Unavailable);
    });
}
