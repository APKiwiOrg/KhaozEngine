using System;
using System.IO;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapEdit;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

public sealed class NativeStoragePublicationRefusalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedStorage_RefusesBeforeContentOrHistoryChangesAndAcceptsRepair(bool missing)
    {
        using var f = new NativeLifecycleFixture();
        new MutationService(f.Session).PlacementLabel("gate", "unsaved");
        string before = MapDocumentFile.SaveText(f.SessionDocument);
        var history = f.Session.NativeHistoryState;
        byte[] saved = f.ReadSavedBytes();
        File.Delete(f.ValidPath);
        if (!missing) Directory.CreateDirectory(f.ValidPath);
        var error = Assert.Throws<MapDocumentException>(() => f.Session.Retile(32));
        Assert.Equal(MapDocumentRefusal.ToolStorageFormChanged, error.Reason);
        Assert.Equal(f.ValidPath, error.Path);
        Assert.Equal(missing ? MapDocumentForm.None : MapDocumentForm.Tiled, error.DocumentForm);
        Assert.Equal(MapDocumentForm.Monolithic, error.ExpectedDocumentForm);
        Assert.Equal(before, MapDocumentFile.SaveText(f.SessionDocument));
        Assert.Equal(history, f.Session.NativeHistoryState);
        Assert.True(f.Session.IsDirty);
        if (!missing)
        {
            Assert.Empty(Directory.GetFileSystemEntries(f.ValidPath));
            Directory.Delete(f.ValidPath);
        }
        else Assert.False(File.Exists(f.ValidPath));
        File.WriteAllBytes(f.ValidPath, saved);
        Assert.Equal(32, f.Session.Retile(32).TileSize);
    }

    [Fact]
    public void ParentlessTarget_IdentifiesThePathBeforeAnySavedContentChanges()
    {
        using var f = new NativeLifecycleFixture();
        byte[] before = f.ReadSavedBytes();
        string root = Path.GetPathRoot(f.ValidPath)!;
        var error = Assert.Throws<MapDocumentException>(() => NativeDocumentService.SaveMonolithicStaged(
            f.Document, root, MapDocRegistry.CreateDefault()));
        Assert.Equal(MapDocumentRefusal.ToolParentDirectoryMissing, error.Reason);
        Assert.Equal(root, error.Path);
        Assert.Equal(before, f.ReadSavedBytes());
        string target = Path.Combine(f.ResourceRoot, "parent-control.json");
        NativeDocumentService.SaveMonolithicStaged(f.Document, target, MapDocRegistry.CreateDefault());
        Assert.Equal(f.Document.Id, MapDocumentFile.Load(target).Id);
    }

    [Fact]
    public void PrefabRootAdmission_RequiresOneManifestRoot()
    {
        var error = Assert.Throws<MapDocumentException>(() => PrefabAssetPublisher.RequireAssetRootForTests(0, "hut"));
        Assert.Equal(MapDocumentRefusal.ToolAssetRootRequired, error.Reason);
        Assert.Equal("hut", error.AssetId);
        Assert.Equal("Publish", error.Operation);
        PrefabAssetPublisher.RequireAssetRootForTests(1, "hut");
    }

    [Theory]
    [InlineData("different")]
    [InlineData("absent")]
    [InlineData("io")]
    [InlineData("known")]
    public void ImmutablePrefabPublication_RefusesWithoutChangingTheLiveMapAndAcceptsRepair(string fault)
    {
        var session = PrefabEditorFixtures.Session(PrefabFixtures.Create());
        var service = new PrefabService(session);
        var asset = service.Open("hut");
        var marker = PrefabEditorFixtures.Marker(asset);
        service.Edit(marker with { Value = marker.Value with { Name = "Changed" } });
        var files = new PublicationFiles(fault);
        var publisher = new PrefabAssetPublisher(session, fault == "io" ? null : files);
        string directory = Path.Combine(Path.GetDirectoryName(session.DocumentPath)!, "published");
        if (fault == "io") File.WriteAllBytes(directory, [7, 8, 9]);
        new MutationService(session).TerrainEdit(seed: 42);
        var before = PrefabEditorFixtures.State(session);
        Assert.True(session.NativeHistoryState.Depth > 0);
        string other = PrefabEditorFixtures.OtherMapBefore(session);
        byte[] saved = File.ReadAllBytes(session.DocumentPath!);
        var error = Assert.Throws<MapDocumentException>(() => publisher.Publish(service.Export(), "hut", "published", true));
        Assert.Equal(fault == "known" ? MapDocumentRefusal.DigestMismatch : fault == "io" ? MapDocumentRefusal.ToolResourceWriteFailed : MapDocumentRefusal.ToolImmutableResourceConflict, error.Reason);
        Assert.NotNull(error.Path);
        Assert.NotNull(error.ResourceId);
        if (fault == "known")
        {
            Assert.Same(files.Failure, error.InnerException);
            Assert.Equal("upstream-field", error.FieldPath);
            Assert.Equal("collider-sharer", error.OtherAssetId);
        }
        else if (fault == "io") Assert.NotNull(error.InnerException);
        else
        {
            Assert.NotNull(error.ExpectedHash);
            if (fault == "different") Assert.NotEqual(error.ExpectedHash, error.ActualHash);
            else Assert.Null(error.ActualHash);
        }
        Assert.Equal(before, PrefabEditorFixtures.State(session));
        Assert.Equal(other, PrefabEditorFixtures.OtherMapNow(session));
        Assert.Equal(saved, File.ReadAllBytes(session.DocumentPath!));
        if (fault == "io")
        {
            Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(directory));
            File.Delete(directory);
        }
        else if (fault == "different") File.Delete(error.Path!);
        files.Fault = "none";
        Assert.NotNull(publisher.Publish(service.Export(), "hut", "published", true).Mutation);
    }

    sealed class PublicationFiles(string fault) : IMapPrefabPublicationFiles
    {
        public string Fault { get; set; } = fault;
        public MapDocumentException Failure { get; } = new(MapDocumentRefusal.DigestMismatch, "selected resource refusal")
        { FieldPath = "upstream-field", OtherAssetId = "collider-sharer" };
        public void Checkpoint(string name) { }
        public void PublishNew(string path, ReadOnlyMemory<byte> bytes)
        {
            if (Fault == "known") throw Failure;
            if (Fault == "absent") return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Fault == "different" ? [9, 8, 7] : bytes.ToArray());
        }
    }
}
