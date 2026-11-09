using System;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapSceneCompletenessTests
{
    [Fact]
    public void MetadataAdmission_RefusesBeforeRetention()
    {
        using var fixture = NativeSceneFixtures.Create("growth");
        using var document = MapDocumentSource.FromDocument(fixture.Document);
        var result = MapSceneSource.Capture(document, fixture.Assets, new(MaxMetadataRows: 0));
        Assert.Equal(MapSceneSourceStatus.CapacityExceeded, result.Status);
        Assert.Null(result.Source);
        Assert.Equal(0, result.Work.RetainedBytes);
        Assert.Equal(0, result.Work.MetadataRows);
    }

    [Fact]
    public void MissingUnloadedAndKnownEmpty_AreDifferentObservations()
    {
        using var fixture = NativeSceneFixtures.Create("growth");
        using var disk = NativeSceneFixtures.OpenTiled(fixture);
        string directory = disk.Tiles.SourceDirectory!;
        try
        {
            var empty = NativeSceneFixtures.Open(NativeSceneFixtures.Create("empty"), false).Acquire(MapSceneScopeTests.Window());
            Assert.True(empty.Witness.CompleteForScope);
            Assert.NotEmpty(empty.Witness.KnownEmpty);
            var partial = MapDocumentFile.LoadTiled(directory, new MapTileRect(new(-1, -1), new(-1, -1)));
            using var memory = MapDocumentSource.FromDocument(partial);
            var unloaded = MapSceneSource.Capture(memory, fixture.Assets);
            Assert.Equal(MapSceneSourceStatus.Incomplete, unloaded.Status);
            Assert.Null(unloaded.Source);
            string tile = Directory.GetFiles(Path.Combine(directory, "tiles"), "*.json", SearchOption.AllDirectories)[0];
            File.Delete(tile);
            var missing = MapSceneSource.Capture(disk, fixture.Assets);
            Assert.Equal(MapSceneSourceStatus.Missing, missing.Status);
            Assert.Null(missing.Source);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Dependencies_KeepCapturedGenerationAndBudgets()
    {
        using var fixture = NativeSceneFixtures.Create("growth");
        using var disk = NativeSceneFixtures.OpenTiled(fixture);
        string directory = disk.Tiles.SourceDirectory!;
        try
        {
            var source = MapSceneSource.Capture(disk, fixture.Assets).Source!;
            var scene = source.Acquire(MapSceneScopeTests.Window()).Scene!;
            fixture.Document.DisplayName = "new manifest generation";
            MapDocumentFile.SaveTiled(fixture.Document, directory);
            var stale = scene.AcquireDependencies(MapSceneScopeTests.Window(-64, 0));
            Assert.Equal(MapSceneStatus.Stale, stale.Status);
            Assert.Null(stale.Scene);
            Assert.False(stale.Witness.CompleteForScope);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public void DependencyAcquisition_CannotWidenCapturedQueryBudgets()
    {
        using var fixture = NativeSceneFixtures.Create("spanning");
        var source = NativeSceneFixtures.Open(fixture, false);
        var scope = MapSceneScopeTests.Window();
        var scene = source.Acquire(scope with { Surfaces = scope.Surfaces with { Limits = new(MaxPageReads: 1) } }).Scene!;
        var result = scene.AcquireDependencies(scope);
        Assert.Equal(MapSceneStatus.Invalid, result.Status);
        Assert.Null(result.Scene);
        Assert.False(result.Witness.CompleteForScope);
        Assert.Equal(0, result.Work.PayloadReads);
    }

    [Fact]
    public void DuplicateTileMembers_RefuseEvenWhenCanonicalValuesAreUnchanged()
    {
        using var fixture = NativeSceneFixtures.Create("tile-owner");
        using var document = NativeSceneFixtures.OpenTiled(fixture);
        string path = Directory.GetFiles(Path.Combine(document.Tiles.SourceDirectory!, "tiles"), "*.json", SearchOption.AllDirectories).Single();
        string original = File.ReadAllText(path);
        string corrupted = original.Replace("\"id\": \"spanning-leaf\"", "\"id\": \"spanning-leaf\", \"id\": \"spanning-leaf\"");
        Assert.NotEqual(original, corrupted);
        File.WriteAllText(path, corrupted);
        var result = MapSceneSource.Capture(document, fixture.Assets);
        Assert.Equal(MapSceneSourceStatus.Corrupt, result.Status);
        Assert.Null(result.Source);
        Assert.Equal(0, result.Work.RetainedBytes);
    }

    [Fact]
    public void ReleasedTileMemberCasing_KeepsCanonicalIdentity()
    {
        using var fixture = NativeSceneFixtures.Create("tile-owner");
        using var document = NativeSceneFixtures.OpenTiled(fixture);
        string path = Directory.GetFiles(Path.Combine(document.Tiles.SourceDirectory!, "tiles"), "*.json", SearchOption.AllDirectories).Single();
        string text = File.ReadAllText(path).Replace("\"placements\"", "\"Placements\"").Replace("\"id\"", "\"ID\"");
        File.WriteAllText(path, text);
        var result = MapSceneSource.Capture(document, fixture.Assets);
        Assert.Equal(MapSceneSourceStatus.Ready, result.Status);
        Assert.Contains(result.Source!.Acquire(MapSceneScopeTests.Window()).Scene!.Placements, p => p.PlacementId == "spanning-leaf");
    }

    [Fact]
    public void AdvertisedUnloadedSurfacePayload_ProducesIncompleteWithoutKnownEmpty()
    {
        var fixture = KhaozEngine.Tests.MapDoc.Prefabs.PrefabFixtures.Create();
        using var native = new NativeSceneFixture(fixture.Document, fixture.Assets);
        using var disk = NativeSceneFixtures.OpenTiled(native);
        var captured = MapSceneSource.Capture(disk, fixture.Assets);
        Assert.Equal(MapSceneSourceStatus.Ready, captured.Status);
        var stored = Assert.IsType<KhaozEngine.MapDoc.Storage.MapStoredSurfaceSource>(captured.Source!.Data.Surfaces);
        var partial = MapDocumentFile.LoadTiled(disk.Tiles.SourceDirectory!, new MapTileRect(new(100, 100), new(100, 100)));
        partial.Tiles = new MapTileIndex(partial.TileSize, MapDocumentHash.SchemeVersion, null, partial.Tiles!.Entries,
            surfaces: stored.Index.Clone());
        using var memory = MapDocumentSource.FromDocument(partial);
        var source = MapSceneSource.Capture(memory, fixture.Assets);
        Assert.Equal(MapSceneSourceStatus.Ready, source.Status);
        var unloaded = source.Source!.Acquire(MapSceneScopeTests.Window(-64, 64));
        Assert.Equal(MapSceneStatus.Incomplete, unloaded.Status);
        Assert.Null(unloaded.Scene);
        Assert.Empty(unloaded.Witness.KnownEmpty);
        Assert.False(unloaded.Witness.CompleteForScope);
    }

}
