using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using KhaozEngine.Render3D;
using KhaozEngine.Tools.Coverage;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

public sealed class CoverageDiagnosticsProcessTests
{
    // Inline and packed campaigns report the same complete diagnostics without changing the original outcome.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Holed_build_exports_all_witnesses_without_publishing_acceptance_and_is_deterministic(bool packed)
    {
        using var files = new CoverageBuildFiles("diagnostics");
        files.Manifest["samples"]![1]!["stateKey"] = 1;
        files.Write();
        if (packed) CoveragePackedFixture.Pack(files);
        File.WriteAllText(files.TablePath, "table-sentinel");
        File.WriteAllText(files.EvidencePath, "evidence-sentinel");
        var legacy = await Execute(files, "build", diagnostics: false);
        var result = await Execute(files, "build");
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(legacy, result);
        Assert.Equal("table-sentinel", File.ReadAllText(files.TablePath));
        Assert.Equal("evidence-sentinel", File.ReadAllText(files.EvidencePath));
        using var json = Read(files, "build", "refused");
        var root = json.RootElement;
        var holes = Category(root, "holes", 1);
        Assert.Equal(1ul, holes[0].GetProperty("stateKey").GetUInt64());
        Assert.Equal(new[] { "a", "b" }, holes[0].GetProperty("sampleIds").EnumerateArray().Select(s => s.GetString()));
        Category(root, "domains", 1);
        Category(root, "samples", 2);
        Category(root, "candidates", 2);
        Category(root, "candidatePairs", 4);
        Assert.Equal(1, root.GetProperty("findingCount").GetInt32());
        byte[] first = File.ReadAllBytes(PathFor(files));
        Assert.Equal(result, await Execute(files, "build"));
        Assert.Equal(first, File.ReadAllBytes(PathFor(files)));
    }

    [Theory]
    [InlineData("build", false)]
    [InlineData("verify-samples", false)]
    [InlineData("build", true)]
    [InlineData("verify-samples", true)]
    public async Task Accepted_sample_commands_replace_stale_diagnostics_and_preserve_same_build_output_bytes(string command, bool packed)
    {
        using var files = new CoverageBuildFiles("diagnostics");
        if (packed) CoveragePackedFixture.Pack(files);
        Assert.Equal(0, (await Execute(files, "build", diagnostics: false)).ExitCode);
        if (command == "verify-samples")
        {
            File.Delete(files.EvidencePath);
            Assert.Equal(0, (await Execute(files, command, diagnostics: false)).ExitCode);
        }
        byte[] table = File.ReadAllBytes(files.TablePath), evidence = File.ReadAllBytes(files.EvidencePath);
        File.WriteAllText(PathFor(files), "stale-refusal");
        Assert.Equal((0, "", ""), await Execute(files, command));
        using var json = Read(files, command, "accepted");
        Assert.Equal(0, json.RootElement.GetProperty("findingCount").GetInt32());
        Category(json.RootElement, "holes", 0);
        Assert.Equal(table, File.ReadAllBytes(files.TablePath));
        Assert.Equal(evidence, File.ReadAllBytes(files.EvidencePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Candidate_refusal_keeps_its_reason_and_witness_without_hiding_later_candidates(bool packed)
    {
        using var files = new CoverageBuildFiles("diagnostics");
        files.Manifest["samples"]![0]!["actorModel"] = CoverageBuildFiles.Matrix(Matrix4x4.CreateScale(4));
        files.Write();
        if (packed) CoveragePackedFixture.Pack(files);
        var legacy = await Execute(files, "build", diagnostics: false);
        Assert.Equal(1, legacy.ExitCode);
        Assert.Equal(legacy, await Execute(files, "build"));
        using var json = Read(files, "build", "refused");
        var candidates = Category(json.RootElement, "candidates", 2);
        Assert.Equal("refused", candidates[0].GetProperty("outcome").GetString());
        Assert.Equal("CandidateConstruction", candidates[0].GetProperty("reason").GetString());
        Assert.Equal("a", candidates[0].GetProperty("sampleId").GetString());
        Assert.NotEqual("none", candidates[0].GetProperty("failure").GetProperty("code").GetString());
        Assert.Equal("constructed", candidates[1].GetProperty("outcome").GetString());
        Assert.Equal("b", candidates[1].GetProperty("sampleId").GetString());
        Category(json.RootElement, "candidatePairs", 2);
        Assert.False(File.Exists(files.TablePath));
        Assert.False(File.Exists(files.EvidencePath));
    }

    [Fact]
    public async Task Refused_verify_lists_every_dense_failure_and_changed_record_depth_with_identifiers()
    {
        using var files = CoverageVerifyProcessTests.Files();
        Assert.Equal(0, (await Execute(files, "build", diagnostics: false)).ExitCode);
        File.WriteAllText(files.EvidencePath, "sentinel");
        var rows = files.Manifest["domains"]![0]!["denseRows"]!;
        rows[1]!["resolvedStateKey"] = 999;
        rows[2]!["sample"]!["commands"]![0]!["transitionWeight"] = .25;
        rows[3]!["resolvedStateKey"] = 2;
        files.Write();
        var legacy = await Execute(files, "verify", diagnostics: false);
        Assert.Equal(1, legacy.ExitCode);
        Assert.Equal(legacy, await Execute(files, "verify"));
        Assert.Equal("sentinel", File.ReadAllText(files.EvidencePath));
        using var json = Read(files, "verify", "refused");
        var failures = Category(json.RootElement, "replayFailures", 3);
        Assert.Contains(failures.EnumerateArray(), f => f.GetProperty("denseIndex").GetInt32() == 1
            && f.GetProperty("resolvedStateKey").GetUInt64() == 999 && f.GetProperty("reason").GetString() == "ReplayHole");
        Assert.Contains(failures.EnumerateArray(), f => f.GetProperty("denseIndex").GetInt32() == 3
            && f.GetProperty("reason").GetString() == "SelectedReplayFailure");
        var changed = json.RootElement.GetProperty("changedRecordDepth").GetProperty("items");
        Assert.Contains(changed.EnumerateArray(), d => d.GetProperty("denseIndex").GetInt32() == 2
            && d.GetProperty("commandId").GetUInt32() == 23 && d.GetProperty("equipmentInstanceId").GetUInt64() == 17
            && d.GetProperty("depth").GetProperty("observedMm").GetDouble() > 0
            && d.GetProperty("depth").GetProperty("upperEstimateMm").GetDouble() > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Accepted_verify_keeps_inherited_contact_separate_from_acceptance_findings(bool packed)
    {
        using var files = CoverageVerifyProcessTests.Files();
        AddInheritedTriangle(files);
        if (packed) CoveragePackedFixture.Pack(files);
        Assert.Equal(0, (await Execute(files, "build", diagnostics: false)).ExitCode);
        File.Delete(files.EvidencePath);
        Assert.Equal(0, (await Execute(files, "verify")).ExitCode);
        using var json = Read(files, "verify", "accepted");
        Assert.Equal(0, json.RootElement.GetProperty("findingCount").GetInt32());
        Category(json.RootElement, "replayFailures", 0);
        var inherited = json.RootElement.GetProperty("inheritedDepth").GetProperty("items");
        Assert.Contains(inherited.EnumerateArray(), d => d.GetProperty("depth").GetProperty("observedMm").GetDouble() > 0);
    }

    [Fact]
    public async Task Missing_dense_work_reports_every_domain()
    {
        using var files = new CoverageBuildFiles("diagnostics");
        var second = files.Manifest["domains"]![0]!.DeepClone();
        second["id"] = "other";
        second["authoredFrameIds"] = new JsonArray("frame-b");
        files.Manifest["domains"]![0]!["authoredFrameIds"] = new JsonArray("frame-a");
        files.Manifest["domains"]!.AsArray().Add(second);
        files.Manifest["samples"]![1]!["domainId"] = "other";
        files.Write();
        Assert.Equal(0, (await Execute(files, "build", diagnostics: false)).ExitCode);
        Assert.Equal(1, (await Execute(files, "verify")).ExitCode);
        using var json = Read(files, "verify", "refused");
        var missing = Category(json.RootElement, "missingDenseWork", 2);
        Assert.Equal(new[] { "clip", "other" }, missing.EnumerateArray().Select(d => d.GetProperty("domainId").GetString()));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("manifest")]
    [InlineData("table")]
    [InlineData("evidence")]
    [InlineData("source")]
    [InlineData("malformed-input")]
    [InlineData("malformed-resume")]
    public async Task Malformed_input_or_diagnostics_alias_writes_nothing(string defect)
    {
        using var files = new CoverageBuildFiles("diagnostics");
        if (defect == "malformed-resume")
        {
            Assert.Equal(0, (await Execute(files, "build", diagnostics: false)).ExitCode);
            File.WriteAllText(files.EvidencePath, "{");
        }
        string path = defect switch
        {
            "manifest" => files.ManifestPath,
            "table" => files.TablePath,
            "evidence" => files.EvidencePath,
            "source" => Path.Combine(files.Folder, "host.glb"),
            _ => PathFor(files)
        };
        if (!File.Exists(path)) File.WriteAllText(path, "diagnostic-sentinel");
        byte[] original = File.ReadAllBytes(path);
        if (defect == "malformed-input") File.WriteAllText(files.ManifestPath, "{");
        var result = await Execute(files, "build", path: path, extra: defect == "unknown" ? ["--unknown", "value"] : []);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_diagnostic_publication_rolls_back_accepted_artifacts(bool existing)
    {
        using var files = new CoverageBuildFiles("diagnostics");
        if (existing)
        {
            Assert.Equal(0, (await Execute(files, "build", diagnostics: false)).ExitCode);
            // Valid noncanonical whitespace makes a missed evidence rollback observable.
            File.WriteAllText(files.EvidencePath, JsonNode.Parse(File.ReadAllBytes(files.EvidencePath))!
                .ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        byte[]? table = existing ? File.ReadAllBytes(files.TablePath) : null;
        byte[]? evidence = existing ? File.ReadAllBytes(files.EvidencePath) : null;
        string directory = Path.Combine(files.Folder, "directory");
        Directory.CreateDirectory(directory);
        var failure = await Execute(files, "build", path: directory);
        Assert.Equal(1, failure.ExitCode);
        Assert.Equal(nameof(CoverageBuildValidationRefusal.OutputIo), failure.Error.TrimEnd());
        if (existing)
        {
            Assert.Equal(table, File.ReadAllBytes(files.TablePath));
            Assert.Equal(evidence, File.ReadAllBytes(files.EvidencePath));
        }
        else
        {
            Assert.False(File.Exists(files.TablePath));
            Assert.False(File.Exists(files.EvidencePath));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_diagnostic_parent_refuses_staging_before_accepted_publication(bool existing)
    {
        using var files = new CoverageBuildFiles("diagnostics");
        if (existing)
        {
            Assert.Equal(0, (await Execute(files, "build", diagnostics: false)).ExitCode);
            // Valid noncanonical whitespace makes a missed evidence rollback observable.
            File.WriteAllText(files.EvidencePath, JsonNode.Parse(File.ReadAllBytes(files.EvidencePath))!
                .ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        byte[]? table = existing ? File.ReadAllBytes(files.TablePath) : null;
        byte[]? evidence = existing ? File.ReadAllBytes(files.EvidencePath) : null;
        string path = Path.Combine(files.Folder, "missing", "diagnostics.json");
        Assert.Equal(1, (await Execute(files, "build", path: path)).ExitCode);
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        Assert.Empty(Directory.GetFiles(files.Folder, "*.tmp"));
        Assert.Empty(Directory.GetFiles(files.Folder, "*.bak"));
        if (existing)
        {
            Assert.Equal(table, File.ReadAllBytes(files.TablePath));
            Assert.Equal(evidence, File.ReadAllBytes(files.EvidencePath));
        }
        else
        {
            Assert.False(File.Exists(files.TablePath));
            Assert.False(File.Exists(files.EvidencePath));
        }
    }

    [Theory]
    [InlineData("gap", "DenseSequence", false)]
    [InlineData("raw-pose", "SelectedReplayFailure", false)]
    [InlineData("gap", "DenseSequence", true)]
    [InlineData("raw-pose", "SelectedReplayFailure", true)]
    public async Task Early_replay_refusal_does_not_hide_later_dense_work(string defect, string reason, bool packed)
    {
        using var files = CoverageVerifyProcessTests.Files();
        Assert.Equal(0, (await Execute(files, "build", diagnostics: false)).ExitCode);
        var rows = files.Manifest["domains"]![0]!["denseRows"]!;
        if (defect == "gap") rows[1]!["denseIndex"] = 9;
        else rows[1]!["sample"]!["actorModel"] = CoverageBuildFiles.Matrix(Matrix4x4.CreateScale(0));
        rows[4]!["resolvedStateKey"] = 999;
        files.Write();
        // The table built from the inline campaign is the packed campaign's table too, since only the manifest differs.
        if (packed) CoveragePackedFixture.Pack(files);
        var legacy = await Execute(files, "verify", diagnostics: false);
        Assert.Equal(1, legacy.ExitCode);
        Assert.Equal(legacy, await Execute(files, "verify"));
        using var json = Read(files, "verify", "refused");
        var failures = json.RootElement.GetProperty("replayFailures").GetProperty("items");
        Assert.Contains(failures.EnumerateArray(), f => f.GetProperty("denseIndex").GetInt32() == (defect == "gap" ? 9 : 1)
            && f.GetProperty("reason").GetString() == reason);
        Assert.Contains(failures.EnumerateArray(), f => f.GetProperty("denseIndex").GetInt32() == 4
            && f.GetProperty("resolvedStateKey").GetUInt64() == 999 && f.GetProperty("reason").GetString() == "ReplayHole");
    }

    static JsonElement Category(JsonElement root, string name, int count)
    {
        var category = root.GetProperty(name);
        Assert.Equal(count, category.GetProperty("total").GetInt32());
        Assert.Equal(0, category.GetProperty("omitted").GetInt32());
        Assert.Equal(count, category.GetProperty("items").GetArrayLength());
        return category.GetProperty("items");
    }
    static JsonDocument Read(CoverageBuildFiles files, string command, string outcome)
    {
        // The strict reader admits a single file or a complete set and proves zero omissions in every category.
        using var session = CoverageArtifactReader.Open(PathFor(files), files.Folder);
        using var payload = session.OpenPayload();
        var json = JsonDocument.Parse(payload);
        Assert.Equal("khaozengine.coverage-diagnostics/2", json.RootElement.GetProperty("schema").GetString());
        Assert.Equal("diagnostic-not-acceptance", json.RootElement.GetProperty("scope").GetString());
        Assert.Equal(command, json.RootElement.GetProperty("command").GetString());
        Assert.Equal(outcome, json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(64, json.RootElement.GetProperty("inputSha256").GetString()!.Length);
        return json;
    }
    static string PathFor(CoverageBuildFiles files) => Path.Combine(files.Folder, "diagnostics.json");
    static async Task<(int ExitCode, string Output, string Error)> Execute(CoverageBuildFiles files, string command,
        bool diagnostics = true, string? path = null, string[]? extra = null)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string arg in new[] { typeof(Program).Assembly.Location, command, "--manifest", files.ManifestPath,
            "--table", files.TablePath, "--evidence", files.EvidencePath }) start.ArgumentList.Add(arg);
        if (diagnostics) { start.ArgumentList.Add("--diagnostics"); start.ArgumentList.Add(path ?? PathFor(files)); }
        foreach (string arg in extra ?? []) start.ArgumentList.Add(arg);
        start.Environment["KE_GPU_TESTS"] = "0";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
    }

    static void AddInheritedTriangle(CoverageBuildFiles files)
    {
        var input = CoverageBuildInput.Read(files.ManifestPath);
        var source = input.Topology.Source;
        int n = source.Positions.Length;
        Vector3[] extra = [new(-4, -3, .15f), new(4, -3, .15f), new(0, 6, .15f)];
        byte[] host = CoverageBuildFiles.Mesh([.. source.Positions, .. extra],
            [.. source.Triangles.SelectMany(t => new uint[] { (uint)t.A, (uint)t.B, (uint)t.C }), (uint)n, (uint)n + 1, (uint)n + 2],
            [.. source.SkinVertices, .. extra.Select(p => new SkinnedVertex { Position = p, Normal = Vector3.UnitZ,
                Color = Vector4.One, BoneWeights = new(1, 0, 0, 0) })]);
        var map = input.Topology.Map;
        int group = map.Topology.Max(t => t.SurfaceId) + 1;
        var updated = new HostCoverageMap(CoverageBuildInputJson.Hash(host),
            map.Topology.Concat(extra.Select((_, i) => new CoverageTopologyEntry(0, 0, n + i, group + i))), map.Regions, map.Refinement, map.Authoring);
        files.Manifest["host"] = files.Save("host.glb", host);
        files.Manifest["map"] = files.Save("map.json", HostCoverageMapJson.Write(updated));
        var companion = JsonNode.Parse(File.ReadAllBytes(Path.Combine(files.Folder, "companion.json")))!;
        companion["hostMapSha256"] = HostCoverageMapJson.SemanticSha256(updated);
        files.Configuration["companion"] = files.Save("companion.json", System.Text.Encoding.UTF8.GetBytes(companion.ToJsonString()));
        files.WriteConfiguration();
    }
}
