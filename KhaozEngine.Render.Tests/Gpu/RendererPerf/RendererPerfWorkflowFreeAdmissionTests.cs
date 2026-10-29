using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

[Collection("NativeDeviceLifecycle")]
public sealed class RendererPerfWorkflowFreeAdmissionTests
{
    [Theory]
    [InlineData("recipes", "Recipe list is incomplete.")]
    [InlineData("blocks", "Expanded block list is incomplete.")]
    [InlineData("controls", "Control list is incomplete.")]
    [InlineData("unitSeconds", "deadline differs from the preset.")]
    [InlineData("frameMilliseconds", "deadline differs from the preset.")]
    [InlineData("batchSize", "Pacing, scope capacity or conditioning count")]
    [InlineData("baselineMainSha", "Expected a lowercase 40-digit hexadecimal hash.")]
    [InlineData("configuration", "Explicit Release runtime")]
    [InlineData("layout", "RendererPerfProtocolException")]
    [InlineData("bom", "RendererPerfProtocolException")]
    public void Managed_refusal_stops_the_free_job_before_matrix_output(string fault, string reason)
    {
        using var workspace = new RendererPerfWorkflowTrustTests.AdmissionWorkspace();
        var fixture = workspace.Fixture;
        Prepare(fixture, "water-optics-v2", "linux-vulkan");
        switch (fault)
        {
            case "recipes": case "blocks": case "controls": fixture.Manifest[fault] = new JsonArray(); break;
            case "unitSeconds": case "frameMilliseconds": fixture.Manifest["limits"]![fault] = 0; break;
            case "batchSize": fixture.Manifest[fault] = 0; break;
            case "baselineMainSha": fixture.Manifest[fault] = "x"; break;
            case "configuration": fixture.Manifest["runtime"]![fault] = "Debug"; break;
            case "layout": fixture.Manifest["blocks"]![0]![fault] = "bogus"; break;
        }
        fixture.SaveManifest();
        if (fault == "bom")
        {
            File.WriteAllBytes(fixture.ManifestPath, [0xef, 0xbb, 0xbf, .. File.ReadAllBytes(fixture.ManifestPath)]);
            fixture.Environment["RENDERER_PERF_MANIFEST_SHA256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fixture.ManifestPath)));
        }
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfManifest.Parse(File.ReadAllBytes(fixture.ManifestPath)));
        var result = workspace.Select();
        Assert.True(result.Code != 0, result.Output);
        Assert.Contains(reason, result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(workspace.Output) && File.ReadAllText(workspace.Output).Contains("matrix=", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(RendererPerfColdLimitPolicyTests.Classes), MemberType = typeof(RendererPerfColdLimitPolicyTests))]
    public void Trusted_command_and_free_job_admit_every_valid_execution_class(string preset, string leg)
    {
        using var workspace = new RendererPerfWorkflowTrustTests.AdmissionWorkspace();
        var fixture = workspace.Fixture;
        Prepare(fixture, preset, leg);
        fixture.Accept("Authorize", "authorizationSha");
        var result = Admit(fixture);
        Assert.True(result.Code == 0, result.Output);
        Assert.Contains("Admitted renderer manifest with no GPU providers registered.", result.Output, StringComparison.Ordinal);
        result = workspace.Select();
        Assert.True(result.Code == 0, result.Output);
        Assert.Contains("matrix=", File.ReadAllText(workspace.Output), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("manifestSha256")]
    [InlineData("workloadSha")]
    [InlineData("authorizationSha")]
    [InlineData("parentSha")]
    [InlineData("parentCount")]
    [InlineData("changedPaths")]
    [InlineData("runAttempt")]
    [InlineData("leg")]
    [InlineData("runner")]
    [InlineData("workflow")]
    [InlineData("manifestChangeKind")]
    [InlineData("previousRunId")]
    public void Command_refuses_authorization_facts_that_disagree_with_git_or_dispatch(string field)
    {
        using var fixture = new RendererPerfWorkflowContractTests.WorkflowFixture();
        fixture.Accept("Authorize", "authorizationSha");
        string path = Path.Combine(fixture.Reports, "authorization.json");
        var facts = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        facts[field] = field switch
        {
            "parentCount" or "runAttempt" => JsonValue.Create(2),
            "changedPaths" => new JsonArray("extra.txt"),
            _ => JsonValue.Create("disagreement")
        };
        File.WriteAllText(path, facts.ToJsonString());
        var result = Admit(fixture);
        Assert.True(result.Code == 1, result.Output);
        Assert.Contains(field == "runner" ? "Dispatch leg, runner or workflow differs" : "Authorization facts disagree", result.Output, StringComparison.Ordinal);
    }

    // A workload .gitmodules with ignore = all hides a gitlink from a plain diff, so every authorization
    // diff must ignore the requested checkout's submodule policy.
    [Fact]
    public void Authorization_refuses_a_gitlink_hidden_by_workload_submodule_policy()
    {
        using var workspace = new RendererPerfWorkflowTrustTests.AdmissionWorkspace();
        var fixture = workspace.Fixture;
        Prepare(fixture, "water-optics-v2", "linux-vulkan");
        fixture.Git("reset", "--quiet", "--hard", fixture.Workload);
        File.WriteAllText(Path.Combine(fixture.Directory, ".gitmodules"),
            "[submodule \"vendor/admission-hidden\"]\n\tpath = vendor/admission-hidden\n\turl = ./missing\n\tignore = all\n");
        fixture.Git("add", "--", ".gitmodules");
        fixture.Git("commit", "--quiet", "-m", "test(renderer-perf): workload hiding a submodule");
        string workload = fixture.Git("rev-parse", "HEAD").Trim();
        fixture.Manifest["workloadSha"] = workload;
        fixture.Environment["RENDERER_PERF_WORKLOAD_SHA"] = workload;
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.ManifestPath)!);
        fixture.SaveManifest();
        fixture.Git("add", "--", "tools/Renderer.Perf.Tests/authorized/pilot-linux.json");
        fixture.Git("commit", "--quiet", "-m", "test(renderer-perf): synthetic authorization");
        fixture.Environment["TESTED_REF"] = fixture.Git("rev-parse", "HEAD").Trim();
        fixture.Accept("Authorize", "authorizationSha");
        fixture.Git("update-index", "--add", "--cacheinfo", "160000," + fixture.Workload + ",vendor/admission-hidden");
        fixture.Git("commit", "--quiet", "--amend", "--no-edit");
        string authorization = fixture.Git("rev-parse", "HEAD").Trim();
        fixture.Environment["TESTED_REF"] = authorization;
        string facts = Path.Combine(fixture.Reports, "authorization.json");
        var supplied = JsonNode.Parse(File.ReadAllBytes(facts))!.AsObject();
        supplied["authorizationSha"] = authorization;
        File.WriteAllText(facts, supplied.ToJsonString());
        Assert.Contains("vendor/admission-hidden", fixture.Git("diff", "--name-status", "--no-renames", "--ignore-submodules=none", workload, "HEAD"), StringComparison.Ordinal);

        var authorize = fixture.Run("Authorize");
        Assert.True(authorize.Code != 0, authorize.Output);
        Assert.Contains("Authorization must add only", authorize.Output, StringComparison.Ordinal);
        var admit = Admit(fixture);
        Assert.True(admit.Code == 1, admit.Output);
        Assert.Contains("Authorization must add only the manifest to its immutable workload parent.", admit.Output, StringComparison.Ordinal);
        var select = workspace.Select();
        Assert.True(select.Code != 0, select.Output);
        Assert.False(File.Exists(workspace.Output) && File.ReadAllText(workspace.Output).Contains("matrix=", StringComparison.Ordinal));
    }

    [Fact]
    public void Matrix_depends_on_trusted_free_admission_success()
    {
        string legs = RendererPerfWorkflowText.Read().Job("legs").Split("\n  bench:", 2)[0];
        string[] steps = legs.Split("      - ");
        string build = Assert.Single(steps, s => s.StartsWith("name: Build trusted renderer admission verifier\n", StringComparison.Ordinal));
        Assert.Contains("working-directory: renderer-perf-trusted", build, StringComparison.Ordinal);
        Assert.Contains("dotnet build tools/Renderer.Perf.Verify/Renderer.Perf.Verify.csproj -c Release", build, StringComparison.Ordinal);
        string admission = Assert.Single(steps, s => s.StartsWith("id: renderer-admission\n", StringComparison.Ordinal));
        Assert.Contains("renderer-perf-trusted/tools/Renderer.Perf.Verify/bin/Release/net10.0/Renderer.Perf.Verify.dll", admission, StringComparison.Ordinal);
        Assert.Contains("admit", admission, StringComparison.Ordinal);
        Assert.Contains("set -euo pipefail", admission, StringComparison.Ordinal);
        string pick = Assert.Single(steps, s => s.StartsWith("id: pick\n", StringComparison.Ordinal));
        Assert.Contains("steps.renderer-admission.outcome == 'success'", pick, StringComparison.Ordinal);
        Assert.True(legs.IndexOf(build, StringComparison.Ordinal) < legs.IndexOf(admission, StringComparison.Ordinal));
        Assert.True(legs.IndexOf(admission, StringComparison.Ordinal) < legs.IndexOf(pick, StringComparison.Ordinal));
    }

    static void Prepare(RendererPerfWorkflowContractTests.WorkflowFixture fixture, string preset, string leg)
    {
        fixture.Manifest = RendererPerfColdLimitPolicyTests.ManifestJson(preset, leg);
        fixture.Manifest["workloadSha"] = fixture.Workload;
        fixture.Environment["LEGS"] = leg;
        RendererPerfManifest.Parse(Encoding.UTF8.GetBytes(fixture.Manifest.ToJsonString()));
        fixture.SaveManifest();
    }

    static (int Code, string Output) Admit(RendererPerfWorkflowContractTests.WorkflowFixture fixture, [CallerFilePath] string source = "")
    {
        string repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../.."));
        return fixture.Execute("dotnet", [Path.Combine(repo, "tools/Renderer.Perf.Verify/bin/Release/net10.0/Renderer.Perf.Verify.dll"),
            "admit", Path.GetFullPath(fixture.Git("rev-parse", "--show-toplevel").Trim()), fixture.Reports]);
    }
}
