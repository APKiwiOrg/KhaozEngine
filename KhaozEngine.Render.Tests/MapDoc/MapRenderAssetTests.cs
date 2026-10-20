using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapRenderAssetTests
{
    [Fact]
    public void RenderAdapter_PreservesMeshOriginUnitsPartsEveryLodAndLights()
    {
        MapAssetClosure closure = NativeRenderAssetFixtures.Create();
        var budget = new GltfDecodeBudget(new());
        var proof = MapRenderExtentValidator.Validate(closure, ["prop-a"], budget);
        Assert.Equal(MapRenderAssetStatus.Complete, proof.Status);
        var loaded = MapRenderAssets.LoadSelected(closure, ["prop-a"], proof.Proofs!, budget);
        Assert.Equal(MapRenderAssetStatus.Complete, loaded.Status);
        using var assets = loaded.Assets!;
        byte[] detachedGlb = closure.GetResource("mesh").Bytes.ToArray();
        Array.Fill(detachedGlb, (byte)0);
        Assert.Same(closure, assets.Closure);
        Assert.Equal(3, assets.ById["prop-a"].Lods.Count);
        Assert.Equal(2, assets.ById["prop-a"].Lods[0].Parts.Count);
        foreach (var lod in assets.ById["prop-a"].Lods)
        {
            Assert.Equal(new Vector3(-.02f, .03f, .04f), lod.Parts[0].Mesh.Vertices[0].Position);
            Assert.Equal(.5f, lod.Parts[0].Maps.AlphaCutoff);
            Assert.Equal(.25f, lod.Parts[1].Maps.AlphaCutoff);
            Assert.Equal(new byte[] { 220, 20, 20, 255 }, lod.Parts[0].Maps.Albedo!.Value.Rgba);
        }
        Assert.Equal(new[] { "light" }, assets.ById["prop-a"].Descriptor.LightResourceIds);
        Assert.True(assets.RetainedBytes > 0);
        assets.ById["prop-a"].Lods[0].Parts[0].Mesh.Vertices[0].Position = Vector3.Zero;
        assets.ById["prop-a"].Lods[0].Parts[0].Maps.Albedo!.Value.Rgba[0] = 0;
        Assert.Equal(new Vector3(-.02f, .03f, .04f), assets.ById["prop-a"].Lods[0].Parts[0].Mesh.Vertices[0].Position);
        Assert.Equal(220, assets.ById["prop-a"].Lods[0].Parts[0].Maps.Albedo!.Value.Rgba[0]);
        Assert.Equal(MapRenderAssetStatus.Complete, MapRenderExtentValidator.Validate(closure, ["prop-a"], new(new())).Status);
        assets.Dispose();
        Assert.Equal(0, budget.OwnedBytes);
    }

    [Fact]
    public void ReferencedCorruptPixels_RefuseAndReleaseEveryPartialTier()
    {
        var closure = NativeRenderAssetFixtures.Create("corrupt-image");
        var budget = new GltfDecodeBudget(new());
        var proof = MapRenderExtentValidator.Validate(closure, ["prop-a"], budget);
        Assert.Equal(MapRenderAssetStatus.Complete, proof.Status);
        var loaded = MapRenderAssets.LoadSelected(closure, ["prop-a"], proof.Proofs!, budget);
        Assert.Equal(MapRenderAssetStatus.Invalid, loaded.Status);
        Assert.Null(loaded.Assets);
        Assert.Equal(0, budget.OwnedBytes);
        Assert.Equal(budget.Limits.MaxPeakBytes, budget.RemainingBytes);
    }

    [Fact]
    public void RequiredResourceFailure_PrecedesEveryUpload()
    {
        var closure = NativeRenderAssetFixtures.Create("bad-image");
        var budget = new GltfDecodeBudget(new());
        var proof = MapRenderExtentValidator.Validate(closure, ["prop-a"], budget);
        var loaded = MapRenderAssets.LoadSelected(closure, ["prop-a"], proof.Proofs!, budget);
        Assert.Equal(MapRenderAssetStatus.Unsupported, loaded.Status);
        Assert.Null(loaded.Assets);
        Assert.Equal(0, budget.OwnedBytes);
    }

    [Fact]
    public void OpaqueCompatibilityLoad_KeepsReleasedBehaviour()
    {
        var closure = NativeRenderAssetFixtures.Create("opaque");
        var asset = closure.GetAsset("prop-a");
        var expected = GltfLoader.LoadFlattenedAlbedo(closure.GetResource("mesh").Bytes, "released");
        var actual = NativeMapAssetLoader.Load(asset, closure);
        Assert.Equal(expected.Indices32, actual.Indices32);
        Assert.Equal(expected.Vertices.Select(v => v.Position * .01f), actual.Vertices.Select(v => v.Position));
        var budget = new GltfDecodeBudget(new());
        Assert.Equal(MapRenderAssetStatus.Unsupported,
            MapPreparedInputLease.LoadSelected(closure, ["prop-a"], ["paint"],
                MapRenderExtentValidator.Validate(closure, ["prop-a"], budget).Proofs!, budget).Status);
        Assert.Equal(0, budget.OwnedBytes);
    }

    [Fact]
    public void SelectedAssets_RefuseMissingStaleAndCapacityWithoutRetention()
    {
        var closure = NativeRenderAssetFixtures.Create();
        var budget = new GltfDecodeBudget(new());
        var proofs = MapRenderExtentValidator.Validate(closure, ["prop-a"], budget).Proofs!;
        Assert.Equal(MapRenderAssetStatus.Missing, MapRenderAssets.LoadSelected(closure, ["absent"], proofs, budget).Status);
        Assert.Equal(MapRenderAssetStatus.Stale, MapRenderAssets.LoadSelected(
            NativeRenderAssetFixtures.Create("opaque"), ["prop-a"], proofs, budget).Status);
        Assert.Equal(MapRenderAssetStatus.CapacityExceeded,
            MapRenderAssets.LoadSelected(closure, ["prop-a"], proofs, budget, new(MaxParts: 1)).Status);
        Assert.Equal(MapRenderAssetStatus.CapacityExceeded,
            MapRenderAssets.LoadSelected(closure, ["prop-a"], proofs, budget, new(MaxLods: 1)).Status);
        Assert.Equal(MapRenderAssetStatus.CapacityExceeded,
            MapRenderAssets.LoadSelected(closure, ["prop-a"], proofs, budget, new(MaxDecodedBytes: 1)).Status);
        Assert.Equal(0, budget.OwnedBytes);
    }
}
