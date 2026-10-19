using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.Tests.MapDoc.Foliage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

public sealed class MapFoliagePhysicalBoundaryTests
{
    [Fact]
    public void InteractionEnvelopeOneMetre_IsNeverAFoliageBlocker()
    {
        var fixture = FoliageFixtures.Clearance("solid");
        FoliageFixtures.WithClearance(fixture, l => FoliageFixtures.ClearanceLayer(l,
            rules: [new("tiny", "tree", null, MapFoliageClearanceKind.Solid, new(-0.05f, -0.05f, 0.05f, 0.05f))]));
        var capture = MapFoliageGenerationContext.Capture(fixture.Document, fixture.Assets);
        Assert.Equal(MapFoliageStatus.Complete, capture.Status);
        var context = capture.Context!;
        var layer = context.Foliage!.Layers[0];
        var support = MapFoliageSupport.Prepare(context, layer, new(0, 0), new());
        var clearance = MapFoliageClearance.Prepare(context, layer, new(0, 0), new());
        Assert.Equal(MapFoliageReason.Accepted, clearance.Evaluate(support.Sample(0.5f, -0.5f), 0.5f, -0.5f).Reason);
        var before = MapWorldBuilder.Build(fixture.Document, fixture.Assets, new(FoliageFixtures.Options(), "foliage-physical/1", new()));
        var envelope = Assert.Single(before.Placements).Envelope.Bounds;
        Assert.Equal(1d, envelope.MaxY - envelope.MinY, 5);
        FoliageFixtures.WithClearance(fixture, l => FoliageFixtures.ClearanceLayer(l,
            masks: [new("tiny-mask", FoliageFixtures.Owner("prop"), FoliageFixtures.Surface("floor-1"),
                MapFoliageClearanceKind.Solid, new(-0.05f, -0.05f, 0.05f, 0.05f))]));
        var after = MapWorldBuilder.Build(fixture.Document, fixture.Assets, new(FoliageFixtures.Options(), "foliage-physical/1", new()));
        Assert.Equal(before.Statics.Select(s => s.Digest), after.Statics.Select(s => s.Digest));
        Assert.Equal(before.Placements.Select(p => p.Digest), after.Placements.Select(p => p.Digest));
        var orphan = MapDocumentFile.LoadText(MapDocumentFile.SaveText(fixture.Document));
        orphan.Placements.Clear();
        Assert.Throws<MapDocumentException>(() => MapWorldBuilder.Build(orphan, fixture.Assets, new(FoliageFixtures.Options(), "foliage-physical/1", new())));
    }
}
