using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public class DefinitionTests
{
    [Fact]
    public void Definition_captures_owned_parts()
    {
        var parts = new List<AttachmentPartDefinition>
        {
            new SkinnedAttachmentPartDefinition(2, new("parts/plate.glb", AttachmentFixtures.Hash)),
            AttachmentFixtures.Part(1),
        };
        var first = new AttachmentSetDefinition(parts);
        var reordered = new AttachmentSetDefinition(new[] { parts[1], parts[0] });
        parts.Clear();

        Assert.Equal(first.SemanticSha256, reordered.SemanticSha256);
        Assert.Single(first.Meshes);
        Assert.Equal(2, first.Parts.Count);
        Assert.Equal(1u, first.Parts[0].Id);
        Assert.Equal(2u, first.Parts[1].Id);
        Assert.IsType<RigidAttachmentPartDefinition>(first.Parts[0]);
        Assert.IsType<SkinnedAttachmentPartDefinition>(first.Parts[1]);
        Assert.Throws<NotSupportedException>(() => ((IList<AttachmentPartDefinition>)first.Parts).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<AttachmentMeshRef>)first.Meshes).Clear());
    }

    [Fact]
    public void Meshes_AreOrderedByOrdinalKey()
    {
        var set = new AttachmentSetDefinition(new[]
        {
            AttachmentFixtures.Part(1) with { Mesh = new("z.glb", AttachmentFixtures.Hash) },
            AttachmentFixtures.Part(2) with { Mesh = new("A.glb", AttachmentFixtures.Hash) },
        });
        Assert.Equal("A.glb", set.Meshes[0].Key);
        Assert.Equal("z.glb", set.Meshes[1].Key);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("null-part")]
    [InlineData("duplicate")]
    [InlineData("zero")]
    [InlineData("joint")]
    [InlineData("key")]
    [InlineData("hash")]
    [InlineData("conflicting-hash")]
    [InlineData("singular")]
    [InlineData("non-affine")]
    [InlineData("non-finite")]
    [InlineData("unknown-kind")]
    [InlineData("duplicate-across-kinds")]
    [InlineData("conflicting-hash-across-kinds")]
    [InlineData("skinned-key")]
    [InlineData("skinned-hash")]
    [InlineData("skinned-utf16")]
    public void Constructor_RejectsInvalidProgrammaticDefinitions(string name)
    {
        RigidAttachmentPartDefinition part = AttachmentFixtures.Part(1);
        AttachmentPartDefinition[] parts = name switch
        {
            "empty" => Array.Empty<RigidAttachmentPartDefinition>(),
            "null-part" => new RigidAttachmentPartDefinition[] { null! },
            "duplicate" => new[] { part, part },
            "zero" => new[] { part with { Id = 0 } },
            "joint" => new[] { part with { Joint = " " } },
            "key" => new[] { part with { Mesh = new("../part.glb", AttachmentFixtures.Hash) } },
            "hash" => new[] { part with { Mesh = new("part.glb", "bad") } },
            "conflicting-hash" => new[] { part, AttachmentFixtures.Part(2) with { Mesh = new("parts/plate.glb", new string('b', 64)) } },
            "singular" => new[] { part with { Local = default } },
            "non-affine" => new[] { part with { Local = Matrix4x4.CreatePerspectiveFieldOfView(1, 1, 1, 10) } },
            "non-finite" => new[] { part with { Local = Matrix4x4.CreateTranslation(float.NaN, 0, 0) } },
            "unknown-kind" => [new UnknownAttachmentPart(1, part.Mesh)],
            "duplicate-across-kinds" => [part, new SkinnedAttachmentPartDefinition(1, part.Mesh)],
            "conflicting-hash-across-kinds" => [part, new SkinnedAttachmentPartDefinition(2, new(part.Mesh.Key, new string('b', 64)))],
            "skinned-key" => [new SkinnedAttachmentPartDefinition(1, new("../part.glb", AttachmentFixtures.Hash))],
            "skinned-hash" => [new SkinnedAttachmentPartDefinition(1, new("part.glb", "bad"))],
            "skinned-utf16" => [new SkinnedAttachmentPartDefinition(1, new("part\uD800.glb", AttachmentFixtures.Hash))],
            _ => throw new ArgumentException(name),
        };
        Assert.ThrowsAny<ArgumentException>(() => new AttachmentSetDefinition(parts));
    }

    [Fact]
    public void References_RequireEveryJointAndMatchingMeshHash()
    {
        var set = new AttachmentSetDefinition(new[] { AttachmentFixtures.Part(1) });
        var joints = new HashSet<string>(StringComparer.Ordinal) { "hip" };
        var meshes = new Dictionary<string, string> { ["parts/plate.glb"] = AttachmentFixtures.Hash };
        set.ValidateReferences(joints, meshes);

        joints.Clear();
        InvalidDataException jointError = Assert.Throws<InvalidDataException>(() => set.ValidateReferences(joints, meshes));
        Assert.Contains("1", jointError.Message);
        Assert.Contains("hip", jointError.Message);
        joints.Add("hip");
        meshes.Clear();
        Assert.Contains("parts/plate.glb", Assert.Throws<InvalidDataException>(() => set.ValidateReferences(joints, meshes)).Message);
        meshes["parts/plate.glb"] = new string('b', 64);
        Assert.Throws<InvalidDataException>(() => set.ValidateReferences(joints, meshes));
    }

    [Fact]
    public void References_require_all_meshes_and_only_explicit_rigid_joints()
    {
        var skinned = new SkinnedAttachmentPartDefinition(2, new("parts/garment.glb", AttachmentFixtures.Hash));
        var set = new AttachmentSetDefinition([AttachmentFixtures.Part(1), skinned]);
        var joints = new HashSet<string>(StringComparer.Ordinal) { "hip" };
        var meshes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["parts/plate.glb"] = AttachmentFixtures.Hash,
            ["parts/garment.glb"] = AttachmentFixtures.Hash,
        };
        set.ValidateReferences(joints, meshes);
        new AttachmentSetDefinition([skinned]).ValidateReferences(new HashSet<string>(), meshes);

        meshes.Remove(skinned.Mesh.Key);
        Assert.Contains(skinned.Mesh.Key, Assert.Throws<InvalidDataException>(() => set.ValidateReferences(joints, meshes)).Message);
        meshes[skinned.Mesh.Key] = new string('b', 64);
        Assert.Throws<InvalidDataException>(() => set.ValidateReferences(joints, meshes));
        meshes[skinned.Mesh.Key] = AttachmentFixtures.Hash;
        joints.Clear();
        Assert.Contains("hip", Assert.Throws<InvalidDataException>(() => set.ValidateReferences(joints, meshes)).Message);
    }

    [Fact]
    public void References_remain_ordinal_with_case_insensitive_collections()
    {
        var set = new AttachmentSetDefinition([AttachmentFixtures.Part(1)]);
        var joints = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "HIP" };
        var meshes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["parts/plate.glb"] = AttachmentFixtures.Hash };
        Assert.Throws<InvalidDataException>(() => set.ValidateReferences(joints, meshes));
        joints.Clear();
        joints.Add("hip");
        meshes.Clear();
        meshes["PARTS/PLATE.GLB"] = AttachmentFixtures.Hash;
        Assert.Throws<InvalidDataException>(() => set.ValidateReferences(joints, meshes));
    }

    [Fact]
    public void SemanticRevision_ChangesWithMeshBytesJointOrLocalTransform()
    {
        RigidAttachmentPartDefinition part = AttachmentFixtures.Part(1);
        string initial = new AttachmentSetDefinition(new[] { part }).SemanticSha256;
        foreach (RigidAttachmentPartDefinition changed in new[]
        {
            part with { Mesh = new(part.Mesh.Key, new string('b', 64)) },
            part with { Joint = "knee" },
            part with { Local = Matrix4x4.CreateTranslation(1, 2, 3) },
        })
            Assert.NotEqual(initial, new AttachmentSetDefinition(new[] { changed }).SemanticSha256);
    }

    [Fact]
    public void Semantic_revision_distinguishes_part_kinds_and_skinned_mesh_revisions()
    {
        RigidAttachmentPartDefinition rigid = AttachmentFixtures.Part(1);
        var skinned = new SkinnedAttachmentPartDefinition(1, rigid.Mesh);
        string revision = new AttachmentSetDefinition([skinned]).SemanticSha256;
        Assert.NotEqual(new AttachmentSetDefinition([rigid]).SemanticSha256, revision);
        Assert.NotEqual(revision, new AttachmentSetDefinition([skinned with { Mesh = new(rigid.Mesh.Key, new string('b', 64)) }]).SemanticSha256);
    }

    sealed record UnknownAttachmentPart(uint Id, AttachmentMeshRef Mesh) : AttachmentPartDefinition(Id, Mesh);
}

internal static class AttachmentFixtures
{
    internal const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal const string Identity = "[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1]";
    internal static RigidAttachmentPartDefinition Part(uint id) => new(id, new("parts/plate.glb", Hash), "hip", Matrix4x4.Identity);
    internal static string PartJson(uint id = 1) => $$"""{"id":{{id}},"mesh":{"key":"parts/plate.glb","sha256":"{{Hash}}"},"joint":"hip","local":{{Identity}}} """;
    internal static string SetJson(string parts) => $$"""{"formatVersion":1,"parts":[{{parts}}]}""";
}
