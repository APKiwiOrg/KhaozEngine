using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoveragePerfDispatchTests
{
    [Theory]
    [InlineData("KE_COVERAGE_PERF_UNIT", "../inline-driver")]
    [InlineData("KE_COVERAGE_PERF_UNIT", "INLINE")]
    [InlineData("KE_COVERAGE_PERF_UNIT", "inline-driver\n")]
    [InlineData("KE_COVERAGE_PERF_MANIFEST_SHA256", "ABCDEF")]
    [InlineData("KE_COVERAGE_PERF_WORKLOAD_SHA", "ABCDEF")]
    [InlineData("KE_COVERAGE_PERF_WORKLOAD_SHA", "")]
    public void Malformed_dispatch_refuses_before_reading_workload_identity(string key, string value)
    {
        using var files = new Files();
        var env = files.Environment();
        env[key] = value;
        Assert.Throws<InvalidDataException>(() => CoveragePerfDispatch.Load(files.Path, name => env.GetValueOrDefault(name),
            (_, _) => throw new InvalidOperationException("Authorization lookup must not run.")));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("bytes")]
    [InlineData("workload")]
    [InlineData("authorization")]
    [InlineData("malformed-authorization")]
    public void Exact_file_bytes_and_actual_checkout_must_match_the_dispatch(string refusal)
    {
        using var files = new Files();
        var env = files.Environment();
        if (refusal == "missing") File.Delete(files.Manifest);
        if (refusal == "bytes") File.AppendAllText(files.Manifest, "\n");
        if (refusal == "workload") env["KE_COVERAGE_PERF_WORKLOAD_SHA"] = new string('f', 40);
        var error = Assert.Throws<InvalidDataException>(() => CoveragePerfDispatch.Load(files.Path, name => env.GetValueOrDefault(name),
            (_, _) => refusal switch
            {
                "authorization" => throw new InvalidDataException("Authorization commit's parent differs from KE_COVERAGE_PERF_WORKLOAD_SHA."),
                "malformed-authorization" => "HEAD",
                _ => Files.Authorization,
            }));
        Assert.Contains(refusal switch
        {
            "missing" => "missing",
            "bytes" => "SHA-256",
            "workload" => "Manifest workloadSha differs",
            "authorization" => "parent differs",
            _ => "authorization commit SHA",
        }, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("C0", "Linux", "vulkan-native", "")]
    [InlineData("G1", "Linux", "vulkan-native", "linux-vulkan")]
    [InlineData("G2", "Windows", "direct3d11-native", "windows-d3d11")]
    [InlineData("G3", "Windows", "vulkan-native", "windows-vulkan")]
    public void Identity_uses_verified_workload_and_workflow_runner_and_leg_facts(string preset, string os, string backend, string leg)
    {
        using var files = new Files(preset);
        var env = files.Environment();
        env["RUNNER_OS"] = os;
        env["KE_GRAPHICS_BACKEND"] = backend;
        env["KE_COVERAGE_PERF_LEG"] = leg;
        var request = CoveragePerfDispatch.Load(files.Path, name => env.GetValueOrDefault(name), (_, _) => Files.Authorization);
        var identity = CoveragePerfDispatch.Identity(request, name => env.GetValueOrDefault(name));
        Assert.Equal(files.Run.Identity.WorkloadSha, identity.WorkloadSha);
        Assert.Equal(Files.Authorization, identity.AuthorizationCommitSha);
        Assert.NotEqual(identity.WorkflowSha, identity.WorkloadSha);
        Assert.Equal(files.Run.Identity.Leg, identity.Leg);
        Assert.Equal(files.Run.Identity.Runner, identity.Runner);
        Assert.Equal(Environment.ProcessId, identity.ProcessId);
        Assert.Equal("123456789-1-" + Environment.ProcessId, identity.RunId);
    }

    // The run's facts exist only at dispatch. They are recorded beside the workload and never matched to the manifest.
    [Theory]
    [InlineData("1", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("987654321", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData("9223372036854775807", "1111111111111111111111111111111111111111")]
    public void Any_first_attempt_run_is_recorded_without_binding_the_manifest(string runId, string workflowSha)
    {
        using var files = new Files();
        var env = files.Environment();
        env["GITHUB_RUN_ID"] = runId;
        env["GITHUB_SHA"] = workflowSha;
        var request = CoveragePerfDispatch.Load(files.Path, name => env.GetValueOrDefault(name), (_, _) => Files.Authorization);
        CoveragePerfRunIdentity? identity = null;
        Assert.Null(Record.Exception(() => identity = CoveragePerfDispatch.Identity(request, name => env.GetValueOrDefault(name))));
        Assert.Equal(workflowSha, identity!.WorkflowSha);
        Assert.Equal(runId, identity.WorkflowRunId);
        Assert.Equal(1, identity.WorkflowRunAttempt);
        Assert.Equal(Environment.ProcessId, identity.ProcessId);
        Assert.Equal(runId + "-1-" + Environment.ProcessId, identity.RunId);
    }

    [Theory]
    [InlineData("GITHUB_RUN_ATTEMPT", "2", "GITHUB_RUN_ATTEMPT must be 1, a GitHub re-run cannot reuse an approval")]
    [InlineData("GITHUB_RUN_ATTEMPT", "3", "GITHUB_RUN_ATTEMPT must be 1, a GitHub re-run cannot reuse an approval")]
    [InlineData("GITHUB_RUN_ATTEMPT", null, "GITHUB_RUN_ATTEMPT is required")]
    [InlineData("GITHUB_RUN_ATTEMPT", "", "GITHUB_RUN_ATTEMPT is required")]
    [InlineData("GITHUB_RUN_ATTEMPT", "0", "GITHUB_RUN_ATTEMPT has an invalid identity format")]
    [InlineData("GITHUB_RUN_ATTEMPT", "01", "GITHUB_RUN_ATTEMPT has an invalid identity format")]
    [InlineData("GITHUB_RUN_ATTEMPT", "+1", "GITHUB_RUN_ATTEMPT has an invalid identity format")]
    [InlineData("GITHUB_RUN_ATTEMPT", " 1", "GITHUB_RUN_ATTEMPT has an invalid identity format")]
    [InlineData("GITHUB_RUN_ATTEMPT", "1.0", "GITHUB_RUN_ATTEMPT has an invalid identity format")]
    [InlineData("GITHUB_RUN_ATTEMPT", "one", "GITHUB_RUN_ATTEMPT has an invalid identity format")]
    [InlineData("GITHUB_RUN_ID", null, "GITHUB_RUN_ID is required")]
    [InlineData("GITHUB_RUN_ID", "", "GITHUB_RUN_ID is required")]
    [InlineData("GITHUB_RUN_ID", "0", "GITHUB_RUN_ID has an invalid identity format")]
    [InlineData("GITHUB_RUN_ID", "0123", "GITHUB_RUN_ID has an invalid identity format")]
    [InlineData("GITHUB_RUN_ID", "-5", "GITHUB_RUN_ID has an invalid identity format")]
    [InlineData("GITHUB_RUN_ID", "different-run", "GITHUB_RUN_ID has an invalid identity format")]
    [InlineData("GITHUB_RUN_ID", "123456789\n", "GITHUB_RUN_ID has an invalid identity format")]
    [InlineData("GITHUB_RUN_ID", "99999999999999999999", "GITHUB_RUN_ID has an invalid identity format")]
    public void Only_the_first_attempt_of_a_well_formed_run_is_admitted(string key, string? value, string reason)
    {
        using var files = new Files();
        var env = files.Environment();
        if (value is null) env.Remove(key);
        else env[key] = value;
        var request = CoveragePerfDispatch.Load(files.Path, name => env.GetValueOrDefault(name), (_, _) => Files.Authorization);
        var error = Assert.Throws<InvalidDataException>(() => CoveragePerfDispatch.Identity(request, name => env.GetValueOrDefault(name)));
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("RUNNER_OS", "Windows")]
    [InlineData("RUNNER_ENVIRONMENT", "self-hosted")]
    [InlineData("KE_GRAPHICS_BACKEND", "metal-native")]
    public void Approval_and_actual_route_disagreement_refuse(string key, string value)
    {
        using var files = new Files();
        var env = files.Environment();
        env[key] = value;
        var request = CoveragePerfDispatch.Load(files.Path, name => env.GetValueOrDefault(name), (_, _) => Files.Authorization);
        Assert.Throws<InvalidDataException>(() => CoveragePerfDispatch.Identity(request, name => env.GetValueOrDefault(name)));
    }

    [Fact]
    public void Repository_root_resolves_from_the_test_location_and_git_reads_its_actual_HEAD()
    {
        string root = CoveragePerfDiscoveryTests.Root();
        Assert.Equal(root, CoveragePerfDispatch.RepositoryRoot(System.IO.Path.Combine(root, "tools")));
        Assert.Matches("^[0-9a-f]{40}\n$", CoveragePerfAuthorizationCommit.Git(root, "rev-parse", "--verify", "HEAD"));
    }

    sealed class Files : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "khaozengine-ec-perf-8b", Guid.NewGuid().ToString("N"));
        internal string Manifest => System.IO.Path.Combine(Path, "inline-driver.json");
        internal CoveragePerfFakeRun Run { get; }
        internal static readonly string Authorization = new('b', 40);
        internal Files(string preset = "C0")
        {
            Run = new(preset);
            Directory.CreateDirectory(Path);
            File.WriteAllBytes(Manifest, Run.Json);
        }
        internal Dictionary<string, string> Environment() => new()
        {
            ["KE_COVERAGE_PERF_UNIT"] = Run.Identity.UnitId,
            ["KE_COVERAGE_PERF_MANIFEST_SHA256"] = Run.Identity.ManifestSha256,
            ["KE_COVERAGE_PERF_WORKLOAD_SHA"] = Run.Identity.WorkloadSha,
            ["GITHUB_SHA"] = Run.Identity.WorkflowSha,
            ["GITHUB_RUN_ID"] = Run.Identity.WorkflowRunId,
            ["GITHUB_RUN_ATTEMPT"] = "1",
            ["GITHUB_WORKFLOW"] = Run.Identity.Workflow,
            ["RUNNER_OS"] = "Linux",
            ["RUNNER_ENVIRONMENT"] = "github-hosted",
            ["KE_GRAPHICS_BACKEND"] = "vulkan-native",
        };
        public void Dispose() => Directory.Delete(Path, true);
    }
}
