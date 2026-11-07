using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageClosureAdmissionTests
{
    [Fact]
    public void FoliagePreflight_RequiresAVerifiedClosureBeforeAnyWrite()
    {
        var f = FoliageFixtures.Create();
        string before = MapDocumentFile.SaveText(f.Document);
        var refusal = Assert.Throws<MapDocumentException>(() => MapDocumentEditFinalizer.Preflight(f.Document, null));
        Assert.Equal(MapDocumentRefusal.AssetClosureRequired, refusal.Reason);
        Assert.Equal(f.Document.Id, refusal.DocumentId);
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
        MapDocumentEditFinalizer.Preflight(f.Document, f.Assets);
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
    }
}
