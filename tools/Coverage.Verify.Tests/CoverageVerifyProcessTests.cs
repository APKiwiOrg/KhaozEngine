using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using KhaozEngine.Render3D;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using KhaozEngine.Tools.Coverage;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

public sealed class CoverageVerifyProcessTests
{
    [Fact]
    public async Task Verify_publishes_complete_dense_and_whole_skin_evidence()
    {
        using var files = Files();
        Assert.Equal(0, Program.Main(Args(files, "build")));
        // A full verification has its own evidence scope and can replace sample evidence for these same inputs.
        File.Delete(files.EvidencePath);
        var result = await Execute(files);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        using var json = JsonDocument.Parse(File.ReadAllBytes(files.EvidencePath));
        var root = json.RootElement;
        Assert.True(root.GetProperty("complete").GetBoolean());
        Assert.Equal("declared-samples-and-dense-whole-skin", root.GetProperty("scope").GetString());
        Assert.Equal(5, root.GetProperty("domains")[0].GetProperty("denseRows").GetArrayLength());
        var replay = root.GetProperty("replay");
        Assert.Equal(5, replay.GetProperty("poseCount").GetInt32());
        Assert.Equal(4096, replay.GetProperty("queryBudgetPerClassEquipmentPose").GetInt32());
        Assert.Equal(.1, replay.GetProperty("depthResolutionMm").GetDouble());
        Assert.False(string.IsNullOrWhiteSpace(replay.GetProperty("arithmeticLimitations").GetString()));
        Assert.Equal(5, replay.GetProperty("poses").GetArrayLength());
        Assert.True(replay.GetProperty("queries").GetInt32() > 0);
        Assert.Equal(0, replay.GetProperty("changedRecordObservedMm").GetDouble());
        Assert.Equal(0, replay.GetProperty("changedRecordUpperEstimateMm").GetDouble());
        Assert.Equal(0, replay.GetProperty("unresolvedChangedRecordPoses").GetInt32());
        byte[] accepted = File.ReadAllBytes(files.EvidencePath);
        Assert.Equal(0, (await Execute(files)).ExitCode);
        Assert.Equal(accepted, File.ReadAllBytes(files.EvidencePath));
    }

    [Theory]
    [InlineData("missing", 1, "MissingDenseRows")]
    [InlineData("gap", 1, "DenseSequence")]
    [InlineData("phase", 1, "PrecedingPhaseTick")]
    [InlineData("hole", 1, "ReplayHole")]
    [InlineData("selected", 1, "SelectedReplayFailure")]
    [InlineData("penetration", 1, "ChangedRecordPenetration")]
    [InlineData("unknown", 2, "UnknownMember")]
    [InlineData("fraction", 2, "InvalidValue")]
    public async Task Refusal_preserves_accepted_bytes_and_reports_public_reason_and_identifiers(string defect, int exit, string reason)
    {
        using var files = Files();
        Assert.Equal(0, Program.Main(Args(files, "build")));
        File.Delete(files.EvidencePath);
        Assert.Equal(0, (await Execute(files)).ExitCode);
        byte[] table = File.ReadAllBytes(files.TablePath), evidence = File.ReadAllBytes(files.EvidencePath);
        var original = files.Manifest.DeepClone();
        var rows = files.Manifest["domains"]![0]!["denseRows"]!.AsArray();
        switch (defect)
        {
            case "missing": rows.Clear(); break;
            case "gap": rows[2]!["denseIndex"] = 3; break;
            case "phase": rows[2]!["precedingPhaseTick"] = 1; break;
            case "hole": rows[2]!["resolvedStateKey"] = 999; break;
            case "selected": rows[2]!["resolvedStateKey"] = 2; break;
            case "penetration": rows[2]!["sample"]!["commands"]![0]!["transitionWeight"] = .25; break;
            case "unknown": rows[2]!["hiddenClamp"] = true; break;
            case "fraction": rows[2]!["precedingPhaseTick"] = .5; break;
        }
        files.Write();
        var result = await Execute(files);
        Assert.Equal(exit, result.ExitCode);
        if (exit == 1)
        {
            using var refusal = JsonDocument.Parse(result.Error);
            Assert.Equal(reason, refusal.RootElement.GetProperty("Reason").GetString());
            Assert.Equal("clip", refusal.RootElement.GetProperty("DomainId").GetString());
            if (defect != "missing") Assert.Equal(defect == "gap" ? 3 : 2, refusal.RootElement.GetProperty("DenseIndex").GetInt32());
            if (defect == "penetration")
            {
                Assert.Equal(23u, refusal.RootElement.GetProperty("CommandId").GetUInt32());
                Assert.Equal(17ul, refusal.RootElement.GetProperty("EquipmentInstanceId").GetUInt64());
            }
        }
        else
        {
            Assert.Equal(reason, result.Error.Split(' ')[0]);
            Assert.Equal("$.domains[0].denseRows[2]." + (defect == "unknown" ? "hiddenClamp" : "precedingPhaseTick"),
                result.Error.Split(' ')[1].Trim());
        }
        Assert.Equal(table, File.ReadAllBytes(files.TablePath));
        Assert.Equal(evidence, File.ReadAllBytes(files.EvidencePath));
        File.WriteAllText(files.ManifestPath, original.ToJsonString());
        Assert.Equal(0, (await Execute(files)).ExitCode);
    }

    [Theory]
    [InlineData(true, "SampleHole")]
    [InlineData(false, "SelectedSampleFailure")]
    public async Task Table_refusals_name_the_sample_and_preserve_the_exact_supplied_table(bool hole, string reason)
    {
        using var files = Files();
        Assert.Equal(0, Program.Main(Args(files, "build")));
        File.Delete(files.EvidencePath);
        Assert.Equal(0, (await Execute(files)).ExitCode);
        byte[] acceptedTable = File.ReadAllBytes(files.TablePath), acceptedEvidence = File.ReadAllBytes(files.EvidencePath);
        var table = CoverageRecessionTableBinary.Read(acceptedTable, "synthetic-accepted");
        var changed = CoverageRecessionTable.Create(table.Identity, table.Representatives,
            hole ? Enumerable.Range(0, table.EntryCount).SelectMany(i => table.Entry(i).ToArray()).ToArray()
                : new float[table.EntryCount * table.PositionGroupCount],
            table.States.ToArray().Select(s => hole && s.Key.Value == 1 ? s with { EntryIndex = -1 } : s).ToArray());
        byte[] supplied = CoverageRecessionTableBinary.Write(changed);
        File.WriteAllBytes(files.TablePath, supplied);
        var result = await Execute(files);
        Assert.Equal(1, result.ExitCode);
        using var refusal = JsonDocument.Parse(result.Error);
        Assert.Equal(reason, refusal.RootElement.GetProperty("Reason").GetString());
        Assert.Equal("clip", refusal.RootElement.GetProperty("DomainId").GetString());
        Assert.Equal("a", refusal.RootElement.GetProperty("SampleId").GetString());
        Assert.Equal(1ul, refusal.RootElement.GetProperty("StateKey").GetUInt64());
        Assert.Equal(supplied, File.ReadAllBytes(files.TablePath));
        Assert.Equal(acceptedEvidence, File.ReadAllBytes(files.EvidencePath));
        File.WriteAllBytes(files.TablePath, acceptedTable);
        Assert.Equal(0, (await Execute(files)).ExitCode);
    }

    [Fact]
    public void Dense_verification_uses_the_shared_evidence_bound_and_malformed_resume_path()
    {
        using var files = Files();
        Assert.Equal(0, Program.Main(Args(files, "build")));
        File.Delete(files.EvidencePath);
        Assert.Equal(0, Program.Main(Args(files, "verify")));
        byte[] table = File.ReadAllBytes(files.TablePath), accepted = File.ReadAllBytes(files.EvidencePath);
        Assert.Equal(2, Program.Run(Args(files, "verify"), accepted.Length - 1));
        Assert.Equal(accepted, File.ReadAllBytes(files.EvidencePath));
        File.WriteAllText(files.EvidencePath, "{");
        Assert.Equal(2, Program.Main(Args(files, "verify")));
        Assert.Equal(Encoding.UTF8.GetBytes("{"), File.ReadAllBytes(files.EvidencePath));
        Assert.Equal(table, File.ReadAllBytes(files.TablePath));
        File.WriteAllBytes(files.EvidencePath, accepted);
        Assert.Equal(0, Program.Run(Args(files, "verify"), accepted.Length));
    }

    [Fact]
    public async Task Verify_blocks_cross_command_intrusion_by_a_changed_triangle_without_overwriting_accepted_bytes()
    {
        using var files = new CoverageBuildFiles("task7");
        var fixture = new CoverageCrossCommandFixture();
        fixture.Write(files, crossing: false);
        var input = CoverageBuildInput.Read(files.ManifestPath);
        File.WriteAllBytes(files.TablePath, CoverageRecessionTableBinary.Write(CoverageCrossCommandFixture.Table(input)));
        Assert.Equal(0, (await Execute(files)).ExitCode);
        byte[] table = File.ReadAllBytes(files.TablePath), evidence = File.ReadAllBytes(files.EvidencePath);
        fixture.Write(files, crossing: true);
        var result = await Execute(files);
        Assert.Equal(1, result.ExitCode);
        using var refusal = JsonDocument.Parse(result.Error);
        var root = refusal.RootElement;
        Assert.Equal("ChangedRecordPenetration", root.GetProperty("Reason").GetString());
        Assert.Equal(CoverageCrossCommandFixture.Domain, root.GetProperty("DomainId").GetString());
        Assert.Equal(0, root.GetProperty("DenseIndex").GetInt32());
        Assert.Equal("current", root.GetProperty("SampleId").GetString());
        Assert.Equal(1ul, root.GetProperty("StateKey").GetUInt64());
        Assert.Equal(0, root.GetProperty("EntryIndex").GetInt32());
        Assert.Equal(CoverageCrossCommandFixture.BoxCommand, root.GetProperty("CommandId").GetUInt32());
        Assert.Equal(CoverageCrossCommandFixture.BoxEquipment, root.GetProperty("EquipmentInstanceId").GetUInt64());
        Assert.Equal(table, File.ReadAllBytes(files.TablePath));
        Assert.Equal(evidence, File.ReadAllBytes(files.EvidencePath));
        fixture.Write(files, crossing: false);
        Assert.Equal(0, (await Execute(files)).ExitCode);
    }

    internal static CoverageBuildFiles Files()
    {
        var files = new CoverageBuildFiles("task7");
        var rows = new JsonArray();
        for (int i = 0; i < 5; i++)
        {
            var sample = files.Manifest["samples"]![i == 4 ? 1 : 0]!.DeepClone();
            sample.AsObject().Remove("stateKey");
            rows.Add(new JsonObject
            {
                ["denseIndex"] = i, ["precedingPhaseTick"] = i / 4, ["resolvedStateKey"] = i == 4 ? 2 : 1,
                ["sample"] = sample, ["looped"] = false, ["clamped"] = false,
            });
        }
        files.Manifest["domains"]![0]!["denseRows"] = rows;
        files.Write();
        return files;
    }
    static string[] Args(CoverageBuildFiles files, string command) => [command, "--manifest", files.ManifestPath, "--table", files.TablePath, "--evidence", files.EvidencePath];
    static async Task<(int ExitCode, string Error)> Execute(CoverageBuildFiles files)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (string arg in Args(files, "verify")) start.ArgumentList.Add(arg);
        start.Environment["KE_GPU_TESTS"] = "0";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await stdout;
        return (process.ExitCode, await stderr);
    }
}
