using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageRequiredEditRefusalTests
{
    [Theory]
    [InlineData("list", false, "Edits")]
    [InlineData("list", true, "Edits")]
    [InlineData("step", false, "Edits[0]")]
    [InlineData("layer", false, "Layer")]
    [InlineData("layer", true, "Layer")]
    public void MissingEditValues_RefuseBeforeWritingAndAcceptTheRepairedValue(string fault, bool preflight, string field)
    {
        var fixture = FoliageFixtures.Create();
        var original = fixture.Document.Foliage;
        string before = FoliageStorageFixtures.Snapshot(fixture.Document);
        MapFoliageEdit edit = fault switch
        {
            "list" => new MapFoliageEditSequence(null!, null),
            "step" => new MapFoliageEditSequence([null!], null),
            _ => new MapSetFoliageLayer(null!),
        };
        var error = Assert.Throws<MapDocumentException>(() => Run(edit));
        Assert.Equal(MapDocumentRefusal.SchemaValueRequired, error.Reason);
        Assert.Equal(field, error.FieldPath);
        if (fault != "layer") Assert.Equal(fixture.Document.Id, error.DocumentId);
        if (fault == "step") Assert.Equal(0, error.Index);
        Assert.Same(original, fixture.Document.Foliage);
        Assert.Equal(before, FoliageStorageFixtures.Snapshot(fixture.Document));
        Run(fault == "layer" ? new MapSetFoliageLayer(original!.Layers[0]) : new MapFoliageEditSequence([], null));

        void Run(MapFoliageEdit request)
        {
            if (preflight) MapFoliageEdits.Preflight(fixture.Document, fixture.Assets, request);
            else MapFoliageEdits.Prepare(fixture.Document, fixture.Assets, request);
        }
    }
}
