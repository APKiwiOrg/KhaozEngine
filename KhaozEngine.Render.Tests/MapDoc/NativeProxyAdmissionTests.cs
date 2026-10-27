using System;
using System.Buffers.Binary;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("AllocSensitive")]
public sealed class NativeProxyAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefusedProxy_DoesNotScanMetadataOrDecodeEmbeddedBuffers(bool planLimit)
    {
        var closure = NativeRenderAssetFixtures.Placements(coarsestLod: MetadataHeavyGlb());
        using var assets = NativeMapPlacementTests.Assets(closure);
        var asset = assets.ById["prop-a"]; var tier = asset.Lods[^1];
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: planLimit ? long.MaxValue : 1024));
        using var active = admission.Enter();
        using var window = planLimit ? admission.LimitPreparation(1024, "MaxPlanBytes") : null;
        using var baseline = admission.Workspace(); baseline.Reserve(128);
        void Refused() => Assert.Throws<MapSceneCapacityException>(() => MapPlacementProxySource.Load(asset, tier, closure));
        Refused();
        int model = GltfDecodeAdmission.ModelDecodeEntries;
        long allocated = AllocationMeasurement.Measure("refused proxy source", Refused, 32 * 1024 * 1024);
        Assert.Equal(model, GltfDecodeAdmission.ModelDecodeEntries);
        Assert.Equal(128, admission.WorkspaceBytes);
        Assert.Equal(0, admission.RetainedBytes);
        Assert.True(allocated < 16_384, $"Refused proxy allocated {allocated} bytes before admission.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProxyWorkspace_ReturnsToBaselineAfterSuccessOrFailure(bool fail)
    {
        var closure = NativeRenderAssetFixtures.Placements();
        using var assets = NativeMapPlacementTests.Assets(closure);
        var asset = assets.ById["prop-a"]; var tier = asset.Lods[^1];
        var demand = GltfDecodeAdmission.Measure(closure.GetResource(tier.ResourceId).Bytes, new(new()));
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: demand.PeakBytes + 128));
        using var active = admission.Enter();
        using var baseline = admission.Workspace(); baseline.Reserve(128);
        int model = GltfDecodeAdmission.ModelDecodeEntries;
        admission.AllocationAdmitted = bytes =>
        {
            Assert.Equal(demand.PeakBytes, bytes);
            Assert.Equal(128 + demand.PeakBytes, admission.WorkspaceBytes);
            Assert.Equal(model, GltfDecodeAdmission.ModelDecodeEntries);
            if (fail) throw new InvalidOperationException("injected admitted-workspace failure");
        };
        if (fail) Assert.Throws<InvalidOperationException>(() => MapPlacementProxySource.Load(asset, tier, closure));
        else Assert.True(MapPlacementProxySource.Load(asset, tier, closure).TriangleCount > 0);
        Assert.Equal(model + (fail ? 0 : 1), GltfDecodeAdmission.ModelDecodeEntries);
        Assert.Equal(128, admission.WorkspaceBytes);
        Assert.Equal(0, admission.RetainedBytes);
    }

    static byte[] MetadataHeavyGlb()
    {
        byte[] glb = NativeRenderAssetFixtures.Glb();
        int length = BinaryPrimitives.ReadInt32LittleEndian(glb.AsSpan(12));
        var root = JsonNode.Parse(glb.AsSpan(20, length))!.AsObject();
        root["extras"] = new string('x', 128 * 1024);
        root["buffers"]!.AsArray().Add(new JsonObject
        {
            ["byteLength"] = 256 * 1024,
            ["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(new byte[256 * 1024])
        });
        return NativeRenderAssetFixtures.Pack(root, glb.AsSpan(28 + length).ToArray());
    }
}
