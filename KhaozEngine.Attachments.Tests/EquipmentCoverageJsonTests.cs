using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

/// <summary>Strict format 1 equipment coverage companion: canonical bytes, domain-separated semantic identity,
/// refusals and immutable snapshots. All data is synthetic.</summary>
public class EquipmentCoverageJsonTests
{
    const string AttachmentHash = "1111111111111111111111111111111111111111111111111111111111111111";
    const string HostMapHash = "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd";
    const string Chest =
        "{\"id\":1,\"partId\":2,\"hostRegion\":\"chest\",\"coverageLocal\":[1,0,0,0,0,1,0,0,0,0,1,0,0,0.25,0.5,1]," +
        "\"inwardSupport\":[{\"mesh\":0,\"primitive\":0,\"firstTriangle\":0,\"count\":4},{\"mesh\":0,\"primitive\":1,\"firstTriangle\":0,\"count\":2}]," +
        "\"collarWidth\":0.05,\"minZ\":-0.2,\"maxZ\":0.3,\"clearance\":0.004,\"maxWorldRecession\":0.03}";
    const string Back =
        "{\"id\":7,\"partId\":2,\"hostRegion\":\"back\",\"coverageLocal\":[0,1,0,0,-1,0,0,0,0,0,1,0,0,0,0,1]," +
        "\"inwardSupport\":[{\"mesh\":1,\"primitive\":0,\"firstTriangle\":3,\"count\":1}]," +
        "\"collarWidth\":0.1,\"minZ\":-1,\"maxZ\":1,\"clearance\":0.5,\"maxWorldRecession\":0}";
    const string Canonical = "{\"formatVersion\":1,\"attachmentSha256\":\"" + AttachmentHash + "\",\"hostMapSha256\":\"" + HostMapHash +
        "\",\"commands\":[" + Chest + "," + Back + "]}";

    // Same semantics: different member order, unsorted commands and support ranges, negative zeros and whitespace.
    const string Reordered =
        "\n{\"commands\":[{\"maxWorldRecession\":-0,\"clearance\":5e-1,\"maxZ\":1.0,\"minZ\":-1,\"collarWidth\":0.1," +
        "\"inwardSupport\":[{\"count\":1,\"firstTriangle\":3,\"primitive\":0,\"mesh\":1}]," +
        "\"coverageLocal\":[-0,1,0,0,-1,-0,0,0,0,0,1,0,0,0,0,1],\"hostRegion\":\"back\",\"partId\":2,\"id\":7}, " +
        "{\"id\":1,\"partId\":2,\"hostRegion\":\"chest\",\"coverageLocal\":[1,0,0,0,0,1,0,0,0,0,1,0,-0,0.25,0.5,1]," +
        "\"inwardSupport\":[{\"mesh\":0,\"primitive\":1,\"firstTriangle\":0,\"count\":2},{\"mesh\":0,\"primitive\":0,\"firstTriangle\":0,\"count\":4}]," +
        "\"collarWidth\":0.05,\"minZ\":-0.2,\"maxZ\":0.3,\"clearance\":0.004,\"maxWorldRecession\":0.03}]," +
        "\"hostMapSha256\":\"" + HostMapHash + "\",\"attachmentSha256\":\"" + AttachmentHash + "\",\"formatVersion\":1} ";

    [Fact]
    public void Canonical_round_trip_has_fixed_bytes()
    {
        byte[] canonical = Encoding.UTF8.GetBytes(Canonical);
        EquipmentCoverageDefinition definition = EquipmentCoverageJson.Read(canonical, "equipment");
        Assert.Equal(canonical, EquipmentCoverageJson.Write(definition));

        EquipmentCoverageDefinition reordered = EquipmentCoverageJson.Read(Encoding.UTF8.GetBytes(Reordered), "equipment-reordered");
        Assert.Equal(Canonical, Encoding.UTF8.GetString(EquipmentCoverageJson.Write(reordered)));

        Assert.Equal(AttachmentHash, definition.AttachmentSha256);
        Assert.Equal(HostMapHash, definition.HostMapSha256);
        Assert.Equal(new uint[] { 1, 7 }, definition.Commands.Select(command => command.Id));
        EquipmentCoverageCommand chest = definition.Commands[0];
        Assert.Equal((2u, "chest"), (chest.PartId, chest.HostRegion));
        Assert.Equal(Matrix4x4.CreateTranslation(0f, 0.25f, 0.5f), chest.CoverageLocal);
        Assert.Equal(new[] { new EquipmentSupportRange(0, 0, 0, 4), new EquipmentSupportRange(0, 1, 0, 2) }, chest.InwardSupport);
        Assert.Equal((0.05f, -0.2f, 0.3f, 0.004f, 0.03f), (chest.CollarWidth, chest.MinZ, chest.MaxZ, chest.Clearance, chest.MaxWorldRecession));
        Assert.Equal(0f, definition.Commands[1].MaxWorldRecession);
    }

    [Fact]
    public void Semantic_digest_is_domain_separated_and_independent_of_byte_hash()
    {
        byte[] canonical = Encoding.UTF8.GetBytes(Canonical);
        byte[] reorderedBytes = Encoding.UTF8.GetBytes(Reordered);
        string semantic = EquipmentCoverageJson.SemanticSha256(EquipmentCoverageJson.Read(canonical, "equipment"));

        byte[] prefixed = [.. Encoding.UTF8.GetBytes("KhaozEngine.EquipmentCoverage/1\n"), .. canonical];
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(prefixed)), semantic);
        Assert.NotEqual(Convert.ToHexStringLower(SHA256.HashData(canonical)), semantic);
        Assert.NotEqual(Convert.ToHexStringLower(SHA256.HashData(canonical)), Convert.ToHexStringLower(SHA256.HashData(reorderedBytes)));
        Assert.Equal(semantic, EquipmentCoverageJson.SemanticSha256(EquipmentCoverageJson.Read(reorderedBytes, "equipment-reordered")));

        byte[] retargeted = Encoding.UTF8.GetBytes(Canonical.Replace("\"hostRegion\":\"back\"", "\"hostRegion\":\"belly\"", StringComparison.Ordinal));
        Assert.NotEqual(semantic, EquipmentCoverageJson.SemanticSha256(EquipmentCoverageJson.Read(retargeted, "equipment")));
    }

    [Theory]
    [InlineData("not-object", "root: expected an object")]
    [InlineData("trailing-comma", "trailing comma")]
    [InlineData("unknown-root", "root: unknown property 'extra'")]
    [InlineData("unknown-command", "commands[1]: unknown property 'extra'")]
    [InlineData("unknown-support", "inwardSupport[0]: unknown property 'extra'")]
    [InlineData("duplicate-root", "duplicate property 'formatVersion'")]
    [InlineData("escaped-duplicate-command-member", "duplicate property 'id'")]
    [InlineData("missing-clearance", "missing 'clearance'")]
    [InlineData("missing-commands", "missing 'commands'")]
    [InlineData("version-2", "unsupported formatVersion")]
    [InlineData("uppercase-attachment-hash", "attachmentSha256 must be a lowercase SHA-256")]
    [InlineData("short-host-map-hash", "hostMapSha256 must be a lowercase SHA-256")]
    [InlineData("duplicate-command-id", "IDs must be unique")]
    [InlineData("zero-command-id", "IDs must be positive")]
    [InlineData("command-id-overflow", "'id' must be a positive uint")]
    [InlineData("zero-part-id", "part IDs must be positive")]
    [InlineData("empty-host-region", "host region must be non-blank")]
    [InlineData("malformed-host-region", "UTF-16 JSON text")]
    [InlineData("short-coverage-local", "coverageLocal must contain 16")]
    [InlineData("long-coverage-local", "coverageLocal must contain 16")]
    [InlineData("nan-coverage-local", "invalid start of a value")]
    [InlineData("overflow-coverage-local", "'coverageLocal[12]' must be a finite float")]
    [InlineData("projective-coverage-local", "invertible and not mirrored")]
    [InlineData("singular-coverage-local", "invertible and not mirrored")]
    [InlineData("mirrored-coverage-local", "invertible and not mirrored")]
    [InlineData("empty-support", "at least one inward support range")]
    [InlineData("zero-support-count", "invalid support range")]
    [InlineData("negative-support-mesh", "invalid support range mesh -1")]
    [InlineData("support-range-overflow", "invalid support range")]
    [InlineData("overlapping-support", "support ranges mesh 0 primitive 0 triangles 0+4 and mesh 0 primitive 0 triangles 3+2 overlap")]
    [InlineData("support-over-limit", "more than 1024 inward support triangles")]
    [InlineData("zero-collar", "collarWidth must be finite and positive")]
    [InlineData("negative-collar", "collarWidth must be finite and positive")]
    [InlineData("nan-collar", "invalid start of a value")]
    [InlineData("zero-clearance", "clearance must be finite and positive")]
    [InlineData("negative-clearance", "clearance must be finite and positive")]
    [InlineData("equal-caps", "minZ below maxZ")]
    [InlineData("inverted-caps", "minZ below maxZ")]
    [InlineData("infinite-cap", "'maxZ' must be a finite float")]
    [InlineData("negative-recession", "maxWorldRecession must be finite and non-negative")]
    [InlineData("empty-commands", "1 to 4 commands")]
    [InlineData("five-commands", "'commands' exceeds the limit of 4")]
    public void Malformed_or_over_limit_sidecar_refuses(string name, string reason)
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => EquipmentCoverageJson.Read(Encoding.UTF8.GetBytes(Case(name)), name + ".coverage.json"));
        Assert.StartsWith(name + ".coverage.json: ", error.Message, StringComparison.Ordinal);
        Assert.True(error.Message.Contains(reason, StringComparison.Ordinal), error.Message);
    }

    [Fact]
    public void Four_commands_and_full_support_budget_are_accepted()
    {
        string support = $$"""[{"mesh":0,"primitive":0,"firstTriangle":0,"count":{{EquipmentCoverageCommand.MaxSupportTriangles - 1}}},{"mesh":0,"primitive":0,"firstTriangle":2000,"count":1}]""";
        string full = Chest.Replace(Support(Chest), support, StringComparison.Ordinal);
        EquipmentCoverageDefinition definition = EquipmentCoverageJson.Read(Encoding.UTF8.GetBytes(Commands(EquipmentCoverageDefinition.MaxCommands, full)), "equipment");
        Assert.Equal(4, definition.Commands.Count);
        Assert.Equal(1024, definition.Commands[0].InwardSupport.Sum(range => range.Count));
    }

    [Fact]
    public void Constructor_snapshots_caller_collections_and_exposes_no_mutable_state()
    {
        var support = new[] { new EquipmentSupportRange(0, 1, 0, 2), new EquipmentSupportRange(0, 0, 0, 4) };
        var command = new EquipmentCoverageCommand(3, 2, "chest", Matrix4x4.Identity, support, 0.05f, -0.2f, 0.3f, 0.004f, 0.03f);
        var commands = new List<EquipmentCoverageCommand> { command };
        var definition = new EquipmentCoverageDefinition(AttachmentHash, HostMapHash, commands);
        string semantic = EquipmentCoverageJson.SemanticSha256(definition);

        support[0] = new(9, 9, 9, 9);
        commands.Clear();

        Assert.Equal(new[] { new EquipmentSupportRange(0, 0, 0, 4), new EquipmentSupportRange(0, 1, 0, 2) }, Assert.Single(definition.Commands).InwardSupport);
        Assert.Equal(semantic, EquipmentCoverageJson.SemanticSha256(definition));
        Assert.False(definition.Commands is EquipmentCoverageCommand[] || definition.Commands is List<EquipmentCoverageCommand>);
        Assert.False(command.InwardSupport is EquipmentSupportRange[]);
        Assert.Throws<NotSupportedException>(() => ((IList<EquipmentSupportRange>)command.InwardSupport)[0] = default);
    }

    [Fact]
    public void Constructor_refuses_invalid_programmatic_data()
    {
        EquipmentSupportRange[] support = [new(0, 0, 0, 1)];
        var command = new EquipmentCoverageCommand(1, 1, "chest", Matrix4x4.Identity, support, 0.05f, -1f, 1f, 0.01f, 0.1f);
        Assert.Throws<ArgumentException>(() => new EquipmentCoverageCommand(1, 1, "chest", Matrix4x4.CreateScale(-1f, 1f, 1f), support, 0.05f, -1f, 1f, 0.01f, 0.1f));
        Assert.Throws<ArgumentException>(() => new EquipmentCoverageCommand(1, 1, "chest", Matrix4x4.Identity, support, 0.05f, -1f, 1f, float.NaN, 0.1f));
        Assert.Throws<ArgumentException>(() => new EquipmentCoverageCommand(1, 1, "chest\uD800", Matrix4x4.Identity, support, 0.05f, -1f, 1f, 0.01f, 0.1f));
        Assert.Throws<ArgumentException>(() => new EquipmentCoverageDefinition(AttachmentHash, HostMapHash, [command, command]));
        Assert.Throws<ArgumentException>(() => new EquipmentCoverageDefinition("ABC", HostMapHash, [command]));
        Assert.Throws<ArgumentNullException>(() => new EquipmentCoverageDefinition(AttachmentHash, HostMapHash, null!));
    }

    static string Swap(string from, string to) => Canonical.Replace(from, to, StringComparison.Ordinal);

    static string Support(string command)
    {
        int start = command.IndexOf("\"inwardSupport\":", StringComparison.Ordinal) + "\"inwardSupport\":".Length;
        return command[start..(command.IndexOf(']', start) + 1)];
    }

    static string Commands(int count, string template) =>
        "{\"formatVersion\":1,\"attachmentSha256\":\"" + AttachmentHash + "\",\"hostMapSha256\":\"" + HostMapHash + "\",\"commands\":[" +
        string.Join(',', Enumerable.Range(1, count).Select(id => template.Replace("\"id\":1,", $"\"id\":{id},", StringComparison.Ordinal))) + "]}";

    static string Case(string name) => name switch
    {
        "not-object" => "[]",
        "trailing-comma" => Swap("\"maxWorldRecession\":0}", "\"maxWorldRecession\":0,}"),
        "unknown-root" => Swap("\"commands\":", "\"extra\":0,\"commands\":"),
        "unknown-command" => Swap("\"id\":7,", "\"id\":7,\"extra\":0,"),
        "unknown-support" => Swap("\"firstTriangle\":3,\"count\":1", "\"firstTriangle\":3,\"count\":1,\"extra\":0"),
        "duplicate-root" => Swap("\"formatVersion\":1", "\"formatVersion\":1,\"formatVersion\":1"),
        "escaped-duplicate-command-member" => Swap("\"id\":7,", "\"id\":7,\"\\u0069d\":7,"),
        "missing-clearance" => Swap("\"clearance\":0.5,", ""),
        "missing-commands" => Swap(",\"commands\":[" + Chest + "," + Back + "]", ""),
        "version-2" => Swap("\"formatVersion\":1", "\"formatVersion\":2"),
        "uppercase-attachment-hash" => Swap(AttachmentHash, AttachmentHash.Replace('1', 'A')),
        "short-host-map-hash" => Swap(HostMapHash, HostMapHash[..62]),
        "duplicate-command-id" => Swap("\"id\":7,", "\"id\":1,"),
        "zero-command-id" => Swap("\"id\":7,", "\"id\":0,"),
        "command-id-overflow" => Swap("\"id\":7,", "\"id\":4294967296,"),
        "zero-part-id" => Swap("\"id\":7,\"partId\":2", "\"id\":7,\"partId\":0"),
        "empty-host-region" => Swap("\"hostRegion\":\"back\"", "\"hostRegion\":\"\""),
        "malformed-host-region" => Swap("\"hostRegion\":\"back\"", "\"hostRegion\":\"\\uDC00\""),
        "short-coverage-local" => Swap("[0,1,0,0,-1,0,0,0,0,0,1,0,0,0,0,1]", "[1,0,0]"),
        "long-coverage-local" => Swap("[0,1,0,0,-1,0,0,0,0,0,1,0,0,0,0,1]", "[0,1,0,0,-1,0,0,0,0,0,1,0,0,0,0,1,0]"),
        "nan-coverage-local" => Swap("[0,1,0,0,-1,0,0,0,0,0,1,0,0,0,0,1]", "[NaN,1,0,0,-1,0,0,0,0,0,1,0,0,0,0,1]"),
        "overflow-coverage-local" => Swap("[0,1,0,0,-1,0,0,0,0,0,1,0,0,0,0,1]", "[0,1,0,0,-1,0,0,0,0,0,1,0,1e100,0,0,1]"),
        "projective-coverage-local" => Swap("[0,1,0,0,-1,0,0,0,0,0,1,0,0,0,0,1]", "[0,1,0,0.5,-1,0,0,0,0,0,1,0,0,0,0,1]"),
        "singular-coverage-local" => Swap("[0,1,0,0,-1,0,0,0,0,0,1,0,0,0,0,1]", "[0,1,0,0,0,1,0,0,0,0,1,0,0,0,0,1]"),
        "mirrored-coverage-local" => Swap("[0,1,0,0,-1,0,0,0,0,0,1,0,0,0,0,1]", "[0,1,0,0,1,0,0,0,0,0,1,0,0,0,0,1]"),
        "empty-support" => Swap("[{\"mesh\":1,\"primitive\":0,\"firstTriangle\":3,\"count\":1}]", "[]"),
        "zero-support-count" => Swap("\"firstTriangle\":3,\"count\":1", "\"firstTriangle\":3,\"count\":0"),
        "negative-support-mesh" => Swap("{\"mesh\":1,\"primitive\":0", "{\"mesh\":-1,\"primitive\":0"),
        "support-range-overflow" => Swap("\"firstTriangle\":3,\"count\":1", "\"firstTriangle\":2147483647,\"count\":1"),
        "overlapping-support" => Swap("{\"mesh\":0,\"primitive\":1,\"firstTriangle\":0,\"count\":2}", "{\"mesh\":0,\"primitive\":0,\"firstTriangle\":3,\"count\":2}"),
        "support-over-limit" => Swap("{\"mesh\":0,\"primitive\":1,\"firstTriangle\":0,\"count\":2}", $"{{\"mesh\":0,\"primitive\":1,\"firstTriangle\":0,\"count\":{EquipmentCoverageCommand.MaxSupportTriangles - 3}}}"),
        "zero-collar" => Swap("\"collarWidth\":0.1", "\"collarWidth\":0"),
        "negative-collar" => Swap("\"collarWidth\":0.1", "\"collarWidth\":-0.1"),
        "nan-collar" => Swap("\"collarWidth\":0.1", "\"collarWidth\":NaN"),
        "zero-clearance" => Swap("\"clearance\":0.5", "\"clearance\":-0"),
        "negative-clearance" => Swap("\"clearance\":0.5", "\"clearance\":-0.5"),
        "equal-caps" => Swap("\"minZ\":-1,\"maxZ\":1", "\"minZ\":1,\"maxZ\":1"),
        "inverted-caps" => Swap("\"minZ\":-1,\"maxZ\":1", "\"minZ\":1,\"maxZ\":-1"),
        "infinite-cap" => Swap("\"maxZ\":1,", "\"maxZ\":1e100,"),
        "negative-recession" => Swap("\"maxWorldRecession\":0}", "\"maxWorldRecession\":-0.001}"),
        "empty-commands" => Swap("[" + Chest + "," + Back + "]", "[]"),
        "five-commands" => Commands(EquipmentCoverageDefinition.MaxCommands + 1, Chest),
        _ => throw new ArgumentException(name),
    };
}
