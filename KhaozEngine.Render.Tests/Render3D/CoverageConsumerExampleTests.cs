using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Examples;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageConsumerExampleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Published_example_compiles_and_keeps_legacy_adapters_optional(bool legacy)
    {
        using var f = new CoverageAttachmentFixture();
        var assets = Assets(f);
        int meshes = f.Scene.LiveMeshCount, skins = f.Scene.LiveSkinnedMeshCount;
        var inner = new Scene3DAttachmentScene(f.Scene);
        IAttachmentScene adapter = legacy ? new LegacyEquipmentScene(inner) : inner;
        using var actor = new EquipmentCoverageConsumer(f.Scene, adapter, f.Source, assets.Glb,
            assets.Map, Contract(), MotionKey.From(123));
        Assert.Equal(!legacy, actor.TryEquip("example-set.json", assets.SetHash,
            "example-coverage.json", assets.CompanionHash, 17, out var failure));
        Assert.Equal(legacy ? CoverageFailureCode.UnsupportedAdapter : CoverageFailureCode.None, failure.Code);
        if (!legacy)
        {
            Assert.False(actor.TryEquip("example-set.json", assets.SetHash,
                "example-coverage.json", assets.CompanionHash, 0, out failure));
            Assert.Equal(CoverageFailureCode.IdentityMismatch, failure.Code);
        }
        f.Scene.Begin();
        Assert.True(actor.Queue(Palette(actor.Skeleton), Matrix4x4.Identity, out failure), failure.ToString());
        Assert.Equal(2, f.Scene.QueuedSkinnedInstancesForTests.Count);
        Assert.Equal(legacy ? 0 : 2, f.Scene.QueuedInstancesForTests.Count);
        var token = f.Scene.QueuedSkinnedInstancesForTests[0].Coverage;
        Assert.Equal(legacy, token.Preparation is null);
        f.Render();
        if (!legacy) Assert.Equal(CoveragePresentation.Dispatched, f.Scene.GetCoverageOutcome(token).Presentation);
        actor.Dispose();
        actor.Dispose();
        Assert.Equal(meshes, f.Scene.LiveMeshCount);
        Assert.Equal(skins, f.Scene.LiveSkinnedMeshCount);
        Assert.Throws<ObjectDisposedException>(() => actor.TryUnequip(out _));
    }

    [Fact]
    public void Published_example_queues_current_uncovered_host_and_equipment_on_preparation_refusal()
    {
        using var f = new CoverageAttachmentFixture();
        var assets = Assets(f);
        using var actor = new EquipmentCoverageConsumer(f.Scene, new Scene3DAttachmentScene(f.Scene),
            f.Source, assets.Glb, assets.Map, Contract(), MotionKey.From(123));
        Assert.True(actor.TryEquip("example-set.json", assets.SetHash,
            "example-coverage.json", assets.CompanionHash, 17, out _));
        f.Scene.Begin();
        var mirrored = Matrix4x4.CreateScale(-1, 1, 1);
        Assert.False(actor.Queue(Palette(actor.Skeleton), mirrored, out var failure));
        Assert.Equal(CoverageFailureCode.UnsupportedTransform, failure.Code);
        Assert.Equal(2, f.Scene.QueuedSkinnedInstancesForTests.Count);
        Assert.All(f.Scene.QueuedSkinnedInstancesForTests, draw =>
        {
            Assert.Null(draw.Coverage.Preparation);
            Assert.Equal(mirrored, draw.World);
        });
        Assert.Equal(2, f.Scene.QueuedInstancesForTests.Count);
        Assert.All(f.Scene.QueuedInstancesForTests, draw => Assert.Equal(mirrored, draw.World));
        f.Render();
        Assert.Equal(0, f.Scene.DispatchedHostPreparationCount);
    }

    [Fact]
    public void Published_example_refuses_unknown_sidecar_members_before_host_upload()
    {
        using var f = new CoverageAttachmentFixture();
        int skins = f.Scene.LiveSkinnedMeshCount;
        byte[] glb = CoverageImportTests.HostFixture().Build();
        var map = JsonNode.Parse(HostCoverageMapJson.Write(CoverageImportTests.Map(glb)))!.AsObject();
        map["unknown"] = true;
        var error = Assert.Throws<InvalidDataException>(() => new EquipmentCoverageConsumer(f.Scene,
            new Scene3DAttachmentScene(f.Scene), f.Source, glb,
            Encoding.UTF8.GetBytes(map.ToJsonString()), Contract(), MotionKey.From(123)));
        Assert.Contains("unknown", error.Message);
        Assert.Equal(skins, f.Scene.LiveSkinnedMeshCount);
    }

    static Matrix4x4[] Palette(Skeleton skeleton) => Enumerable.Repeat(Matrix4x4.Identity, skeleton.BoneCount).ToArray();
    static SkeletonContract Contract() => new([new("joint-0", null, true)]);

    static (byte[] Glb, byte[] Map, string SetHash, string CompanionHash) Assets(CoverageAttachmentFixture f)
    {
        _ = f.Load();
        var rigid = (RigidAttachmentPartDefinition)f.Loaded.Definition.Parts[0];
        string setHash = f.Source.Add("example-set.json", AttachmentSetJson.Write(new AttachmentSetDefinition(
            [new RigidAttachmentPartDefinition(1, rigid.Mesh, "joint-0", Matrix4x4.Identity)])));
        byte[] glb = CoverageImportTests.HostFixture().Build();
        var map = CoverageImportTests.Map(glb);
        string companionHash = f.Source.Add("example-coverage.json", EquipmentCoverageJson.Write(
            new EquipmentCoverageDefinition(setHash, HostCoverageMapJson.SemanticSha256(map), [f.Command(region: "patch")])));
        return (glb, HostCoverageMapJson.Write(map), setHash, companionHash);
    }
}
