using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class DocumentPersistenceRefusalTests
{
    [Theory]
    [InlineData("newer", MapDocumentRefusal.DocumentVersionUnsupported)]
    [InlineData("missing", MapDocumentRefusal.DocumentMigrationMissing)]
    [InlineData("null", MapDocumentRefusal.DocumentMigrationReturnedNull)]
    public void MigrationAdmission_ReportsTheSourceVersionAndRepair(string fault, MapDocumentRefusal reason)
    {
        string valid = NativeFixtures.AnalyticV3Json();
        if (fault == "newer") valid = MapDocumentFile.SaveText(MapDocumentFile.LoadText(valid));
        var root = JsonNode.Parse(valid)!.AsObject();
        var options = new MapDocumentLoadOptions();
        if (fault == "newer") root["formatVersion"] = MapDocumentFile.CurrentFormatVersion + 1;
        else if (fault == "missing") options.Migrations.Remove(3);
        else options.Migrations[3] = _ => null!;
        var error = Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(root.ToJsonString(), options, "source"));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(fault == "newer" ? MapDocumentFile.CurrentFormatVersion + 1 : 3, error.ActualVersion);
        Assert.Equal("source", error.SourceName);
        Assert.Equal("$.formatVersion", error.FieldPath);
        if (fault == "missing") Assert.Equal(4, error.TargetVersion);
        if (fault == "newer") Assert.Equal(new[] { MapDocumentFile.CurrentFormatVersion }, error.SupportedVersions);
        Assert.Equal(MapDocumentFile.CurrentFormatVersion, MapDocumentFile.LoadText(valid).FormatVersion);
    }

    [Fact]
    public void MissingDocumentRead_IdentifiesThePathBesideAnExistingFileControl()
    {
        string directory = SurfaceStorageFixtures.SaveToTemp(SurfaceStorageFixtures.FlatPatches(1));
        try
        {
            string path = Path.Combine(directory, "missing.json");
            var error = Assert.Throws<MapDocumentException>(() => MapDocumentFile.Load(path));
            Assert.Equal(MapDocumentRefusal.DocumentReadFailed, error.Reason);
            Assert.Equal(path, error.Path);
            Assert.Equal(path, error.SourceName);
            MapDocumentFile.Save(SurfaceStorageFixtures.FlatPatches(1), path);
            Assert.Single(MapDocumentFile.Load(path).Surfaces.Patches);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SaveDispatch_RefusesBeforeChangingAnySavedContent(bool missingSource)
    {
        MapDocument doc = SurfaceStorageFixtures.FlatPatches(1);
        string root = SurfaceStorageFixtures.SaveToTemp(doc);
        try
        {
            string path = Path.Combine(root, "new.json");
            var before = AssertFixtures.Files(root);
            Assert.NotEmpty(before);
            var error = Assert.Throws<MapDocumentException>(() =>
            {
                if (missingSource) MapDocumentFile.SaveAuto(doc, path);
                else MapDocumentFile.SaveAs(doc, path, MapDocumentForm.None);
            });
            Assert.Equal(missingSource ? MapDocumentRefusal.DocumentSaveSourceMissing : MapDocumentRefusal.DocumentSaveFormInvalid, error.Reason);
            Assert.Equal(path, error.Path);
            Assert.Equal(MapDocumentForm.None, error.DocumentForm);
            Assert.Equal(before, AssertFixtures.Files(root));
            MapDocumentFile.SaveAs(doc, path, MapDocumentForm.Monolithic);
            Assert.Single(MapDocumentFile.Load(path).Surfaces.Patches);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void PartialWholeSave_PreservesTheExistingFileBeforeTheWholeControl()
    {
        MapDocument doc = SurfaceStorageFixtures.FlatPatches(1);
        string root = SurfaceStorageFixtures.SaveToTemp(doc);
        try
        {
            string path = Path.Combine(root, "whole.json");
            MapDocumentFile.Save(doc, path);
            var index = doc.Tiles;
            doc.Tiles = new MapTileIndex(doc.TileSize, MapDocumentHash.SchemeVersion, root, [new(new(0, 0), "hash", false)]);
            var before = AssertFixtures.Files(root);
            Assert.NotEmpty(before);
            var error = Assert.Throws<MapDocumentException>(() => MapDocumentFile.Save(doc, path));
            Assert.Equal(MapDocumentRefusal.CompleteDocumentRequired, error.Reason);
            Assert.Equal(doc.Id, error.DocumentId);
            Assert.Equal("SaveWholeDocument", error.Operation);
            Assert.Equal(before, AssertFixtures.Files(root));
            doc.Tiles = index;
            MapDocumentFile.Save(doc, path);
            Assert.Single(MapDocumentFile.Load(path).Surfaces.Patches);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("surfaces")]
    [InlineData("surfacePatches")]
    [InlineData("duplicate")]
    public void SurfaceSetters_KeepTheJsonBaseAndTypedSubject(string fault)
    {
        var doc = new MapDocument();
        MapSurfacePatch patch = SurfaceStorageFixtures.FlatPatch(new("ground", 0, 0), 0);
        var error = Assert.Throws<MapSurfaceJsonException>(() =>
        {
            if (fault == "surfaces") doc.SurfaceRefs = null!;
            else doc.SurfacePatches = fault == "duplicate" ? [patch, patch] : null;
        });
        Assert.IsAssignableFrom<JsonException>(error);
        Assert.Equal(fault == "duplicate" ? MapDocumentRefusal.IdentityDuplicate : MapDocumentRefusal.SchemaMemberNull, error.Reason);
        Assert.Equal(fault == "surfaces" ? "$.surfaces" : "$.surfacePatches", error.FieldPath);
        if (fault == "duplicate") { Assert.Equal(patch.Key, error.Patch); Assert.Equal(1, error.Index); }
        var wrapped = MapDocumentJsonErrors.Wrap(error, "source", "invalid");
        Assert.Equal(error.Reason, wrapped.Reason);
        Assert.Equal(error.FieldPath, wrapped.FieldPath);
        Assert.Equal(error.Patch, wrapped.Patch);
        Assert.Equal(error.Index, wrapped.Index);
        doc.SurfaceRefs = [];
        doc.SurfacePatches = [patch];
        Assert.Single(doc.Surfaces.Patches);
    }

    [Fact]
    public void UnknownFeature_UsesTheRegistryRuleAndARegisteredControl()
    {
        var registry = new MapDocRegistry();
        var feature = new LakeFeatureDoc();
        var error = Assert.Throws<MapDocumentException>(() => registry.BuildFeature(feature));
        Assert.Equal(MapDocumentRefusal.DocumentTerrainFeatureUnknown, error.Reason);
        Assert.Equal("lake", error.ActualText);
        Assert.Equal("Type", error.FieldPath);
        registry.RegisterFeature("lake", typeof(LakeFeatureDoc), value => ((LakeFeatureDoc)value).Build());
        Assert.NotNull(registry.BuildFeature(feature));
    }

    [Theory]
    [InlineData("X", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("Z", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("Y", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("Yaw", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("Scale", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("scale-zero", MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("Tags", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("tag-null", MapDocumentRefusal.SchemaValueRequired)]
    public void PlacementTransform_SelectsTheActualFaultBeforeLaterBranches(string fault, MapDocumentRefusal reason)
    {
        MapPlacement placement = SurfaceStorageFixtures.ExplicitYPlacement("placement", 0, 0);
        if (fault == "X") placement.X = float.PositiveInfinity;
        else if (fault == "Z") placement.Z = float.PositiveInfinity;
        else if (fault == "Y") placement.Y = float.PositiveInfinity;
        else if (fault == "Yaw") placement.Yaw = float.PositiveInfinity;
        else if (fault == "Scale") placement.Scale = float.PositiveInfinity;
        else if (fault == "scale-zero") placement.Scale = 0;
        else if (fault == "Tags") placement.Tags = null!;
        else placement.Tags = [null!];
        var error = Assert.Throws<MapDocumentException>(() => MapBoundDocumentValidation.RequirePlacementTransformForTests(placement));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(placement.Id, error.PlacementId);
        Assert.Equal(fault == "scale-zero" ? "Scale" : fault == "tag-null" ? "Tags[0]" : fault, error.FieldPath);
        if (reason == MapDocumentRefusal.ValueNonfinite) Assert.Equal(double.PositiveInfinity, error.ActualNumber);
        if (fault == "scale-zero") Assert.Equal(0, error.ActualNumber);
        if (fault == "tag-null") Assert.Equal(0, error.Index);
        placement.X = 0; placement.Z = 0; placement.Y = 10; placement.Yaw = 0; placement.Scale = 1; placement.Tags = [];
        MapBoundDocumentValidation.RequirePlacementTransformForTests(placement);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HashOrder_ReportsBothTilesAndAdmitsTheStrictAscendingBoundary(bool duplicate)
    {
        var first = new MapTileEntry(new(1, 2), "hash", true);
        var second = new MapTileEntry(duplicate ? first.Coord : new(0, 2), "hash", true);
        var error = Assert.Throws<MapDocumentException>(() => MapDocumentHash.Compose("manifest", [first, second]));
        Assert.Equal(MapDocumentRefusal.OrderInvalid, error.Reason);
        Assert.Equal(first.Coord, error.PreviousTile);
        Assert.Equal(second.Coord, error.Tile);
        Assert.NotEmpty(MapDocumentHash.Compose("manifest", [first, second with { Coord = new(2, 2) }]));
    }
}
