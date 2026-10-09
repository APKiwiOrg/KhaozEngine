using System;
using System.Linq;
using KhaozEngine.MapDoc;
using Xunit;
using static KhaozEngine.Tests.MapDoc.StoredContentIdentityTests;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>The stored whole token refuses every document the document token refuses, with the same message.</summary>
public sealed class StoredIdentityValidationTests
{
    static void AssertSameRefusal(MapDocument document, string directory, string expected)
    {
        Assert.Equal(expected, Assert.Throws<MapDocumentException>(() => Token(document)).Message);
        Assert.Equal(expected, Assert.Throws<MapDocumentException>(() => Token(directory)).Message);
    }

    static MapDocument Sample() => StoredContentIdentityTests.Content();

    static MapPlacement Tree2(MapDocument doc) => doc.Placements.Single(p => p.Id == "tree-2");

    [Fact]
    public void StoredIdentity_PlacementAssetOutsideTheClosureRefuses() => TiledDocFixture.InDirectory(directory =>
    {
        MapDocument doc = Sample();
        Tree2(doc).AssetId = "ghost";
        MapDocumentFile.SaveTiled(doc, directory);

        AssertSameRefusal(MapDocumentFile.LoadTiled(directory), directory, "Native asset 'ghost' is not in the verified closure.");
    });

    [Fact]
    public void StoredIdentity_EmptyPlacementAssetIdRefuses() => TiledDocFixture.InDirectory(directory =>
    {
        MapDocumentFile.SaveTiled(Sample(), directory);
        RewritePlacement(directory, "tree-2", p => p["assetId"] = "");

        AssertSameRefusal(MapDocumentFile.LoadTiled(directory), directory, "Placement 'tree-2' requires a native asset ID.");
    });

    [Fact]
    public void StoredIdentity_NonPositiveScaleRefuses() => TiledDocFixture.InDirectory(directory =>
    {
        MapDocument doc = Sample();
        Tree2(doc).Scale = 0f;
        MapDocumentFile.SaveTiled(Sample(), directory);
        RewritePlacement(directory, "tree-2", p => p["scale"] = 0f);

        AssertSameRefusal(doc, directory, "placement 'tree-2': scale must be positive.");
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

        AssertSameRefusal(doc, directory, "placement 'tree-2': numericId exceeds numericIdHighWaterMark.");
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

        AssertSameRefusal(doc, directory, "placement 'tree-2': duplicate numericId '1'.");
    });

    [Fact]
    public void StoredIdentity_MissingPlayableBoundsRefuses() => TiledDocFixture.InDirectory(directory =>
    {
        MapDocumentFile.SaveTiled(Sample(), directory);
        EditManifest(directory, root => Assert.True(root.Remove("playableBounds")));

        AssertSameRefusal(MapDocumentFile.LoadTiled(directory), directory, "Native document requires playable bounds.");
    });
}
