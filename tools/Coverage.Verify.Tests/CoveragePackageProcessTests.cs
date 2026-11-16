using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.Render3D;
using KhaozEngine.Tools.Coverage;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Tools.Coverage;

/// <summary>The packaged executable contract (#1814). Ordinary tooling CI runs it against the source-built tool. A
/// candidate package runs the same assertions through an absolute `KE_COVERAGE_TOOL_EXECUTABLE`, with no skip.</summary>
public sealed class CoveragePackageProcessTests(ITestOutputHelper output) : IDisposable
{
    readonly CoverageCliScratch scratch = new();
    public void Dispose() => scratch.Dispose();

    static readonly string[] Commands = ["build", "verify-samples", "verify"];

    [Fact]
    public async Task Tool_package_contract_reads_both_manifest_versions_and_verifies_complete_sets()
    {
        output.WriteLine("Coverage executable under test: " + CoverageToolProcess.Selected);
        // Format 1 inline PanelTwo is the control an earlier package also accepts. Each command has its own evidence
        // path, and each small document stays one legacy file.
        using (var inline = CoverageVerifyProcessTests.Files())
            foreach (string command in Commands)
            {
                string evidence = Path.Combine(inline.Folder, command + ".json");
                Assert.Equal(0, (await Execute([command, "--manifest", inline.ManifestPath, "--table", inline.TablePath, "--evidence", evidence])).ExitCode);
                AssertLegacyEvidence(evidence);
            }
        // Format 2: the same campaign packed into KEPP chunks, through all three commands, each with a complete
        // schema 2 report.
        using var packed = CoveragePackedFixture.Files();
        CoveragePackedFixture.Pack(packed);
        foreach (string command in Commands)
        {
            string evidence = Path.Combine(packed.Folder, command + ".json");
            var result = await Execute(Args(packed, command, evidence));
            Assert.True(result.ExitCode == 0, $"{command} exited {result.ExitCode}: {result.Error}");
            AssertLegacyEvidence(evidence);
            Assert.Null(Report(packed, command).ResumedEvidenceSha256);
        }
        // The README's packed campaign, as a consumer reproduces it. It declares no dense rows, so it is built only.
        using (var documented = new CoverageBuildFiles(scratchDirectory: scratch.Root))
        {
            string readme = KhaozEngine.Tests.CoverageTooling.CoverageUnitSelectionTests.Checkout("tools/Coverage.Verify/README.md");
            CoveragePackedProcessTests.WriteDocumentedCampaign(documented, CoveragePackedProcessTests.DocumentedManifest(readme));
            Assert.Equal(0, (await Execute(Args(documented, "build", documented.EvidencePath))).ExitCode);
            AssertLegacyEvidence(documented.EvidencePath);
            Report(documented, "build");
        }
        // A multipart set keeps the executable's own provenance: its build document is republished with reduced parts.
        string root = Path.Combine(packed.Folder, "build.json");
        byte[] legacy = File.ReadAllBytes(root);
        IReadOnlyList<string> parts = Republish(root);
        Assert.True(parts.Count >= 2);
        string setRoot = CoverageArtifactSet.HashFile(root);
        // Resume reads every part before comparing records, so a substituted last part refuses and changes nothing.
        byte[] original = File.ReadAllBytes(parts[^1]);
        byte[] substituted = original.ToArray();
        substituted[^1] ^= 1;
        File.WriteAllBytes(parts[^1], substituted);
        var before = Snapshot(packed.Folder);
        var refused = await Execute(Args(packed, "build", root));
        Assert.Equal(2, refused.ExitCode);
        Assert.Equal(nameof(CoverageBuildInputRefusal.ArtifactPartHashMismatch), refused.Error.Split(' ')[0]);
        Assert.Equal(before, Snapshot(packed.Folder));
        // Control: the intact set resumes, names the root it read, and the same document is published as one file.
        File.WriteAllBytes(parts[^1], original);
        Assert.Equal(0, (await Execute(Args(packed, "build", root))).ExitCode);
        Assert.Equal(setRoot, Report(packed, "build").ResumedEvidenceSha256);
        Assert.Equal(legacy, File.ReadAllBytes(root));
        // The executable reports the exact engine it was built with and the unchanged carry arithmetic contract.
        var version = await Execute(["version"], standardOutput: true);
        Assert.Equal(0, version.ExitCode);
        using var json = JsonDocument.Parse(version.Output);
        Assert.Equal(typeof(CoverageRecessionTable).Assembly.GetName().Version!.ToString(3), json.RootElement.GetProperty("engineVersion").GetString());
        Assert.Equal(CoverageArithmeticContract.Version, json.RootElement.GetProperty("carryArithmeticContractVersion").GetInt32());
    }

    static string[] Args(CoverageBuildFiles files, string command, string evidence) =>
        [command, "--manifest", files.ManifestPath, "--table", files.TablePath, "--evidence", evidence, "--diagnostics", ReportPath(files, command)];

    static string ReportPath(CoverageBuildFiles files, string command) => Path.Combine(files.Folder, command + "-diagnostics.json");

    void AssertLegacyEvidence(string evidence)
    {
        Assert.False(Directory.Exists(evidence + ".parts"));
        using var read = CoverageArtifactReader.Open(evidence, scratch.Root);
        Assert.Equal(CoverageArtifactSet.HashFile(evidence), read.RootSha256);
        using var payload = read.OpenPayload();
        Assert.True(CoverageLogicalDocumentReader.Validate(payload, CoverageArtifactKind.Evidence).Header.Complete);
    }

    /// <summary>Reads an accepted report through the strict reader, checking its schema and every category.</summary>
    CoverageArtifactHeader Report(CoverageBuildFiles files, string command)
    {
        using var read = CoverageArtifactReader.Open(ReportPath(files, command), scratch.Root);
        using var payload = read.OpenPayload();
        var header = CoverageLogicalDocumentReader.Validate(payload, CoverageArtifactKind.Diagnostics).Header;
        Assert.Equal("khaozengine.coverage-diagnostics/2", header.PayloadSchema);
        Assert.Equal((command, "accepted"), (header.Command, header.Outcome));
        return header;
    }

    /// <summary>Republishes a complete legacy evidence root as a hashed set split at 4,096 bytes and returns its part
    /// paths in ordinal order.</summary>
    IReadOnlyList<string> Republish(string root)
    {
        string logical = Path.Combine(scratch.Root, "logical.json");
        File.Copy(root, logical);
        CoverageArtifactHeader header;
        using (var input = File.OpenRead(logical)) header = CoverageLogicalDocumentReader.Validate(input, CoverageArtifactKind.Evidence).Header;
        using var staged = CoverageArtifactWriter.Stage(root, header, s => { using var source = File.OpenRead(logical); source.CopyTo(s); },
            scratch.Root, 4096, 4096);
        CoveragePublisher.Publish([], [staged], []);
        File.Delete(logical);
        return staged.Parts.OrderBy(p => p.Ordinal).Select(p => p.DestinationPath).ToArray();
    }

    static Dictionary<string, string> Snapshot(string folder) => Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
        .ToDictionary(p => Path.GetRelativePath(folder, p), CoverageArtifactSet.HashFile);

    async Task<(int ExitCode, string Error, string Output)> Execute(string[] args, bool standardOutput = false)
    {
        var start = CoverageToolProcess.Create(args);
        // Session and staging scratch of the child stay under this test's private temporary root.
        foreach (string name in OperatingSystem.IsWindows() ? new[] { "TEMP", "TMP" } : ["TMPDIR"]) start.Environment[name] = scratch.Root;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string text = await stdout;
        return (process.ExitCode, await stderr, standardOutput ? text : "");
    }
}
