using System;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageProposalAdmissionTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(false, 4)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    [InlineData(true, 4)]
    public void FirstLayer_AdmitsTheProposalBeforeCopyExpansionCaptureOrStampReuse(bool chunkless, int path)
    {
        var fixture = FoliageFixtures.PaintTargets(4097);
        var layer = fixture.Document.Foliage!.Layers[0];
        if (chunkless) layer = FoliageComparisonFixtures.Change(layer, l => l["chunks"] = new System.Text.Json.Nodes.JsonArray());
        fixture.Document.Foliage = null;
        MapBoundDocumentValidation.Validate(fixture.Document, fixture.Assets);
        var proposal = MapDocumentFile.LoadText(MapDocumentFile.SaveText(fixture.Document));
        proposal.Foliage = new(1, [layer]);
        MapBoundDocumentValidation.Validate(proposal, fixture.Assets);
        var prepared = new MapPrefabEditResult(proposal, new([], [], [], [], [], [], false) { Foliage = true },
            new(null, null, [], [], [], [], MapNativeInvalidation.None));
        var stamped = prepared with
        {
            Stamp = new(new(MapDocumentEditFinalizer.Fingerprint(fixture.Document), fixture.Assets, null),
                new(MapDocumentEditFinalizer.Fingerprint(proposal), fixture.Assets, null), [], [])
        };
        Action[] paths =
        {
            () => MapFoliageEdits.Prepare(fixture.Document, fixture.Assets, new MapSetFoliageLayer(layer)),
            () => MapDocumentEditFinalizer.Complete(fixture.Document, prepared, fixture.Assets, fixture.Assets),
            () => MapDocumentEditFinalizer.Complete(fixture.Document, stamped, fixture.Assets, fixture.Assets),
            () => MapDocumentEditFinalizer.Finalize(fixture.Document, stamped, fixture.Assets),
            () => MapDocumentEditFinalizer.Staged(fixture.Document, proposal, stamped.Effects, fixture.Assets, fixture.Assets,
                stamped.Stamp, null, stamped.Stamp!.Before.Fingerprint, stamped.Stamp.After.Fingerprint,
                () => throw new InvalidOperationException("before expansion"), () => throw new InvalidOperationException("after expansion")),
        };
        long copies = MapPrefabEditPreparation.ThreadCopies, expansions = MapPrefabResolver.ThreadExpansions;
        long captures = MapFoliageGenerationContext.ThreadCaptures;
        Assert.StartsWith("Foliage CapacityExceeded", Assert.Throws<MapDocumentException>(paths[path]).Message, StringComparison.Ordinal);
        Assert.Equal((0L, 0L, 0L), (MapPrefabEditPreparation.ThreadCopies - copies,
            MapPrefabResolver.ThreadExpansions - expansions, MapFoliageGenerationContext.ThreadCaptures - captures));
        Assert.Null(fixture.Document.Foliage);
    }
}
