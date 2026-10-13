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
    public void Definition_CapturesInputAndOrdersPartsWithoutDuplicatingMeshes()
    {
        var parts = new List<RigidAttachmentPartDefinition> { AttachmentFixtures.Part(2), AttachmentFixtures.Part(1) };
        var first = new RigidAttachmentSetDefinition(parts);
        var reordered = new RigidAttachmentSetDefinition(new[] { parts[1], parts[0] });
        parts.Clear();

        Assert.Equal(first.SemanticSha256, reordered.SemanticSha256);
        Assert.Single(first.Meshes);
        Assert.Equal(2, first.Parts.Count);
        Assert.Equal(1u, first.Parts[0].Id);
        Assert.Equal(2u, first.Parts[1].Id);
        Assert.Throws<NotSupportedException>(() => ((IList<RigidAttachmentPartDefinition>)first.Parts).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<AttachmentMeshRef>)first.Meshes).Clear());
    }

    [Fact]
    public void Meshes_AreOrderedByOrdinalKey()
    {
        var set = new RigidAttachmentSetDefinition(new[]
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
    public void Constructor_RejectsInvalidProgrammaticDefinitions(string name)
    {
        RigidAttachmentPartDefinition part = AttachmentFixtures.Part(1);
        RigidAttachmentPartDefinition[] parts = name switch
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
            _ => throw new ArgumentException(name),
        };
        Assert.ThrowsAny<ArgumentException>(() => new RigidAttachmentSetDefinition(parts));
    }

    [Fact]
    public void References_RequireEveryJointAndMatchingMeshHash()
    {
        var set = new RigidAttachmentSetDefinition(new[] { AttachmentFixtures.Part(1) });
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
    public void SemanticRevision_ChangesWithMeshBytesJointOrLocalTransform()
    {
        RigidAttachmentPartDefinition part = AttachmentFixtures.Part(1);
        string initial = new RigidAttachmentSetDefinition(new[] { part }).SemanticSha256;
        foreach (RigidAttachmentPartDefinition changed in new[]
        {
            part with { Mesh = new(part.Mesh.Key, new string('b', 64)) },
            part with { Joint = "knee" },
            part with { Local = Matrix4x4.CreateTranslation(1, 2, 3) },
        })
            Assert.NotEqual(initial, new RigidAttachmentSetDefinition(new[] { changed }).SemanticSha256);
    }
}

internal static class AttachmentFixtures
{
    internal const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal const string Identity = "[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1]";
    internal static RigidAttachmentPartDefinition Part(uint id) => new(id, new("parts/plate.glb", Hash), "hip", Matrix4x4.Identity);
    internal static string PartJson(uint id = 1) => $$"""{"id":{{id}},"mesh":{"key":"parts/plate.glb","sha256":"{{Hash}}"},"joint":"hip","local":{{Identity}}} """;
    internal static string SetJson(string parts) => $$"""{"formatVersion":1,"parts":[{{parts}}]}""";
}
