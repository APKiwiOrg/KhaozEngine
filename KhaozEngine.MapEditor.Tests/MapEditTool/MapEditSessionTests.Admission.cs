using System;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEdit;
using Xunit;
using SampleDocs = KhaozEngine.Tests.MapDoc.MapDocumentFileTests;
using TiledFixture = KhaozEngine.Tests.MapDoc.TiledDocFixture;

namespace KhaozEngine.Tests.MapEditTool;

public partial class MapEditSessionTests
{
    [Fact]
    public void SetWindow_ThrowsOnMonolithicDocument()
    {
        string dir = NewTempDir();
        try
        {
            string path = Path.Combine(dir, "zone.map.json");
            MapDocumentFile.Save(SampleDocs.SampleDoc(), path);
            var session = new MapEditSession();
            session.Open(path);

            Assert.True(session.HasDocument);
            Assert.False(session.IsDirty);
            Assert.False(session.WindowStatus().Tiled);
            string before = session.WithDocument((doc, registry) => MapDocumentFile.SaveText(doc, registry));
            var error = Assert.Throws<MapSessionException>(() => session.SetWindow(-10f, -10f, 10f, 10f));
            Assert.IsAssignableFrom<InvalidOperationException>(error);
            Assert.Equal(MapDocumentRefusal.ToolTiledSessionRequired, error.Reason);
            Assert.Equal(path, error.Path);
            Assert.Equal("SetWindow", error.Operation);
            Assert.Equal(before, session.WithDocument((doc, registry) => MapDocumentFile.SaveText(doc, registry)));

            session.ConvertToTiled(Path.Combine(dir, "tiled"));
            Assert.True(session.SetWindow(-10f, -10f, 10f, 10f).Tiled);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void SetWindow_ThrowsWhenDirtyWithoutDiscard()
    {
        string dir = TiledFixture.NewDirectory();
        try
        {
            MapDocumentFile.SaveTiled(TiledFixture.SampleDoc(), dir);
            var session = new MapEditSession();
            session.Open(dir);

            session.Mutate((d, r) =>
            {
                d.Placements.Add(new MapPlacement { Id = "new-1", Kind = "rock", X = 1f, Z = 1f });
                return 0;
            }, worldChanged: false);
            Assert.True(session.IsDirty);

            string before = session.WithDocument((doc, registry) => MapDocumentFile.SaveText(doc, registry));
            var error = Assert.Throws<MapSessionException>(() => session.SetWindow(-100f, -100f, 100f, 100f));
            Assert.Equal(MapDocumentRefusal.ToolUnsavedChanges, error.Reason);
            Assert.Equal("IsDirty", error.FieldPath);
            Assert.Equal("SetWindow", error.Operation);
            Assert.Equal(before, session.WithDocument((doc, registry) => MapDocumentFile.SaveText(doc, registry)));
            Assert.True(session.IsDirty);

            session.SetWindow(-100f, -100f, 100f, 100f, discard: true);
            Assert.False(session.IsDirty);
            Assert.False(session.WithDocument((doc, _) => doc.Placements.Any(p => p.Id == "new-1")));
        }
        finally { TiledFixture.Delete(dir); }
    }

    [Fact]
    public void Create_RefusesAnExistingTiledDirectory()
    {
        string dir = TiledFixture.NewDirectory();
        try
        {
            MapDocumentFile.SaveTiled(TiledFixture.SampleDoc(), dir);
            var session = new MapEditSession();

            Assert.Throws<IOException>(() =>
                session.Create(dir, "new-id", "New", -10f, -10f, 10f, 10f, overwrite: false));
            string before = MapDocumentFile.SaveText(MapDocumentFile.LoadTiled(dir));
            var error = Assert.Throws<MapDocumentException>(() =>
                session.Create(dir, "new-id", "New", -10f, -10f, 10f, 10f, overwrite: true));
            Assert.Equal(MapDocumentRefusal.DocumentStorageFormConflict, error.Reason);
            Assert.Equal(dir, error.Path);
            Assert.Equal(MapDocumentForm.Tiled, error.DocumentForm);
            Assert.Equal(before, MapDocumentFile.SaveText(MapDocumentFile.LoadTiled(dir)));
            Assert.False(session.HasDocument);
            Assert.NotNull(session.Create(Path.Combine(dir, "new-map.json"), "new-id", "New", -10f, -10f, 10f, 10f, overwrite: true));
        }
        finally { TiledFixture.Delete(dir); }
    }
}
