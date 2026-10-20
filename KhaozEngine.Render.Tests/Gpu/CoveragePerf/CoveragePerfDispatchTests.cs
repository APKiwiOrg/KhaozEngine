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
            () => throw new InvalidOperationException("Workload lookup must not run.")));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("bytes")]
    [InlineData("workload")]
    public void Exact_file_bytes_and_actual_checkout_must_match_the_dispatch(string refusal)
    {
        using var files = new Files();
        var env = files.Environment();
        if (refusal == "missing") File.Delete(files.Manifest);
        if (refusal == "bytes") File.AppendAllText(files.Manifest, "\n");
        var error = Assert.Throws<InvalidDataException>(() => CoveragePerfDispatch.Load(files.Path,
            name => env.GetValueOrDefault(name), () => refusal == "workload" ? new string('f', 40) : files.Run.Identity.WorkloadSha));
        Assert.Contains(refusal == "missing" ? "missing" : refusal == "bytes" ? "SHA-256" : "workload SHA", error.Message, StringComparison.Ordinal);
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
        var request = CoveragePerfDispatch.Load(files.Path, name => env.GetValueOrDefault(name), () => files.Run.Identity.WorkloadSha);
        var identity = CoveragePerfDispatch.Identity(request, name => env.GetValueOrDefault(name));
        Assert.Equal(files.Run.Identity.WorkloadSha, identity.WorkloadSha);
        Assert.NotEqual(identity.WorkflowSha, identity.WorkloadSha);
        Assert.Equal(files.Run.Identity.Leg, identity.Leg);
        Assert.Equal(files.Run.Identity.Runner, identity.Runner);
        Assert.Equal(Environment.ProcessId, identity.ProcessId);
        Assert.Equal("123456789-1-" + Environment.ProcessId, identity.RunId);
    }

    [Theory]
    [InlineData("GITHUB_SHA", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("GITHUB_RUN_ID", "different-run")]
    [InlineData("GITHUB_RUN_ATTEMPT", "2")]
    [InlineData("RUNNER_OS", "Windows")]
    [InlineData("RUNNER_ENVIRONMENT", "self-hosted")]
    [InlineData("KE_GRAPHICS_BACKEND", "metal-native")]
    public void Approval_and_actual_route_disagreement_refuse(string key, string value)
    {
        using var files = new Files();
        var env = files.Environment();
        env[key] = value;
        var request = CoveragePerfDispatch.Load(files.Path, name => env.GetValueOrDefault(name), () => files.Run.Identity.WorkloadSha);
        Assert.Throws<InvalidDataException>(() => CoveragePerfDispatch.Identity(request, name => env.GetValueOrDefault(name)));
    }

    [Fact]
    public void Workload_reader_resolves_the_repository_and_reads_its_actual_HEAD()
    {
        string root = CoveragePerfDiscoveryTests.Root();
        Assert.Equal(root, CoveragePerfDispatch.RepositoryRoot(System.IO.Path.Combine(root, "tools")));
        Assert.Matches("^[0-9a-f]{40}$", CoveragePerfDispatch.WorkloadSha(root));
    }

    sealed class Files : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "khaozengine-ec-perf-8b", Guid.NewGuid().ToString("N"));
        internal string Manifest => System.IO.Path.Combine(Path, "inline-driver.json");
        internal CoveragePerfFakeRun Run { get; }
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
