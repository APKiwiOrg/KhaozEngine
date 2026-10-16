using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public class JsonTests
{
    [Theory]
    [InlineData("stone-body.v1.json", "878eb4752608bcc51aa5d8447d76782ae21323c08bd0c974cff8a1e12fd1c8ea")]
    [InlineData("stone-helmet.v1.json", "66d0bd8d083cb5b8cbebb968f96d8920980778ce7558e7b0e46229db00b3d9df")]
    [InlineData("stone-legs.v1.json", "57cce9c9f089b3b1bcd041863997fd547e02cfbe46378c8c98a8cc88b6eea798")]
    public void Shipped_stone_v1_bytes_and_semantic_hash_are_unchanged(string fixtureName, string expectedSha)
    {
        byte[] shippedBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName));
        Assert.Equal(expectedSha, Convert.ToHexStringLower(SHA256.HashData(shippedBytes)));
        AttachmentSetDefinition legacy = AttachmentSetJson.Parse(shippedBytes, fixtureName);
        Assert.All(legacy.Parts, part => Assert.IsType<RigidAttachmentPartDefinition>(part));
        byte[] written = AttachmentSetJson.Write(legacy);
        Assert.Equal(shippedBytes, written);
        Assert.Equal(expectedSha, legacy.SemanticSha256);
        using JsonDocument document = JsonDocument.Parse(written);
        Assert.Equal(1, document.RootElement.GetProperty("formatVersion").GetInt32());
    }

    [Fact]
    public void Legacy_attachment_bytes_are_unchanged()
    {
        string plate = $$"""{"key":"parts/plate.glb","sha256":"{{AttachmentFixtures.Hash}}"}""";
        string cloth = $$"""{"key":"parts/cloth.glb","sha256":"{{new string('b', 64)}}"}""";
        var shoulder = new RigidAttachmentPartDefinition(2, new("parts/plate.glb", AttachmentFixtures.Hash), "spine", Matrix4x4.CreateTranslation(0.5f, -0.25f, 2f));
        var legacyRigid = new AttachmentSetDefinition([shoulder, AttachmentFixtures.Part(1)]);
        var legacyMixed = new AttachmentSetDefinition([new SkinnedAttachmentPartDefinition(3, new("parts/cloth.glb", new string('b', 64))), AttachmentFixtures.Part(1)]);
        byte[] legacyV1Bytes = Encoding.UTF8.GetBytes(
            $$"""{"formatVersion":1,"parts":[{"id":1,"mesh":{{plate}},"joint":"hip","local":{{AttachmentFixtures.Identity}}},""" +
            $$"""{"id":2,"mesh":{{plate}},"joint":"spine","local":[1,0,0,0,0,1,0,0,0,0,1,0,0.5,-0.25,2,1]}]}""");
        byte[] legacyV2Bytes = Encoding.UTF8.GetBytes(
            $$"""{"formatVersion":2,"parts":[{"id":1,"kind":"rigid","mesh":{{plate}},"joint":"hip","local":{{AttachmentFixtures.Identity}}},""" +
            $$"""{"id":3,"kind":"skinned","mesh":{{cloth}}}]}""");

        Assert.Equal(legacyV1Bytes, AttachmentSetJson.Write(legacyRigid));
        Assert.Equal(legacyV2Bytes, AttachmentSetJson.Write(legacyMixed));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(legacyV1Bytes)), legacyRigid.SemanticSha256);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(legacyV2Bytes)), legacyMixed.SemanticSha256);
        Assert.Equal(legacyV1Bytes, AttachmentSetJson.Write(AttachmentSetJson.Parse(legacyV1Bytes, "legacy-v1.json")));
        Assert.Equal(legacyV2Bytes, AttachmentSetJson.Write(AttachmentSetJson.Parse(legacyV2Bytes, "legacy-v2.json")));
    }

    [Fact]
    public void Mixed_v2_round_trips_and_sorts_by_id()
    {
        string rigid = AttachmentFixtures.PartJson().TrimEnd().Replace("\"id\":1,", "\"id\":1,\"kind\":\"rigid\",", StringComparison.Ordinal);
        string skinned = $$$"""{"id":2,"kind":"skinned","mesh":{"key":"parts/garment.glb","sha256":"{{{AttachmentFixtures.Hash}}}"}}""";
        AttachmentSetDefinition mixed = Parse(V2(skinned + "," + rigid));
        Assert.Equal(new uint[] { 1, 2 }, mixed.Parts.Select(part => part.Id));

        byte[] written = AttachmentSetJson.Write(mixed);
        using JsonDocument document = JsonDocument.Parse(written);
        Assert.Equal(2, document.RootElement.GetProperty("formatVersion").GetInt32());
        Assert.Equal(V2(rigid + "," + skinned), Encoding.UTF8.GetString(written));
        AttachmentSetDefinition reparsed = AttachmentSetJson.Parse(written, "mixed.json");
        Assert.IsType<RigidAttachmentPartDefinition>(reparsed.Parts[0]);
        Assert.IsType<SkinnedAttachmentPartDefinition>(reparsed.Parts[1]);
        Assert.Equal(mixed.SemanticSha256, reparsed.SemanticSha256);
        Assert.Equal(written, AttachmentSetJson.Write(reparsed));
    }

    [Fact]
    public void Rigid_only_v2_canonicalizes_to_v1()
    {
        string rigid = AttachmentFixtures.PartJson().TrimEnd();
        AttachmentSetDefinition parsed = Parse(V2(rigid.Replace("\"id\":1,", "\"id\":1,\"kind\":\"rigid\",", StringComparison.Ordinal)));
        byte[] expected = Encoding.UTF8.GetBytes(AttachmentFixtures.SetJson(rigid));
        byte[] written = AttachmentSetJson.Write(parsed);
        Assert.Equal(expected, written);
        Assert.Equal(Parse(AttachmentFixtures.SetJson(rigid)).SemanticSha256, parsed.SemanticSha256);
        using JsonDocument document = JsonDocument.Parse(written);
        Assert.Equal(1, document.RootElement.GetProperty("formatVersion").GetInt32());
    }

    [Fact]
    public void Skinned_only_v2_round_trips_without_rigid_members()
    {
        string json = V2($$$"""{"id":1,"kind":"skinned","mesh":{"key":"parts/garment.glb","sha256":"{{{AttachmentFixtures.Hash}}}"}}""");
        AttachmentSetDefinition parsed = Parse(json);
        Assert.IsType<SkinnedAttachmentPartDefinition>(Assert.Single(parsed.Parts));
        Assert.Equal(json, Encoding.UTF8.GetString(AttachmentSetJson.Write(parsed)));
    }

    [Fact]
    public void Canonical_matrices_normalize_signed_zero_and_preserve_float_values()
    {
        string local = "[1,-0,0,0,0,1,0,0,0,0,1,0,0.125,-2.5,3.75,1]";
        string part = AttachmentFixtures.PartJson().TrimEnd().Replace(AttachmentFixtures.Identity, local, StringComparison.Ordinal);
        string expected = AttachmentFixtures.SetJson(part.Replace("-0", "0", StringComparison.Ordinal));
        Assert.Equal(expected, Encoding.UTF8.GetString(AttachmentSetJson.Write(Parse(AttachmentFixtures.SetJson(part)))));
    }

    [Theory]
    [InlineData("unknown-kind")]
    [InlineData("case-changed-kind")]
    [InlineData("non-string-kind")]
    [InlineData("null-kind")]
    [InlineData("missing-kind")]
    [InlineData("v1-kind")]
    [InlineData("skinned-joint")]
    [InlineData("skinned-local")]
    [InlineData("duplicate-root")]
    [InlineData("duplicate-part")]
    [InlineData("duplicate-kind")]
    [InlineData("duplicate-skinned-part")]
    [InlineData("duplicate-mesh")]
    [InlineData("escaped-duplicate-root")]
    [InlineData("escaped-duplicate-part")]
    [InlineData("escaped-duplicate-kind")]
    [InlineData("escaped-duplicate-mesh")]
    [InlineData("unknown-root")]
    [InlineData("unknown-rigid")]
    [InlineData("unknown-skinned")]
    [InlineData("unknown-mesh")]
    [InlineData("duplicate-id-across-kinds")]
    [InlineData("missing-mesh")]
    [InlineData("unsupported-version")]
    [InlineData("zero-id")]
    [InlineData("conflicting-hashes")]
    public void V2_refuses_ambiguous_members(string refusal)
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => ParseV2Case(refusal));
        Assert.Contains(refusal + ".json", error.Message);
    }

    [Fact]
    public void CanonicalJson_IsIndependentOfWhitespaceAndArrayOrder()
    {
        AttachmentSetDefinition first = Parse(AttachmentFixtures.SetJson(AttachmentFixtures.PartJson(2) + "," + AttachmentFixtures.PartJson()));
        AttachmentSetDefinition reordered = Parse("\n " + AttachmentFixtures.SetJson(AttachmentFixtures.PartJson() + "," + AttachmentFixtures.PartJson(2)));
        Assert.Equal(first.SemanticSha256, reordered.SemanticSha256);
        string expected = AttachmentFixtures.SetJson(AttachmentFixtures.PartJson().TrimEnd() + "," + AttachmentFixtures.PartJson(2).TrimEnd());
        Assert.Equal(expected, Encoding.UTF8.GetString(AttachmentSetJson.Write(first)));
        Assert.Equal(first.SemanticSha256, AttachmentSetJson.Parse(AttachmentSetJson.Write(first), "canonical.json").SemanticSha256);
    }

    [Fact]
    public void Parse_OwnsValuesAfterInputBytesChange()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(AttachmentFixtures.SetJson(AttachmentFixtures.PartJson()));
        AttachmentSetDefinition parsed = AttachmentSetJson.Parse(bytes, "owned.json");
        Array.Clear(bytes);
        Assert.Equal("hip", Assert.IsType<RigidAttachmentPartDefinition>(Assert.Single(parsed.Parts)).Joint);
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
    [InlineData("malformed-string")]
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

    static AttachmentSetDefinition Parse(string json) => AttachmentSetJson.Parse(Encoding.UTF8.GetBytes(json), "fixture.json");

    static string V2(string parts) => $$"""{"formatVersion":2,"parts":[{{parts}}]}""";

    static AttachmentSetDefinition ParseV2Case(string name)
    {
        string rigid = AttachmentFixtures.PartJson().TrimEnd().Replace("\"id\":1,", "\"id\":1,\"kind\":\"rigid\",", StringComparison.Ordinal);
        string skinned = $$$"""{"id":2,"kind":"skinned","mesh":{"key":"parts/plate.glb","sha256":"{{{AttachmentFixtures.Hash}}}"}}""";
        string json = name switch
        {
            "unknown-kind" => V2(rigid.Replace("\"rigid\"", "\"cloth\"", StringComparison.Ordinal)),
            "case-changed-kind" => V2(rigid.Replace("\"rigid\"", "\"Rigid\"", StringComparison.Ordinal)),
            "non-string-kind" => V2(rigid.Replace("\"rigid\"", "1", StringComparison.Ordinal)),
            "null-kind" => V2(rigid.Replace("\"rigid\"", "null", StringComparison.Ordinal)),
            "missing-kind" => V2(AttachmentFixtures.PartJson()),
            "v1-kind" => AttachmentFixtures.SetJson(rigid),
            "skinned-joint" => V2(skinned.Replace("\"kind\":", "\"joint\":\"hip\",\"kind\":", StringComparison.Ordinal)),
            "skinned-local" => V2(skinned.Replace("\"kind\":", "\"local\":" + AttachmentFixtures.Identity + ",\"kind\":", StringComparison.Ordinal)),
            "duplicate-root" => V2(rigid).Replace("\"formatVersion\":2", "\"formatVersion\":2,\"formatVersion\":2", StringComparison.Ordinal),
            "duplicate-part" => V2(rigid.Replace("\"id\":1", "\"id\":1,\"id\":1", StringComparison.Ordinal)),
            "duplicate-kind" => V2(rigid.Replace("\"kind\":\"rigid\"", "\"kind\":\"rigid\",\"kind\":\"skinned\"", StringComparison.Ordinal)),
            "duplicate-skinned-part" => V2(skinned.Replace("\"id\":2", "\"id\":2,\"id\":2", StringComparison.Ordinal)),
            "duplicate-mesh" => V2(skinned.Replace("\"key\":", "\"key\":\"other.glb\",\"key\":", StringComparison.Ordinal)),
            "escaped-duplicate-root" => V2(rigid).Replace("\"formatVersion\":2", "\"formatVersion\":2,\"\\u0066ormatVersion\":2", StringComparison.Ordinal),
            "escaped-duplicate-part" => V2(rigid.Replace("\"id\":1", "\"id\":1,\"\\u0069d\":1", StringComparison.Ordinal)),
            "escaped-duplicate-kind" => V2(rigid.Replace("\"kind\":\"rigid\"", "\"kind\":\"rigid\",\"\\u006bind\":\"rigid\"", StringComparison.Ordinal)),
            "escaped-duplicate-mesh" => V2(skinned.Replace("\"key\":", "\"\\u006bey\":\"other.glb\",\"key\":", StringComparison.Ordinal)),
            "unknown-root" => V2(rigid).Replace("\"parts\":", "\"extra\":0,\"parts\":", StringComparison.Ordinal),
            "unknown-rigid" => V2(rigid.Replace("\"id\":", "\"extra\":0,\"id\":", StringComparison.Ordinal)),
            "unknown-skinned" => V2(skinned.Replace("\"id\":", "\"extra\":0,\"id\":", StringComparison.Ordinal)),
            "unknown-mesh" => V2(skinned.Replace("\"key\":", "\"extra\":0,\"key\":", StringComparison.Ordinal)),
            "duplicate-id-across-kinds" => V2(rigid + "," + skinned.Replace("\"id\":2", "\"id\":1", StringComparison.Ordinal)),
            "missing-mesh" => V2("{\"id\":1,\"kind\":\"skinned\"}"),
            "unsupported-version" => V2(skinned).Replace("\"formatVersion\":2", "\"formatVersion\":3", StringComparison.Ordinal),
            "zero-id" => V2(skinned.Replace("\"id\":2", "\"id\":0", StringComparison.Ordinal)),
            "conflicting-hashes" => V2(rigid + "," + skinned.Replace(AttachmentFixtures.Hash, new string('b', 64), StringComparison.Ordinal)),
            _ => throw new ArgumentException(name),
        };
        return AttachmentSetJson.Parse(Encoding.UTF8.GetBytes(json), name + ".json");
    }

    static AttachmentSetDefinition ParseCase(string name)
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
            "malformed-string" => AttachmentFixtures.SetJson(part.Replace("\"hip\"", "\"\\uD800\"", StringComparison.Ordinal)),
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
        return AttachmentSetJson.Parse(Encoding.UTF8.GetBytes(json), name + ".json");
    }
}
