using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapResolverFindingTests
{
    [Theory]
    [InlineData("unsupported-recipe", "supportRecipe", 2)]
    [InlineData("legacy-recipe", "supportRecipe", 1)]
    [InlineData("legacy-surfaces", "surfaces", 1)]
    [InlineData("legacy-binding", "placements.supportBinding", 1)]
    [InlineData("authored-recipe", "supportRecipe", 1)]
    [InlineData("unsupported-resolver", "resolverIdentity", 1)]
    public void Native_diagnostics_keep_typed_resolver_findings_beside_their_prose(string fault, string field, int count)
    {
        var doc = NativeResolverFixtures.Create().Document;
        switch (fault)
        {
            case "unsupported-recipe": doc.SupportRecipe = (MapSupportRecipe)99; break;
            case "legacy-recipe": doc.SupportRecipe = MapSupportRecipe.AuthoredBindingsV2; break;
            case "legacy-surfaces": doc.Surfaces = SupportFixtures.CaveWithPlacements().Surfaces; break;
            case "legacy-binding": doc.Placements[0].SupportBinding = new(MapSupportBindingKind.Surface, "floor", null, null, null, null); break;
            case "authored-recipe": doc.ResolverIdentity = new(1, 2); break;
            case "unsupported-resolver": doc.ResolverIdentity = new(1, 3); break;
        }
        var errors = new List<string>();
        var findings = new List<MapDocumentFinding>();
        MapNativeValidation.Validate(doc, errors, findings);
        Assert.Equal(count, findings.Count);
        Assert.All(findings, finding =>
        {
            Assert.Equal(MapDocumentRefusal.ResolverContractMismatch, finding.Reason);
            Assert.Equal(doc.ResolverIdentity!.PayloadVersion, finding.PayloadVersion);
            Assert.Equal(doc.ResolverIdentity.ResolverVersion, finding.ResolverVersion);
            Assert.Equal(doc.SupportRecipe, finding.SupportRecipe);
            Assert.Equal(field, finding.FieldPath);
        });
        Assert.Equal(findings.Select(f => f.Message), errors.Where(e => findings.Any(f => f.Message == e)));

        // Fix the one resolver-contract defect before checking the accepted control.
        doc.SupportRecipe = MapSupportRecipe.LegacyXzCallbackV1;
        doc.ResolverIdentity = new(1, 1);
        doc.Surfaces = new MapSurfaceSet();
        doc.Placements[0].SupportBinding = null;
        errors.Clear(); findings.Clear();
        MapNativeValidation.Validate(doc, errors, findings);
        Assert.Empty(findings);
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("foliage", 1)]
    [InlineData("foliage", 2)]
    [InlineData("water", 1)]
    [InlineData("water", 2)]
    [InlineData("prefab", 1)]
    [InlineData("prefab", 2)]
    public void Content_diagnostics_keep_the_resolver_context_without_converting_mixed_adapters(string kind, int resolver)
    {
        var doc = NativeResolverFixtures.Create().Document;
        if (kind == "foliage") doc.Foliage = new(1, []);
        if (kind == "water") doc.Water = new();
        if (kind == "prefab") doc.BuildingIntent = new(1, [], [], [], []);
        doc.ResolverIdentity = new(1, resolver);
        var errors = new List<string>();
        var findings = new List<MapDocumentFinding>();
        Validate();
        var finding = Assert.Single(findings);
        Assert.Equal(MapDocumentRefusal.ResolverContractMismatch, finding.Reason);
        Assert.Equal(1, finding.PayloadVersion);
        Assert.Equal(resolver, finding.ResolverVersion);
        Assert.Equal(resolver == 1 ? "resolverIdentity" : "supportRecipe", finding.FieldPath);
        Assert.Equal(2, finding.ExpectedResolverVersion);
        Assert.Equal(MapSupportRecipe.LegacyXzCallbackV1, finding.SupportRecipe);
        Assert.Equal(MapSupportRecipe.AuthoredBindingsV2, finding.ExpectedSupportRecipe);
        Assert.Equal(kind, finding.SubjectKind);
        Assert.Equal(finding.Message, Assert.Single(errors));

        doc.ResolverIdentity = new(1, 2);
        doc.SupportRecipe = MapSupportRecipe.AuthoredBindingsV2;
        errors.Clear(); findings.Clear();
        Validate();
        Assert.Empty(findings);
        Assert.Empty(errors);

        void Validate()
        {
            if (kind == "foliage") MapFoliageValidation.ValidateDocument(doc, errors, findings);
            if (kind == "water") MapWaterValidation.Validate(doc, errors, new MapWaterWorkBudget(), findings);
            if (kind == "prefab") errors.AddRange(MapPrefabValidation.Validate(doc, null, findings));
        }
    }
}
