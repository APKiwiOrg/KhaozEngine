using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.Render3D;
using KhaozEngine.Tools.Coverage;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

/// <summary>Complete diagnostic sets: zero omissions across parts, producer and resume identity, and preservation of
/// accepted artifacts and the previous report when publication fails.</summary>
public sealed class CoverageDiagnosticSetTests
{
    static readonly string Scratch = Path.Combine(Path.GetTempPath(), "khaozengine-coverage-packed-input", "task-6");
    static readonly string[] Blocking = ["holes", "selectedFailures", "replayFailures", "missingDenseWork", "analysisRefusals", "runRefusals"];

    // A future version of a known set envelope is an unsupported version, not a malformed legacy diagnostic.
    [Theory]
    [InlineData("evidence", null)]
    [InlineData("diagnostics", null)]
    [InlineData("legacy-diagnostics", null)]
    [InlineData("evidence", 2L)]
    [InlineData("diagnostics", 2L)]
    public void Unknown_versions_of_known_set_schemas_refuse_as_unsupported(string root, long? version)
    {
        using var fixture = new CoveragePublicationFixture();
        fixture.Publish(fixture.Stage("schema"));
        string path = root == "evidence" ? fixture.Evidence : fixture.Diagnostics;
        if (root == "legacy-diagnostics")
        {
            path = Path.Combine(fixture.Output, "legacy-diagnostics.json");
            using var set = CoverageArtifactReader.Open(fixture.Diagnostics, fixture.Scratch);
            using var payload = set.OpenPayload();
            using var legacy = File.Create(path);
            payload.CopyTo(legacy);
        }
        if (version is not { } value)
        {
            using var read = CoverageArtifactReader.Open(path, fixture.Scratch);
            Assert.Equal(CoverageArtifactSet.HashFile(path), read.RootSha256);
            return;
        }
        var index = JsonNode.Parse(File.ReadAllBytes(path))!;
        string schema = (string)index["schema"]!;
        Assert.EndsWith("-set/1", schema, StringComparison.Ordinal);
        index["schema"] = schema[..^1] + value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(path, index.ToJsonString());
        var error = Assert.Throws<CoverageBuildDataException>(() => CoverageArtifactReader.Open(path, fixture.Scratch));
        Assert.Equal(CoverageBuildInputRefusal.UnsupportedVersion, error.Reason);
        Assert.Equal(value, error.FormatVersion);
        Assert.Equal("$.schema", error.Path);
    }

    [Fact]
    public void Diagnostics_export_every_collected_item_across_parts_with_zero_omissions()
    {
        using var files = Holed();
        // A reduced artifact bound forces a multi-part set from the small PanelHole campaign.
        Assert.Equal(1, Program.Run(Args(files, "build"), 4096));
        var index = JsonNode.Parse(File.ReadAllBytes(PathFor(files)))!;
        Assert.Equal("khaozengine.coverage-diagnostics-set/1", (string)index["schema"]!);
        Assert.True((int)index["partCount"]! > 1);
        using (var refused = Read(files, "build", "refused"))
        {
            Assert.Equal(1, Category(refused.RootElement, "holes").GetArrayLength());
            Assert.Equal(4, Category(refused.RootElement, "candidatePairs").GetArrayLength());
            Assert.Equal(2, Category(refused.RootElement, "candidates").GetArrayLength());
            Assert.Equal(1, refused.RootElement.GetProperty("findingCount").GetInt32());
        }
        using var accepted = new CoverageBuildFiles(scratchDirectory: Scratch);
        Assert.Equal(0, Program.Main(Args(accepted, "build")));
        using var control = Read(accepted, "build", "accepted");
        Assert.Equal(0, control.RootElement.GetProperty("findingCount").GetInt32());
        foreach (string name in Blocking) Assert.Equal(0, Category(control.RootElement, name).GetArrayLength());
        Assert.Equal(4, Category(control.RootElement, "candidatePairs").GetArrayLength());
    }

    // An index that cannot name every part refuses before publication. Nothing is truncated or replaced.
    [Fact]
    public void Diagnostic_index_bound_refuses_without_replacing_prior_diagnostics()
    {
        using var files = Holed();
        File.WriteAllText(PathFor(files), "prior-report");
        Assert.Equal(1, Program.Run(Args(files, "build"), 1024));
        Assert.Equal("prior-report", File.ReadAllText(PathFor(files)));
        Assert.False(Directory.Exists(PathFor(files) + ".parts"));
        Assert.Equal(1, Program.Run(Args(files, "build"), 4096));
        using var control = Read(files, "build", "refused");
        Assert.Equal(1, Category(control.RootElement, "holes").GetArrayLength());
    }

    // Producer hashes name the loaded assemblies. The resumed hash names the exact root bytes read, never the payload.
    // Neither proves that a report is fresh for two identical invocations.
    [Fact]
    public void Diagnostic_identity_names_the_producer_and_the_exact_resumed_root_read()
    {
        // Dense rows make the evidence large enough to span parts at the reduced bound below.
        using var files = CoverageVerifyProcessTests.Files();
        Assert.Equal(0, Program.Main(Args(files, "build")));
        using (var first = Read(files, "build", "accepted"))
        {
            Assert.Equal(Sha256(typeof(Program).Assembly.Location), first.RootElement.GetProperty("toolCodeSha256").GetString());
            Assert.Equal(Sha256(typeof(CoverageRefinement).Assembly.Location), first.RootElement.GetProperty("render3DCodeSha256").GetString());
            Assert.Equal(JsonValueKind.Null, first.RootElement.GetProperty("resumedEvidenceSha256").ValueKind);
        }
        byte[] canonical = File.ReadAllBytes(files.EvidencePath);
        // Valid noncanonical whitespace is admitted by resume, and its exact bytes are what was read.
        File.WriteAllText(files.EvidencePath, JsonNode.Parse(canonical)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        string indented = Sha256(files.EvidencePath);
        Assert.NotEqual(Convert.ToHexStringLower(SHA256.HashData(canonical)), indented);
        Assert.Equal(0, Program.Main(Args(files, "build")));
        using (var legacy = Read(files, "build", "accepted"))
            Assert.Equal(indented, legacy.RootElement.GetProperty("resumedEvidenceSha256").GetString());
        Assert.Equal(canonical, File.ReadAllBytes(files.EvidencePath));
        string root = PublishEvidenceSet(files, canonical);
        Assert.NotEqual(Convert.ToHexStringLower(SHA256.HashData(canonical)), root);
        Assert.Equal(0, Program.Main(Args(files, "build")));
        using var set = Read(files, "build", "accepted");
        Assert.Equal(root, set.RootElement.GetProperty("resumedEvidenceSha256").GetString());
    }

    [Theory]
    [InlineData(true, "ReplaceDiagnostic", 0, true)]
    [InlineData(true, "ReplaceEvidence", 0, true)]
    [InlineData(false, "StagePart", 1, true)]
    [InlineData(false, "StagePart", 1, false)]
    [InlineData(false, "ReplaceDiagnostic", 0, true)]
    public void Diagnostic_set_failure_preserves_accepted_artifacts_and_prior_report(bool accepted, string step, int ordinal, bool prior)
    {
        using var files = accepted ? new CoverageBuildFiles(scratchDirectory: Scratch) : Holed();
        byte[]? table = null, evidence = null;
        if (accepted)
        {
            Assert.Equal(0, Program.Main(Args(files, "build", diagnostics: false)));
            // Valid noncanonical whitespace makes a missed evidence rollback observable.
            File.WriteAllText(files.EvidencePath, JsonNode.Parse(File.ReadAllBytes(files.EvidencePath))!
                .ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            (table, evidence) = (File.ReadAllBytes(files.TablePath), File.ReadAllBytes(files.EvidencePath));
        }
        if (prior) File.WriteAllText(PathFor(files), "prior-report");
        // The refused report spans parts at the reduced bound, so a late part can fail.
        int limit = accepted ? 67_108_864 : 4096;
        var fault = new CoveragePublicationFault(Enum.Parse<CoveragePublicationStep>(step), ordinal);
        Assert.Equal(1, Program.RunForTests(Args(files, "build"), limit, fault));
        if (prior) Assert.Equal("prior-report", File.ReadAllText(PathFor(files)));
        else Assert.False(File.Exists(PathFor(files)));
        Assert.Equal(table, File.Exists(files.TablePath) ? File.ReadAllBytes(files.TablePath) : null);
        Assert.Equal(evidence, File.Exists(files.EvidencePath) ? File.ReadAllBytes(files.EvidencePath) : null);
        // Without the fault the same run replaces the prior report with its complete outcome.
        Assert.Equal(accepted ? 0 : 1, Program.Run(Args(files, "build"), limit));
        using var control = Read(files, "build", accepted ? "accepted" : "refused");
        Assert.Equal(accepted ? 0 : 1, control.RootElement.GetProperty("findingCount").GetInt32());
        if (accepted) foreach (string name in Blocking) Assert.Equal(0, Category(control.RootElement, name).GetArrayLength());
    }

    // An independent roster of the schema's categories, in order.
    static readonly string[] Categories = ["domains", "samples", "candidates", "candidateEntries", "candidatePairs", "entries",
        "pairResults", "selectedClaims", "holes", "selectedFailures", "replayFailures", "missingDenseWork", "changedRecordDepth",
        "inheritedDepth", "analysisRefusals", "runRefusals"];

    static CoverageBuildFiles Holed()
    {
        var files = new CoverageBuildFiles(scratchDirectory: Scratch);
        files.Manifest["samples"]![1]!["stateKey"] = 1;
        files.Write();
        return files;
    }
    static string PathFor(CoverageBuildFiles files) => Path.Combine(files.Folder, "diagnostics.json");
    static string[] Args(CoverageBuildFiles files, string command, bool diagnostics = true) => diagnostics
        ? [command, "--manifest", files.ManifestPath, "--table", files.TablePath, "--evidence", files.EvidencePath, "--diagnostics", PathFor(files)]
        : [command, "--manifest", files.ManifestPath, "--table", files.TablePath, "--evidence", files.EvidencePath];
    static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>Reads the report through the strict reader, so a set is admitted only with every part, and proves
    /// that every category is present, in order and complete.</summary>
    static JsonDocument Read(CoverageBuildFiles files, string command, string outcome)
    {
        using var session = CoverageArtifactReader.Open(PathFor(files), files.Folder);
        using var payload = session.OpenPayload();
        var json = JsonDocument.Parse(payload);
        var root = json.RootElement;
        Assert.Equal("khaozengine.coverage-diagnostics/2", root.GetProperty("schema").GetString());
        Assert.Equal("diagnostic-not-acceptance", root.GetProperty("scope").GetString());
        Assert.Equal(command, root.GetProperty("command").GetString());
        Assert.Equal(outcome, root.GetProperty("outcome").GetString());
        Assert.Equal(Categories, root.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object
            && p.Value.TryGetProperty("items", out _)).Select(p => p.Name));
        foreach (string name in Categories) Category(root, name);
        return json;
    }
    static JsonElement Category(JsonElement root, string name)
    {
        var category = root.GetProperty(name);
        Assert.Equal(0, category.GetProperty("omitted").GetInt32());
        Assert.Equal(category.GetProperty("items").GetArrayLength(), category.GetProperty("total").GetInt32());
        return category.GetProperty("items");
    }

    static string PublishEvidenceSet(CoverageBuildFiles files, byte[] payload)
    {
        Assert.True(payload.Length > 4096);
        CoverageArtifactHeader header;
        using (var check = new MemoryStream(payload)) header = CoverageLogicalDocumentReader.Validate(check, CoverageArtifactKind.Evidence).Header;
        using var staged = CoverageArtifactWriter.Stage(files.EvidencePath, header, s => s.Write(payload), files.Folder, 4096, 4096);
        Assert.NotEmpty(staged.Parts);
        CoveragePublisher.Publish([], [staged], []);
        return staged.RootSha256;
    }
}
