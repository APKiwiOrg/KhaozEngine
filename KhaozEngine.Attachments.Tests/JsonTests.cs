using System;
using System.IO;
using System.Linq;
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

    // Captured from the format 2 writer before format 3 existed. The stone fixtures above pin format 1 the same way.
    const string MixedV2SemanticSha256 = "a485d9b7cd078c549a59089bd73caff3a232f9ba1b05b20c06a24ccbd394b541";

    [Fact]
    public void A_format_2_definition_keeps_its_semantic_hash()
    {
        byte[] committed = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "mixed.v2.json"));
        AttachmentSetDefinition parsed = AttachmentSetJson.Parse(committed, "mixed.v2.json");
        Assert.Equal(MixedV2SemanticSha256, parsed.SemanticSha256);
        Assert.Equal(committed, AttachmentSetJson.Write(parsed));
    }

    [Theory]
    [InlineData(AttachmentLayer.Body, "body", "2902d15422e07799eed6dd3b0b4203242bcdfc9419dfade9a8486e564dfdc3f8")]
    [InlineData(AttachmentLayer.Underclothes, "underclothes", "c785a9b3d2be99f7f1e1d96aa3557701a68e1b0f23488c70b1f302b9af3e4605")]
    [InlineData(AttachmentLayer.Garment, "garment", "5fd9e7579ef53adc536649752ff57596568bb59e228e1bd0188626ef917056c7")]
    [InlineData(AttachmentLayer.Armour, "armour", "fd8667238dd01c60894e624ab29ababef584dc583c27dad691df65602a81f85f")]
    [InlineData(AttachmentLayer.Outerwear, "outerwear", "fc7fd18ed062d5a17573d4dcf2470d1e4543e1e93b723e1807e76111fb28247d")]
    public void A_format_3_set_round_trips_layer_covers_and_region_tags(AttachmentLayer layer, string name, string expectedSha)
    {
        string json = AttachmentFixtures.RegionsSetJson(name);
        AttachmentSetDefinition parsed = Parse(json);
        Assert.Equal(layer, parsed.Layer);
        Assert.Equal(["a", "b"], parsed.Covers);
        Assert.Equal(AttachmentFixtures.TaggedRigid(), parsed.Parts[0]);
        Assert.Equal(AttachmentFixtures.TaggedSkinned(), parsed.Parts[1]);

        byte[] written = AttachmentSetJson.Write(parsed);
        Assert.Equal(json, Encoding.UTF8.GetString(written));
        Assert.Equal(expectedSha, parsed.SemanticSha256);
        Assert.Equal(expectedSha, AttachmentFixtures.RegionsSet(layer).SemanticSha256);
        Assert.Equal(parsed.SemanticSha256, AttachmentSetJson.Parse(written, "round-trip.json").SemanticSha256);
    }

    [Fact]
    public void A_format_4_set_round_trips_its_canonical_bytes()
    {
        string canonical = AttachmentFixtures.MotionSetJson();
        AttachmentSetDefinition set = Parse(canonical);
        ChainMotionDefinition motion = Assert.IsType<ChainMotionDefinition>(Assert.IsType<SkinnedAttachmentPartDefinition>(Assert.Single(set.Parts)).Motion);
        Assert.Equal(AttachmentFixtures.Motion().Chains, motion.Chains);
        Assert.Equal([new ChainLink(0, 1), new ChainLink(1, 2)], motion.Links);
        Assert.Equal(AttachmentFixtures.Colliders(), set.Colliders);
        byte[] written = AttachmentSetJson.Write(set);
        Assert.Equal(canonical, Encoding.UTF8.GetString(written));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(written)), set.SemanticSha256);
    }

    [Theory]
    [InlineData("links")]
    [InlineData("colliders")]
    [InlineData("signed zero")]
    public void Format_4_canonicalizes_unordered_data_and_signed_zero(string change)
    {
        string canonical = AttachmentFixtures.MotionSetJson();
        string alternate = change switch
        {
            "links" => canonical.Replace("[[0,1],[1,2]]", "[[2,1],[1,0]]", StringComparison.Ordinal),
            "colliders" => canonical.Replace(
                AttachmentFixtures.Collider0Json + "," + AttachmentFixtures.Collider1Json,
                AttachmentFixtures.Collider1Json + "," + AttachmentFixtures.Collider0Json, StringComparison.Ordinal),
            "signed zero" => canonical.Replace("[0,", "[-0,", StringComparison.Ordinal)
                .Replace("\"radius\":0}", "\"radius\":-0}", StringComparison.Ordinal),
            _ => throw new ArgumentException(change),
        };
        Assert.NotEqual(alternate, canonical);
        AttachmentSetDefinition set = Parse(alternate);
        Assert.Equal(canonical, Encoding.UTF8.GetString(AttachmentSetJson.Write(set)));
        Assert.Equal(Parse(canonical).SemanticSha256, set.SemanticSha256);
    }

    [Theory]
    [InlineData("chains")]
    [InlineData("joints")]
    [InlineData("tip")]
    [InlineData("settings")]
    [InlineData("link")]
    [InlineData("collider")]
    public void Format_4_revision_tracks_each_semantic_change(string change)
    {
        string control = AttachmentFixtures.MotionSetJson();
        string changed = change switch
        {
            "chains" => control.Replace(AttachmentFixtures.Chain0Json + "," + AttachmentFixtures.Chain1Json,
                AttachmentFixtures.Chain1Json + "," + AttachmentFixtures.Chain0Json, StringComparison.Ordinal),
            "joints" => control.Replace("\"left0\",\"left1\"", "\"left1\",\"left0\"", StringComparison.Ordinal),
            "tip" => control.Replace("[0,-0.25,0]", "[0,-0.375,0]", StringComparison.Ordinal),
            "settings" => control.Replace("\"damping\":3", "\"damping\":2", StringComparison.Ordinal),
            "link" => control.Replace("[0,1],[1,2]", "[0,2],[1,2]", StringComparison.Ordinal),
            "collider" => control.Replace("\"radius\":0.125", "\"radius\":0.1875", StringComparison.Ordinal),
            _ => throw new ArgumentException(change),
        };
        Assert.NotEqual(Parse(control).SemanticSha256, Parse(changed).SemanticSha256);
    }

    [Theory]
    [InlineData(false, false, 3)]
    [InlineData(true, false, 4)]
    [InlineData(false, true, 4)]
    [InlineData(true, true, 4)]
    public void Write_uses_the_lowest_format_for_a_worn_set(bool motion, bool colliders, int expectedVersion)
    {
        var part = AttachmentFixtures.TaggedSkinned() with
        {
            Motion = motion ? new ChainMotionDefinition([AttachmentFixtures.Chain(0)], []) : null,
        };
        var set = new AttachmentSetDefinition([part], AttachmentLayer.Garment, [], colliders ? AttachmentFixtures.Colliders() : []);
        byte[] written = AttachmentSetJson.Write(set);
        using JsonDocument document = JsonDocument.Parse(written);
        JsonElement root = document.RootElement;
        Assert.Equal(expectedVersion, root.GetProperty("formatVersion").GetInt32());
        Assert.Equal(colliders, root.TryGetProperty("colliders", out _));
        Assert.Equal(motion, root.GetProperty("parts")[0].TryGetProperty("motion", out JsonElement partMotion));
        if (motion) Assert.False(partMotion.TryGetProperty("links", out _));
        Assert.Equal(set.SemanticSha256, AttachmentSetJson.Parse(written, "round-trip.json").SemanticSha256);
    }

    [Fact]
    public void Format_4_without_motion_or_colliders_canonicalizes_to_format_3()
    {
        string format3 = AttachmentFixtures.RegionsSetJson();
        string format4 = format3.Replace("\"formatVersion\":3,", "\"formatVersion\":4,\"colliders\":[],", StringComparison.Ordinal);
        AttachmentSetDefinition set = Parse(format4);
        Assert.Equal(format3, Encoding.UTF8.GetString(AttachmentSetJson.Write(set)));
        Assert.Equal(Parse(format3).SemanticSha256, set.SemanticSha256);
    }

    [Theory]
    [InlineData("stone-body.v1.json")]
    [InlineData("mixed.v2.json")]
    public void Formats_1_and_2_parse_as_before_with_no_layer(string fixtureName)
    {
        AttachmentSetDefinition parsed = AttachmentSetJson.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName)), fixtureName);
        Assert.Null(parsed.Layer);
        Assert.Empty(parsed.Covers);
        Assert.NotEmpty(parsed.Parts);
        Assert.All(parsed.Parts, part =>
        {
            if (part is RigidAttachmentPartDefinition rigid) Assert.Null(rigid.Region);
            else Assert.Empty(Assert.IsType<SkinnedAttachmentPartDefinition>(part).Regions);
        });
    }

    [Fact]
    public void Write_emits_format_3_only_when_a_layer_is_set()
    {
        RigidAttachmentPartDefinition rigid = AttachmentFixtures.Part(1);
        var skinned = new SkinnedAttachmentPartDefinition(2, new("parts/garment.glb", AttachmentFixtures.Hash));
        Assert.Equal(1, WrittenVersion(new([rigid])));
        Assert.Equal(2, WrittenVersion(new([rigid, skinned])));
        Assert.Equal(AttachmentSetJson.RegionsFormatVersion, WrittenVersion(new([rigid with { Region = "a" }], AttachmentLayer.Armour, [])));
        Assert.Equal(AttachmentSetJson.RegionsFormatVersion, WrittenVersion(AttachmentFixtures.RegionsSet()));
        Assert.Equal(2, AttachmentSetJson.CurrentFormatVersion);
    }

    static int WrittenVersion(AttachmentSetDefinition definition)
    {
        using JsonDocument document = JsonDocument.Parse(AttachmentSetJson.Write(definition));
        return document.RootElement.GetProperty("formatVersion").GetInt32();
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
        var error = Assert.Throws<AttachmentSetDataException>(() => ParseV2Case(refusal));
        AttachmentSetJsonRefusalAssertions.V2(refusal, error);
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
        AttachmentSetJsonRefusalAssertions.V1("duplicate-id", Assert.Throws<AttachmentSetDataException>(() => ParseCase("duplicate-id")));
        AttachmentSetJsonRefusalAssertions.V1("unknown-property", Assert.Throws<AttachmentSetDataException>(() => ParseCase("unknown-property")));
        AttachmentSetJsonRefusalAssertions.V1("non-affine-matrix", Assert.Throws<AttachmentSetDataException>(() => ParseCase("non-affine-matrix")));
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
    [InlineData("version-string")]
    [InlineData("version-overflow")]
    [InlineData("id-string")]
    [InlineData("id-negative")]
    [InlineData("matrix-not-array")]
    [InlineData("matrix-string-component")]
    [InlineData("empty-joint")]
    public void Parse_RejectsInvalidDataWithSourceContext(string name)
    {
        var error = Assert.Throws<AttachmentSetDataException>(() => ParseCase(name));
        AttachmentSetJsonRefusalAssertions.V1(name, error);
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
        var error = Assert.Throws<AttachmentSetDataException>(() => Parse(json));
        Assert.Equal(AttachmentSetRefusal.MeshKeyInvalid, error.Reason);
        Assert.Equal("fixture.json", error.SourceName);
        Assert.Equal(1u, error.PartId);
        Assert.Equal(key, error.MeshKey);
        Assert.Equal("parts", error.ParamName);
        Assert.IsType<AttachmentSetArgumentException>(error.InnerException);
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
            "unsupported-version" => V2(skinned).Replace("\"formatVersion\":2", "\"formatVersion\":5", StringComparison.Ordinal),
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
            "singular" => AttachmentFixtures.SetJson(part.Replace(AttachmentFixtures.Identity, "[0,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1]", StringComparison.Ordinal)),
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
            "version-string" => AttachmentFixtures.SetJson(part).Replace("Version\":1", "Version\":\"1\"", StringComparison.Ordinal),
            "version-overflow" => AttachmentFixtures.SetJson(part).Replace("Version\":1", "Version\":2147483648", StringComparison.Ordinal),
            "id-string" => AttachmentFixtures.SetJson(part.Replace("\"id\":1", "\"id\":\"1\"", StringComparison.Ordinal)),
            "id-negative" => AttachmentFixtures.SetJson(part.Replace("\"id\":1", "\"id\":-1", StringComparison.Ordinal)),
            "matrix-not-array" => AttachmentFixtures.SetJson(part.Replace(AttachmentFixtures.Identity, "null", StringComparison.Ordinal)),
            "matrix-string-component" => AttachmentFixtures.SetJson(part.Replace(AttachmentFixtures.Identity, "[1,\"0\",0,0,0,1,0,0,0,0,1,0,0,0,0,1]", StringComparison.Ordinal)),
            "empty-joint" => AttachmentFixtures.SetJson(part.Replace("\"hip\"", "\" \"", StringComparison.Ordinal)),
            "wrong-joint" => AttachmentFixtures.SetJson(part.Replace("\"hip\"", "null", StringComparison.Ordinal)),
            _ => throw new ArgumentException(name),
        };
        return AttachmentSetJson.Parse(Encoding.UTF8.GetBytes(json), name + ".json");
    }
}
