using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

[Collection("NativeDeviceLifecycle")]
public sealed class RendererPerfWorkflowContractTests
{
    [Fact]
    public void Pwsh_children_use_fixture_owned_caches_outside_the_repository()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return;
        string cache;
        string otherCache;
        using (var fixture = new WorkflowFixture())
        using (var other = new WorkflowFixture())
        {
            string inheritedCache = Path.Combine(fixture.Directory, "shared-cache");
            fixture.Environment["XDG_CACHE_HOME"] = inheritedCache;
            other.Environment["XDG_CACHE_HOME"] = inheritedCache;
            var result = fixture.Execute("pwsh", ["-NoProfile", "-Command", "$env:XDG_CACHE_HOME"]);
            Assert.True(result.Code == 0, result.Output);
            cache = result.Output.Trim();
            Assert.NotEqual(inheritedCache, cache);
            Assert.True(System.IO.Directory.Exists(cache));
            Assert.StartsWith(".." + Path.DirectorySeparatorChar, Path.GetRelativePath(fixture.Directory, cache), StringComparison.Ordinal);
            Assert.Empty(fixture.Git("status", "--porcelain"));
            var otherResult = other.Execute("pwsh", ["-NoProfile", "-Command", "$env:XDG_CACHE_HOME"]);
            Assert.True(otherResult.Code == 0, otherResult.Output);
            otherCache = otherResult.Output.Trim();
            Assert.NotEqual(cache, otherCache);
            Assert.True(System.IO.Directory.Exists(otherCache));
        }
        Assert.False(System.IO.Directory.Exists(cache));
        Assert.False(System.IO.Directory.Exists(otherCache));
    }

    [Fact]
    public void Refuses_combined_modes_all_legs_and_freeform_filter()
    {
        using var fixture = new WorkflowFixture();
        fixture.Accept("Select", "jobMinutes=12");
        fixture.Refuse("Select", "COVERAGE_PERF_UNIT", "another-unit");
        fixture.Refuse("Select", "COVERAGE_PERF_MANIFEST_SHA256", new string('a', 64));
        fixture.Refuse("Select", "LEGS", "all");
        fixture.Refuse("Select", "LEGS", "unknown");
        fixture.Refuse("Select", "FILTER", "Category=RendererPerformance");
        fixture.Refuse("Select", "WORKFLOW_RUN_ATTEMPT", "2");
        fixture.Refuse("Select", "TESTED_REF", "main");
        fixture.Refuse("Select", "RENDERER_PERF_UNIT", "");
        fixture.Refuse("Select", "RENDERER_PERF_UNIT", "../escape");
        fixture.Manifest = RendererPerfProtocolTests.ManifestJson("full");
        fixture.Manifest["workloadSha"] = fixture.Workload;
        RendererPerfRehearsalManifestTests.Parse(fixture.Manifest);
        fixture.SaveManifest();
        fixture.Accept("Select", "jobMinutes=35");
        fixture.Manifest["limits"]!["jobMinutes"] = 60;
        fixture.SaveManifest();
        Assert.NotEqual(0, fixture.Run("Select").Code);
    }

    [Fact]
    public void Authorization_adds_only_renderer_manifest_to_workload_parent()
    {
        using var fixture = new WorkflowFixture();
        fixture.Accept("Authorize", "authorizationSha");
        fixture.Refuse("Authorize", "RENDERER_PERF_MANIFEST_SHA256", new string('b', 64));
        fixture.Refuse("Authorize", "RENDERER_PERF_WORKLOAD_SHA", new string('a', 40));
        fixture.Refuse("Authorize", "TESTED_REF", fixture.Workload);
        File.WriteAllText(Path.Combine(fixture.Directory, "extra.txt"), "unauthorized addition");
        fixture.Git("add", "--", "extra.txt");
        fixture.Git("commit", "--amend", "--no-edit", "--", "extra.txt");
        fixture.Environment["TESTED_REF"] = fixture.Git("rev-parse", "HEAD").Trim();
        Assert.NotEqual(0, fixture.Run("Authorize").Code);
    }

    [Fact]
    public void Requires_one_executed_test_and_final_matching_report()
    {
        using var fixture = new WorkflowFixture();
        fixture.WriteEvidence();
        fixture.Accept("Verify", "evidence accepted");
        foreach (string counters in new[] { "2/2/2/0/0", "1/0/0/0/1", "1/1/0/1/0" })
        {
            fixture.WriteEvidence(counters);
            Assert.NotEqual(0, fixture.Run("Verify").Code);
        }
        fixture.WriteEvidence();
        File.Delete(Path.Combine(fixture.Reports, "measurement.trx"));
        Assert.NotEqual(0, fixture.Run("Verify").Code);
        fixture.WriteEvidence();
        File.Delete(Path.Combine(fixture.Reports, "pilot-linux.json"));
        Assert.NotEqual(0, fixture.Run("Verify").Code);
        foreach (string field in new[] { "manifestSha256", "workloadSha", "authorizationSha", "workflowSha", "runId" })
        {
            fixture.WriteEvidence();
            fixture.Report["identity"]![field] = "mismatch";
            fixture.SaveReport();
            Assert.NotEqual(0, fixture.Run("Verify").Code);
        }
        fixture.WriteEvidence();
        fixture.Report["publication"]!["finalUnitPublished"] = false;
        fixture.SaveReport();
        Assert.NotEqual(0, fixture.Run("Verify").Code);
        fixture.WriteEvidence();
        fixture.Report["identity"]!["softwareAdapter"] = true;
        fixture.SaveReport();
        Assert.NotEqual(0, fixture.Run("Verify").Code);
    }

    internal sealed class WorkflowFixture : IDisposable
    {
        readonly RendererPerfPowerShellCache _powerShellCache = new();
        internal readonly string Directory;
        internal string Reports => Path.Combine(Directory, "reports");
        internal string ManifestPath => Path.Combine(Directory, "tools/Renderer.Perf.Tests/authorized/pilot-linux.json");
        internal JsonObject Manifest = RendererPerfProtocolTests.ManifestJson();
        internal JsonObject Report = null!;
        internal readonly Dictionary<string, string> Environment = new(StringComparer.Ordinal);
        internal readonly string Workload;

        internal WorkflowFixture(string? directory = null, bool hostileAdmission = false)
        {
            Directory = directory ?? Path.Combine(Path.GetTempPath(), "khaozengine-renderer-perf-task-13", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
            System.IO.Directory.CreateDirectory(Reports);
            Git("init", "--quiet");
            Git("config", "user.name", "Contract test");
            Git("config", "user.email", "contract@example.invalid");
            Git("config", "commit.gpgsign", "false");
            Git("config", "core.hooksPath", Path.Combine(Directory, "no-hooks"));
            File.WriteAllText(Path.Combine(Directory, "workload.txt"), "synthetic workload, never executed");
            if (hostileAdmission)
            {
                System.IO.Directory.CreateDirectory(Path.Combine(Directory, "scripts"));
                System.IO.Directory.CreateDirectory(Path.Combine(Directory, ".github/workflows"));
                File.WriteAllText(Path.Combine(Directory, "scripts/renderer-perf-workflow.ps1"), """
                    param([string]$Mode)
                    if ($Mode -eq 'Select') {
                        'matrix={"include":[{"leg":"linux-vulkan","runner":"self-hosted","backend":"vulkan-native","jobMinutes":999},{"leg":"linux-vulkan","runner":"self-hosted","backend":"vulkan-native","jobMinutes":999}]}'
                    }
                    exit 0
                    """);
                File.WriteAllText(Path.Combine(Directory, ".github/workflows/gpu-hosted-bench.yml"), "attacker-controlled selector");
                Git("add", "--", "scripts/renderer-perf-workflow.ps1", ".github/workflows/gpu-hosted-bench.yml");
            }
            Git("add", "--", "workload.txt");
            Git("commit", "--quiet", "-m", "test(renderer-perf): synthetic workload");
            Workload = Git("rev-parse", "HEAD").Trim();
            Manifest["workloadSha"] = Workload;
            Environment["RENDERER_PERF_UNIT"] = "pilot-linux";
            Environment["RENDERER_PERF_WORKLOAD_SHA"] = Workload;
            Environment["LEGS"] = "linux-vulkan";
            Environment["FILTER"] = "FullyQualifiedName~GpuCapabilitiesDiagTests";
            Environment["WORKFLOW_RUN_ATTEMPT"] = "1";
            Environment["COVERAGE_PERF_UNIT"] = "";
            Environment["COVERAGE_PERF_MANIFEST_SHA256"] = "";
            Environment["COVERAGE_PERF_WORKLOAD_SHA"] = "";
            Environment["KE_RENDERER_PERF_REPORTS"] = Reports;
            Environment["GITHUB_RUN_ID"] = "123";
            Environment["GITHUB_WORKFLOW_SHA"] = new string('c', 40);
            SaveManifest();
            Git("add", "--", "tools/Renderer.Perf.Tests/authorized/pilot-linux.json");
            Git("commit", "--quiet", "-m", "test(renderer-perf): synthetic authorization", "--", "tools/Renderer.Perf.Tests/authorized/pilot-linux.json");
            Environment["TESTED_REF"] = Git("rev-parse", "HEAD").Trim();
        }

        internal void SaveManifest()
        {
            File.WriteAllText(ManifestPath, Manifest.ToJsonString());
            Environment["RENDERER_PERF_MANIFEST_SHA256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(ManifestPath)));
        }
        internal void WriteEvidence(string counters = "1/1/1/0/0", RendererPerfReport? source = null)
        {
            string[] counts = counters.Split('/');
            File.WriteAllText(Path.Combine(Reports, "measurement.trx"), $"""
                <TestRun><ResultSummary><Counters total="{counts[0]}" executed="{counts[1]}" passed="{counts[2]}" failed="{counts[3]}" notExecuted="{counts[4]}" /></ResultSummary>
                <Results><UnitTestResult outcome="Passed" testName="Renderer.Perf.Tests.RendererPerfTests.Measure_authorized_unit" /></Results></TestRun>
                """);
            var report = source ?? RendererPerfAnalysisFixture.Report();
            report = report with
            {
                Manifest = RendererPerfManifest.Parse(System.Text.Encoding.UTF8.GetBytes(Manifest.ToJsonString())),
                Identity = report.Identity with
                {
                    ManifestSha256 = Environment["RENDERER_PERF_MANIFEST_SHA256"],
                    WorkloadSha = Workload,
                    AuthorizationSha = Environment["TESTED_REF"],
                    WorkflowSha = Environment["GITHUB_WORKFLOW_SHA"],
                    RunId = "123"
                }
            };
            using (var fixture = new RendererPerfCompilerPublicationFixture(report, directory: Reports))
            {
                var result = fixture.Publish(RendererPerfPublicationWriter.Files(Reports));
                Assert.Empty(result.Failures);
            }
            Report = JsonNode.Parse(File.ReadAllBytes(Path.Combine(Reports, "pilot-linux.json")))!.AsObject();
            SaveReport();
        }
        internal void SaveReport() => File.WriteAllText(Path.Combine(Reports, "pilot-linux.json"), Report.ToJsonString());
        internal void Accept(string mode, string output)
        {
            var result = Run(mode);
            Assert.True(result.Code == 0, result.Output);
            Assert.Contains(output, result.Output, StringComparison.Ordinal);
        }
        internal void Refuse(string mode, string key, string value)
        {
            Environment.TryGetValue(key, out string? previous);
            Environment[key] = value;
            try { Assert.NotEqual(0, Run(mode).Code); }
            finally { if (previous is null) Environment.Remove(key); else Environment[key] = previous; }
        }
        internal (int Code, string Output) Run(string mode) => Execute("pwsh", ["-NoProfile", "-File", Script(), "-Mode", mode]);
        internal string Git(params string[] args)
        {
            var result = Execute("git", args);
            Assert.True(result.Code == 0, result.Output);
            return result.Output;
        }
        internal (int Code, string Output) Execute(string executable, string[] args)
        {
            var start = new ProcessStartInfo(executable) { WorkingDirectory = Directory, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in args) start.ArgumentList.Add(arg);
            foreach (var pair in Environment) start.Environment[pair.Key] = pair.Value;
            if (executable == "pwsh") _powerShellCache.Apply(start);
            // The fixture cannot inherit a caller's native GPU enablement.
            start.Environment.Remove("KE_GPU_TESTS");
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            // These finite headless commands complete independently of the machine's current load.
            process.WaitForExit();
            return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        }
        static string Script([CallerFilePath] string source = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../../scripts/renderer-perf-workflow.ps1"));
        public void Dispose()
        {
            try { RendererPerfFixtureDirectory.Delete(Directory); }
            finally { _powerShellCache.Dispose(); }
        }
    }
}
