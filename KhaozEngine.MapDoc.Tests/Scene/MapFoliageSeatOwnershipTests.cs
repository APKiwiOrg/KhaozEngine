using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Tests.MapDoc.Foliage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapFoliageSeatOwnershipTests
{
    [Fact]
    public void WitnessOnlySeats_ReturnToTheirEntryOwnershipWhileDensitySurvives()
    {
        var fixture = FoliageFixtures.Clearance("solid");
        fixture.Document.Placements[0].Y = 100;
        var original = fixture.Document.Foliage!;
        fixture.Document.Foliage = new(original.PayloadVersion,
            [FoliageFixtures.ClearanceLayer(original.Layers[0], owners: [new(FoliageFixtures.Owner("prop"), FoliageFixtures.Surface("floor-1"))])]);
        using var saved = WindowFoliageFixtures.Scene(fixture);
        var source = NativeSceneFixtures.Open(saved, false);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 20_000_000));
        var diagnostics = admission.ObserveAcquisition();
        var boundaries = new List<MapAcquisitionBoundary>();
        diagnostics.Boundary = boundary =>
        {
            if (boundary.Stage == MapAcquisitionStage.FoliageSeat) boundaries.Add(boundary);
        };
        using var active = admission.Enter();
        var layer = fixture.Document.Foliage!.Layers[0];
        var result = MapFoliageGenerationContext.CaptureWindow(source, WindowFoliageFixtures.Scope(layer, new(0, 0)));
        Assert.True(result.Status == MapFoliageStatus.Complete, string.Join("\n", result.Findings));
        Assert.NotEmpty(boundaries);
        for (int i = 0; i < boundaries.Count; i += 2)
        {
            Assert.True(boundaries[i].Entering);
            Assert.False(boundaries[i + 1].Entering);
            Assert.Equal(boundaries[i].RetainedBytes, boundaries[i + 1].RetainedBytes);
            Assert.Equal(boundaries[i].WorkspaceBytes, boundaries[i + 1].WorkspaceBytes);
            Assert.Equal(boundaries[i].Receipts, boundaries[i + 1].Receipts);
        }
        Assert.NotEmpty(((IMapFoliageInputs)result.Context!).Foliage!.Layers.SelectMany(l => l.Chunks));
        Assert.True(diagnostics.Callers[MapAcquisitionCaller.FoliageSeat].Scopes > 0);
        Assert.Equal(0, admission.WorkspaceBytes);
        result.Context!.PreparationOwnership!.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }
}
