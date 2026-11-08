using System;
using System.Linq;
using KhaozEngine.MapDoc;
using Xunit;
using static KhaozEngine.Tests.MapDoc.StoredContentIdentityTests;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>The stored whole token refuses every document the document token refuses, with the same rule and semantic identifiers.</summary>
public sealed class StoredIdentityValidationTests
{
    static void AssertSameRefusal(MapDocument document, string directory, MapDocumentRefusal reason,
        Action<MapDocumentException> identifiers)
    {
        var loaded = Assert.Throws<MapDocumentException>(() => Token(document));
        var stored = Assert.Throws<MapDocumentException>(() => Token(directory));
        foreach (MapDocumentException error in new[] { loaded, stored })
        {
            Assert.Equal(reason, error.Reason);
            identifiers(error);
        }
        // Paths and indices are local to the document or tile slice. The semantic finding order agrees.
        Assert.Equal(loaded.Findings?.Select(f => (f.Reason, f.EntityId, f.SubjectKind, f.PlacementId,
            f.ActualNumber, f.MinimumNumber, f.NumericId, f.HighWaterMark)),
            stored.Findings?.Select(f => (f.Reason, f.EntityId, f.SubjectKind, f.PlacementId,
                f.ActualNumber, f.MinimumNumber, f.NumericId, f.HighWaterMark)));
    }

    static void AssertRepaired(MapDocument document, string directory) => Assert.Equal(Token(document), Token(directory));

    static MapDocument Sample() => StoredContentIdentityTests.Content();

    static MapPlacement Tree2(MapDocument doc) => doc.Placements.Single(p => p.Id == "tree-2");

    [Fact]
    public void StoredIdentity_PlacementAssetOutsideTheClosureRefuses() => TiledDocFixture.InDirectory(directory =>
    {
        MapDocument doc = Sample();
        Tree2(doc).AssetId = "ghost";
        MapDocumentFile.SaveTiled(doc, directory);

        AssertSameRefusal(MapDocumentFile.LoadTiled(directory), directory, MapDocumentRefusal.ReferenceMissing, error =>
        {
            Assert.Equal("ghost", error.AssetId);
            Assert.Equal("asset", error.SubjectKind);
        });
        Tree2(doc).AssetId = "tree";
        RewritePlacement(directory, "tree-2", p => p["assetId"] = "tree");
        AssertRepaired(doc, directory);
    });

    [Fact]
    public void StoredIdentity_EmptyPlacementAssetIdRefuses() => TiledDocFixture.InDirectory(directory =>
    {
        MapDocumentFile.SaveTiled(Sample(), directory);
        RewritePlacement(directory, "tree-2", p => p["assetId"] = "");

        MapDocument doc = MapDocumentFile.LoadTiled(directory);
        AssertSameRefusal(doc, directory, MapDocumentRefusal.SchemaValueRequired, error =>
        {
            Assert.Equal("tree-2", error.PlacementId);
            Assert.Equal("AssetId", error.FieldPath);
        });
        Tree2(doc).AssetId = "tree";
        RewritePlacement(directory, "tree-2", p => p["assetId"] = "tree");
        AssertRepaired(doc, directory);
    });

    [Fact]
    public void StoredIdentity_NonPositiveScaleRefuses() => TiledDocFixture.InDirectory(directory =>
    {
        MapDocument doc = Sample();
        Tree2(doc).Scale = 0f;
        MapDocumentFile.SaveTiled(Sample(), directory);
        RewritePlacement(directory, "tree-2", p => p["scale"] = 0f);

        AssertSameRefusal(doc, directory, MapDocumentRefusal.ValueOutsideRange, error =>
        {
            Assert.Equal("tree-2", error.PlacementId);
            Assert.Equal("tree-2", error.EntityId);
            Assert.Equal("placement", error.SubjectKind);
            Assert.Equal(0, error.ActualNumber);
            Assert.Equal(0, error.MinimumNumber);
            Assert.EndsWith(".scale", error.FieldPath, StringComparison.Ordinal);
            Assert.Single(error.Findings!);
        });
        Tree2(doc).Scale = 1;
        RewritePlacement(directory, "tree-2", p => p["scale"] = 1f);
        AssertRepaired(doc, directory);
    });

    [Fact]
    public void StoredIdentity_NumericIdAboveTheHighWaterMarkRefuses() => TiledDocFixture.InDirectory(directory =>
    {
        MapDocument saved = Sample();
        saved.NumericIdHighWaterMark = 2;
        Tree2(saved).NumericId = 2;
        MapDocumentFile.SaveTiled(saved, directory);
        RewritePlacement(directory, "tree-2", p => p["numericId"] = "9");
        MapDocument doc = Sample();
        doc.NumericIdHighWaterMark = 2;
        Tree2(doc).NumericId = 9;

        AssertSameRefusal(doc, directory, MapDocumentRefusal.NumericIdHighWaterExceeded, error =>
        {
            Assert.Equal("tree-2", error.PlacementId);
            Assert.Equal("tree-2", error.EntityId);
            Assert.Equal("placement", error.SubjectKind);
            Assert.Equal(9, error.NumericId);
            Assert.Equal(2, error.HighWaterMark);
            Assert.EndsWith(".numericId", error.FieldPath, StringComparison.Ordinal);
            Assert.Single(error.Findings!);
        });
        Tree2(doc).NumericId = 2;
        RewritePlacement(directory, "tree-2", p => p["numericId"] = "2");
        AssertRepaired(doc, directory);
    });

    [Fact]
    public void StoredIdentity_DuplicateNumericIdAcrossTilesRefuses() => TiledDocFixture.InDirectory(directory =>
    {
        MapDocument saved = Sample();
        saved.NumericIdHighWaterMark = 2;
        saved.Placements.Single(p => p.Id == "tree-1").NumericId = 1;
        Tree2(saved).NumericId = 2;
        MapDocumentFile.SaveTiled(saved, directory);
        Assert.NotEqual(PinnedTileHolding(directory, "\"tree-1\"").Coord, PinnedTileHolding(directory, "\"tree-2\"").Coord);
        RewritePlacement(directory, "tree-2", p => p["numericId"] = "1");
        MapDocument doc = Sample();
        doc.NumericIdHighWaterMark = 2;
        doc.Placements.Single(p => p.Id == "tree-1").NumericId = 1;
        Tree2(doc).NumericId = 1;

        AssertSameRefusal(doc, directory, MapDocumentRefusal.IdentityDuplicate, error =>
        {
            Assert.Equal("tree-2", error.PlacementId);
            Assert.Equal("tree-2", error.EntityId);
            Assert.Equal("placement", error.SubjectKind);
            Assert.Equal(1, error.NumericId);
            Assert.Equal(2, error.HighWaterMark);
            Assert.EndsWith(".numericId", error.FieldPath, StringComparison.Ordinal);
            Assert.Single(error.Findings!);
        });
        Tree2(doc).NumericId = 2;
        RewritePlacement(directory, "tree-2", p => p["numericId"] = "2");
        AssertRepaired(doc, directory);
    });

    [Fact]
    public void StoredIdentity_MissingPlayableBoundsRefuses() => TiledDocFixture.InDirectory(directory =>
    {
        MapDocumentFile.SaveTiled(Sample(), directory);
        EditManifest(directory, root => Assert.True(root.Remove("playableBounds")));

        MapDocument doc = MapDocumentFile.LoadTiled(directory);
        AssertSameRefusal(doc, directory, MapDocumentRefusal.SchemaValueRequired, error =>
        {
            Assert.Equal(doc.Id, error.DocumentId);
            Assert.Equal("PlayableBounds", error.FieldPath);
        });
        doc.PlayableBounds = Sample().PlayableBounds;
        EditManifest(directory, root => root["playableBounds"] = System.Text.Json.JsonSerializer.SerializeToNode(doc.PlayableBounds));
        AssertRepaired(doc, directory);
    });
}
