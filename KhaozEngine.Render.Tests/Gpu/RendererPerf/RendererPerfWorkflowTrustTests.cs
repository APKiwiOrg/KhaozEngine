using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfWorkflowTrustTests
{
    [Fact]
    public void Renderer_measurement_environment_opts_out_of_cli_telemetry()
    {
        string environment = RendererJob().Split("    env:\n", 2)[1].Split("    steps:\n", 2)[0];
        Assert.Equal("1", Setting(environment, "DOTNET_CLI_TELEMETRY_OPTOUT"));
    }

    [Fact]
    public void Renderer_job_installs_an_exact_sdk_version()
    {
        string[] installs = RendererJob().Split("      - ")
            .Where(step => step.StartsWith("name: Install isolated renderer SDK", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, installs.Length);
        foreach (string install in installs)
        {
            string? url = Setting(install, "DOTNET_URL");
            Assert.NotNull(url);
            string version = Regex.Match(url, @"/Sdk/([^/]+)/").Groups[1].Value;
            Assert.Matches(@"^[0-9]+\.[0-9]+\.[0-9]+$", version);
        }
    }

    static string RendererJob() => RendererPerfWorkflowText.Read().Job("renderer-bench");

    static string? Setting(string block, string name) => block.Split('\n')
        .Select(line => line.Trim().Split(':', 2))
        .SingleOrDefault(pair => pair.Length == 2 && pair[0] == name)?[1].Trim().Trim('\'', '"');

    [Theory]
    [InlineData("pilot", "linux-vulkan", "gpu-t4-linux", 12)]
    [InlineData("full", "linux-vulkan", "gpu-t4-linux", 35)]
    [InlineData("full", "windows-vulkan", "gpu-t4-windows", 50)]
    [InlineData("full", "windows-d3d11", "gpu-t4-windows", 50)]
    [InlineData("pilot", "windows-d3d11", "gpu-t4-windows", 20)]
    [InlineData("water-optics-v2", "linux-vulkan", "gpu-t4-linux", 35)]
    [InlineData("water-optics-v2", "windows-d3d11", "gpu-t4-windows", 50)]
    public void Requested_admission_and_selector_cannot_change_allocation(string preset, string leg, string runner, int minutes)
    {
        using var workspace = new AdmissionWorkspace();
        var fixture = workspace.Fixture;
        fixture.Manifest = RendererPerfColdLimitPolicyTests.ManifestJson(preset, leg);
        fixture.Manifest["workloadSha"] = fixture.Workload;
        RendererPerfRehearsalManifestTests.Parse(fixture.Manifest);
        fixture.Environment["LEGS"] = leg;
        fixture.SaveManifest();
        fixture.Git("add", "--", "tools/Renderer.Perf.Tests/authorized/pilot-linux.json");
        fixture.Git("commit", "--amend", "--no-edit");
        fixture.Environment["TESTED_REF"] = fixture.Git("rev-parse", "HEAD").Trim();

        var result = workspace.Select();
        Assert.True(result.Code == 0, result.Output);
        var matrix = JsonNode.Parse(File.ReadAllLines(workspace.Output).Single(s => s.StartsWith("matrix=", StringComparison.Ordinal))[7..])!;
        var row = Assert.Single(matrix["include"]!.AsArray());
        Assert.Equal(runner, row!["runner"]!.GetValue<string>());
        Assert.Equal(leg, row["leg"]!.GetValue<string>());
        Assert.Equal(minutes, row["jobMinutes"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Partial_renderer_inputs_cannot_inject_ordinary_or_coverage_matrix(bool coverage)
    {
        using var workspace = new AdmissionWorkspace();
        workspace.Fixture.Environment["RENDERER_PERF_UNIT"] = "";
        if (coverage) workspace.Fixture.Environment["COVERAGE_PERF_UNIT"] = "coverage-unit";
        var result = workspace.Select();
        Assert.NotEqual(0, result.Code);
        Assert.False(File.Exists(workspace.Output) && File.ReadAllText(workspace.Output).Contains("matrix=", StringComparison.Ordinal));
    }

    internal sealed class AdmissionWorkspace : IDisposable
    {
        readonly string _root = Path.Combine(Path.GetTempPath(), "khaozengine-renderer-perf-task-13-fix-r1", Guid.NewGuid().ToString("N"));
        internal readonly RendererPerfWorkflowContractTests.WorkflowFixture Fixture;
        internal string Output => Path.Combine(_root, "output");
        readonly string _selector;

        internal AdmissionWorkspace([CallerFilePath] string source = "")
        {
            string repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../.."));
            string legs = RendererPerfWorkflowText.Read().Job("legs").Split("\n  bench:", 2)[0];
            string requested = _root;
            foreach (string step in legs.Split("      - ").Where(s => s.Contains("uses: actions/checkout@", StringComparison.Ordinal)))
            {
                string? path = step.Split('\n').FirstOrDefault(s => s.StartsWith("          path:", StringComparison.Ordinal))?.Split(':', 2)[1].Trim();
                string destination = path is null ? _root : Path.Combine(_root, path);
                if (step.Contains("ref: ${{ inputs.ref }}", StringComparison.Ordinal)) requested = destination;
                else
                {
                    Assert.Contains("ref: ${{ github.workflow_sha }}", step, StringComparison.Ordinal);
                    Directory.CreateDirectory(Path.Combine(destination, "scripts"));
                    File.Copy(Path.Combine(repo, "scripts/renderer-perf-workflow.ps1"), Path.Combine(destination, "scripts/renderer-perf-workflow.ps1"));
                }
            }
            Fixture = new(requested, hostileAdmission: true);
            Fixture.Environment["GITHUB_WORKSPACE"] = _root;
            Fixture.Environment["GITHUB_OUTPUT"] = Output;
            string run = legs.Split("        run: |\n", 2)[1];
            _selector = string.Join('\n', run.TrimEnd().Split('\n').Select(s => s.Length >= 10 ? s[10..] : s));
        }
        internal (int Code, string Output) Select()
        {
            string shell = OperatingSystem.IsWindows()
                ? RendererPerfWorkflowShell.WindowsBash(Fixture.Git("--exec-path").Trim()) : "bash";
            return Fixture.Execute(shell, ["-e", "-o", "pipefail", "-c", _selector]);
        }
        public void Dispose()
        {
            Fixture.Dispose();
            if (Directory.Exists(_root)) RendererPerfFixtureDirectory.Delete(_root);
        }
    }
}
