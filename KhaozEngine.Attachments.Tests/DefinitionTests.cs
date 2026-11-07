using System;
using System.Collections.Generic;
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
    [InlineData("empty", AttachmentSetRefusal.PartsEmpty)]
    [InlineData("null-part", AttachmentSetRefusal.PartNull)]
    [InlineData("duplicate", AttachmentSetRefusal.PartIdInvalidOrDuplicate)]
    [InlineData("zero", AttachmentSetRefusal.PartIdInvalidOrDuplicate)]
    [InlineData("joint", AttachmentSetRefusal.JointNameEmpty)]
    [InlineData("key", AttachmentSetRefusal.MeshKeyInvalid)]
    [InlineData("hash", AttachmentSetRefusal.MeshHashInvalid)]
    [InlineData("conflicting-hash", AttachmentSetRefusal.MeshHashConflict)]
    [InlineData("singular", AttachmentSetRefusal.LocalTransformInvalid)]
    [InlineData("non-affine", AttachmentSetRefusal.LocalTransformInvalid)]
    [InlineData("non-finite", AttachmentSetRefusal.LocalTransformInvalid)]
    [InlineData("unknown-kind", AttachmentSetRefusal.PartKindUnsupported)]
    [InlineData("duplicate-across-kinds", AttachmentSetRefusal.PartIdInvalidOrDuplicate)]
    [InlineData("conflicting-hash-across-kinds", AttachmentSetRefusal.MeshHashConflict)]
    [InlineData("skinned-key", AttachmentSetRefusal.MeshKeyInvalid)]
    [InlineData("skinned-hash", AttachmentSetRefusal.MeshHashInvalid)]
    [InlineData("skinned-utf16", AttachmentSetRefusal.MeshKeyInvalid)]
    public void Constructor_RejectsInvalidProgrammaticDefinitions(string name, AttachmentSetRefusal reason)
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
            "singular" => new[] { part with { Local = Matrix4x4.CreateScale(0, 1, 1) } },
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
        Assert.Single(new AttachmentSetDefinition([part]).Parts);
        var error = Assert.Throws<AttachmentSetArgumentException>(() => new AttachmentSetDefinition(parts));
        Assert.IsAssignableFrom<ArgumentException>(error);
        Assert.Equal(reason, error.Reason);
        Assert.Equal("parts", error.ParamName);
        if (reason == AttachmentSetRefusal.PartsEmpty) Assert.Equal(0, error.PartCount);
        else if (reason == AttachmentSetRefusal.PartNull) Assert.Equal(0, error.PartIndex);
        else
        {
            Assert.Equal(name is "conflicting-hash" or "conflicting-hash-across-kinds" ? 2u : name == "zero" ? 0u : 1u, error.PartId);
            if (reason == AttachmentSetRefusal.PartIdInvalidOrDuplicate)
                Assert.Equal(name == "zero" ? 0 : 1, error.PartIndex);
            if (reason == AttachmentSetRefusal.PartKindUnsupported) Assert.Equal(typeof(UnknownAttachmentPart), error.ActualType);
            if (reason is AttachmentSetRefusal.MeshKeyInvalid or AttachmentSetRefusal.MeshHashInvalid or AttachmentSetRefusal.MeshHashConflict)
                Assert.Equal(parts[^1].Mesh.Key, error.MeshKey);
            if (reason is AttachmentSetRefusal.MeshHashInvalid or AttachmentSetRefusal.MeshHashConflict)
                Assert.Equal(parts[^1].Mesh.Sha256, error.DeclaredHash);
            if (reason == AttachmentSetRefusal.MeshHashConflict) Assert.Equal(AttachmentFixtures.Hash, error.ExistingHash);
        }
    }

    [Fact]
    public void References_RequireEveryJointAndMatchingMeshHash()
    {
        var set = new AttachmentSetDefinition(new[] { AttachmentFixtures.Part(1) });
        var joints = new HashSet<string>(StringComparer.Ordinal) { "hip" };
        var meshes = new Dictionary<string, string> { ["parts/plate.glb"] = AttachmentFixtures.Hash };
        set.ValidateReferences(joints, meshes);

        joints.Clear();
        AttachmentSetReferenceAssertions.Joint(() => set.ValidateReferences(joints, meshes), 1, "hip");
        joints.Add("hip");
        meshes.Clear();
        AttachmentSetReferenceAssertions.Mesh(() => set.ValidateReferences(joints, meshes), 1, "parts/plate.glb", null);
        meshes["parts/plate.glb"] = new string('b', 64);
        AttachmentSetReferenceAssertions.Mesh(() => set.ValidateReferences(joints, meshes), 1, "parts/plate.glb", new string('b', 64));
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
        AttachmentSetReferenceAssertions.Mesh(() => set.ValidateReferences(joints, meshes), 2, "parts/garment.glb", null);
        meshes[skinned.Mesh.Key] = new string('b', 64);
        AttachmentSetReferenceAssertions.Mesh(() => set.ValidateReferences(joints, meshes), 2, "parts/garment.glb", new string('b', 64));
        meshes[skinned.Mesh.Key] = AttachmentFixtures.Hash;
        joints.Clear();
        AttachmentSetReferenceAssertions.Joint(() => set.ValidateReferences(joints, meshes), 1, "hip");
    }

    [Fact]
    public void References_remain_ordinal_with_case_insensitive_collections()
    {
        var set = new AttachmentSetDefinition([AttachmentFixtures.Part(1)]);
        var joints = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "HIP" };
        var meshes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["parts/plate.glb"] = AttachmentFixtures.Hash };
        AttachmentSetReferenceAssertions.Joint(() => set.ValidateReferences(joints, meshes), 1, "hip");
        joints.Clear();
        joints.Add("hip");
        meshes.Clear();
        meshes["PARTS/PLATE.GLB"] = AttachmentFixtures.Hash;
        AttachmentSetReferenceAssertions.Mesh(() => set.ValidateReferences(joints, meshes), 1, "parts/plate.glb", null);
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
