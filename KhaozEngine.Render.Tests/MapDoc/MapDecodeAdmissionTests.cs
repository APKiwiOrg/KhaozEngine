using System;
using System.Buffers.Binary;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapDecodeAdmissionTests
{
    [Theory]
    [InlineData("emissiveTexture", "corrupt")]
    [InlineData("emissiveTexture", "over-budget")]
    [InlineData("occlusionTexture", "corrupt")]
    [InlineData("occlusionTexture", "over-budget")]
    [InlineData("extension", "corrupt")]
    [InlineData("extension", "over-budget")]
    public void UnsupportedReferencedTextures_RefuseBeforeModelMeshOrPixelEntry(string channel, string fault)
    {
        byte[] glb = NativeRenderAssetFixtures.UnsupportedTexture(channel, fault);
        var budget = new GltfDecodeBudget(new(MaxImagePixels: 1));
        int model = GltfDecodeAdmission.ModelDecodeEntries, arrays = GltfDecodeAdmission.LargeAllocationEntries;
        int pixels = ImageDecodeAdmission.PixelDecodeEntries;
        Assert.Equal(GltfDecodeStatus.Unsupported, Assert.Throws<GltfDecodeRefusal>(() =>
            GltfLoader.LoadPartsWithMaterialsStrict(glb, "unsupported-texture", budget)).Status);
        Assert.Equal(model, GltfDecodeAdmission.ModelDecodeEntries);
        Assert.Equal(arrays, GltfDecodeAdmission.LargeAllocationEntries);
        Assert.Equal(pixels, ImageDecodeAdmission.PixelDecodeEntries);
        Assert.Equal(0, budget.OwnedBytes);
        Assert.Equal(budget.Limits.MaxPeakBytes, budget.RemainingBytes);
        Assert.Equal(GltfDecodeStatus.Unsupported, Assert.Throws<GltfDecodeRefusal>(() => GltfDecodeAdmission.Measure(glb, budget)).Status);
        var compatible = GltfLoader.LoadPartsWithMaterials(glb, "released-texture-policy");
        Assert.Equal(2, compatible.Count);
        Assert.Equal(new byte[] { 220, 20, 20, 255 }, compatible[0].Maps.Albedo!.Value.Rgba);
    }

    [Theory]
    [InlineData("vertices")]
    [InlineData("indices")]
    [InlineData("pixels")]
    [InlineData("images")]
    public void EachDecodeCeiling_RefusesBeforeModelOrPixelEntry(string limit)
    {
        var limits = limit switch
        {
            "vertices" => new GltfDecodeLimits(MaxVertices: 5),
            "indices" => new GltfDecodeLimits(MaxIndices: 5),
            "pixels" => new GltfDecodeLimits(MaxImagePixels: 0),
            _ => new GltfDecodeLimits(MaxImages: 0)
        };
        var budget = new GltfDecodeBudget(limits);
        int model = GltfDecodeAdmission.ModelDecodeEntries, pixels = ImageDecodeAdmission.PixelDecodeEntries;
        Assert.Equal(GltfDecodeStatus.CapacityExceeded, Assert.Throws<GltfDecodeRefusal>(() =>
            GltfLoader.LoadPartsWithMaterialsStrict(NativeRenderAssetFixtures.Glb(), "ceiling", budget)).Status);
        Assert.Equal(model, GltfDecodeAdmission.ModelDecodeEntries);
        Assert.Equal(pixels, ImageDecodeAdmission.PixelDecodeEntries);
        Assert.Equal(0, budget.OwnedBytes);
    }
    [Fact]
    public void UnrepresentableRgbaArray_RefusesEvenWithAnEnlargedCallerAllowance()
    {
        var budget = new GltfDecodeBudget(new(MaxPeakBytes: long.MaxValue, MaxImagePixels: int.MaxValue));
        Assert.False(GltfDecodeAdmission.Measure(NativeRenderAssetFixtures.Glb("large-image"), budget).Allowed);
        int before = GltfDecodeAdmission.ModelDecodeEntries;
        Assert.Equal(GltfDecodeStatus.CapacityExceeded, Assert.Throws<GltfDecodeRefusal>(() =>
            GltfLoader.LoadPartsWithMaterialsStrict(NativeRenderAssetFixtures.Glb("large-image"), "unrepresentable", budget)).Status);
        Assert.Equal(before, GltfDecodeAdmission.ModelDecodeEntries);
        byte[] glb = NativeRenderAssetFixtures.Glb("large-image");
        int imageOffset = BinaryPrimitives.ReadInt32LittleEndian(glb.AsSpan(12)) + 100;
        int pixels = ImageDecodeAdmission.PixelDecodeEntries;
        Assert.Equal(ImageDecodeStatus.CapacityExceeded, Assert.Throws<ImageDecodeRefusal>(() =>
            ImageRgba.DecodeBounded(glb.AsSpan(imageOffset), long.MaxValue, int.MaxValue)).Status);
        Assert.Equal(pixels, ImageDecodeAdmission.PixelDecodeEntries);
    }
    [Theory]
    [InlineData("large-image")]
    [InlineData("large-accessor")]
    public void DecodeAdmission_RefusesBeforeLargeAllocationEntry(string variant)
    {
        int model = GltfDecodeAdmission.ModelDecodeEntries, arrays = GltfDecodeAdmission.LargeAllocationEntries;
        int pixels = ImageDecodeAdmission.PixelDecodeEntries;
        var budget = new GltfDecodeBudget(new(MaxPeakBytes: 65536));
        Assert.Equal(GltfDecodeStatus.CapacityExceeded, Assert.Throws<GltfDecodeRefusal>(() =>
            GltfLoader.LoadPartsWithMaterialsStrict(NativeRenderAssetFixtures.Glb(variant), "large", budget)).Status);
        Assert.Equal(model, GltfDecodeAdmission.ModelDecodeEntries);
        Assert.Equal(arrays, GltfDecodeAdmission.LargeAllocationEntries);
        Assert.Equal(pixels, ImageDecodeAdmission.PixelDecodeEntries);
        Assert.Equal(0, budget.OwnedBytes);
    }

    [Fact]
    public void Measure_DoesNotReserveAndIncludesInstancedCorners()
    {
        var budget = new GltfDecodeBudget(new());
        var first = GltfDecodeAdmission.Measure(NativeRenderAssetFixtures.Glb(), budget);
        var second = GltfDecodeAdmission.Measure(NativeRenderAssetFixtures.Glb("instanced"), budget);
        Assert.True(first.Allowed);
        Assert.True(second.OwnedBytes > first.OwnedBytes);
        Assert.Equal(0, budget.OwnedBytes);
        Assert.Equal(0, budget.PeakBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => budget.ReleaseOwned(1));
        Assert.False(budget.TryReserve(long.MaxValue, 1));
    }

    [Fact]
    public void SharedRemainingAllowance_RefusesNextLodBeforeDecode()
    {
        var closure = NativeRenderAssetFixtures.Create();
        var proof = MapRenderExtentValidator.Validate(closure, ["prop-a"], new(new())).Proofs!;
        byte[] bytes = closure.GetResource("mesh").Bytes.ToArray();
        var measure = GltfDecodeAdmission.Measure(bytes, new(new()));
        var budget = new GltfDecodeBudget(new(MaxPeakBytes: measure.PeakBytes + bytes.Length));
        int before = GltfDecodeAdmission.ModelDecodeEntries;
        var result = MapRenderAssets.LoadSelected(closure, ["prop-a"], proof, budget);
        Assert.Equal(MapRenderAssetStatus.CapacityExceeded, result.Status);
        Assert.Equal(before + 1, GltfDecodeAdmission.ModelDecodeEntries);
        Assert.Equal(0, budget.OwnedBytes);
        var tiny = new GltfDecodeBudget(new(MaxPeakBytes: bytes.Length - 1));
        before = GltfDecodeAdmission.ModelDecodeEntries;
        Assert.Equal(MapRenderAssetStatus.CapacityExceeded, MapRenderAssets.LoadSelected(closure, ["prop-a"], proof, tiny).Status);
        Assert.Equal(before, GltfDecodeAdmission.ModelDecodeEntries);
    }

    [Fact]
    public void ImageProbeAndBoundedDecode_RejectBeforePixels()
    {
        var bytes = NativeRenderAssetFixtures.Png();
        Assert.Equal(new ImageDecodeInfo(1, 1, 4, bytes.Length), ImageDecodeAdmission.Probe(bytes));
        int before = ImageDecodeAdmission.PixelDecodeEntries;
        Assert.Equal(ImageDecodeStatus.CapacityExceeded,
            Assert.Throws<ImageDecodeRefusal>(() => ImageRgba.DecodeBounded(bytes, 1, 1)).Status);
        Assert.Equal(before, ImageDecodeAdmission.PixelDecodeEntries);
        Assert.Equal(new byte[] { 220, 20, 20, 255 }, ImageRgba.DecodeBounded(bytes, 4096, 1).Pixels);
        Assert.Throws<ImageDecodeRefusal>(() => ImageDecodeAdmission.Probe(new byte[] { 1, 2 }));
        Assert.Equal(GltfDecodeStatus.Unsupported, Assert.Throws<GltfDecodeRefusal>(() =>
            GltfLoader.LoadPartsWithMaterialsStrict(NativeRenderAssetFixtures.Glb("external"), "external", new(new()))).Status);
    }
}
