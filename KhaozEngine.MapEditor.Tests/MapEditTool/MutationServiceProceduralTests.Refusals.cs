using System;
using System.IO;
using System.Linq;
using KhaozEngine.MapEdit;
using KhaozEngine.Terrain;
using Xunit;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapEditor;

namespace KhaozEngine.Tests.MapEditTool;

public partial class MutationServiceProceduralTests
{
    static MapEditorCommandException AssertProceduralRefusal(Action operation, string kind, string id,
        MapDocumentRefusal reason = MapDocumentRefusal.IdentityDuplicate)
        => EditorCommandRefusalAssertions.Throws(operation, reason, kind, id);

    [Fact]
    public void BiomeBandAdd_UnknownBiome_ThrowsArgumentException()
    {
        string dir = NewTempDir();
        try
        {
            (MapEditSession session, MutationService mutation) = OpenSample(dir);
            string before = AdmissionState(session);
            var error = Assert.Throws<MapMutationArgumentException>(() => mutation.BiomeBandAdd(biome: "Volcano"));
            Assert.Equal(MapDocumentRefusal.SchemaEnumUnsupported, error.Reason);
            Assert.Equal("biome", error.ParamName);
            Assert.Equal("Volcano", error.SubmittedText);
            Assert.Equal(typeof(BiomeId), error.EnumType);
            Assert.Equal(Enum.GetNames<BiomeId>(), error.AcceptedValues);
            Assert.Equal(before, AdmissionState(session));
            Assert.NotNull(mutation.BiomeBandAdd(biome: "Meadow"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ScatterRuleEdit_NoParams_Throws()
    {
        string dir = NewTempDir();
        try
        {
            (MapEditSession session, MutationService mutation) = OpenSample(dir);
            string before = AdmissionState(session);
            var error = Assert.Throws<MapMutationArgumentException>(() => mutation.ScatterRuleEdit("trees", 0));
            Assert.Equal(MapDocumentRefusal.ToolMutationEmpty, error.Reason);
            Assert.Equal("scatter_rule_edit", error.Operation);
            Assert.Equal("trees", error.LayerId);
            Assert.Equal(0, error.Index);
            Assert.Null(error.ParamName);
            Assert.Equal(before, AdmissionState(session));
            Assert.NotNull(mutation.ScatterRuleEdit("trees", 0, density: 0.5f));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ScatterRuleAdd_UnknownLayer_ThrowsWithPreciseMessage()
    {
        string dir = NewTempDir();
        try
        {
            (MapEditSession session, MutationService mutation) = OpenSample(dir);

            string before = AdmissionState(session);
            var missing = Assert.Throws<MapMutationException>(() => mutation.ScatterRuleAdd("ghost-layer", "Meadow"));
            Assert.Equal(MapDocumentRefusal.ReferenceMissing, missing.Reason);
            Assert.Equal("ghost-layer", missing.LayerId);

            Assert.Equal(MapDocumentRefusal.ReferenceMissing,
                Assert.Throws<MapMutationException>(() => mutation.ScatterRuleEdit("ghost-layer", 0, density: 0.5f)).Reason);
            Assert.Equal(MapDocumentRefusal.ReferenceMissing,
                Assert.Throws<MapMutationException>(() => mutation.ScatterRuleRemove("ghost-layer", 0)).Reason);
            Assert.Equal(before, AdmissionState(session));

            Assert.Equal(1, mutation.ScatterRuleAdd("trees", "Meadow").Index);
            Assert.Equal(0, mutation.ScatterRuleEdit("trees", 0, density: 0.5f).Index);
            Assert.Equal(0, mutation.ScatterRuleRemove("trees", 0).Index);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData(":1", MapDocumentRefusal.SchemaValueRequired, "Kinds.Id")]
    [InlineData("oak:notanumber", MapDocumentRefusal.SchemaMalformed, "Kinds.Weight")]
    public void ScatterRuleAdd_InvalidKindText_RejectedAndReverted(string entry, MapDocumentRefusal reason, string field)
    {
        string dir = NewTempDir();
        try
        {
            (MapEditSession session, MutationService mutation) = OpenSample(dir);
            string before = session.WithDocument((doc, r) => MapDocumentFile.SaveText(doc, r));
            bool dirtyBefore = session.IsDirty;

            var error = Assert.Throws<MapMutationArgumentException>(() =>
                mutation.ScatterRuleAdd("trees", "Meadow", kinds: new[] { entry }));
            Assert.IsAssignableFrom<ArgumentException>(error);
            Assert.Equal(reason, error.Reason);
            Assert.Equal(field, error.FieldPath);
            Assert.Equal(entry, error.SubmittedText);
            Assert.Equal(0, error.Index);
            Assert.Null(error.ParamName);

            Assert.Equal(before, session.WithDocument((doc, r) => MapDocumentFile.SaveText(doc, r)));
            Assert.Equal(dirtyBefore, session.IsDirty);

            mutation.ScatterRuleAdd("trees", "Meadow", kinds: new[] { "oak:2" });
            MapPropKind oak = Assert.Single(session.WithDocument((doc, _) => doc.ScatterLayers.Single(l => l.Name == "trees").Rules[1].Kinds));
            Assert.Equal(("oak", 2f), (oak.Id, oak.Weight));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
