using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Tests.MapDoc.Storage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Water;

public sealed class MapWaterOccupiedRefusalTests
{
    [Fact]
    public void UnavailableAnchor_RetainsItsPatchAndAcceptsCompleteAcquisition()
    {
        var doc = WaterFixtures.OceanOverCaves();
        var domain = MapWaterDomain.Compile(doc.Water!.Bodies.Single(b => b.Id == "ocean").Domain);
        var view = WaterFixtures.ViewWithUnloaded(doc, "lake-floor");
        var error = Assert.Throws<MapDocumentException>(() => MapWaterOccupiedPieces.Of(view, domain.Min, domain.Max, new(), new(new())));
        Assert.Equal(MapDocumentRefusal.AcquisitionIncomplete, error.Reason);
        Assert.Equal(MapAcquireStatus.Incomplete, error.AcquireStatus);
        Assert.Equal(new MapPatchKey("lake-floor", 0, 0), error.Patch);
        Assert.Equal(MapPatchStatus.Unloaded, error.PatchStatus);
        Assert.NotEmpty(MapWaterOccupiedPieces.Of(CaveFixtures.View(doc), domain.Min, domain.Max, new(), new(new())));
    }

    [Fact]
    public void WindowNotProvenComplete_RetainsItsRuleAndAcceptsTheCompleteView()
    {
        var doc = WaterFixtures.PondsApart();
        var domain = MapWaterDomain.Compile(doc.Water!.Bodies.Single().Domain);
        var view = WaterFixtures.NearPondView(doc);
        Assert.True(view.Witness.Complete);
        var max = new MapExactXz(domain.Max.X.Add(new(1, 1)), domain.Max.Z);
        var error = Assert.Throws<MapDocumentException>(() => MapWaterOccupiedPieces.Of(view, domain.Min, max, new(), new(new())));
        Assert.Equal(MapDocumentRefusal.AcquisitionIncomplete, error.Reason);
        Assert.Equal(MapAcquireStatus.Complete, error.AcquireStatus);
        Assert.Equal("Scope", error.FieldPath);
        Assert.NotEmpty(MapWaterOccupiedPieces.Of(CaveFixtures.View(doc), domain.Min, max, new(), new(new())));
    }

    [Fact]
    public void UnrepresentableAcquisition_RetainsItsStatusAndAcceptsOrdinaryUnits()
    {
        var doc = AcquisitionBoundFixtures.AdversarialUnits();
        var view = ScopeFixtures.Acquire(MapDocumentSurfaceSource.Capture(doc), AcquisitionBoundFixtures.AdversarialScope);
        Assert.Equal(MapAcquireStatus.NotRepresentable, view.Status);
        var min = new MapExactXz(new(0, 1), new(0, 1));
        var max = new MapExactXz(new(1, 1), new(1, 1));
        var error = Assert.Throws<MapDocumentException>(() => MapWaterOccupiedPieces.Of(view, min, max, new(), new(new())));
        Assert.Equal(MapDocumentRefusal.GeometryNotRepresentable, error.Reason);
        Assert.Equal(MapAcquireStatus.NotRepresentable, error.AcquireStatus);
        Assert.Empty(MapWaterOccupiedPieces.Of(MapScopedSurfaces.CompleteView(doc.Surfaces), min, max, new(), new(new())));
    }
}
