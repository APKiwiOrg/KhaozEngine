using System;
using System.Collections.Generic;
using System.Text;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public class AuthoredStringTests
{
    [Theory]
    [InlineData("joint", '\uD800')]
    [InlineData("joint", '\uDC00')]
    [InlineData("mesh", '\uD800')]
    [InlineData("mesh", '\uDC00')]
    public void Constructor_RejectsMalformedUtf16WithParameterContext(string field, char surrogate)
    {
        RigidAttachmentPartDefinition part = AttachmentFixtures.Part(7);
        string reference = field == "joint" ? "hip" + surrogate : "parts/plate" + surrogate + ".glb";
        part = field == "joint" ? part with { Joint = reference } : part with { Mesh = new(reference, AttachmentFixtures.Hash) };

        var error = Assert.Throws<AttachmentSetArgumentException>(() => new AttachmentSetDefinition(new[] { part }));

        Assert.Equal("parts", error.ParamName);
        Assert.Equal(field == "joint" ? AttachmentSetRefusal.JointNameMalformedUtf16 : AttachmentSetRefusal.MeshKeyInvalid, error.Reason);
        Assert.Equal(7u, error.PartId);
        if (field == "joint") Assert.Equal(reference, error.JointName);
        else Assert.Equal(reference, error.MeshKey);
    }

    [Theory]
    [InlineData("joint", "\\uD800")]
    [InlineData("joint", "\\uDC00")]
    [InlineData("mesh", "\\uD800")]
    [InlineData("mesh", "\\uDC00")]
    public void Parse_RejectsMalformedUtf16WithSourceContext(string field, string escape)
    {
        string json = AttachmentFixtures.SetJson(AttachmentFixtures.PartJson()).Replace(
            field == "joint" ? "hip" : "parts/plate.glb", escape, StringComparison.Ordinal);

        var error = Assert.Throws<AttachmentSetDataException>(() =>
            AttachmentSetJson.Parse(Encoding.UTF8.GetBytes(json), "malformed.json"));

        Assert.Equal(AttachmentSetRefusal.JsonValueShapeInvalid, error.Reason);
        Assert.Equal("malformed.json", error.SourceName);
        Assert.NotNull(error.InnerException);
    }

    [Theory]
    [InlineData("hip\U0001F9B4", "parts/plate\U0001F9B4.glb")]
    [InlineData("\U00010000hip\U0010FFFF", "\U00010000parts/plate\U0010FFFF.glb")]
    [InlineData("hip\uFFFD", "parts/plate\uFFFD.glb")]
    [InlineData("h\u00EDp", "parts/pl\u00E1te.glb")]
    [InlineData("hi\u0301p", "parts/pla\u0301te.glb")]
    public void WriteParse_PreservesValidUnicodeIdentityAndReferenceResolution(string joint, string key)
    {
        var definition = new AttachmentSetDefinition(new[]
        {
            AttachmentFixtures.Part(7) with { Joint = joint, Mesh = new(key, AttachmentFixtures.Hash) },
        });

        byte[] canonical = AttachmentSetJson.Write(definition);
        AttachmentSetDefinition parsed = AttachmentSetJson.Parse(canonical, "unicode.json");

        RigidAttachmentPartDefinition part = Assert.IsType<RigidAttachmentPartDefinition>(Assert.Single(parsed.Parts));
        Assert.Equal(joint, part.Joint);
        Assert.Equal(key, part.Mesh.Key);
        Assert.Equal(definition.SemanticSha256, parsed.SemanticSha256);
        Assert.Equal(canonical, AttachmentSetJson.Write(parsed));
        parsed.ValidateReferences(new HashSet<string>(StringComparer.Ordinal) { joint },
            new Dictionary<string, string>(StringComparer.Ordinal) { [key] = AttachmentFixtures.Hash });
    }

    [Theory]
    [InlineData("joint")]
    [InlineData("mesh")]
    public void UnicodeNormalization_DoesNotCollapseOrdinalIdentity(string field)
    {
        RigidAttachmentPartDefinition part = AttachmentFixtures.Part(7);
        RigidAttachmentPartDefinition composed = field == "joint"
            ? part with { Joint = "h\u00EDp" } : part with { Mesh = new("parts/pl\u00E1te.glb", AttachmentFixtures.Hash) };
        RigidAttachmentPartDefinition decomposed = field == "joint"
            ? part with { Joint = "hi\u0301p" } : part with { Mesh = new("parts/pla\u0301te.glb", AttachmentFixtures.Hash) };
        var first = new AttachmentSetDefinition(new[] { composed });
        var second = new AttachmentSetDefinition(new[] { decomposed });

        Assert.NotEqual(first.SemanticSha256, second.SemanticSha256);
        AttachmentSetDefinition parsed = AttachmentSetJson.Parse(AttachmentSetJson.Write(first), "ordinal.json");
        var joints = new HashSet<string>(StringComparer.Ordinal) { decomposed.Joint };
        var meshes = new Dictionary<string, string>(StringComparer.Ordinal) { [decomposed.Mesh.Key] = AttachmentFixtures.Hash };
        if (field == "joint")
            AttachmentSetReferenceAssertions.Joint(() => parsed.ValidateReferences(joints, meshes), 7, "h\u00EDp");
        else
            AttachmentSetReferenceAssertions.Mesh(() => parsed.ValidateReferences(joints, meshes), 7, "parts/pl\u00E1te.glb", null);
    }
}
