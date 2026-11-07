using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Identity;

public sealed class DocumentIdentityAdmissionTests
{
    [Theory]
    [InlineData(1, "resolver", MapDocumentRefusal.ResolverContractMismatch, "ResolverVersion")]
    [InlineData(2, "resolver", MapDocumentRefusal.ResolverContractMismatch, "ResolverVersion")]
    [InlineData(1, "builder", MapDocumentRefusal.SchemaValueRequired, "BuilderId")]
    [InlineData(2, "builder", MapDocumentRefusal.SchemaValueRequired, "BuilderId")]
    [InlineData(1, "version", MapDocumentRefusal.ValueOutsideRange, "BuilderVersion")]
    [InlineData(2, "version", MapDocumentRefusal.ValueOutsideRange, "BuilderVersion")]
    [InlineData(1, "hash", MapDocumentRefusal.SchemaValueRequired, "OptionsHash")]
    [InlineData(2, "hash", MapDocumentRefusal.SchemaValueRequired, "OptionsHash")]
    public void IdentityOptions_SelectEachCompoundBranch(int resolver, string fault, MapDocumentRefusal reason, string field)
    {
        var fixture = FormatFourFixtures.Build();
        MapDocument doc = fixture.Document;
        if (resolver == 2) { doc.ResolverIdentity = new(1, 2); doc.SupportRecipe = MapSupportRecipe.AuthoredBindingsV2; }
        MapResolveOptions control = fixture.Options with { ResolverVersion = resolver, BuilderVersion = 1 };
        MapResolveOptions refused = fault switch
        {
            "resolver" => control with { ResolverVersion = 3 },
            "builder" => control with { BuilderId = " " },
            "version" => control with { BuilderVersion = 0 },
            _ => control with { OptionsHash = " " },
        };
        var error = Assert.Throws<MapDocumentException>(() => Compute(refused));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(field, error.FieldPath);
        if (fault == "resolver") { Assert.Equal(3, error.OptionsResolverVersion); Assert.Equal(resolver, error.ExpectedResolverVersion); }
        if (fault == "version") { Assert.Equal(0, error.ActualInteger); Assert.Equal(1, error.MinimumInteger); }
        if (fault is "builder" or "hash") Assert.Equal(" ", error.ActualText);
        Assert.NotEmpty(Compute(control));
        string Compute(MapResolveOptions options) => resolver == 1 ? MapAuthoredIdentity.Compute(doc, fixture.Assets, options)
            : MapAuthoredIdentityV2.Compute(doc, fixture.Assets, options);
    }

    [Theory]
    [InlineData("placement")]
    [InlineData("spawn")]
    [InlineData("player spawn")]
    public void AuthoredContentDuplicate_KeepsItsTypedNamespaceAndIdentity(string kind)
    {
        var doc = new MapDocument();
        if (kind == "placement") doc.Placements.AddRange([new() { Id = "repeat" }, new() { Id = "repeat" }]);
        else if (kind == "spawn") doc.Spawns.AddRange([new() { Id = "repeat" }, new() { Id = "repeat" }]);
        else doc.PlayerSpawns.AddRange([new() { Id = "repeat" }, new() { Id = "repeat" }]);
        var error = Assert.Throws<MapDocumentException>(() => MapAuthoredContentDigest.Compute(doc));
        Assert.Equal(MapDocumentRefusal.IdentityDuplicate, error.Reason);
        Assert.Equal(kind, error.SubjectKind);
        Assert.Equal("repeat", error.EntityId);
        if (kind == "placement") doc.Placements.RemoveAt(1);
        else if (kind == "spawn") doc.Spawns.RemoveAt(1);
        else doc.PlayerSpawns.RemoveAt(1);
        Assert.NotEmpty(MapAuthoredContentDigest.Compute(doc));
    }

    [Fact]
    public void SculptDigestDuplicate_KeepsItsCoordinateWithoutParsingProse()
    {
        var tile = new MapSculptTile(-3, 4);
        var digest = new MapAuthoredContentDigest();
        var finding = Assert.IsType<MapDocumentFinding>(digest.AddFinding([], [], [], [tile, tile]));
        Assert.Equal(MapDocumentRefusal.IdentityDuplicate, finding.Reason);
        Assert.Equal("sculpt tile", finding.SubjectKind);
        Assert.Equal(new MapTileCoord(-3, 4), finding.SculptTile);
        var control = new MapAuthoredContentDigest();
        Assert.Null(control.AddFinding([], [], [], [tile]));
        Assert.NotEmpty(control.Finish());
    }

    [Fact]
    public void IncompleteScopedIdentity_UsesTheAcquisitionCompletenessRule()
    {
        var refused = Identity(false);
        var error = Assert.Throws<MapDocumentException>(refused.RequireComplete);
        Assert.Equal(MapDocumentRefusal.AcquisitionIncomplete, error.Reason);
        Assert.Equal("RequireCompleteIdentity", error.Operation);
        Identity(true).RequireComplete();
        static MapScopedIdentity Identity(bool complete) => new("root", "scope", WorldFrame.Origin, [], [], [], [], 1, 1, complete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeValidation_IdentifiesMissingBoundsOrPlacementAsset(bool asset)
    {
        var fixture = FormatFourFixtures.Build();
        var doc = fixture.Document;
        var bounds = doc.PlayableBounds;
        var placement = doc.Placements[0];
        string? assetId = placement.AssetId;
        if (asset) placement.AssetId = null;
        else doc.PlayableBounds = null;
        var error = Assert.Throws<MapDocumentException>(() => MapBoundDocumentValidation.ValidateLocal(doc));
        Assert.Equal(MapDocumentRefusal.SchemaValueRequired, error.Reason);
        Assert.Equal(asset ? "AssetId" : "PlayableBounds", error.FieldPath);
        if (asset) Assert.Equal(placement.Id, error.PlacementId);
        doc.PlayableBounds = bounds;
        placement.AssetId = assetId;
        MapBoundDocumentValidation.ValidateLocal(doc);
    }

    [Theory]
    [InlineData("MinX")]
    [InlineData("MinZ")]
    [InlineData("MaxX")]
    [InlineData("MaxZ")]
    public void NativeStorageBounds_RefuseInfinityAndAdmitTheLastFiniteValue(string field)
    {
        MapDocument doc = FormatFourFixtures.Build().Document;
        float value = field.StartsWith("Min", StringComparison.Ordinal) ? float.NegativeInfinity : float.PositiveInfinity;
        Set(value);
        var error = Assert.Throws<MapDocumentException>(() => MapBoundDocumentValidation.ValidateLocal(doc));
        Assert.Equal(MapDocumentRefusal.ValueNonfinite, error.Reason);
        Assert.Equal("Bounds." + field, error.FieldPath);
        Assert.Equal(value, error.ActualNumber);
        Set(value < 0 ? -float.MaxValue : float.MaxValue);
        MapBoundDocumentValidation.ValidateLocal(doc);
        void Set(float number)
        {
            if (field == "MinX") doc.Bounds.MinX = number;
            else if (field == "MinZ") doc.Bounds.MinZ = number;
            else if (field == "MaxX") doc.Bounds.MaxX = number;
            else doc.Bounds.MaxZ = number;
        }
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("count")]
    [InlineData("set")]
    public void RootMismatch_KeepsBothOwnedRootIdentitySnapshots(string fault)
    {
        var fixture = FormatFourFixtures.Build();
        var roots = fixture.Document.NativeAssets;
        if (fault == "duplicate") roots.Add(roots[0]);
        else if (fault == "count") roots.RemoveAt(0);
        else roots[0] = roots[0] with { Id = "other" };
        string[] actual = roots.Select(r => r.Id).ToArray();
        string[] expected = fixture.Assets.Roots.Select(r => r.Id).ToArray();
        var error = Assert.Throws<MapDocumentException>(() => MapBoundDocumentValidation.RequireRoots(roots, fixture.Assets));
        Assert.Equal(MapDocumentRefusal.DocumentAssetRootsMismatch, error.Reason);
        Assert.Equal(actual, error.ResourceIds);
        Assert.Equal(expected, error.ExpectedResourceIds);
        roots.Clear();
        Assert.Equal(actual, error.ResourceIds);
        Assert.Equal(expected, error.ExpectedResourceIds);
        MapBoundDocumentValidation.RequireRoots(fixture.Assets.Roots, fixture.Assets);
    }

    [Fact]
    public void PartialNativeValidation_RequiresTheWholeDocument()
    {
        var fixture = FormatFourFixtures.Build();
        var doc = fixture.Document;
        doc.Tiles = new MapTileIndex(doc.TileSize, MapDocumentHash.SchemeVersion, null, [new(new(0, 0), "hash", false)]);
        var error = Assert.Throws<MapDocumentException>(() => MapBoundDocumentValidation.ValidateLocal(doc));
        Assert.Equal(MapDocumentRefusal.CompleteDocumentRequired, error.Reason);
        Assert.Equal(doc.Id, error.DocumentId);
        Assert.Equal("ValidateNativeDocument", error.Operation);
        doc.Tiles = null;
        MapBoundDocumentValidation.ValidateLocal(doc);
    }
}
