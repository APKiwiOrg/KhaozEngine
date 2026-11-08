using System;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.Tools.Coverage;
using System.Text.Json.Nodes;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

public sealed class CoverageMixedConfigurationTests
{
    [Fact]
    public void Mixed_rigid_command_and_uncommanded_garment_build_verify_and_bind_the_same_configuration()
    {
        using var files = CoverageVerifyProcessTests.Files();
        byte[] hostBytes = File.ReadAllBytes(Path.Combine(files.Folder, "host.glb"));
        var original = AttachmentSetJson.Parse(File.ReadAllBytes(Path.Combine(files.Folder, "attachment.json")), "fixture");
        var garment = new SkinnedAttachmentPartDefinition(2, new("host.glb", CoverageBuildInputJson.Hash(hostBytes)));
        var plate = (RigidAttachmentPartDefinition)Assert.Single(original.Parts);
        plate = plate with { Mesh = new("support.glb", plate.Mesh.Sha256) };
        byte[] attachment = AttachmentSetJson.Write(new([plate, garment]));
        files.Configuration["attachment"] = files.Save("attachment.json", attachment);
        var companion = EquipmentCoverageJson.Read(File.ReadAllBytes(Path.Combine(files.Folder, "companion.json")), "fixture");
        files.Configuration["companion"] = files.Save("companion.json", EquipmentCoverageJson.Write(
            new(CoverageBuildInputJson.Hash(attachment), companion.HostMapSha256, companion.Commands)));
        files.Configuration["parts"]!.AsArray().Add(new JsonObject
        {
            ["id"] = 2, ["asset"] = files.Save("host.glb", hostBytes), ["hostJoint"] = "",
            ["socket"] = CoverageBuildFiles.Matrix(Matrix4x4.Identity),
            ["bodyAlignment"] = CoverageBuildFiles.Matrix(Matrix4x4.Identity),
        });
        files.WriteConfiguration();
        var input = CoverageBuildInputJson.Read(files.ManifestPath);
        var built = CoverageTableBuilder.Build(input);
        Assert.True(built.Evidence.Complete);
        Assert.True(CoverageSampleVerifier.Verify(input, built.Table).Complete);
        Assert.True(CoverageReplay.Verify(input, built.Table).Accepted);
        using var device = new FakeGpuDevice();
        using var scene = new Scene3D(device, new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm));
        var map = HostCoverageMapJson.Read(File.ReadAllBytes(Path.Combine(files.Folder, "map.json")), "fixture");
        var parts = GltfLoader.LoadSkinnedPartsWithMaterials(hostBytes, "fixture", map);
        var mesh = parts[0].Mesh;
        var skeleton = mesh.Skeleton!;
        var joints = new ContractJointMap(skeleton, new SkeletonContract([
            new("joint-0", null, false), new("joint-1", "joint-0", false)]));
        var adapter = new Scene3DAttachmentScene(scene);
        using var cache = new AttachmentSetCache(adapter, new Files(files.Folder));
        var loaded = cache.Get("attachment.json", CoverageBuildInputJson.Hash(attachment));
        var bound = loaded.Bind(joints, mesh);
        byte[] companionBytes = File.ReadAllBytes(Path.Combine(files.Folder, "companion.json"));
        var coverage = cache.GetCoverage(loaded, "companion.json", CoverageBuildInputJson.Hash(companionBytes));
        // The real runtime binding authenticates the table's configuration digest, including the garment.
        using var binding = new HostCoverageBinding(adapter, skeleton, parts.Select(p => scene.LoadSkinnedMesh(p.Mesh)).ToArray(),
            MotionKey.From(1), CoverageRecessionTableBinary.Read(CoverageRecessionTableBinary.Write(built.Table), "fixture"));
        Assert.True(binding.TrySetEquipment([new(17, bound, coverage)], out var failure), failure.ToString());
        Assert.Equal(input.Identity.ConfigurationSha256, built.Table.Identity.ConfigurationSha256);
        scene.Begin();
        Assert.True(binding.TryPrepare(skeleton, input.Samples[0].Palette, Matrix4x4.Identity, new(1),
            new(default, Matrix4x4.Identity), out _, out failure), failure.ToString());
        Assert.Contains(binding.PreparedEquipmentDraws.ToArray(), d => d.IsSkinned);
        var invalidCompanion = JsonNode.Parse(companionBytes)!;
        invalidCompanion["commands"]![0]!["partId"] = 2;
        files.Configuration["companion"] = files.Save("companion.json", System.Text.Encoding.UTF8.GetBytes(invalidCompanion.ToJsonString()));
        files.WriteConfiguration();
        var refusal = Assert.Throws<CoverageBuildDataException>(() => CoverageBuildInputJson.Read(files.ManifestPath));
        Assert.Equal(CoverageBuildInputRefusal.AssetBinding, refusal.Reason);
        Assert.Equal("$.parts.id", refusal.Path);
        files.Configuration["companion"] = files.Save("companion.json", companionBytes);
        files.WriteConfiguration();
        Assert.Equal(input.Identity, CoverageBuildInputJson.Read(files.ManifestPath).Identity);
    }

    sealed class Files(string folder) : IAttachmentAssetSource
    { public ReadOnlyMemory<byte> Read(string key) => File.ReadAllBytes(Path.Combine(folder, key)); }
}
