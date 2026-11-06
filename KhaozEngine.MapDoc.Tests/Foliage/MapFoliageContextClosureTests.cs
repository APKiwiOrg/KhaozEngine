using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageContextClosureTests
{
    [Fact]
    public void NumericExpansion_PropagatesEveryFindingWithoutChangingTheDocument()
    {
        var fixture = FoliageFixtures.Create();
        var document = fixture.Document;
        document.Placements.Add(new() { Id = "first", Kind = "item", AssetId = "reed-a", NumericId = 7,
            Y = 1 });
        document.Placements.Add(new() { Id = "second", Kind = "item", AssetId = "reed-a", NumericId = 7,
            Y = 1 });
        string before = Snapshot(document);
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabResolver.Expand(document, fixture.Assets));
        Assert.Equal(MapDocumentRefusal.NumericIdHighWaterExceeded, error.Reason);
        Assert.Equal("first", error.PlacementId);
        Assert.Equal(7, error.NumericId);
        Assert.Equal(0, error.HighWaterMark);
        Assert.Equal(new[] { MapDocumentRefusal.NumericIdHighWaterExceeded, MapDocumentRefusal.IdentityDuplicate,
            MapDocumentRefusal.NumericIdHighWaterExceeded }, error.Findings!.Select(f => f.Reason));
        var result = MapFoliageGenerationContext.Capture(document, fixture.Assets);
        Assert.Equal(MapFoliageStatus.Invalid, result.Status);
        Assert.Null(result.Context);
        Assert.Equal(error.Reason, result.Finding!.Reason);
        Assert.Equal("first", result.Finding.PlacementId);
        Assert.Equal(error.Findings, result.Findings);
        Assert.Equal(before, Snapshot(document));
        document.NumericIdHighWaterMark = 8;
        document.Placements[1].NumericId = 8;
        Assert.NotNull(MapPrefabResolver.Expand(document, fixture.Assets));
        Assert.Equal(MapFoliageStatus.Complete, MapFoliageGenerationContext.Capture(document, fixture.Assets).Status);
    }

    [Fact]
    public void BoundFoliageRefusal_KeepsTheOrderedSnapshotThroughFinalization()
    {
        var fixture = FoliageFixtures.Create();
        var resources = MapPrefabResolver.Expand(fixture.Document, fixture.Assets);
        var layer = fixture.Document.Foliage!.Layers[0];
        var draft = new FoliageLayerDraft(layer) { Models = [new("absent-a", 1), new("absent-b", 1)] };
        fixture.Document.Foliage = new(1, [draft.Build()]);
        string before = Snapshot(fixture.Document);
        var result = MapFoliageGenerationContext.Capture(fixture.Document, fixture.Assets);
        Assert.Equal(MapFoliageStatus.Invalid, result.Status);
        Assert.Equal(MapDocumentRefusal.ReferenceMissing, result.Finding!.Reason);
        Assert.Equal("absent-a", result.Finding.AssetId);
        var findings = result.Findings!;
        Assert.Equal(new[] { "absent-a", "absent-b" }, findings.Select(f => f.AssetId));
        Assert.Throws<NotSupportedException>(() => ((IList<MapDocumentFinding>)findings)[0] = findings[1]);
        var side = new MapDocumentFinalizationSide("captured", fixture.Assets, resources);
        var error = Assert.Throws<MapDocumentException>(() => side.Capture(fixture.Document));
        Assert.Equal(result.Finding.Reason, error.Reason);
        Assert.Equal("absent-a", error.AssetId);
        Assert.Equal(MapFoliageStatus.Invalid, error.FoliageStatus);
        Assert.Equal(result.Findings, error.Findings);
        Assert.Equal(before, Snapshot(fixture.Document));
        fixture.Document.Foliage = new(1, [layer]);
        Assert.Equal(MapFoliageStatus.Complete, MapFoliageGenerationContext.Capture(fixture.Document, fixture.Assets).Status);
        Assert.NotNull(side.Capture(fixture.Document));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SnapshotAdmission_IdentifiesTheMismatchThroughFinalization(bool fingerprint)
    {
        var fixture = FoliageFixtures.Create();
        var resources = MapPrefabResolver.Expand(fixture.Document, fixture.Assets);
        if (fingerprint) fixture.Document.Id += "-changed";
        else fixture.Document.Surfaces.Refs.Add(fixture.Document.Surfaces.Refs[0] with { Id = "different" });
        string before = Snapshot(fixture.Document);
        var result = MapFoliageGenerationContext.CaptureEffective(fixture.Document, fixture.Assets, resources);
        Assert.Equal(MapFoliageStatus.Invalid, result.Status);
        Assert.Equal(MapDocumentRefusal.AcquisitionSnapshotChanged, result.Finding!.Reason);
        var error = Assert.Throws<MapDocumentException>(() =>
            new MapDocumentFinalizationSide("captured", fixture.Assets, resources).Capture(fixture.Document));
        Assert.Equal(result.Finding.Reason, error.Reason);
        Assert.Equal(before, Snapshot(fixture.Document));
        if (fingerprint) fixture.Document.Id = "foliage-fixture";
        else fixture.Document.Surfaces.Refs.RemoveAt(fixture.Document.Surfaces.Refs.Count - 1);
        Assert.Equal(MapFoliageStatus.Complete,
            MapFoliageGenerationContext.CaptureEffective(fixture.Document, fixture.Assets, resources).Status);
    }

    [Theory]
    [InlineData("limit exceeded")]
    [InlineData("overflow")]
    public void TypedIdentifierKeyword_DoesNotSelectACapacityStatus(string identifier)
    {
        // X25: a caller's identifier must not turn a missing reference into a capacity refusal.
        var error = new MapDocumentException(MapDocumentRefusal.ReferenceMissing, identifier) { ResourceId = identifier };
        Assert.Equal(MapFoliageStatus.Invalid, MapFoliageContextRefusals.Classify(error));
        var capacity = MapPrefabCapacity.CollectionRefusal("children", 2, 1, "Prefab child limit exceeded or collection missing.");
        Assert.Equal(MapFoliageStatus.CapacityExceeded, MapFoliageContextRefusals.Classify(capacity));
        var missing = MapPrefabCapacity.CollectionRefusal("children", null, 1, "Prefab child limit exceeded or collection missing.");
        Assert.Equal(MapFoliageStatus.CapacityExceeded, MapFoliageContextRefusals.Classify(missing));
        Assert.Equal(MapFoliageStatus.CapacityExceeded,
            MapFoliageContextRefusals.Classify(new MapDocumentException("consumer limit exceeded")));
        Assert.Equal(MapFoliageStatus.NotRepresentable, MapFoliageContextRefusals.Classify(new OverflowException()));
        var overflow = Assert.Throws<MapDocumentException>(() => MapPrefabPreflight.AddProduct(long.MaxValue, 1, 1));
        Assert.Equal(MapFoliageStatus.Invalid, MapFoliageContextRefusals.Classify(overflow));
    }

    static string Snapshot(MapDocument document) => JsonSerializer.Serialize(document,
        MapDocumentFile.CreateOptions(MapDocRegistry.CreateDefault(), write: true));
}
