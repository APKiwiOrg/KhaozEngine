using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEdit;
using KhaozEngine.Tests.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

public partial class MutationServiceProceduralTests
{
    [Theory]
    [InlineData("spawn")]
    [InlineData("player_spawn")]
    [InlineData("region")]
    [InlineData("scatter_layer")]
    [InlineData("companion_layer")]
    [InlineData("edit_companion_layer")]
    public void DuplicateMissingSource_IdentifiesItsNamespaceAndAdmitsExistingSource(string kind) =>
        TiledDocFixture.InDirectory(root =>
        {
            var (session, mutation) = OpenSample(root);
            string before = AdmissionState(session);
            var error = Assert.Throws<MapMutationException>(() => kind == "edit_companion_layer"
                ? mutation.CompanionLayerEdit("missing-source", seed: 7) : mutation.ElementDuplicate(kind, "missing-source"));
            Assert.IsAssignableFrom<InvalidOperationException>(error);
            Assert.Equal(MapDocumentRefusal.ReferenceMissing, error.Reason);
            Assert.Equal("test-zone", error.DocumentId);
            Assert.Equal("missing-source", error.EntityId);
            Assert.Equal(kind == "edit_companion_layer" ? "companion layer" : kind.Replace('_', ' '), error.SubjectKind);
            Assert.Equal(before, AdmissionState(session));
            if (kind == "player_spawn") mutation.PlayerSpawnAdd(0, 0);
            string id = session.WithDocument((doc, _) => kind switch
            {
                "spawn" => doc.Spawns[0].Id,
                "player_spawn" => doc.PlayerSpawns[0].Id,
                "region" => doc.Regions[0].Name,
                "scatter_layer" => doc.ScatterLayers[0].Name,
                _ => doc.CompanionLayers[0].Name,
            });
            Assert.NotNull(kind == "edit_companion_layer" ? mutation.CompanionLayerEdit(id, seed: 7) : mutation.ElementDuplicate(kind, id));
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingEditedShape_IdentifiesItsIndexAndAdmitsRestoredShape(bool scatter) => TiledDocFixture.InDirectory(root =>
    {
        var (session, mutation) = OpenSample(root);
        MapShapeDoc shape = session.WithDocument((doc, _) =>
        {
            var saved = scatter ? doc.ScatterOverrides[0].Shape! : doc.Exclusions[0].Shape!;
            if (scatter) doc.ScatterOverrides[0].Shape = null;
            else doc.Exclusions[0].Shape = null;
            return saved;
        });
        string before = AdmissionState(session);
        void Edit()
        {
            const string json = "{\"type\":\"rect\",\"minX\":-1,\"minZ\":-1,\"maxX\":1,\"maxZ\":1}";
            if (scatter) mutation.ScatterOverrideEdit(0, shapeJson: json);
            else mutation.ExclusionEdit(0, json);
        }
        var error = Assert.Throws<MapMutationException>(Edit);
        Assert.Equal(MapDocumentRefusal.SchemaValueRequired, error.Reason);
        Assert.Equal(0, error.Index);
        Assert.Equal(scatter ? "ScatterOverrides.Shape" : "Exclusions.Shape", error.FieldPath);
        Assert.Equal("test-zone", error.DocumentId);
        Assert.Equal(before, AdmissionState(session));
        session.WithDocument((doc, _) =>
        {
            if (scatter) doc.ScatterOverrides[0].Shape = shape;
            else doc.Exclusions[0].Shape = shape;
            return 0;
        });
        Edit();
    });

    [Theory]
    [InlineData("override")]
    [InlineData("bake")]
    [InlineData("freeze")]
    [InlineData("sculpt")]
    public void SpecializedMutation_ReportsProducerFindingsAndRevertsBeforeRepair(string verb) => TiledDocFixture.InDirectory(root =>
    {
        var (session, mutation) = OpenSample(root);
        session.WithDocument((doc, _) => doc.Id = "");
        string before = AdmissionState(session);
        void Edit()
        {
            switch (verb)
            {
                case "override": mutation.ScatterOverrideEdit(0, densityMultiplier: 2); break;
                case "bake": mutation.BakeRegion("trees", -2, -2, 2, 2); break;
                case "freeze": mutation.FreezeZone(); break;
                default: mutation.SculptApply("raise", 0, 0, 2, 1, 1); break;
            }
        }
        var error = Assert.Throws<MapMutationException>(Edit);
        Assert.Equal(MapDocumentRefusal.SchemaValueRequired, error.Reason);
        Assert.Equal("$.id", error.FieldPath);
        var finding = Assert.Single(error.Findings!);
        Assert.Equal(MapDocumentRefusal.SchemaValueRequired, finding.Reason);
        Assert.Equal("$.id", finding.FieldPath);
        Assert.Equal(before, AdmissionState(session));
        session.WithDocument((doc, _) => doc.Id = "test-zone");
        Edit();
        Assert.Empty(session.Validate().StructuralErrors);
    });

    [Fact]
    public void UntranslatableFeature_ReportsItsTypeAndAdmitsABuiltin()
    {
        var source = new UntranslatableFeature { Name = "kept" };
        var error = Assert.Throws<MapMutationException>(() => MutationService.TranslateUnsupportedFeatureForTests(source, 7));
        Assert.Equal(MapDocumentRefusal.ToolFeatureTranslationUnsupported, error.Reason);
        Assert.Equal(7, error.Index);
        Assert.Equal("custom", error.FeatureType);
        Assert.Equal("element_duplicate", error.Operation);
        Assert.Equal("kept", source.Name);
        var translated = Assert.IsType<LakeFeatureDoc>(MutationService.TranslateUnsupportedFeatureForTests(new LakeFeatureDoc(), 7));
        Assert.Equal(2, translated.CenterX);
        Assert.Equal(2, translated.CenterZ);
    }

    [Fact]
    public void MutationArgument_AcceptedValuesAreAnOwnedReadOnlySnapshot()
    {
        string[] source = ["first", "second"];
        var error = new MapMutationArgumentException(MapDocumentRefusal.SchemaEnumUnsupported, "argument") { AcceptedValues = source };
        source[0] = "changed";
        Assert.Equal(new[] { "first", "second" }, error.AcceptedValues);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)error.AcceptedValues!)[0] = "changed");
    }

    static string AdmissionState(MapEditSession session) => session.WithDocument((doc, registry) =>
        JsonSerializer.Serialize(doc, MapDocumentFile.CreateOptions(registry, write: true))) + session.IsDirty;

    sealed class UntranslatableFeature : MapFeature
    {
        public override string Type => "custom";
    }
}
