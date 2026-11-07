using System;
using System.IO;
using System.Security.Cryptography;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.Physics;
using KhaozEngine.MapEdit;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>The collider edit's refusals and edge cases, and the native mesh loader's self-containment rule.</summary>
public class NativeCollisionRefusalTests
{
    [Theory]
    [InlineData(false, "body.bin", "$.buffers[0].uri")]
    [InlineData(true, "albedo.png", "$.images[0].uri")]
    public void NativeLoader_RefusesExternalUris(bool image, string uri, string field)
    {
        var f = image ? NativeEditorAssetFixtures.ExternalImageMeshAsset() : NativeEditorAssetFixtures.ExternalBufferMeshAsset();
        var error = Assert.Throws<MapDocumentException>(() => NativeMapAssetLoader.Load(f.Asset, f.Closure));
        Assert.Equal(MapDocumentRefusal.AssetGlbExternalUri, error.Reason);
        Assert.Equal(f.Asset.Id, error.AssetId);
        Assert.Equal(f.Asset.MeshResourceId, error.ResourceId);
        Assert.Equal(uri, error.Uri);
        Assert.Equal(field, error.FieldPath);
        const string dataImageUri = "data:image/png;base64," +
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==";
        var control = NativeEditorAssetFixtures.DataBufferMeshAsset(image ? dataImageUri : null);
        Assert.NotEmpty(NativeMapAssetLoader.Load(control.Asset, control.Closure).Vertices);
    }

    [Fact]
    public void NativeLoader_RejectsADescriptorFromAnotherClosure()
    {
        var f = NativeEditorAssetFixtures.DataBufferMeshAsset();
        var other = NativeEditorAssetFixtures.DataBufferMeshAsset();
        var error = Assert.Throws<MapDocumentException>(() => NativeMapAssetLoader.Load(other.Asset, f.Closure));
        Assert.Equal(MapDocumentRefusal.AssetDescriptorMismatch, error.Reason);
        Assert.Equal(f.Asset.Id, error.AssetId);
        Assert.NotEmpty(NativeMapAssetLoader.Load(f.Asset, f.Closure).Vertices);
    }

    [Theory]
    [InlineData("header")]
    [InlineData("chunk")]
    [InlineData("json")]
    [InlineData("mesh")]
    public void NativeLoader_ReportsMalformedAndUnreadableMeshes(string fault)
    {
        byte[] bytes = fault == "header" ? new byte[19] : NativeEditorAssetFixtures.GlbWithJson(fault == "json" ? "{" : "{}");
        if (fault == "chunk") bytes[16] = 0;
        var f = NativeEditorAssetFixtures.MeshAsset("malformed", bytes);
        var error = Assert.Throws<MapDocumentException>(() => NativeMapAssetLoader.Load(f.Asset, f.Closure));
        Assert.Equal(fault == "mesh" ? MapDocumentRefusal.AssetPayloadUnreadable : MapDocumentRefusal.SchemaMalformed, error.Reason);
        Assert.Equal(f.Asset.Id, error.AssetId);
        Assert.Equal(f.Asset.MeshResourceId, error.ResourceId);
        var control = NativeEditorAssetFixtures.DataBufferMeshAsset();
        Assert.NotEmpty(NativeMapAssetLoader.Load(control.Asset, control.Closure).Vertices);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeLoader_KeepsKnownExternalLoaderRefusals(bool nested)
    {
        var f = NativeEditorAssetFixtures.DataBufferMeshAsset();
        var original = new MapDocumentException(MapDocumentRefusal.SchemaMemberDuplicate, "known refusal")
        {
            ResourceId = "nested-resource",
            SourceName = "nested-source",
            FieldPath = "$.member",
            ResourceRoot = "nested-root",
            SupportedVersions = [1, 3],
            Uri = "nested-uri",
            ShapeType = typeof(MapDocument),
        };
        var error = NativeMapAssetLoader.MeshLoadFailure(nested ? new InvalidOperationException("loader", original) : original,
            f.Asset, f.Closure.GetResource(f.Asset.MeshResourceId));
        Assert.Equal(original.Reason, error.Reason);
        Assert.Equal(original.ResourceId, error.ResourceId);
        Assert.Equal(original.SourceName, error.SourceName);
        Assert.Equal(original.FieldPath, error.FieldPath);
        Assert.Equal(original.ResourceRoot, error.ResourceRoot);
        Assert.Equal(original.SupportedVersions, error.SupportedVersions);
        Assert.Equal(original.Uri, error.Uri);
        Assert.Equal(original.ShapeType, error.ShapeType);
        Assert.Equal(f.Asset.Id, error.AssetId);
        Assert.Same(original, error.InnerException);
        Assert.NotEmpty(NativeMapAssetLoader.Load(f.Asset, f.Closure).Vertices);
    }

    [Fact]
    public void NativeLoader_LoadsAMeshWhoseBufferIsAnEmbeddedDataUri()
    {
        var f = NativeEditorAssetFixtures.DataBufferMeshAsset();
        var mesh = NativeMapAssetLoader.Load(f.Asset, f.Closure);
        Assert.Equal(3, mesh.Vertices.Length);
        Assert.Equal(2f, NativeEditorAssetFixtures.MaxVertexY(mesh));
    }

    [Theory]
    [InlineData("tilted-wall", MapDocumentRefusal.ToolColliderBoxTilted, "tilted.collider", null)]
    [InlineData("shared-a", MapDocumentRefusal.ToolColliderResourceShared, "shared.collider", null)]
    [InlineData("nested-wall", MapDocumentRefusal.ReferenceMissing, "nested.collider", null)]
    [InlineData("split-wall", MapDocumentRefusal.ReferenceMissing, "split.collider", "kit")]
    [InlineData("rock-mesh", MapDocumentRefusal.ToolColliderShapeUnsupported, "rock.collider", null)]
    public void UnsupportedColliderOrManifest_RefusesHeightEdits(string assetId, MapDocumentRefusal reason,
        string resourceId, string? rootId)
    {
        using var f = new NativeCollisionToolFixture();
        PrimeHistory(f);
        byte[] before = f.ReadAssetDirectoryDigest();
        string content = Content(f);
        var history = f.Session.NativeHistoryState;
        var error = Assert.Throws<MapDocumentException>(() => f.Service.SetHeights(assetId, 0f, 1f, dryRun: false));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(assetId, error.AssetId);
        Assert.Equal(resourceId, error.ResourceId);
        Assert.Equal(rootId, error.SourceName);
        if (assetId == "shared-a") Assert.Equal("shared-b", error.OtherAssetId);
        if (assetId == "tilted-wall") Assert.Equal(0, error.Index);
        if (assetId == "rock-mesh") Assert.Equal(typeof(TriangleMeshShape), error.ShapeType);
        Assert.Equal(before, f.ReadAssetDirectoryDigest());
        Assert.Equal(content, Content(f));
        Assert.Equal(history, f.Session.NativeHistoryState);
        Assert.False(f.Session.IsDirty);
        using var control = new NativeCollisionToolFixture(assetId);
        Assert.True(control.Service.SetHeights(assetId, 0f, 1f, dryRun: false).Applied);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingCollider_IdentifiesTheAssetWithoutChangingTheSession(bool measure)
    {
        using var f = new NativeCollisionToolFixture();
        PrimeHistory(f);
        byte[] before = f.ReadAssetDirectoryDigest();
        string content = Content(f);
        var history = f.Session.NativeHistoryState;
        var error = Assert.Throws<MapDocumentException>(() =>
        {
            if (measure) f.Service.Measure("no-collider-1");
            else f.Service.SetHeights("no-collider", 0, 1, dryRun: false);
        });
        Assert.Equal(MapDocumentRefusal.ToolColliderMissing, error.Reason);
        Assert.Equal("no-collider", error.AssetId);
        Assert.Equal(before, f.ReadAssetDirectoryDigest());
        Assert.Equal(content, Content(f));
        Assert.Equal(history, f.Session.NativeHistoryState);
        using var control = new NativeCollisionToolFixture("no-collider");
        if (measure) Assert.Equal("no-collider", control.Service.Measure("no-collider-1").AssetId);
        else Assert.True(control.Service.SetHeights("no-collider", 0, 1, dryRun: false).Applied);
    }

    [Fact]
    public void MissingPlacement_IdentifiesTheReferenceAndPreservesContent()
    {
        using var f = new NativeCollisionToolFixture();
        string before = Content(f);
        var error = Assert.Throws<MapDocumentException>(() => f.Service.Measure("missing-placement"));
        Assert.Equal(MapDocumentRefusal.ReferenceMissing, error.Reason);
        Assert.Equal("missing-placement", error.PlacementId);
        Assert.Equal(before, Content(f));
        Assert.Equal("lone-box", f.Service.Measure("lone-1").AssetId);
    }

    [Theory]
    [InlineData(float.NaN, 1f, MapDocumentRefusal.ValueNonfinite, "bottom")]
    [InlineData(0f, float.PositiveInfinity, MapDocumentRefusal.ValueNonfinite, "top")]
    [InlineData(1f, 1f, MapDocumentRefusal.BoundsInvalid, "top")]
    [InlineData(2f, 1f, MapDocumentRefusal.BoundsInvalid, "top")]
    public void InvalidHeights_SelectTheFirstFailedBranchAndPreserveContent(float bottom, float top,
        MapDocumentRefusal reason, string field)
    {
        using var f = new NativeCollisionToolFixture();
        PrimeHistory(f);
        byte[] before = f.ReadAssetDirectoryDigest();
        string content = Content(f);
        var history = f.Session.NativeHistoryState;
        var error = Assert.Throws<MapDocumentException>(() => f.Service.SetHeights("lone-box", bottom, top, dryRun: false));
        Assert.Equal(reason, error.Reason);
        Assert.Equal("lone-box", error.AssetId);
        Assert.Equal(field, error.FieldPath);
        Assert.Equal(field == "bottom" ? (double)bottom : top, error.ActualNumber);
        Assert.Equal(before, f.ReadAssetDirectoryDigest());
        Assert.Equal(content, Content(f));
        Assert.Equal(history, f.Session.NativeHistoryState);
        Assert.True(f.Service.SetHeights("lone-box", 0f, 1f, dryRun: false).Applied);
    }

    [Theory]
    [InlineData(float.PositiveInfinity, 1f, MapDocumentRefusal.ValueNonfinite, "Position.Y")]
    [InlineData(0f, float.PositiveInfinity, MapDocumentRefusal.ValueNonfinite, "HalfExtents.Y")]
    [InlineData(0f, 0f, MapDocumentRefusal.ValueOutsideRange, "HalfExtents.Y")]
    public void ResizedBoxAdmission_ReportsTheActualComputedField(float position, float halfHeight,
        MapDocumentRefusal reason, string field)
    {
        var error = Assert.Throws<MapDocumentException>(() =>
            NativeColliderAdmission.RequireFiniteResizedBoxForTests("box", 2, position, halfHeight));
        Assert.Equal(reason, error.Reason);
        Assert.Equal("box", error.AssetId);
        Assert.Equal(2, error.Index);
        Assert.Equal(field, error.FieldPath);
        Assert.Equal((double)(field == "Position.Y" ? position : halfHeight), error.ActualNumber);
        NativeColliderAdmission.RequireFiniteResizedBoxForTests("box", 2, 0f, 1f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublishedPlanAdmission_IdentifiesTheFirstMismatchingResource(bool manifest)
    {
        var collider = new MapAssetRef("collider", "collider.coll", new string('a', 64), 1);
        var root = new MapAssetRef("root", "root.json", new string('b', 64), 1);
        var different = (manifest ? root : collider) with { Sha256 = new string('c', 64) };
        var error = Assert.Throws<MapDocumentException>(() => NativeColliderAdmission.RequirePublishedColliderPlanForTests(
            "box", collider, root, manifest ? collider : different, manifest ? different : root));
        Assert.Equal(MapDocumentRefusal.ToolColliderPublicationMismatch, error.Reason);
        Assert.Equal("box", error.AssetId);
        Assert.Equal(manifest ? root.Id : collider.Id, error.ResourceId);
        Assert.Equal(manifest ? root.Sha256 : collider.Sha256, error.ExpectedHash);
        Assert.Equal(different.Sha256, error.ActualHash);
        NativeColliderAdmission.RequirePublishedColliderPlanForTests("box", collider, root, collider, root);
    }

    static void PrimeHistory(NativeCollisionToolFixture fixture)
    {
        new MutationService(fixture.Session).PlacementLabel("lone-1", "history-before-refusal");
        fixture.Session.Save();
        Assert.True(fixture.Session.NativeHistoryState.Depth > 0);
    }

    static string Content(NativeCollisionToolFixture fixture) => fixture.Session.WithDocument((doc, registry) =>
        MapDocumentFile.SaveText(doc, registry));

    [Fact]
    public void LoneBox_BecomesACompoundAtTheRequestedHeights()
    {
        using var f = new NativeCollisionToolFixture();
        var edit = f.Service.SetHeights("lone-box", 0.2f, 1.7f, dryRun: false);
        Assert.True(edit.Applied);
        var m = f.Service.Measure("lone-1");
        Assert.Equal(edit.AfterSha256, m.ColliderSha256);
        Assert.Equal(0.2f, m.EffectiveBottom, 5);
        Assert.Equal(1.7f, m.EffectiveTop, 5);
    }

    [Fact]
    public void UnchangedEdit_AppliesNothing()
    {
        using var f = new NativeCollisionToolFixture();
        byte[] before = f.ReadAssetDirectoryDigest();
        var edit = f.Service.SetHeights("wall-variant", 0f, 2.5f, dryRun: false);
        Assert.False(edit.Applied);
        Assert.Equal(edit.BeforeSha256, edit.AfterSha256);
        Assert.Equal(MapNativeInvalidation.None, edit.Effects.Invalidates);
        Assert.Equal(before, f.ReadAssetDirectoryDigest());
        Assert.False(f.Session.IsDirty);
    }

    [Fact]
    public void Writer_RefusesAnOccupiedAddressWithoutOverwriting()
    {
        using var f = new NativeCollisionToolFixture();
        byte[] bytes = { 1, 2, 3, 4 };
        string digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string path = Path.Combine(f.Root, MapAssetFileWriter.ContentDirectory, digest + ".coll");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 9, 9 });
        var writer = new MapAssetFileWriter(f.Root);
        Assert.Throws<MapDocumentException>(() => writer.WriteResource(bytes, "coll"));
        Assert.Equal(new byte[] { 9, 9 }, File.ReadAllBytes(path));
    }
}
