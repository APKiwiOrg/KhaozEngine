using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.Tools.Coverage;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

public sealed class CoverageArtifactResumeTests
{
    [Fact]
    public void Different_command_inventories_are_content_mismatches_despite_matching_provenance()
    {
        var input = CoverageBuildFixture.Input();
        var build = CoverageTableBuilder.Build(input);
        var verification = CoverageSampleVerifier.Verify(input, build.Table);
        Resume(verification, verification.Write());
        var error = Assert.Throws<CoverageBuildValidationException>(() => Resume(verification, build.Evidence.Write()));
        Assert.Equal(CoverageBuildValidationRefusal.ArtifactContentMismatch, error.Reason);
    }
    [Theory]
    [InlineData("trailing", CoverageBuildInputRefusal.InvalidJson)]
    [InlineData("nested-type", CoverageBuildInputRefusal.InvalidValue)]
    [InlineData("missing-pair", CoverageBuildInputRefusal.ArtifactSetInvalid)]
    [InlineData("duplicate-pair", CoverageBuildInputRefusal.ArtifactSetInvalid)]
    [InlineData("pose-count", CoverageBuildInputRefusal.ArtifactSetInvalid)]
    [InlineData("equipment-count", CoverageBuildInputRefusal.ArtifactSetInvalid)]
    [InlineData("missing-claim", CoverageBuildInputRefusal.ArtifactSetInvalid)]
    [InlineData("candidate-reference", CoverageBuildInputRefusal.ArtifactSetInvalid)]
    public void Logical_admission_checks_nested_types_inventory_closure_and_final_eof(string defect, CoverageBuildInputRefusal reason)
    {
        using var fixture = new CoverageArtifactFixture();
        byte[] valid = fixture.Evidence.Write();
        using (var control = new MemoryStream(valid)) CoverageLogicalDocumentReader.Validate(control, CoverageArtifactKind.Evidence);
        var root = JsonNode.Parse(valid)!;
        if (defect == "nested-type") root["domains"]![0]!["denseRows"]![0]!["looped"] = "false";
        if (defect == "missing-pair") root["pairResults"]!.AsArray().RemoveAt(0);
        if (defect == "duplicate-pair") root["pairResults"]![1] = root["pairResults"]![0]!.DeepClone();
        if (defect == "pose-count") root["replay"]!["poseCount"] = 4;
        if (defect == "equipment-count") root["replay"]!["poses"]![0]!["equipmentCount"] = 2;
        if (defect == "missing-claim") root["selectedClaims"]!.AsArray().RemoveAt(0);
        if (defect == "candidate-reference") root["candidateSampleIds"]![0] = "unknown";
        byte[] bytes = Encoding.UTF8.GetBytes(root.ToJsonString() + (defect == "trailing" ? " true" : ""));
        using var input = new MemoryStream(bytes);
        var error = Assert.Throws<CoverageBuildDataException>(() => CoverageLogicalDocumentReader.Validate(input, CoverageArtifactKind.Evidence));
        Assert.Equal(reason, error.Reason);
    }
    // The frozen synthetic documents pin the admitted legacy evidence shapes: build evidence with replay, and
    // sample verification evidence with a null replay and a blend path sample whose authored frame is null. A
    // change to either shape must replace its file under Fixtures on purpose, together with the README and a
    // version decision, because evidence written by earlier tools would then refuse.
    [Theory]
    [InlineData("legacy-evidence-v1.json")]
    [InlineData("legacy-evidence-samples-v1.json")]
    public void Frozen_legacy_evidence_keeps_its_admitted_shape(string name)
    {
        byte[] frozen = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
        using (var stream = new MemoryStream(frozen)) CoverageLogicalDocumentReader.Validate(stream, CoverageArtifactKind.Evidence);
        using var fixture = new CoverageArtifactFixture();
        File.WriteAllBytes(fixture.Root, frozen);
        using var read = CoverageArtifactReader.Open(fixture.Root, fixture.Folder);
        using var payload = read.OpenPayload();
        Assert.Equal(frozen, CoverageArtifactFixture.Bytes(payload));
    }

    [Fact]
    public void Legacy_whitespace_and_object_member_order_are_semantically_equal()
    {
        using var fixture = new CoverageArtifactFixture();
        var root = JsonNode.Parse(fixture.Evidence.Write())!.AsObject();
        var reordered = new JsonObject();
        foreach (var member in System.Linq.Enumerable.Reverse(root)) reordered[member.Key] = member.Value?.DeepClone();
        byte[] bytes = Encoding.UTF8.GetBytes(reordered.ToJsonString(new() { WriteIndented = true }));
        Resume(fixture.Evidence, bytes);
    }

    /// <summary>The CLI's resume path: the strict reader admits the existing root, then every logical record is
    /// compared with the current document.</summary>
    static void Resume(CoverageEvidenceManifest current, byte[] existing)
    {
        string folder = Path.Combine(Path.GetTempPath(), "khaozengine-coverage-packed-input", "resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string root = Path.Combine(folder, "evidence.json");
            File.WriteAllBytes(root, existing);
            using var read = CoverageArtifactReader.Open(root, folder);
            using var actual = read.OpenPayload();
            using var expected = new MemoryStream(current.Write());
            CoverageLogicalDocumentReader.Compare(actual, expected, CoverageArtifactKind.Evidence);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("truncated", CoverageBuildInputRefusal.InvalidJson)]
    [InlineData("duplicate", CoverageBuildInputRefusal.DuplicateMember)]
    [InlineData("wrong-type", CoverageBuildInputRefusal.InvalidValue)]
    [InlineData("invalid-utf8", CoverageBuildInputRefusal.InvalidJson)]
    [InlineData("invalid-surrogate", CoverageBuildInputRefusal.InvalidJson)]
    public void Malformed_legacy_resume_keeps_its_data_refusal_family(string defect, CoverageBuildInputRefusal reason)
    {
        using var fixture = new CoverageArtifactFixture();
        File.WriteAllBytes(fixture.Root, fixture.Evidence.Write());
        using (var control = CoverageArtifactReader.Open(fixture.Root, fixture.Folder)) Assert.NotEmpty(control.RootSha256);
        string json = Encoding.UTF8.GetString(fixture.Evidence.Write());
        if (defect == "truncated") json = "{";
        else if (defect == "duplicate") json = json.Insert(1, "\"formatVersion\":1,");
        else if (defect == "wrong-type") { var root = JsonNode.Parse(json)!; root["tableSha256"] = 7; json = root.ToJsonString(); }
        if (defect == "invalid-utf8")
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            int index = bytes.AsSpan().IndexOf("KhaozEngine.Coverage.Verify/1"u8);
            Assert.True(index >= 0);
            bytes[index] = 0xff;
            File.WriteAllBytes(fixture.Root, bytes);
        }
        else if (defect == "invalid-surrogate")
            File.WriteAllText(fixture.Root, json.Replace("KhaozEngine.Coverage.Verify/1", "\\uD800", StringComparison.Ordinal));
        else File.WriteAllText(fixture.Root, json);
        var error = Assert.Throws<CoverageBuildDataException>(() => CoverageArtifactReader.Open(fixture.Root, fixture.Folder));
        Assert.Equal(reason, error.Reason);
    }
    [Theory]
    [InlineData("inputSha256")]
    [InlineData("tableSha256")]
    [InlineData("toolCodeSha256")]
    [InlineData("render3DCodeSha256")]
    [InlineData("scope")]
    [InlineData("identity.hostSha256")]
    [InlineData("identity.hostMapSha256")]
    [InlineData("identity.configurationSha256")]
    [InlineData("identity.refinementSha256")]
    [InlineData("identity.arithmeticSha256")]
    [InlineData("identity.stateSchemaSha256")]
    [InlineData("pairResults")]
    [InlineData("domains")]
    [InlineData("signed-zero")]
    public void Resume_authenticates_provenance_and_every_logical_record(string member)
    {
        using var fixture = new CoverageArtifactFixture();
        byte[] expected = fixture.Evidence.Write();
        var root = JsonNode.Parse(expected)!;
        if (member == "pairResults") root[member]![0]!["valid"] = false;
        else if (member is "domains" or "signed-zero") root["domains"]![0]!["denseRows"]![0]!["sample"]!["actorModel"]![1] =
            JsonNode.Parse(member == "signed-zero" ? "-0" : "0.125");
        else if (member.StartsWith("identity.", StringComparison.Ordinal)) root["identity"]![member[9..]] = new string('f', 64);
        else root[member] = member == "scope" ? "declared-samples-only" : new string('f', 64);
        byte[] changed = Encoding.UTF8.GetBytes(root.ToJsonString());
        using var admitted = new MemoryStream(changed);
        var header = CoverageLogicalDocumentReader.Validate(admitted, CoverageArtifactKind.Evidence).Header;
        using var staged = CoverageArtifactWriter.Stage(fixture.Root, header, s => s.Write(changed), fixture.Folder, 8192, 1024);
        CoverageArtifactFixture.Publish(staged);
        using var session = CoverageArtifactReader.Open(fixture.Root, fixture.Folder);
        using var actual = session.OpenPayload();
        using var control = new MemoryStream(expected);
        var error = Assert.Throws<CoverageBuildValidationException>(() => CoverageLogicalDocumentReader.Compare(actual, control, CoverageArtifactKind.Evidence));
        Assert.Equal(member is "pairResults" or "domains" or "signed-zero"
            ? CoverageBuildValidationRefusal.ArtifactContentMismatch : CoverageBuildValidationRefusal.ProvenanceMismatch, error.Reason);
    }
}
