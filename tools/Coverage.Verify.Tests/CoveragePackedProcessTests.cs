using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using KhaozEngine.Tools.Coverage;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

/// <summary>The command line over packed campaigns. Executable runs use production limits. In-process runs use the
/// single test entry point only where a reduced bound or a fault is the subject.</summary>
public sealed class CoveragePackedProcessTests : IDisposable
{
    readonly CoverageCliScratch scratch = new();
    string Scratch => scratch.Root;
    public void Dispose() => scratch.Dispose();

    [Theory]
    [InlineData("accepted", new[] { 0, 0, 0 }, null)]
    [InlineData("hole", new[] { 1 }, null)]
    [InlineData("penetration", new[] { 0, 0, 1 }, "ChangedRecordPenetration")]
    public async Task All_three_commands_accept_packed_exact_inputs_and_match_inline_decisions(string variant, int[] exits, string? reason)
    {
        var outcomes = new List<(int Exit, string? Reason, byte[]? Table)>[2];
        for (int packed = 0; packed < 2; packed++)
        {
            using var files = CoveragePackedFixture.Files();
            // PanelHole has no common valid row for key 1. PanelPenetration crosses at dense row 2.
            if (variant == "hole") files.Manifest["samples"]![1]!["stateKey"] = 1;
            if (variant == "penetration") files.Manifest["domains"]![0]!["denseRows"]![2]!["sample"]!["commands"]![0]!["transitionWeight"] = .25;
            files.Write();
            if (packed == 1) CoveragePackedFixture.Pack(files);
            outcomes[packed] = [];
            string[] commands = ["build", "verify-samples", "verify"];
            for (int i = 0; i < exits.Length; i++)
            {
                string evidence = Path.Combine(files.Folder, commands[i] + ".json");
                var result = await Execute([commands[i], "--manifest", files.ManifestPath, "--table", files.TablePath, "--evidence", evidence]);
                Assert.Equal(exits[i], result.ExitCode);
                string? refusal = result.ExitCode == 1 && result.Error.Length > 0
                    ? JsonDocument.Parse(result.Error).RootElement.GetProperty("Reason").GetString() : null;
                if (refusal == "ChangedRecordPenetration")
                {
                    using var error = JsonDocument.Parse(result.Error);
                    Assert.Equal(23u, error.RootElement.GetProperty("CommandId").GetUInt32());
                    Assert.Equal(17ul, error.RootElement.GetProperty("EquipmentInstanceId").GetUInt64());
                }
                if (result.ExitCode == 0) AssertComplete(evidence);
                else Assert.False(File.Exists(evidence));
                outcomes[packed].Add((result.ExitCode, refusal, File.Exists(files.TablePath) ? File.ReadAllBytes(files.TablePath) : null));
            }
            Assert.Equal(reason, outcomes[packed][^1].Reason);
        }
        // Equivalent representations decide identically and publish byte-identical tables.
        Assert.Equal(outcomes[0].Select(o => (o.Exit, o.Reason)), outcomes[1].Select(o => (o.Exit, o.Reason)));
        Assert.Equal(outcomes[0].Select(o => o.Table), outcomes[1].Select(o => o.Table));
    }

    [Theory]
    [InlineData("missing", CoverageBuildInputRefusal.ArtifactPartMissing)]
    [InlineData("truncated", CoverageBuildInputRefusal.ArtifactPartLengthMismatch)]
    [InlineData("substituted", CoverageBuildInputRefusal.ArtifactPartHashMismatch)]
    public void Resume_and_later_reads_check_the_last_part_even_when_the_first_records_match(string defect, CoverageBuildInputRefusal reason)
    {
        using var files = CoveragePackedFixture.Files();
        CoveragePackedFixture.Pack(files);
        string diagnostics = Path.Combine(files.Folder, "diagnostics.json");
        string[] args = ["build", "--manifest", files.ManifestPath, "--table", files.TablePath, "--evidence", files.EvidencePath, "--diagnostics", diagnostics];
        // A reduced bound makes both roots indexed sets with several parts.
        Assert.Equal(0, Run(args, 4096, 4096));
        foreach (string root in new[] { files.EvidencePath, diagnostics })
        {
            string part = LastPart(root);
            byte[] original = File.ReadAllBytes(part);
            Damage(part, original, defect);
            Assert.Equal(reason, Assert.Throws<CoverageBuildDataException>(() => CoverageArtifactReader.Open(root, Scratch)).Reason);
            var before = Snapshot(files.Folder);
            var error = new StringWriter();
            if (root == files.EvidencePath)
            {
                // Resume admission reads the whole set, so the damaged late part refuses before anything is replaced.
                Assert.Equal(2, Run(args, 4096, 4096, error));
                Assert.Equal(reason.ToString(), error.ToString().Split(' ')[0]);
                Assert.Equal(before, Snapshot(files.Folder));
            }
            else
            {
                // Diagnostics are never resumed. A damaged existing part name is never reused or overwritten, and a
                // missing one is written again from the complete new report.
                Assert.Equal(defect == "missing" ? 0 : 1, Run(args, 4096, 4096, error));
                if (defect != "missing") Assert.Equal(before, Snapshot(files.Folder));
            }
            if (defect != "missing" || root == files.EvidencePath) File.WriteAllBytes(part, original);
            using (var read = CoverageArtifactReader.Open(root, Scratch)) Assert.Equal(CoverageArtifactSet.HashFile(root), read.RootSha256);
            Assert.Equal(0, Run(args, 4096, 4096));
        }
    }

    static string LastPart(string root)
    {
        using var index = JsonDocument.Parse(File.ReadAllBytes(root));
        var parts = index.RootElement.GetProperty("parts");
        Assert.True(parts.GetArrayLength() >= 2);
        return Path.Combine(Path.GetDirectoryName(root)!, parts[parts.GetArrayLength() - 1].GetProperty("path").GetString()!);
    }

    static void Damage(string part, byte[] original, string defect)
    {
        if (defect == "missing") File.Delete(part);
        else if (defect == "truncated") File.WriteAllBytes(part, original[..^1]);
        else { byte[] changed = original.ToArray(); changed[^1] ^= 1; File.WriteAllBytes(part, changed); }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void A_late_chunk_unreadable_during_diagnostic_collection_is_reported_as_the_input_refusal(bool penetration, int outcome)
    {
        using var files = CoverageCampaignFixture.Files();
        if (penetration) files.Manifest["domains"]![0]!["denseRows"]![2]!["sample"]!["commands"]![0]!["transitionWeight"] = .25;
        // Three records per chunk put dense row 4 alone in the last chunk, which only dense collection reads late.
        CoverageCampaignFixture.Pack(files);
        int late = files.Manifest["poseChunks"]!.AsArray().Count - 1;
        Assert.Equal(2, late);
        string diagnostics = Path.Combine(files.Folder, "diagnostics.json");
        Assert.Equal(0, Run(["build", "--manifest", files.ManifestPath, "--table", files.TablePath, "--evidence", Path.Combine(files.Folder, "build.json")],
            CoverageArtifactSet.MaxBytes, CoverageArtifactSet.MaxBytes));
        string[] verify = ["verify", "--manifest", files.ManifestPath, "--table", files.TablePath, "--evidence", files.EvidencePath, "--diagnostics", diagnostics];
        // Control: the same run without the fault keeps its outcome and publishes a complete report.
        Assert.Equal(outcome, Run(verify, CoverageArtifactSet.MaxBytes, CoverageArtifactSet.MaxBytes));
        using (var read = CoverageArtifactReader.Open(diagnostics, Scratch)) Assert.Equal(CoverageArtifactSet.HashFile(diagnostics), read.RootSha256);
        var before = Snapshot(files.Folder);
        var error = new StringWriter();
        Assert.Equal(2, Program.RunForTests(verify, CoverageArtifactSet.MaxBytes, CoverageArtifactSet.MaxBytes, Scratch,
            new() { UnreadableChunk = new(late, CoverageRunStage.Diagnostics), Error = error }));
        // A refused run prints its own refusal first. The last line is the input refusal that ended the run.
        string[] reported = error.ToString().Trim().Split('\n')[^1].Trim().Split(' ');
        Assert.Equal(nameof(CoverageBuildInputRefusal.InputIo), reported[0]);
        Assert.Equal($"$.poseChunks[{late}]", reported[1]);
        // Neither an output failure nor a partial report: every output keeps its exact bytes.
        Assert.Equal(before, Snapshot(files.Folder));
        Assert.Equal(outcome, Run(verify, CoverageArtifactSet.MaxBytes, CoverageArtifactSet.MaxBytes));
    }

    [Theory]
    [InlineData("build")]
    [InlineData("verify-samples")]
    public void A_chunk_copy_unreadable_before_numerical_work_is_the_input_refusal(string command)
    {
        using var files = CoverageCampaignFixture.Files();
        CoverageCampaignFixture.Pack(files);
        const int bound = CoverageArtifactSet.MaxBytes;
        if (command != "build")
            Assert.Equal(0, Run(["build", "--manifest", files.ManifestPath, "--table", files.TablePath, "--evidence", Path.Combine(files.Folder, "build.json")], bound, bound));
        string[] args = [command, "--manifest", files.ManifestPath, "--table", files.TablePath, "--evidence", files.EvidencePath];
        // Control: the same command on the same inputs is accepted.
        Assert.Equal(0, Run(args, bound, bound));
        var before = Snapshot(files.Folder);
        var error = new StringWriter();
        // Every pose comes from the session's checked copies after admission. A route that read the whole campaign
        // into resident memory at admission would never notice the missing copy.
        Assert.Equal(2, Program.RunForTests(args, bound, bound, Scratch,
            new() { UnreadableChunk = new(0, CoverageRunStage.NumericalWork), Error = error }));
        string[] reported = error.ToString().Trim().Split('\n')[^1].Trim().Split(' ');
        Assert.Equal([nameof(CoverageBuildInputRefusal.InputIo), "$.poseChunks[0]"], reported);
        Assert.Equal(before, Snapshot(files.Folder));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void A_session_cleanup_failure_is_reported_without_replacing_the_exit_code(bool hole, int exit)
    {
        using var files = CoverageCampaignFixture.Files();
        if (hole) files.Manifest["samples"]![1]!["stateKey"] = 1;
        CoverageCampaignFixture.Pack(files);
        var error = new StringWriter();
        Assert.Equal(exit, Program.RunForTests(["build", "--manifest", files.ManifestPath, "--table", files.TablePath, "--evidence", files.EvidencePath],
            CoverageArtifactSet.MaxBytes, CoverageArtifactSet.MaxBytes, Scratch, new() { FailSessionCleanup = true, Error = error }));
        Assert.Equal(nameof(IOException), error.ToString().Trim().Split('\n')[^1].Trim());
        Assert.Equal(exit == 0, File.Exists(files.TablePath));
    }

    [Fact]
    public async Task Readme_packed_fixture_matches_independent_bytes_and_builds_through_the_executable()
    {
        string readme = KhaozEngine.Tests.CoverageTooling.CoverageUnitSelectionTests.Checkout("tools/Coverage.Verify/README.md");
        string header = Regex.Match(readme, "bytes\\.fromhex\\(\"([0-9a-f]{64})\"\\)").Groups[1].Value;
        string digest = Regex.Match(readme, "hexdigest\\(\\) == \"([0-9a-f]{64})\"").Groups[1].Value;
        string json = Regex.Match(readme, "```json\n(\\{\n  \"formatVersion\": 2,.*?)```", RegexOptions.Singleline).Groups[1].Value;
        Assert.NotEmpty(json);
        // Independent generation, never the production codec.
        byte[] chunk = CoveragePackedFixture.ChunkBytes();
        Assert.Equal(368, chunk.Length);
        Assert.Equal(header, Convert.ToHexStringLower(chunk.AsSpan(0, 32)));
        Assert.Equal(digest, Convert.ToHexStringLower(SHA256.HashData(chunk)));
        // The documented Python lines themselves must produce those bytes, so an edit to them alone fails here.
        Assert.Equal(chunk, DocumentedChunk(readme, header));
        var manifest = JsonNode.Parse(json)!.AsObject();
        var descriptor = manifest["poseChunks"]!.AsArray().Single()!;
        Assert.Equal(digest, descriptor["sha256"]!.GetValue<string>());
        Assert.Equal(chunk.Length, descriptor["byteLength"]!.GetValue<int>());
        using var files = new CoverageBuildFiles(scratchDirectory: Scratch);
        // The documented host, map and configuration references stand for a consumer's own checked assets.
        foreach (string name in new[] { "host", "map", "configuration" }) manifest[name] = files.Manifest[name]!.DeepClone();
        string chunkPath = Path.Combine(files.Folder, descriptor["path"]!.GetValue<string>());
        Directory.CreateDirectory(Path.GetDirectoryName(chunkPath)!);
        File.WriteAllBytes(chunkPath, chunk);
        File.WriteAllText(files.ManifestPath, manifest.ToJsonString());
        var result = await Execute(["build", "--manifest", files.ManifestPath, "--table", files.TablePath, "--evidence", files.EvidencePath]);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, AssertComplete(files.EvidencePath).Totals["samples"]);
    }

    static byte[] DocumentedChunk(string readme, string header)
    {
        static ulong Number(string text) => text.StartsWith("0x", StringComparison.Ordinal)
            ? Convert.ToUInt64(text[2..], 16) : ulong.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        var identity = Regex.Match(readme, "\nidentity = struct\\.pack\\(\"<16I\", ([^)]*)\\)\n");
        var record = Regex.Match(readme, "\nrecord = identity \\* (\\d+) \\+ struct\\.pack\\(\"<IQ\", (\\w+), (\\w+)\\) "
            + "\\+ identity \\* (\\d+) \\+ struct\\.pack\\(\"<I\", (\\w+)\\)\n");
        Assert.True(identity.Success && record.Success);
        uint[] words = identity.Groups[1].Value.Split(',').Select(w => (uint)Number(w.Trim())).ToArray();
        Assert.Equal(16, words.Length);
        var bytes = new List<byte>(Convert.FromHexString(header));
        void Add(ulong value, int size) { for (int i = 0; i < size; i++) bytes.Add((byte)(value >> (8 * i))); }
        void Identity(int count) { for (int c = 0; c < count; c++) foreach (uint word in words) Add(word, 4); }
        Identity((int)Number(record.Groups[1].Value));
        Add(Number(record.Groups[2].Value), 4); Add(Number(record.Groups[3].Value), 8);
        Identity((int)Number(record.Groups[4].Value));
        Add(Number(record.Groups[5].Value), 4);
        return bytes.ToArray();
    }

    CoverageArtifactHeader AssertComplete(string evidence)
    {
        using var read = CoverageArtifactReader.Open(evidence, Scratch);
        Assert.Equal(CoverageArtifactSet.HashFile(evidence), read.RootSha256);
        using var payload = read.OpenPayload();
        var header = CoverageLogicalDocumentReader.Validate(payload, CoverageArtifactKind.Evidence).Header;
        Assert.True(header.Complete);
        return header;
    }

    static Dictionary<string, string> Snapshot(string folder) => Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
        .ToDictionary(p => Path.GetRelativePath(folder, p), CoverageArtifactSet.HashFile);

    int Run(string[] args, int artifactByteLimit, int partByteLimit, TextWriter? error = null) =>
        Program.RunForTests(args, artifactByteLimit, partByteLimit, Scratch, new() { ReportByteLimit = partByteLimit, Error = error });

    async Task<(int ExitCode, string Error)> Execute(string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (string arg in args) start.ArgumentList.Add(arg);
        start.Environment["KE_GPU_TESTS"] = "0";
        // Default session and staging scratch stay under this test's private temporary root.
        foreach (string name in OperatingSystem.IsWindows() ? new[] { "TEMP", "TMP" } : ["TMPDIR"]) start.Environment[name] = Scratch;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await stdout;
        return (process.ExitCode, await stderr);
    }
}
