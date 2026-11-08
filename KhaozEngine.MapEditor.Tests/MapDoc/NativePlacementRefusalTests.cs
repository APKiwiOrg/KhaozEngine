using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativePlacementRefusalTests
{
    [Fact]
    public void BlankPlacementIdentity_PreservesBothHistoryStacksAndAdmitsRepair()
    {
        using var fixture = new NativePlacementHistoryFixture();
        fixture.Editor.Execute(new SetPlacementLabelCommand("existing", "previous"));
        Assert.True(fixture.Editor.Undo());
        string before = MapDocumentFile.SaveText(fixture.Document);
        var error = Assert.Throws<MapDocumentException>(() =>
            fixture.Editor.Execute(new AllocateNativePlacementCommand(fixture.NewProp(""), true)));
        Assert.Equal(MapDocumentRefusal.SchemaValueRequired, error.Reason);
        Assert.Equal(fixture.Document.Id, error.DocumentId);
        Assert.Equal("placement", error.SubjectKind);
        Assert.Equal("Placements.Id", error.FieldPath);
        Assert.Equal("", error.EntityId);
        Assert.Equal(before, MapDocumentFile.SaveText(fixture.Document));
        Assert.True(fixture.Editor.Redo());
        Assert.True(fixture.Editor.Undo());
        Assert.Equal(before, MapDocumentFile.SaveText(fixture.Document));
        fixture.Editor.Execute(new AllocateNativePlacementCommand(fixture.NewProp("fresh"), true));
        Assert.Contains(fixture.Document.Placements, p => p.Id == "fresh");
    }

    [Fact]
    public void ViewportCompanionHost_IdentifiesTheMissingLayerAndAdmitsItsRepair()
    {
        using var viewport = new ViewportWorld(null!, []);
        var document = new MapDocument { Id = "companion-host", CompanionLayers = [new() { Name = "understory", HostLayer = "trees" }] };
        string before = System.Text.Json.JsonSerializer.Serialize(document);
        var error = Assert.Throws<MapDocumentException>(() => viewport.BuildPropLayers(document));
        Assert.Equal(MapDocumentRefusal.ReferenceMissing, error.Reason);
        Assert.Equal(document.Id, error.DocumentId);
        Assert.Equal("understory", error.LayerId);
        Assert.Equal("trees", error.EntityId);
        Assert.Equal("scatter layer", error.SubjectKind);
        Assert.Equal("CompanionLayers.HostLayer", error.FieldPath);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(document));
        document.ScatterLayers.Add(new() { Name = "trees" });
        Assert.Equal(2, viewport.BuildPropLayers(document).Count);
    }
}
