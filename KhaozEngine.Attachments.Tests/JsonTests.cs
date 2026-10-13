using System;
using System.IO;
using System.Text;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public class JsonTests
{
    [Fact]
    public void CanonicalJson_IsIndependentOfWhitespaceAndArrayOrder()
    {
        RigidAttachmentSetDefinition first = Parse(AttachmentFixtures.SetJson(AttachmentFixtures.PartJson(2) + "," + AttachmentFixtures.PartJson()));
        RigidAttachmentSetDefinition reordered = Parse("\n " + AttachmentFixtures.SetJson(AttachmentFixtures.PartJson() + "," + AttachmentFixtures.PartJson(2)));
        Assert.Equal(first.SemanticSha256, reordered.SemanticSha256);
        string expected = AttachmentFixtures.SetJson(AttachmentFixtures.PartJson().TrimEnd() + "," + AttachmentFixtures.PartJson(2).TrimEnd());
        Assert.Equal(expected, Encoding.UTF8.GetString(RigidAttachmentSetJson.Write(first)));
        Assert.Equal(first.SemanticSha256, RigidAttachmentSetJson.Parse(RigidAttachmentSetJson.Write(first), "canonical.json").SemanticSha256);
    }

    [Fact]
    public void Parse_OwnsValuesAfterInputBytesChange()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(AttachmentFixtures.SetJson(AttachmentFixtures.PartJson()));
        RigidAttachmentSetDefinition parsed = RigidAttachmentSetJson.Parse(bytes, "owned.json");
        Array.Clear(bytes);
        Assert.Equal("hip", Assert.Single(parsed.Parts).Joint);
        Assert.Equal(AttachmentFixtures.Hash, Assert.Single(parsed.Meshes).Sha256);
    }

    [Fact]
    public void InvalidCases_PinRequiredRefusals()
    {
        Assert.Throws<InvalidDataException>(() => ParseCase("duplicate-id"));
        Assert.Throws<InvalidDataException>(() => ParseCase("unknown-property"));
        Assert.Throws<InvalidDataException>(() => ParseCase("non-affine-matrix"));
    }

    [Theory]
    [InlineData("duplicate-root")]
    [InlineData("duplicate-part")]
    [InlineData("duplicate-mesh")]
    [InlineData("version-2")]
    [InlineData("id-zero")]
    [InlineData("id-overflow")]
    [InlineData("missing-mesh")]
    [InlineData("missing-key")]
    [InlineData("missing-hash")]
    [InlineData("missing-joint")]
    [InlineData("missing-local")]
    [InlineData("missing-version")]
    [InlineData("missing-parts")]
    [InlineData("empty-parts")]
    [InlineData("malformed-hash")]
    [InlineData("uppercase-hash")]
    [InlineData("nan")]
    [InlineData("overflow")]
    [InlineData("singular")]
    [InlineData("short-matrix")]
    [InlineData("long-matrix")]
    [InlineData("unknown-mesh-property")]
    [InlineData("unknown-part-property")]
    [InlineData("wrong-root")]
    [InlineData("wrong-part")]
    [InlineData("wrong-mesh")]
    [InlineData("wrong-parts")]
    [InlineData("wrong-joint")]
    public void Parse_RejectsInvalidDataWithSourceContext(string name)
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => ParseCase(name));
        Assert.Contains(name + ".json", error.Message);
    }

    [Theory]
    [InlineData("/plate.glb")]
    [InlineData("C:/plate.glb")]
    [InlineData("C:plate.glb")]
    [InlineData("parts\\plate.glb")]
    [InlineData("parts//plate.glb")]
    [InlineData("./plate.glb")]
    [InlineData("../plate.glb")]
    [InlineData("parts/../plate.glb")]
    [InlineData("parts/")]
    [InlineData("")]
    [InlineData("https://example.test/plate.glb")]
    [InlineData("file:plate.glb")]
    [InlineData("part\0.glb")]
    public void Parse_RejectsAssetPathForms(string key)
    {
        string json = AttachmentFixtures.SetJson(AttachmentFixtures.PartJson()).Replace("\"parts/plate.glb\"", System.Text.Json.JsonSerializer.Serialize(key), StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => Parse(json));
    }

    static RigidAttachmentSetDefinition Parse(string json) => RigidAttachmentSetJson.Parse(Encoding.UTF8.GetBytes(json), "fixture.json");

    static RigidAttachmentSetDefinition ParseCase(string name)
    {
        string part = AttachmentFixtures.PartJson();
        string json = name switch
        {
            "duplicate-id" => AttachmentFixtures.SetJson(part + "," + part),
            "duplicate-root" => AttachmentFixtures.SetJson(part).Replace("\"formatVersion\":1", "\"formatVersion\":1,\"formatVersion\":1", StringComparison.Ordinal),
            "duplicate-part" => AttachmentFixtures.SetJson(part.Replace("\"id\":1", "\"id\":1,\"id\":1", StringComparison.Ordinal)),
            "duplicate-mesh" => AttachmentFixtures.SetJson(part.Replace("\"key\":", "\"key\":\"other.glb\",\"key\":", StringComparison.Ordinal)),
            "version-2" => AttachmentFixtures.SetJson(part).Replace("Version\":1", "Version\":2", StringComparison.Ordinal),
            "id-zero" => AttachmentFixtures.SetJson(AttachmentFixtures.PartJson(0)),
            "id-overflow" => AttachmentFixtures.SetJson(part.Replace("\"id\":1", "\"id\":4294967296", StringComparison.Ordinal)),
            "missing-mesh" => AttachmentFixtures.SetJson($$"""{"id":1,"joint":"hip","local":{{AttachmentFixtures.Identity}}}"""),
            "missing-key" => AttachmentFixtures.SetJson(part.Replace("\"key\":\"parts/plate.glb\",", "", StringComparison.Ordinal)),
            "missing-hash" => AttachmentFixtures.SetJson(part.Replace(",\"sha256\":\"" + AttachmentFixtures.Hash + "\"", "", StringComparison.Ordinal)),
            "missing-joint" => AttachmentFixtures.SetJson(part.Replace("\"joint\":\"hip\",", "", StringComparison.Ordinal)),
            "missing-local" => AttachmentFixtures.SetJson(part.Replace(",\"local\":" + AttachmentFixtures.Identity, "", StringComparison.Ordinal)),
            "missing-version" => $$"""{"parts":[{{part}}]}""",
            "missing-parts" => "{\"formatVersion\":1}",
            "empty-parts" => AttachmentFixtures.SetJson(""),
            "malformed-hash" => AttachmentFixtures.SetJson(part.Replace(AttachmentFixtures.Hash, "abcd", StringComparison.Ordinal)),
            "uppercase-hash" => AttachmentFixtures.SetJson(part.Replace(AttachmentFixtures.Hash, AttachmentFixtures.Hash.ToUpperInvariant(), StringComparison.Ordinal)),
            "nan" => AttachmentFixtures.SetJson(part.Replace(AttachmentFixtures.Identity, "[NaN,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1]", StringComparison.Ordinal)),
            "overflow" => AttachmentFixtures.SetJson(part.Replace(AttachmentFixtures.Identity, "[1e100,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1]", StringComparison.Ordinal)),
            "singular" => AttachmentFixtures.SetJson(part.Replace(AttachmentFixtures.Identity, "[0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0]", StringComparison.Ordinal)),
            "non-affine-matrix" => AttachmentFixtures.SetJson(part.Replace(AttachmentFixtures.Identity, "[1,0,0,1,0,1,0,0,0,0,1,0,0,0,0,1]", StringComparison.Ordinal)),
            "short-matrix" => AttachmentFixtures.SetJson(part.Replace(AttachmentFixtures.Identity, "[1]", StringComparison.Ordinal)),
            "long-matrix" => AttachmentFixtures.SetJson(part.Replace(AttachmentFixtures.Identity, "[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1,0]", StringComparison.Ordinal)),
            "unknown-property" => AttachmentFixtures.SetJson(part).Replace("\"parts\":", "\"extra\":0,\"parts\":", StringComparison.Ordinal),
            "unknown-mesh-property" => AttachmentFixtures.SetJson(part.Replace("\"key\":", "\"extra\":0,\"key\":", StringComparison.Ordinal)),
            "unknown-part-property" => AttachmentFixtures.SetJson(part.Replace("\"id\":", "\"extra\":0,\"id\":", StringComparison.Ordinal)),
            "wrong-root" => "[]",
            "wrong-part" => AttachmentFixtures.SetJson("null"),
            "wrong-mesh" => AttachmentFixtures.SetJson(part.Replace("{\"key\":\"parts/plate.glb\",\"sha256\":\"" + AttachmentFixtures.Hash + "\"}", "null", StringComparison.Ordinal)),
            "wrong-parts" => "{\"formatVersion\":1,\"parts\":{}}",
            "wrong-joint" => AttachmentFixtures.SetJson(part.Replace("\"hip\"", "null", StringComparison.Ordinal)),
            _ => throw new ArgumentException(name),
        };
        return RigidAttachmentSetJson.Parse(Encoding.UTF8.GetBytes(json), name + ".json");
    }
}
