using System;
using System.IO;
using KhaozEngine.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeWindowBindingTests
{
    [Fact]
    public void CompleteWindowReplacementBindsTheFreshlyVerifiedClosure()
    {
        using var f = new NativePlacementHistoryFixture();
        string directory = Path.Combine(Path.GetDirectoryName(f.PathName)!, "tiled");
        MapDocumentFile.SaveTiled(f.Document, directory);
        f.CopyResourcesTo(directory);
        f.Session.Open(directory);
        f.Service.PlacementLabel("existing", "bound by open");
        f.Session.Save();
        var before = f.SessionDocument;
        f.Session.SetWindow(-1000, -1000, 1000, 1000);
        Assert.NotSame(before, f.SessionDocument);
        Assert.False(f.SessionDocument.Tiles!.IsPartial);
        MapBoundDocumentValidation.Validate(f.SessionDocument, f.Assets);
        Assert.False(f.Session.IsDirty);
        f.Service.PlacementLabel("existing", "accepted");
        Assert.Equal("accepted", f.SessionDocument.Placements[0].DisplayName);
    }

    [Fact]
    public void FailedWindowLoadPreservesDocumentAndBinding()
    {
        using var f = new NativePlacementHistoryFixture();
        string directory = Path.Combine(Path.GetDirectoryName(f.PathName)!, "tiled");
        MapDocumentFile.SaveTiled(f.Document, directory);
        f.CopyResourcesTo(directory);
        f.Session.Open(directory);
        var before = f.SessionDocument;
        string manifest = Path.Combine(directory, "map.json");
        string saved = File.ReadAllText(manifest);
        File.WriteAllText(manifest, "{}");
        try { Assert.ThrowsAny<Exception>(() => f.Session.SetWindow(-1000, -1000, 1000, 1000)); }
        finally { File.WriteAllText(manifest, saved); }
        Assert.Same(before, f.SessionDocument);
        f.Service.PlacementLabel("existing", "still bound");
        Assert.Equal("still bound", f.SessionDocument.Placements[0].DisplayName);
    }

    [Fact]
    public void StaleResourcesRefuseWindowReplacementAndKeepPriorBinding()
    {
        using var f = new NativePlacementHistoryFixture();
        string directory = Path.Combine(Path.GetDirectoryName(f.PathName)!, "tiled");
        MapDocumentFile.SaveTiled(f.Document, directory);
        f.CopyResourcesTo(directory);
        f.Session.Open(directory);
        var before = f.SessionDocument;
        File.WriteAllText(Path.Combine(directory, "mesh"), "stale mesh bytes");
        Assert.Throws<MapDocumentException>(() => f.Session.SetWindow(-1000, -1000, 1000, 1000));
        Assert.Same(before, f.SessionDocument);
        Assert.Equal(directory, f.Session.DocumentPath);
        Assert.False(f.Session.IsDirty);
        f.Service.PlacementLabel("existing", "prior binding");
        Assert.Equal("prior binding", f.SessionDocument.Placements[0].DisplayName);
    }
}
