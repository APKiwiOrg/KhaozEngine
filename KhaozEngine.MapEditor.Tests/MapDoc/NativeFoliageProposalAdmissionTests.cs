using System;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc.Foliage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeFoliageProposalAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StageFirstLayer_RefusesBeforeAnyCloneExpansionOrCapture(bool chunkless)
    {
        var fixture = FoliageFixtures.PaintTargets(4097);
        var layer = fixture.Document.Foliage!.Layers[0];
        if (chunkless) layer = FoliageStorageFixtures.Change(layer, l => l["chunks"] = new System.Text.Json.Nodes.JsonArray());
        fixture.Document.Foliage = null;
        string before = MapDocumentFile.SaveText(fixture.Document);
        var command = new FoliageEditCommand(new MapSetFoliageLayer(layer));
        long clones = NativeDocumentSnapshot.ThreadClones, copies = MapPrefabEditPreparation.ThreadCopies;
        long expansions = MapPrefabResolver.ThreadExpansions, captures = MapFoliageGenerationContext.ThreadCaptures;

        Assert.StartsWith("Foliage CapacityExceeded", Assert.Throws<MapDocumentException>(() =>
            NativeDocumentTransaction.Stage(fixture.Document, command, false, fixture.Assets)).Message, StringComparison.Ordinal);

        Assert.Equal((0L, 0L, 0L, 0L), (NativeDocumentSnapshot.ThreadClones - clones, MapPrefabEditPreparation.ThreadCopies - copies,
            MapPrefabResolver.ThreadExpansions - expansions, MapFoliageGenerationContext.ThreadCaptures - captures));
        Assert.Equal(before, MapDocumentFile.SaveText(fixture.Document));
        Assert.Null(command.AcceptedStamp);
    }
}
