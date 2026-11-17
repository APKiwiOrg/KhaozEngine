using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.Tools.Coverage;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

public sealed class CoverageArtifactSetTests
{
    [Fact]
    public void Rehashed_parts_still_require_logical_eof_and_matching_totals()
    {
        using var fixture = new CoverageArtifactFixture();
        using var stage = fixture.Stage();
        CoverageArtifactFixture.Publish(stage);
        fixture.ReplacePayload([.. fixture.Evidence.Write(), .. " true"u8.ToArray()]);
        var malformed = Assert.Throws<CoverageBuildDataException>(() => CoverageArtifactReader.Open(fixture.Root, fixture.Folder));
        Assert.Equal(CoverageBuildInputRefusal.InvalidJson, malformed.Reason);
        fixture.ReplacePayload(fixture.Evidence.Write());
        using (var control = CoverageArtifactReader.Open(fixture.Root, fixture.Folder)) Assert.NotEmpty(control.RootSha256);
        var root = JsonNode.Parse(File.ReadAllText(fixture.Root))!;
        root["totals"]!["pairResults"] = fixture.Evidence.PairResults.Count + 1;
        File.WriteAllText(fixture.Root, root.ToJsonString());
        var mismatch = Assert.Throws<CoverageBuildDataException>(() => CoverageArtifactReader.Open(fixture.Root, fixture.Folder));
        Assert.Equal(CoverageBuildInputRefusal.ArtifactSetInvalid, mismatch.Reason);
    }

    [Theory]
    [InlineData("descriptor-length", CoverageBuildInputRefusal.ArtifactPartLengthMismatch)]
    [InlineData("descriptor-hash", CoverageBuildInputRefusal.ArtifactPartHashMismatch)]
    public void Part_descriptors_authenticate_the_bytes_they_name(string defect, CoverageBuildInputRefusal reason)
    {
        using var fixture = new CoverageArtifactFixture();
        using var stage = fixture.Stage();
        CoverageArtifactFixture.Publish(stage);
        using (var control = CoverageArtifactReader.Open(fixture.Root, fixture.Folder)) Assert.NotEmpty(control.RootSha256);
        var root = JsonNode.Parse(File.ReadAllText(fixture.Root))!;
        var descriptor = root["parts"]!.AsArray()[^1]!;
        if (defect == "descriptor-length") descriptor["byteLength"] = (long)descriptor["byteLength"]! + 1;
        else
        {
            string old = (string)descriptor["path"]!, oldHash = (string)descriptor["sha256"]!;
            string hash = new('f', 64), path = old.Replace(oldHash, hash, StringComparison.Ordinal);
            File.Copy(Path.Combine(fixture.Folder, old), Path.Combine(fixture.Folder, path));
            descriptor["sha256"] = hash; descriptor["path"] = path;
        }
        File.WriteAllText(fixture.Root, root.ToJsonString());
        var error = Assert.Throws<CoverageBuildDataException>(() => CoverageArtifactReader.Open(fixture.Root, fixture.Folder));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(stage.Parts.Count - 1, error.ChunkIndex);
    }
    [Fact]
    public void Artifact_index_capacity_is_inclusive_and_never_omits_descriptors()
    {
        using var fixture = new CoverageArtifactFixture();
        File.WriteAllText(fixture.Root, "previous-root");
        using var reference = fixture.Stage(8192, 1024);
        int length = checked((int)new FileInfo(reference.RootStagePath).Length);
        Assert.NotEmpty(reference.Parts);
        using var exact = fixture.Stage(length, 1024);
        Assert.Equal(length, new FileInfo(exact.RootStagePath).Length);
        Assert.Equal(reference.Parts.Count, exact.Parts.Count);
        var error = Assert.Throws<CoverageBuildDataException>(() => fixture.Stage(length - 1, 1024));
        Assert.Equal(CoverageBuildInputRefusal.CapacityExceeded, error.Reason);
        Assert.Equal(length - 1, error.Limit);
        Assert.Equal("previous-root", File.ReadAllText(fixture.Root));
        long maximum = CoverageArtifactSet.MaximumPayloadBytes(67_108_864, 4096);
        long denseWords = 65_536L * (16 + 16 * 1024 + 4 * 33);
        long fixedPairs = 2L * 4_194_304 * 512;
        Assert.True(denseWords * 32 + fixedPairs + 2L * 1024 * 1024 * 1024 < 64L * 1024 * 1024 * 1024);
        Assert.Equal(3L * 1024 * 1024 * 1024 * 1024 + 64L * 1024 * 1024 * 1024, maximum);
        Assert.True((maximum + 67_108_863) / 67_108_864 <= 65_536);
        Assert.True(CoverageArtifactSet.DescriptorCapacityBound("evidence.json", maximum, 67_108_864) + 4096 < 67_108_864);
    }

    [Fact]
    public void Linked_generated_parts_are_refused_and_checked_snapshots_survive_source_changes()
    {
        using var fixture = new CoverageArtifactFixture();
        using var stage = fixture.Stage(8192, 1024);
        CoverageArtifactFixture.Publish(stage);
        using var checkedRead = CoverageArtifactReader.Open(fixture.Root, fixture.Folder);
        var part = stage.Parts[^1];
        string original = part.DestinationPath + ".original";
        File.Move(part.DestinationPath, original);
        File.CreateSymbolicLink(part.DestinationPath, original);
        try
        {
            var error = Assert.Throws<CoverageBuildDataException>(() => CoverageArtifactReader.Open(fixture.Root, fixture.Folder));
            Assert.Equal(CoverageBuildInputRefusal.ArtifactSetInvalid, error.Reason);
            Assert.Equal(stage.Parts.Count - 1, error.ChunkIndex);
            File.WriteAllBytes(original, new byte[] { 0 });
            using var payload = checkedRead.OpenPayload();
            Assert.Equal(fixture.Evidence.Write(), CoverageArtifactFixture.Bytes(payload));
        }
        finally { File.Delete(part.DestinationPath); }
    }
    [Theory]
    [InlineData("number")]
    [InlineData("escape")]
    [InlineData("utf8")]
    public void Token_boundaries_preserve_numeric_escaped_and_utf8_bytes(string token)
    {
        using var fixture = new CoverageArtifactFixture();
        var root = JsonNode.Parse(fixture.Evidence.Write())!;
        root["domains"]![0]!["denseRows"]![0]!["sample"]!["actorModel"]![0] = 1.0000001f;
        root["domains"]![0]!["denseRows"]![0]!["sample"]!["id"] = "é\n";
        root["replay"]!["poses"]![0]!["sampleId"] = "é\n";
        byte[] bytes = Encoding.UTF8.GetBytes(root.ToJsonString(new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        byte[] needle = Encoding.UTF8.GetBytes(token == "number" ? "1.0000001" : token == "escape" ? "\\n" : "é");
        int boundary = bytes.AsSpan().IndexOf(needle) + 1;
        Assert.True(boundary > 1);
        using var stage = CoverageArtifactWriter.Stage(fixture.Root, fixture.Evidence.CreateHeader(), s => s.Write(bytes), fixture.Folder,
            Math.Max(8192, boundary), boundary);
        Assert.True(stage.Parts.Count > 1);
        CoverageArtifactFixture.Publish(stage);
        using var read = CoverageArtifactReader.Open(fixture.Root, fixture.Folder);
        using var payload = read.OpenPayload();
        Assert.Equal(bytes, CoverageArtifactFixture.Bytes(payload));
    }

    [Fact]
    public void An_individual_record_may_cross_part_boundaries()
    {
        using var fixture = new CoverageArtifactFixture();
        using var bytes = new MemoryStream();
        CoverageArtifactFixture.WriteEvidence(bytes, 2, 1, 70_000);
        bytes.Position = 0;
        var header = CoverageLogicalDocumentReader.Validate(bytes, CoverageArtifactKind.Evidence).Header;
        using var stage = CoverageArtifactWriter.Stage(fixture.Root, header, s => s.Write(bytes.ToArray()), fixture.Folder, 131_072, 65_536);
        Assert.True(stage.Parts.Count > 1);
        CoverageArtifactFixture.Publish(stage);
        using var read = CoverageArtifactReader.Open(fixture.Root, fixture.Folder);
        using var payload = read.OpenPayload();
        var recovered = JsonNode.Parse(payload)!;
        Assert.Equal(2, recovered["samples"]!.AsArray().Count);
        Assert.True(((string)recovered["samples"]![0]!["id"]!).Length > 65_536);
        Assert.Equal(bytes.ToArray(), File.ReadAllBytes(stage.Parts[0].StagePath).Concat(stage.Parts.Skip(1).SelectMany(p => File.ReadAllBytes(p.StagePath))).ToArray());
    }
    [Theory]
    [InlineData("missing", CoverageBuildInputRefusal.ArtifactPartMissing)]
    [InlineData("length", CoverageBuildInputRefusal.ArtifactPartLengthMismatch)]
    [InlineData("hash", CoverageBuildInputRefusal.ArtifactPartHashMismatch)]
    public void Every_artifact_read_refuses_missing_truncated_or_substituted_parts(string defect, CoverageBuildInputRefusal reason)
    {
        using var fixture = new CoverageArtifactFixture();
        using var staged = fixture.Stage();
        CoverageArtifactFixture.Publish(staged);
        using (var control = CoverageArtifactReader.Open(fixture.Root, fixture.Folder)) Assert.NotEmpty(control.RootSha256);
        var part = staged.Parts[^1];
        byte[] bytes = File.ReadAllBytes(part.DestinationPath);
        if (defect == "missing") File.Delete(part.DestinationPath);
        else if (defect == "length") File.WriteAllBytes(part.DestinationPath, bytes[..^1]);
        else { bytes[^1] ^= 1; File.WriteAllBytes(part.DestinationPath, bytes); }
        var error = Assert.Throws<CoverageBuildDataException>(() => CoverageArtifactReader.Open(fixture.Root, fixture.Folder));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(staged.Parts.Count - 1, error.ChunkIndex);
        Assert.Equal("$.parts[" + (staged.Parts.Count - 1) + "].path", error.Path);
    }

    [Theory]
    [InlineData("ordinal", CoverageBuildInputRefusal.ArtifactSetInvalid, "$.parts[1].ordinal")]
    [InlineData("duplicate", CoverageBuildInputRefusal.ArtifactSetInvalid, "$.parts[1].ordinal")]
    [InlineData("aggregate", CoverageBuildInputRefusal.ArtifactSetInvalid, "$.payloadSha256")]
    [InlineData("payload-length", CoverageBuildInputRefusal.ArtifactSetInvalid, "$.payloadByteLength")]
    [InlineData("escape", CoverageBuildInputRefusal.ArtifactSetInvalid, "$.parts[0]")]
    [InlineData("unknown", CoverageBuildInputRefusal.UnknownMember, null)]
    public void Artifact_envelopes_require_exact_descriptor_and_payload_closure(string defect, CoverageBuildInputRefusal reason, string? path)
    {
        using var fixture = new CoverageArtifactFixture();
        using var staged = fixture.Stage();
        CoverageArtifactFixture.Publish(staged);
        using (var control = CoverageArtifactReader.Open(fixture.Root, fixture.Folder)) Assert.NotEmpty(control.RootSha256);
        var root = JsonNode.Parse(File.ReadAllText(fixture.Root))!;
        if (defect == "ordinal") root["parts"]![1]!["ordinal"] = 9;
        if (defect == "duplicate") root["parts"]![1] = root["parts"]![0]!.DeepClone();
        if (defect == "aggregate")
        {
            // Every part keeps a correct digest under the renamed directory, so only the aggregate digest is wrong.
            string actual = (string)root["payloadSha256"]!, claimed = new('f', 64);
            root["payloadSha256"] = claimed;
            foreach (var descriptor in root["parts"]!.AsArray())
            {
                string old = (string)descriptor!["path"]!, renamed = old.Replace(actual, claimed, StringComparison.Ordinal);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(fixture.Folder, renamed))!);
                File.Copy(Path.Combine(fixture.Folder, old), Path.Combine(fixture.Folder, renamed));
                descriptor["path"] = renamed;
            }
        }
        if (defect == "payload-length") root["payloadByteLength"] = (long)root["payloadByteLength"]! + 1;
        if (defect == "escape") root["parts"]![0]!["path"] = "../outside.bin";
        if (defect == "unknown") root["extra"] = true;
        File.WriteAllText(fixture.Root, root.ToJsonString());
        var error = Assert.Throws<CoverageBuildDataException>(() => CoverageArtifactReader.Open(fixture.Root, fixture.Folder));
        Assert.Equal(reason, error.Reason);
        if (path != null) Assert.Equal(path, error.Path);
    }
    [Fact]
    public void Small_evidence_keeps_the_complete_legacy_logical_bytes()
    {
        using var fixture = new CoverageArtifactFixture();
        byte[] expected = fixture.Evidence.Write();
        using var stream = new MemoryStream();
        fixture.Evidence.WriteTo(stream);
        Assert.Equal(expected, stream.ToArray());
        using var staged = fixture.Stage(expected.Length, expected.Length);
        Assert.Empty(staged.Parts);
        Assert.Equal(expected, File.ReadAllBytes(staged.RootStagePath));
        CoverageArtifactFixture.Publish(staged);
        using var read = CoverageArtifactReader.Open(fixture.Root, fixture.Folder);
        using var payload = read.OpenPayload();
        Assert.Equal(expected, CoverageArtifactFixture.Bytes(payload));
        Assert.Equal(CoverageBuildInputJson.Hash(expected), read.RootSha256);
    }

    [Fact]
    public void Evidence_spans_parts_without_losing_any_record_or_word()
    {
        using var fixture = new CoverageArtifactFixture();
        using var logical = new MemoryStream();
        CoverageArtifactFixture.WriteEvidence(logical, 4, 2, 32);
        logical.Position = 0;
        var header = CoverageLogicalDocumentReader.Validate(logical, CoverageArtifactKind.Evidence).Header;
        byte[] expected = logical.ToArray();
        using var staged = CoverageArtifactWriter.Stage(fixture.Root, header, s => s.Write(expected), fixture.Folder, 4096, 1024);
        Assert.True(staged.Parts.Count > 1);
        Assert.All(staged.Parts.Take(staged.Parts.Count - 1), part => Assert.Equal(1024, part.ByteLength));
        Assert.All(staged.Parts, part => Assert.Equal(part.Sha256, CoverageBuildInputJson.Hash(File.ReadAllBytes(part.StagePath))));
        CoverageArtifactFixture.Publish(staged);
        using var read = CoverageArtifactReader.Open(fixture.Root, fixture.Folder);
        using var payload = read.OpenPayload();
        byte[] actual = CoverageArtifactFixture.Bytes(payload);
        Assert.Equal(expected, actual);
        var records = JsonNode.Parse(actual)!;
        Assert.Equal(4, records["samples"]!.AsArray().Count);
        Assert.Equal(8, records["pairResults"]!.AsArray().Count);
        var pairs = records["pairResults"]!.AsArray().Select(p => ((int)p!["entryIndex"]!, (string)p["sampleId"]!)).ToHashSet();
        var sampleIds = records["samples"]!.AsArray().Select(s => (string)s!["id"]!).ToArray();
        Assert.Equal(sampleIds.SelectMany(id => new[] { (0, id), (1, id) }).ToHashSet(), pairs);
        using (var typed = read.OpenPayload())
        using (var source = new MemoryStream(expected)) CoverageLogicalDocumentReader.Compare(typed, source, CoverageArtifactKind.Evidence);
        var root = JsonNode.Parse(File.ReadAllText(fixture.Root))!;
        Assert.Equal("khaozengine.coverage-evidence-set/1", (string?)root["schema"]);
        Assert.Equal(expected.Length, (long)root["payloadByteLength"]!);
        Assert.Equal(CoverageBuildInputJson.Hash(expected), (string?)root["payloadSha256"]);
        Assert.Equal(Enumerable.Range(0, staged.Parts.Count), root["parts"]!.AsArray().Select(p => (int)p!["ordinal"]!));
        Assert.Equal(staged.Parts.Select(p => p.Sha256), root["parts"]!.AsArray().Select(p => (string)p!["sha256"]!));
        using var single = CoverageArtifactWriter.Stage(Path.Combine(fixture.Folder, "single.json"), header, s => s.Write(expected),
            fixture.Folder, expected.Length, 1024);
        Assert.Empty(single.Parts);
        Assert.Equal(expected, File.ReadAllBytes(single.RootStagePath));
    }

    [Theory]
    [InlineData("byte")]
    [InlineData("array")]
    [InlineData("async")]
    public void Payload_capacity_covers_every_stream_write_path(string path)
    {
        using var fixture = new CoverageArtifactFixture();
        byte[] logical = fixture.Evidence.Write();
        // With part limit P and artifact limit 65,536*P the descriptor cap and the artifact limit coincide,
        // so a payload of exactly that length stays one legacy file and one more byte must refuse.
        int part = (logical.Length + CoverageArtifactSet.MaxArtifactParts - 1) / CoverageArtifactSet.MaxArtifactParts;
        int bound = CoverageArtifactSet.MaxArtifactParts * part;
        byte[] over = new byte[bound + 1], exact = new byte[bound];
        over.AsSpan().Fill((byte)' ');
        exact.AsSpan().Fill((byte)' ');
        logical.CopyTo(exact, 0);
        var error = Assert.Throws<CoverageBuildDataException>(() => CoverageArtifactWriter.Stage(fixture.Root, fixture.Evidence.CreateHeader(),
            s => Write(s, over, path), fixture.Folder, bound, part));
        Assert.Equal(CoverageBuildInputRefusal.CapacityExceeded, error.Reason);
        Assert.Equal(bound, error.Limit);
        Assert.False(File.Exists(fixture.Root));
        using var control = CoverageArtifactWriter.Stage(fixture.Root, fixture.Evidence.CreateHeader(),
            s => Write(s, exact, path), fixture.Folder, bound, part);
        Assert.Empty(control.Parts);
        Assert.Equal(exact, File.ReadAllBytes(control.RootStagePath));
    }

    static void Write(Stream stream, byte[] bytes, string path)
    {
        if (path == "byte") foreach (byte value in bytes) stream.WriteByte(value);
        else if (path == "array") stream.Write(bytes, 0, bytes.Length);
        else stream.WriteAsync(bytes.AsMemory()).AsTask().GetAwaiter().GetResult();
    }
}
