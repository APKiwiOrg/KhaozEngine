using System.Linq;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapExtentProofTests
{
    [Fact]
    public void ProofHashScratch_AdmitsEscapedMetadataBeforeWriterAllocation()
    {
        var closure = NativeRenderAssetFixtures.Create("escaped-id");
        var budget = new GltfDecodeBudget(new(MaxPeakBytes: 65536));
        var result = MapRenderExtentValidator.Validate(closure, [closure.Assets[0].Id], budget);
        Assert.Equal(MapRenderAssetStatus.CapacityExceeded, result.Status);
        Assert.Null(result.Proofs);
        Assert.Equal(0, budget.OwnedBytes);
        Assert.Equal(65536, budget.RemainingBytes);
    }
    [Fact]
    public void ExtentProofs_DoNotRetainDistantDecodedPayloads()
    {
        var closure = NativeRenderAssetFixtures.Create("distant");
        var budget = new GltfDecodeBudget(new(MaxPeakBytes: 4 * 1024 * 1024));
        int before = ImageDecodeAdmission.PixelDecodeEntries;
        var proof = MapRenderExtentValidator.Validate(closure, closure.Assets.Select(a => a.Id).ToArray(), budget);
        Assert.Equal(MapRenderAssetStatus.Complete, proof.Status);
        Assert.True(proof.PeakTemporaryBytes <= 2 * 1024 * 1024);
        Assert.Equal(24, proof.Proofs!.ByAsset.Count);
        Assert.Equal(before, ImageDecodeAdmission.PixelDecodeEntries);
        Assert.Equal(0, budget.OwnedBytes);
        using var selected = MapRenderAssets.LoadSelected(closure, ["prop-a"], proof.Proofs, budget,
            new(MaxDecodedBytes: 4 * 1024 * 1024)).Assets!;
        Assert.Single(selected.ById);
        Assert.True(selected.RetainedBytes >= 1024 * 1024);
        Assert.DoesNotContain("prop-23", selected.ById.Keys);
    }

    [Fact]
    public void EveryLodAndProofMetadata_AreValidatedBeforeSelection()
    {
        var closure = NativeRenderAssetFixtures.Create("lying-distant");
        var budget = new GltfDecodeBudget(new());
        var result = MapRenderExtentValidator.Validate(closure, closure.Assets.Select(a => a.Id).ToArray(), budget);
        Assert.Equal(MapRenderAssetStatus.Invalid, result.Status);
        Assert.Null(result.Proofs);
        Assert.Equal(0, budget.OwnedBytes);
        closure = NativeRenderAssetFixtures.Create();
        Assert.Equal(MapRenderAssetStatus.CapacityExceeded,
            MapRenderExtentValidator.Validate(closure, ["prop-a"], budget, 1).Status);
        Assert.Equal(MapRenderAssetStatus.Invalid, MapRenderExtentValidator.Validate(
            NativeRenderAssetFixtures.Create("lying-lod"), ["prop-a"], budget).Status);
    }
}
