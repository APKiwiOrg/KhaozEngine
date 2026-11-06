using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEdit.Tools;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.Tests.MapDoc.Foliage;
using ModelContextProtocol;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

public sealed class ToolMutationAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullJsonResult_UsesTheOriginalJsonBaseAndNamesTheSubject(bool shape)
    {
        var registry = MapDocRegistry.CreateDefault();
        var error = Assert.Throws<MapToolJsonException>(() =>
        {
            if (shape) DocJson.ParseShape("null", registry);
            else DocJson.ParseFeature("null", registry);
        });
        Assert.IsAssignableFrom<JsonException>(error);
        Assert.Equal(MapDocumentRefusal.SchemaValueRequired, error.Reason);
        Assert.Equal("$", error.FieldPath);
        Assert.Equal(shape ? "shape" : "terrain feature", error.SubjectKind);
        if (shape) Assert.IsType<DiscShapeDoc>(DocJson.ParseShape("{\"type\":\"disc\",\"radius\":1}", registry));
        else Assert.IsType<LakeFeatureDoc>(DocJson.ParseFeature("{\"type\":\"lake\",\"radius\":1}", registry));
    }

    [Theory]
    [InlineData(false, "", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData(false, "[]", MapDocumentRefusal.SchemaObjectRequired)]
    [InlineData(true, "", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData(true, "[]", MapDocumentRefusal.SchemaObjectRequired)]
    public void FoliageWireShape_IdentifiesTheRequiredParameter(bool chunk, string json, MapDocumentRefusal reason)
    {
        var error = Assert.Throws<MapDocumentException>(() =>
        {
            if (chunk) FoliageWire.ReadChunkKey(json);
            else FoliageWire.ReadLayer(json);
        });
        Assert.Equal(reason, error.Reason);
        Assert.Equal(chunk ? "previewChunkJson" : "layerJson", error.FieldPath);
        if (chunk) Assert.Equal(new MapFoliageChunkKey(0, 0), FoliageWire.ReadChunkKey("{\"x\":0,\"z\":0}"));
        else Assert.Equal("wire-control", FoliageWire.ReadLayer(FoliageToolTests.LayerJson(FoliageFixtures.WireLayer("wire-control"))).Id);
    }

    [Fact]
    public void FoliageBindingConverter_KeepsTheKnownProducerReason()
    {
        var error = Assert.Throws<MapFoliageJsonException>(() => JsonSerializer.Deserialize<MapSupportBinding>(
            "{\"kind\":\"Other\"}", FoliageWire.ToolOptions));
        Assert.IsAssignableFrom<JsonException>(error);
        Assert.Equal(MapDocumentRefusal.SchemaEnumUnsupported, error.Reason);
        Assert.Equal("Other", error.ActualText);
        Assert.Equal(typeof(MapSupportBindingKind), error.EnumType);
        var inner = Assert.IsType<MapDocumentException>(error.InnerException);
        Assert.Equal(error.Reason, inner.Reason);
        Assert.NotNull(JsonSerializer.Deserialize<MapSupportBinding>("{\"kind\":\"Surface\",\"surfaceId\":\"floor\"}", FoliageWire.ToolOptions));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FoliageDryRun_ReportsTheRuleAndPreservesLiveContent(bool newLayer)
    {
        var fixture = FoliageFixtures.ReachableWirePreview();
        var session = FoliageEditorFixtures.Session(fixture);
        try
        {
            var mutation = new MutationService(session);
            mutation.TerrainEdit(seed: 42);
            MapFoliageEdit edit = newLayer ? new MapSetFoliageLayer(FoliageFixtures.WireLayer("new-layer"))
                : new MapRemoveFoliage(FoliageFixtures.WireLayerId);
            string before = FoliageEditorFixtures.State(session);
            byte[] saved = File.ReadAllBytes(session.DocumentPath!);
            var error = Assert.Throws<MapDocumentException>(() => mutation.FoliageApply(edit, dryRun: true));
            Assert.Equal(newLayer ? MapDocumentRefusal.FoliagePreviewLayerNew : MapDocumentRefusal.FoliagePreviewEditUnsupported, error.Reason);
            if (newLayer) Assert.Equal("new-layer", error.LayerId);
            else Assert.Equal(typeof(MapRemoveFoliage), error.EditType);
            Assert.Equal(before, FoliageEditorFixtures.State(session));
            Assert.Equal(saved, File.ReadAllBytes(session.DocumentPath!));
            Assert.True(mutation.FoliageApply(edit).Applied);
        }
        finally { FoliageEditorFixtures.Delete(session); }
    }

    [Fact]
    public void FoliageToolBoundary_RetainsTheTypedRefusalAndAcceptsRepair()
    {
        var session = FoliageEditorFixtures.Session(FoliageFixtures.ReachableWirePreview());
        try
        {
            var tools = new FoliageTools(new(session), new(session));
            string before = FoliageEditorFixtures.State(session);
            var wire = Assert.Throws<McpException>(() => tools.FoliageLayerSet("[]"));
            var inner = Assert.IsType<MapDocumentException>(wire.InnerException);
            Assert.Equal(MapDocumentRefusal.SchemaObjectRequired, inner.Reason);
            Assert.Equal("layerJson", inner.FieldPath);
            Assert.Equal(before, FoliageEditorFixtures.State(session));
            Assert.True(tools.FoliageLayerSet(FoliageToolTests.LayerJson(FoliageFixtures.WireLayer("new-layer"))).Applied);
        }
        finally { FoliageEditorFixtures.Delete(session); }
    }

    [Theory]
    [InlineData("region")]
    [InlineData("shape")]
    [InlineData("scatter")]
    [InlineData("placement")]
    public void MutationLookup_IdentifiesTheMissingSubjectWithoutChangingContent(string kind) => TiledDocFixture.InDirectory(root =>
    {
        var session = new MapEditSession();
        session.Create(Path.Combine(root, "map.json"), "lookups", "Lookups", -10, -10, 10, 10);
        var mutation = new MutationService(session);
        var shape = new DiscShapeDoc { Radius = 1 };
        if (kind == "shape") session.WithDocument((doc, _) => { doc.Regions.Add(new() { Name = "subject", Shape = null }); return 0; });
        string before = Content(session);
        var error = Assert.Throws<MapMutationException>(() =>
        {
            if (kind is "region" or "shape") mutation.RegionEditShape("subject", shape);
            else if (kind == "scatter") mutation.ScatterLayerEdit("subject", seed: 42);
            else mutation.ElementDuplicate("placement", id: "subject");
        });
        Assert.IsAssignableFrom<InvalidOperationException>(error);
        Assert.Equal(kind == "shape" ? MapDocumentRefusal.SchemaValueRequired : MapDocumentRefusal.ReferenceMissing, error.Reason);
        Assert.Equal("subject", error.EntityId);
        Assert.Equal("lookups", error.DocumentId);
        Assert.Equal(kind == "placement" ? "placement" : kind == "scatter" ? "scatter layer" : "region", error.SubjectKind);
        Assert.Equal(kind == "shape" ? "shape" : kind == "scatter" ? "scatterLayers" : kind == "placement" ? "placements" : "regions", error.FieldPath);
        if (kind == "scatter") Assert.Equal("subject", error.LayerId);
        if (kind == "placement") Assert.Equal("subject", error.PlacementId);
        Assert.Equal(before, Content(session));
        if (kind == "shape") session.WithDocument((doc, _) => { doc.Regions[0].Shape = shape; return 0; });
        else if (kind == "region") mutation.RegionAdd("subject", shape);
        else if (kind == "scatter") mutation.ScatterLayerAdd("subject");
        else mutation.PlacementAdd("prop", 0, 0, id: "subject");
        if (kind is "region" or "shape") Assert.NotNull(mutation.RegionEditShape("subject", shape));
        else if (kind == "scatter") Assert.NotNull(mutation.ScatterLayerEdit("subject", seed: 42));
        else Assert.NotNull(mutation.ElementDuplicate("placement", id: "subject"));
    });

    [Theory]
    [InlineData("command")]
    [InlineData("factory")]
    [InlineData("terrain")]
    public void MutationAdapters_KeepTheFirstReasonAndOrderedFindingsAndRevert(string adapter) => TiledDocFixture.InDirectory(root =>
    {
        var session = new MapEditSession();
        session.Create(Path.Combine(root, "map.json"), "adapters", "Adapters", -10, -10, 10, 10);
        var mutation = new MutationService(session);
        mutation.RegionAdd("region", new DiscShapeDoc { Radius = 1 });
        session.WithDocument((doc, _) =>
        {
            foreach (string id in new[] { "duplicate-a", "duplicate-a", "duplicate-b", "duplicate-b" })
                doc.Placements.Add(new() { Id = id, Kind = "prop" });
            return 0;
        });
        string before = Content(session);
        var error = Assert.Throws<MapMutationException>(() => Apply());
        Assert.Equal(MapDocumentRefusal.IdentityDuplicate, error.Reason);
        Assert.Equal("duplicate-a", error.EntityId);
        Assert.Equal("duplicate-a", error.PlacementId);
        Assert.Equal("$.placements[1].id", error.FieldPath);
        Assert.Equal(1, error.Index);
        Assert.NotNull(error.Findings);
        Assert.Equal(2, error.Findings.Count);
        Assert.Equal(new[] { "duplicate-a", "duplicate-b" }, error.Findings.Select(f => f.EntityId));
        Assert.Equal(before, Content(session));
        session.WithDocument((doc, _) => { doc.Placements.RemoveAt(3); doc.Placements.RemoveAt(1); return 0; });
        Assert.NotNull(Apply());
        MutationResult Apply() => adapter switch
        {
            "command" => mutation.RegionAdd("accepted", new DiscShapeDoc { Radius = 1 }),
            "factory" => mutation.RegionEditShape("region", new RectShapeDoc { MinX = -1, MinZ = -1, MaxX = 1, MaxZ = 1 }),
            _ => mutation.TerrainEdit(seed: 42),
        };
    });

    [Fact]
    public void FindingSnapshot_IsOwnedAndReadOnly()
    {
        var first = new MapDocumentFinding(MapDocumentRefusal.ReferenceMissing, "finding") { EntityId = "first" };
        var source = new List<MapDocumentFinding> { first };
        var error = new MapMutationException(first.Reason, "refusal") { Findings = source };
        source.Clear();
        Assert.Same(first, Assert.Single(error.Findings!));
        Assert.Throws<NotSupportedException>(() => ((IList<MapDocumentFinding>)error.Findings!).Clear());
    }

    [Fact]
    public void TerrainEdit_NoParams_Throws() => TiledDocFixture.InDirectory(root =>
    {
        var session = new MapEditSession();
        session.Create(Path.Combine(root, "map.json"), "empty-mutation", "Empty mutation", -10, -10, 10, 10);
        var mutation = new MutationService(session);
        string before = Content(session);
        var error = Assert.Throws<MapMutationArgumentException>(() => mutation.TerrainEdit());
        Assert.IsAssignableFrom<ArgumentException>(error);
        Assert.Null(error.ParamName);
        Assert.Equal(MapDocumentRefusal.ToolMutationEmpty, error.Reason);
        Assert.Equal("terrain_edit", error.Operation);
        Assert.Equal(before, Content(session));
        Assert.NotNull(mutation.TerrainEdit(seed: 42));
    });

    [Fact]
    public void TerrainApply_RequiresNativeAdmissionBeforeChangingContent() => TiledDocFixture.InDirectory(root =>
    {
        var session = new MapEditSession();
        session.Create(Path.Combine(root, "map.json"), "analytic", "Analytic", -10, -10, 10, 10);
        var mutation = new MutationService(session);
        var edit = CaveFixtures.OpenMouthEdit();
        string before = Content(session);
        var error = Assert.Throws<MapDocumentException>(() => mutation.TerrainApply(edit));
        Assert.Equal(MapDocumentRefusal.ToolNativeDocumentRequired, error.Reason);
        Assert.Equal("analytic", error.DocumentId);
        Assert.Equal("TerrainApply", error.Operation);
        Assert.Equal(before, Content(session));
        using var control = TerrainTransactionFixture.Create(CaveFixtures.ClosedMouth());
        Assert.NotNull(control.Service.TerrainApply(edit));
    });

    static string Content(MapEditSession session) => session.WithDocument((doc, registry) =>
        JsonSerializer.Serialize(doc, MapDocumentFile.CreateOptions(registry, write: false)));
}
