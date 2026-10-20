using System;
using System.Text;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapPreparedInputTests
{
    [Fact]
    public void RequiredMaterialImageLimit_RefusesBeforePixelDecode()
    {
        var closure = NativeRenderAssetFixtures.Create();
        var budget = new GltfDecodeBudget(new(MaxImages: 0));
        int before = KhaozEngine.Render2D.ImageDecodeAdmission.PixelDecodeEntries;
        Assert.Equal(MapRenderAssetStatus.CapacityExceeded, Assert.Throws<MapRenderAssetRefusal>(() =>
            MapRenderMaterials.Load(closure, ["paint"], budget)).Status);
        Assert.Equal(before, KhaozEngine.Render2D.ImageDecodeAdmission.PixelDecodeEntries);
        Assert.Equal(0, budget.OwnedBytes);
        Assert.Equal(budget.Limits.MaxPeakBytes, budget.RemainingBytes);
    }
    [Fact]
    public void FailedMaterialInput_ReleasesAlreadyLoadedMeshesOnEveryFailure()
    {
        var closure = NativeRenderAssetFixtures.Create();
        var budget = new GltfDecodeBudget(new());
        var proof = MapRenderExtentValidator.Validate(closure, ["prop-a"], budget).Proofs!;
        Assert.Throws<ArgumentNullException>(() => MapPreparedInputLease.LoadSelected(closure, ["prop-a"], [null!], proof, budget));
        Assert.Equal(0, budget.OwnedBytes);
    }
    [Fact]
    public void SharedLease_RetainsAssetsAndPaintUntilLastOwnerAndDetachesImages()
    {
        var closure = NativeRenderAssetFixtures.Create();
        var budget = new GltfDecodeBudget(new());
        var proof = MapRenderExtentValidator.Validate(closure, ["prop-a"], budget).Proofs!;
        var result = MapPreparedInputLease.LoadSelected(closure, ["prop-a"], ["paint"], proof, budget);
        Assert.Equal(MapRenderAssetStatus.Complete, result.Status);
        var input = result.Input!;
        var retained = input.Retain();
        long charge = budget.OwnedBytes;
        Assert.Equal(charge, input.RetainedBytes);
        Assert.True(charge > 0);
        Assert.Equal(closure.Hash, input.ClosureHash);
        Assert.False(string.IsNullOrEmpty(input.ProofDigest));
        input.Materials["paint"].Maps.Albedo!.Value.Rgba[0] = 0;
        Assert.Equal(220, retained.Materials["paint"].Maps.Albedo!.Value.Rgba[0]);
        input.Dispose();
        Assert.Equal(charge, budget.OwnedBytes);
        Assert.Equal(.7f, retained.Materials["paint"].Roughness);
        retained.Dispose(); retained.Dispose();
        Assert.Equal(0, budget.OwnedBytes);
        Assert.Throws<ObjectDisposedException>(() => retained.Retain());
    }

    [Theory]
    [InlineData("\"unknown\":0,")]
    [InlineData("\"roughness\":0.3,")]
    [InlineData("\"schema\":null,")]
    public void ClosedMaterial_RejectsUnknownDuplicateAndNullMembers(string insert)
    {
        string json = "{" + insert + NativeRenderAssetFixtures.Material()[1..];
        Assert.Throws<MapRenderAssetRefusal>(() => MapRenderMaterialJson.Read(Encoding.UTF8.GetBytes(json), "paint"));
    }

    [Fact]
    public void ClosedMaterial_RejectsFutureAndOutOfDomainValues()
    {
        foreach (string json in new[]
        {
            NativeRenderAssetFixtures.Material().Replace("\"payloadVersion\":1", "\"payloadVersion\":2", StringComparison.Ordinal),
            NativeRenderAssetFixtures.Material().Replace("\"metallic\":0.1", "\"metallic\":2", StringComparison.Ordinal),
            NativeRenderAssetFixtures.Material().Replace("\"normal\":null,", "", StringComparison.Ordinal)
        }) Assert.Throws<MapRenderAssetRefusal>(() => MapRenderMaterialJson.Read(Encoding.UTF8.GetBytes(json), "paint"));
    }
}
