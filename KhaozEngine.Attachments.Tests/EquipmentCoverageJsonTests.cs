using System;
using System.Collections.Generic;
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
    public void Format_one_roundtrips_canonically()
    {
        byte[] canonical = Encoding.UTF8.GetBytes(Canonical);
        EquipmentCoverageDefinition definition = EquipmentCoverageJson.Read(canonical, "equipment");
        Assert.Equal(canonical, EquipmentCoverageJson.Write(definition));
        Assert.Equal("15610482183d9f41ac6e14a9890f02afb0e49d16defd84b0e8e3ddbd97e8a799", EquipmentCoverageJson.SemanticSha256(definition));
        byte[] escaped = Encoding.UTF8.GetBytes(Canonical.Replace("\"commands\"", "\"\\u0063ommands\"", StringComparison.Ordinal)
            .Replace("\"coverageLocal\"", "\"\\u0063overageLocal\"", StringComparison.Ordinal));
        Assert.Equal(canonical, EquipmentCoverageJson.Write(EquipmentCoverageJson.Read(escaped, "equipment-escaped")));
        Assert.Equal(canonical, EquipmentCoverageJson.Write(EquipmentCoverageJson.Read(Encoding.UTF8.GetBytes(Canonical + " \t\r\n"), "equipment-whitespace")));

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
    [InlineData("not-object", EquipmentCoverageRefusal.ExpectedObject, "$")]
    [InlineData("trailing-comma", EquipmentCoverageRefusal.InvalidJson, "$")]
    [InlineData("malformed-member-name", EquipmentCoverageRefusal.InvalidText, "$")]
    [InlineData("unknown-root", EquipmentCoverageRefusal.UnknownMember, "$.extra")]
    [InlineData("unknown-command", EquipmentCoverageRefusal.UnknownMember, "$.commands[1].extra")]
    [InlineData("unknown-support", EquipmentCoverageRefusal.UnknownMember, "$.commands[1].inwardSupport[0].extra")]
    [InlineData("duplicate-root", EquipmentCoverageRefusal.DuplicateMember, "$.formatVersion")]
    [InlineData("escaped-duplicate-command-member", EquipmentCoverageRefusal.DuplicateMember, "$.commands[1].id")]
    [InlineData("missing-clearance", EquipmentCoverageRefusal.MissingMember, "$.commands[1].clearance")]
    [InlineData("missing-commands", EquipmentCoverageRefusal.MissingMember, "$.commands")]
    [InlineData("version-2", EquipmentCoverageRefusal.UnsupportedVersion, "$.formatVersion")]
    [InlineData("uppercase-attachment-hash", EquipmentCoverageRefusal.MalformedDigest, "$.attachmentSha256")]
    [InlineData("short-host-map-hash", EquipmentCoverageRefusal.MalformedDigest, "$.hostMapSha256")]
    [InlineData("duplicate-command-id", EquipmentCoverageRefusal.DuplicateCommand, "$.commands[1].id")]
    [InlineData("zero-command-id", EquipmentCoverageRefusal.InvalidCommandId, "$.commands[1].id")]
    [InlineData("command-id-overflow", EquipmentCoverageRefusal.InvalidCommandId, "$.commands[1].id")]
    [InlineData("zero-part-id", EquipmentCoverageRefusal.InvalidPartId, "$.commands[1].partId")]
    [InlineData("empty-host-region", EquipmentCoverageRefusal.InvalidHostRegion, "$.commands[1].hostRegion")]
    [InlineData("malformed-host-region", EquipmentCoverageRefusal.InvalidText, "$.commands[1].hostRegion")]
    [InlineData("short-coverage-local", EquipmentCoverageRefusal.FrameComponentCapacity, "$.commands[1].coverageLocal")]
    [InlineData("long-coverage-local", EquipmentCoverageRefusal.FrameComponentCapacity, "$.commands[1].coverageLocal")]
    [InlineData("nan-coverage-local", EquipmentCoverageRefusal.InvalidJson, "$")]
    [InlineData("overflow-coverage-local", EquipmentCoverageRefusal.NonFiniteNumber, "$.commands[1].coverageLocal[12]")]
    [InlineData("projective-coverage-local", EquipmentCoverageRefusal.InvalidFrame, "$.commands[1].coverageLocal")]
    [InlineData("singular-coverage-local", EquipmentCoverageRefusal.InvalidFrame, "$.commands[1].coverageLocal")]
    [InlineData("mirrored-coverage-local", EquipmentCoverageRefusal.InvalidFrame, "$.commands[1].coverageLocal")]
    [InlineData("empty-support", EquipmentCoverageRefusal.SupportRangeCapacity, "$.commands[1].inwardSupport")]
    [InlineData("zero-support-count", EquipmentCoverageRefusal.InvalidRange, "$.commands[1].inwardSupport[0]")]
    [InlineData("negative-support-mesh", EquipmentCoverageRefusal.InvalidRange, "$.commands[1].inwardSupport[0]")]
    [InlineData("support-range-overflow", EquipmentCoverageRefusal.InvalidRange, "$.commands[1].inwardSupport[0]")]
    [InlineData("overlapping-support", EquipmentCoverageRefusal.RangeOverlap, "$.commands[0].inwardSupport")]
    [InlineData("support-over-limit", EquipmentCoverageRefusal.SupportTriangleCapacity, "$.commands[0].inwardSupport")]
    [InlineData("zero-collar", EquipmentCoverageRefusal.InvalidCollarWidth, "$.commands[1].collarWidth")]
    [InlineData("negative-collar", EquipmentCoverageRefusal.InvalidCollarWidth, "$.commands[1].collarWidth")]
    [InlineData("nan-collar", EquipmentCoverageRefusal.InvalidJson, "$")]
    [InlineData("zero-clearance", EquipmentCoverageRefusal.InvalidClearance, "$.commands[1].clearance")]
    [InlineData("negative-clearance", EquipmentCoverageRefusal.InvalidClearance, "$.commands[1].clearance")]
    [InlineData("equal-caps", EquipmentCoverageRefusal.InvalidCaps, "$.commands[1].minZ")]
    [InlineData("inverted-caps", EquipmentCoverageRefusal.InvalidCaps, "$.commands[1].minZ")]
    [InlineData("infinite-cap", EquipmentCoverageRefusal.NonFiniteNumber, "$.commands[1].maxZ")]
    [InlineData("negative-recession", EquipmentCoverageRefusal.InvalidRecession, "$.commands[1].maxWorldRecession")]
    [InlineData("empty-commands", EquipmentCoverageRefusal.CommandCapacity, "$.commands")]
    [InlineData("five-commands", EquipmentCoverageRefusal.CommandCapacity, "$.commands")]
    public void Malformed_sidecar_preserves_reason_and_location(string name, EquipmentCoverageRefusal expectedReason, string expectedRootedPath)
    {
        string identity = name + ".coverage.json";
        var refusal = Assert.Throws<EquipmentCoverageDataException>(() => EquipmentCoverageJson.Read(Encoding.UTF8.GetBytes(Case(name)), identity));
        Assert.Equal(expectedReason, refusal.Reason);
        Assert.Equal(identity, refusal.Identity);
        Assert.Equal(expectedRootedPath, refusal.Path);
        if (expectedReason != EquipmentCoverageRefusal.InvalidJson)
            Assert.Equal(expectedReason, Assert.IsType<EquipmentCoverageDataException>(refusal.InnerException).Reason);
        if (expectedReason is EquipmentCoverageRefusal.DuplicateMember or EquipmentCoverageRefusal.UnknownMember or EquipmentCoverageRefusal.MissingMember)
            Assert.Equal(expectedRootedPath[(expectedRootedPath.LastIndexOf('.') + 1)..], refusal.MemberName);
        Assert.NotNull(EquipmentCoverageJson.Read(Encoding.UTF8.GetBytes(Control(name)), identity));
        if (name == "duplicate-command-id") Assert.Equal(1u, refusal.CommandId);
        if (name == "zero-command-id") Assert.Equal(0u, refusal.CommandId);
        if (name == "command-id-overflow") Assert.Equal(4294967296UL, refusal.IdentifierValue);
        if (name == "zero-part-id") Assert.Equal((7u, 0u), (refusal.CommandId, refusal.PartId));
        if (name == "overlapping-support")
            Assert.Equal((1u, 2u, 0, 0, 3), (refusal.CommandId, refusal.PartId, refusal.Mesh, refusal.Primitive, refusal.FirstTriangle));
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

    [Theory]
    [InlineData("commands", 5, EquipmentCoverageRefusal.CommandCapacity, "$.commands", 4)]
    [InlineData("\\u0063ommands", 5, EquipmentCoverageRefusal.CommandCapacity, "$.commands", 4)]
    [InlineData("inwardSupport", 1025, EquipmentCoverageRefusal.SupportRangeCapacity, "$.commands[0].inwardSupport", 1024)]
    [InlineData("coverageLocal", 17, EquipmentCoverageRefusal.FrameComponentCapacity, "$.commands[0].coverageLocal", 16)]
    public void Capacity_refuses_before_reading_invalid_tail(string property, int count, EquipmentCoverageRefusal reason, string path, int limit)
    {
        bool commands = property is "commands" or "\\u0063ommands";
        string prefix = commands ? "{" : "{\"commands\":[{";
        string entry = property == "coverageLocal" ? "0" : "{}";
        string json = prefix + "\"" + property + "\":[" + string.Join(',', Enumerable.Repeat(entry, count)) + ",!";
        var refusal = Assert.Throws<EquipmentCoverageDataException>(() => EquipmentCoverageJson.Read(Encoding.UTF8.GetBytes(json), "equipment"));
        Assert.Equal(reason, refusal.Reason);
        Assert.Equal(path, refusal.Path);
        Assert.Equal(((long)count, (long)limit), (refusal.Count, refusal.Limit));
        Assert.NotNull(EquipmentCoverageJson.Read(Encoding.UTF8.GetBytes(Canonical), "equipment"));
    }

    [Theory]
    [InlineData("{\"extra\":", EquipmentCoverageRefusal.UnknownMember, "$.extra")]
    [InlineData("{\"commands\":[{\"extra\":", EquipmentCoverageRefusal.UnknownMember, "$.commands[0].extra")]
    [InlineData("{\"commands\":[{\"id\":1,\"\\u0069d\":", EquipmentCoverageRefusal.DuplicateMember, "$.commands[0].id")]
    [InlineData("{\"commands\":{", EquipmentCoverageRefusal.ExpectedArray, "$.commands")]
    [InlineData("{\"commands\":[{\"inwardSupport\":{", EquipmentCoverageRefusal.ExpectedArray, "$.commands[0].inwardSupport")]
    [InlineData("{\"commands\":[{\"coverageLocal\":{", EquipmentCoverageRefusal.ExpectedArray, "$.commands[0].coverageLocal")]
    [InlineData("{\"commands\":[{\"inwardSupport\":[{\"mesh\":[", EquipmentCoverageRefusal.InvalidInteger, "$.commands[0].inwardSupport[0].mesh")]
    [InlineData("{\"commands\":[{\"coverageLocal\":[{", EquipmentCoverageRefusal.NonFiniteNumber, "$.commands[0].coverageLocal[0]")]
    [InlineData("{\"attachmentSha256\":[", EquipmentCoverageRefusal.ExpectedString, "$.attachmentSha256")]
    public void Schema_refuses_before_reading_invalid_tail(string prefix, EquipmentCoverageRefusal reason, string path)
    {
        var refusal = Assert.Throws<EquipmentCoverageDataException>(() => EquipmentCoverageJson.Read(Encoding.UTF8.GetBytes(prefix + "!"), "sidecar"));
        Assert.Equal(reason, refusal.Reason);
        Assert.Equal(path, refusal.Path);
        Assert.Equal("sidecar", refusal.Identity);
        Assert.NotNull(EquipmentCoverageJson.Read(Encoding.UTF8.GetBytes(Canonical), "sidecar"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("true")]
    [InlineData("!")]
    public void Trailing_value_or_garbage_refuses(string tail)
    {
        EquipmentCoverageDataException error = Assert.Throws<EquipmentCoverageDataException>(() => EquipmentCoverageJson.Read(Encoding.UTF8.GetBytes(Canonical + tail), "equipment"));
        Assert.Equal(EquipmentCoverageRefusal.InvalidJson, error.Reason);
        Assert.Equal("$", error.Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Constructor_rejects_known_over_limit_counts_before_copy(bool support)
    {
        var refusal = support
            ? Assert.Throws<EquipmentCoverageDataException>(() => new EquipmentCoverageCommand(1, 1, "chest", Matrix4x4.Identity,
                new RefusingCollection<EquipmentSupportRange>(1025), 0.05f, -1f, 1f, 0.01f, 0.1f))
            : Assert.Throws<EquipmentCoverageDataException>(() => new EquipmentCoverageDefinition(AttachmentHash, HostMapHash,
                new RefusingCollection<EquipmentCoverageCommand>(5)));
        Assert.Equal(support ? EquipmentCoverageRefusal.SupportRangeCapacity : EquipmentCoverageRefusal.CommandCapacity, refusal.Reason);
        Assert.Equal(support ? (1025L, 1024L) : (5L, 4L), (refusal.Count, refusal.Limit));
    }

    [Fact]
    public void Constructor_stops_unknown_command_input_at_refusal_witness()
    {
        var command = new EquipmentCoverageCommand(1, 1, "chest", Matrix4x4.Identity, [new(0, 0, 0, 1)], 0.05f, -1f, 1f, 0.01f, 0.1f);
        int consumed = 0;
        IEnumerable<EquipmentCoverageCommand> CommandsWithWitness()
        {
            for (int index = 0; index < 5; index++)
            {
                consumed++;
                yield return command;
            }
            throw new InvalidOperationException("Read beyond the capacity refusal witness.");
        }

        var refusal = Assert.Throws<EquipmentCoverageDataException>(() => new EquipmentCoverageDefinition(AttachmentHash, HostMapHash, CommandsWithWitness()));
        Assert.Equal(EquipmentCoverageRefusal.CommandCapacity, refusal.Reason);
        Assert.Equal((5L, 4L), (refusal.Count, refusal.Limit));
        Assert.Equal(5, consumed);
    }

    sealed class RefusingCollection<T>(int count) : ICollection<T>
    {
        public int Count => count;
        public bool IsReadOnly => true;
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Enumerated a known over-limit collection.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public void CopyTo(T[] array, int arrayIndex) => throw new InvalidOperationException("Copied a known over-limit collection.");
        public void Add(T item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Contains(T item) => throw new NotSupportedException();
        public bool Remove(T item) => throw new NotSupportedException();
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
        Assert.Throws<EquipmentCoverageDataException>(() => new EquipmentCoverageCommand(1, 1, "chest", Matrix4x4.CreateScale(-1f, 1f, 1f), support, 0.05f, -1f, 1f, 0.01f, 0.1f));
        Assert.Throws<EquipmentCoverageDataException>(() => new EquipmentCoverageCommand(1, 1, "chest", Matrix4x4.Identity, support, 0.05f, -1f, 1f, float.NaN, 0.1f));
        Assert.Throws<EquipmentCoverageDataException>(() => new EquipmentCoverageCommand(1, 1, "chest\uD800", Matrix4x4.Identity, support, 0.05f, -1f, 1f, 0.01f, 0.1f));
        Assert.Throws<EquipmentCoverageDataException>(() => new EquipmentCoverageDefinition(AttachmentHash, HostMapHash, [command, command]));
        Assert.Throws<EquipmentCoverageDataException>(() => new EquipmentCoverageDefinition("ABC", HostMapHash, [command]));
        Assert.Throws<ArgumentNullException>(() => new EquipmentCoverageDefinition(AttachmentHash, HostMapHash, null!));
    }

    [Theory]
    [InlineData("commands", EquipmentCoverageRefusal.CommandCapacity, 4)]
    [InlineData("support-ranges", EquipmentCoverageRefusal.SupportRangeCapacity, 1024)]
    [InlineData("support-triangles", EquipmentCoverageRefusal.SupportTriangleCapacity, 1024)]
    public void Every_capacity_reports_its_witness_and_accepts_its_inclusive_limit(string kind, EquipmentCoverageRefusal reason, int limit)
    {
        string Fixture(int count) => kind switch
        {
            "commands" => Commands(count, Chest),
            "support-ranges" => Commands(1, Chest.Replace(Support(Chest), "[" + string.Join(',', Enumerable.Range(0, count)
                .Select(index => $$$"""{"mesh":0,"primitive":0,"firstTriangle":{{{index}}},"count":1}""")) + "]", StringComparison.Ordinal)),
            _ => Commands(1, Chest.Replace(Support(Chest), $$$"""[{"mesh":0,"primitive":0,"firstTriangle":0,"count":{{{count}}}}]""", StringComparison.Ordinal)),
        };
        var refusal = Assert.Throws<EquipmentCoverageDataException>(() => EquipmentCoverageJson.Read(Encoding.UTF8.GetBytes(Fixture(limit + 1)), "bounded"));
        Assert.Equal(reason, refusal.Reason);
        Assert.Equal(((long)limit + 1, (long)limit), (refusal.Count, refusal.Limit));
        Assert.Equal("bounded", refusal.Identity);
        Assert.NotNull(EquipmentCoverageJson.Read(Encoding.UTF8.GetBytes(Fixture(limit)), "bounded"));
    }

    [Theory]
    [InlineData(11, true, false, false, false)]
    [InlineData(64, false, false, false, false)]
    [InlineData(64, true, false, false, false)]
    [InlineData(65, false, false, true, false)]
    [InlineData(65, true, false, true, false)]
    [InlineData(64, true, true, false, false)]
    [InlineData(65, true, true, true, false)]
    [InlineData(64, false, false, false, true)]
    [InlineData(64, true, false, false, true)]
    [InlineData(65, false, true, true, true)]
    [InlineData(4096, true, true, true, false)]
    public void Unknown_member_diagnostics_use_a_decoded_budget_and_truthful_locations(int length, bool escaped, bool nested, bool omitted, bool nonAscii)
    {
        char character = nonAscii ? '\u00e9' : 'x';
        string name = new(character, length);
        string encoded = escaped ? string.Concat(Enumerable.Repeat(nonAscii ? "\\u00e9" : "\\u0078", length)) : name;
        string anchor = "{\"id\":1,";
        string json = nested ? Canonical.Replace(anchor, "{\"" + encoded + "\":0," + anchor[1..], StringComparison.Ordinal)
            : "{\"" + encoded + "\":0," + Canonical[1..];
        string container = nested ? "$.commands[0]" : "$";
        string component = nonAscii ? "[\"" + string.Concat(Enumerable.Repeat("\\u00E9", length)) + "\"]" : "." + name;
        var refusal = Assert.Throws<EquipmentCoverageDataException>(() => EquipmentCoverageJson.Read(Encoding.UTF8.GetBytes(json), "unknown-name"));
        Assert.Equal(EquipmentCoverageRefusal.UnknownMember, refusal.Reason);
        Assert.Equal("unknown-name", refusal.Identity);
        Assert.Equal(omitted ? null : name, refusal.MemberName);
        Assert.Equal(omitted, refusal.MemberNameOmitted);
        Assert.Equal(omitted ? container : container + component, refusal.Path);
        Assert.NotNull(EquipmentCoverageJson.Read(Encoding.UTF8.GetBytes(Canonical), "unknown-name"));
    }

    [Fact]
    public void Context_wrapper_preserves_every_identifier_and_typed_inner_reason()
    {
        var original = new EquipmentCoverageDataException(EquipmentCoverageRefusal.InvalidRange, "data")
        {
            Identity = "source",
            Path = "$.inwardSupport[2]",
            Count = 1025,
            Limit = 1024,
            MemberName = "count",
            MemberNameOmitted = true,
            FormatVersion = 3,
            CommandId = 7,
            PartId = 2,
            RegionKey = "chest",
            Mesh = 4,
            Primitive = 5,
            FirstTriangle = 6,
            IdentifierValue = 7,
        };
        var refusal = original.WithContext(identity: "outer", prefix: "$.commands[1]");
        Assert.Same(original, refusal.InnerException);
        Assert.Equal(original.Reason, refusal.Reason);
        Assert.Equal("outer", refusal.Identity);
        Assert.Equal("$.commands[1].inwardSupport[2]", refusal.Path);
        // This inventory fails by name when a new public identifier lacks a copy assertion.
        Assert.Equal(new[] { "CommandId", "Count", "FirstTriangle", "FormatVersion", "IdentifierValue", "Identity", "Limit", "MemberName", "MemberNameOmitted", "Mesh", "PartId", "Path", "Primitive", "Reason", "RegionKey" },
            typeof(EquipmentCoverageDataException).GetProperties().Where(property => property.DeclaringType == typeof(EquipmentCoverageDataException))
                .Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(original.MemberNameOmitted, refusal.MemberNameOmitted);
        Assert.Equal((original.Count, original.Limit, original.MemberName, original.FormatVersion), (refusal.Count, refusal.Limit, refusal.MemberName, refusal.FormatVersion));
        Assert.Equal((original.CommandId, original.PartId, original.RegionKey, original.IdentifierValue), (refusal.CommandId, refusal.PartId, refusal.RegionKey, refusal.IdentifierValue));
        Assert.Equal((original.Mesh, original.Primitive, original.FirstTriangle), (refusal.Mesh, refusal.Primitive, refusal.FirstTriangle));
    }

    static string Control(string name) => name switch
    {
        "five-commands" => Commands(4, Chest),
        "support-over-limit" => Case(name).Replace("\"count\":1021", "\"count\":1020", StringComparison.Ordinal),
        _ => Canonical,
    };

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
        "malformed-member-name" => "{\"\\uD800\":0," + Canonical[1..],
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
