using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

// Each vector builds its own offline throwaway repository: workload commit A, then authorization commit B.
public sealed class CoveragePerfAuthorizationCommitTests
{
    [Theory]
    [InlineData("accepted", null)]
    [InlineData("head-is-workload", "HEAD is the workload commit")]
    [InlineData("second-file", "must add only tools/EquipmentCoverage.Perf.Tests/authorized/inline-driver.json")]
    [InlineData("source-change", "must add only tools/EquipmentCoverage.Perf.Tests/authorized/inline-driver.json")]
    [InlineData("rename-into-manifest", "D\tsrc/Workload.cs")]
    [InlineData("missing-git", "git is unavailable, so the authorization commit cannot be verified")]
    [InlineData("grandparent", "parent differs from KE_COVERAGE_PERF_WORKLOAD_SHA")]
    [InlineData("merge", "exactly one parent, found 2")]
    [InlineData("manifest-workload", "Manifest workloadSha differs from the dispatched KE_COVERAGE_PERF_WORKLOAD_SHA")]
    [InlineData("hash", "SHA-256 differs from KE_COVERAGE_PERF_MANIFEST_SHA256")]
    public void Authorization_commit_is_the_workload_plus_exactly_one_approved_manifest(string vector, string? refusal)
    {
        using var repository = new Repository();
        string workload = repository.Commit("A workload");
        var run = new CoveragePerfFakeRun();
        if (vector != "manifest-workload") run.ReplaceJson(new string('1', 40), workload);
        string authorization = workload;
        if (vector != "head-is-workload")
        {
            string parent = vector == "grandparent" ? repository.Commit("Intermediate", workload) : workload;
            string[] parents = vector == "merge" ? new[] { workload, repository.Commit("Side", workload) } : new[] { parent };
            string manifest = CoveragePerfAuthorizationCommit.ManifestPath("inline-driver");
            repository.Write(manifest, run.Json);
            if (vector == "rename-into-manifest")
                File.Move(Path.Combine(repository.Root, "src/Workload.cs"), Path.Combine(repository.Root, manifest), true);
            if (vector == "second-file") repository.Write("src/Extra.cs", "class Extra { }\n"u8.ToArray());
            if (vector == "source-change") repository.Write("src/Workload.cs", "class Workload { int changed; }\n"u8.ToArray());
            authorization = repository.Commit("B authorization", parents);
        }
        // The build output carries the approved bytes, so the git rule alone must refuse a bad checkout.
        repository.Output(run.Json);
        var env = Environment(run, workload);
        // On the stress route GITHUB_SHA is the dispatched authorization commit itself.
        env["GITHUB_SHA"] = authorization;
        if (vector == "hash") env["KE_COVERAGE_PERF_MANIFEST_SHA256"] = new string('0', 64);

        CoveragePerfRequest? request = null;
        var error = Record.Exception(() => request = CoveragePerfDispatch.Load(repository.Authorized, name => env.GetValueOrDefault(name),
            (unit, sha) => CoveragePerfAuthorizationCommit.Commit(repository.Root, sha, unit,
                vector == "missing-git" ? Path.Combine(repository.Root, "nonexistent-git") : "git")));

        if (refusal is not null)
        {
            Assert.Contains(refusal, Assert.IsType<InvalidDataException>(error).Message, StringComparison.Ordinal);
            return;
        }
        Assert.Null(error);
        Assert.Equal(workload, request!.WorkloadSha);
        Assert.Equal(authorization, request.AuthorizationSha);
        var identity = CoveragePerfDispatch.Identity(request, name => env.GetValueOrDefault(name));
        Assert.Equal(workload, identity.WorkloadSha);
        Assert.Equal(authorization, identity.AuthorizationCommitSha);
        Assert.Equal(authorization, identity.WorkflowSha);
    }

    [Fact]
    public void Report_json_and_markdown_record_workload_authorization_and_run_identity()
    {
        var report = CoveragePerfReportVectors.Create();
        report = report with
        {
            Identity = report.Identity with
            {
                AuthorizationCommitSha = new string('b', 40),
                WorkflowSha = new string('c', 40),
                WorkflowRunId = "987654321",
                ProcessId = 4242,
            },
        };
        string json = CoveragePerfReportJson.Serialize(report);
        var identity = JsonDocument.Parse(json).RootElement.GetProperty("report").GetProperty("identity");
        Assert.Equal(report.Identity.WorkloadSha, identity.GetProperty("workloadSha").GetString());
        Assert.Equal(new string('b', 40), identity.GetProperty("authorizationCommitSha").GetString());
        Assert.Equal(new string('c', 40), identity.GetProperty("workflowSha").GetString());
        Assert.Equal("987654321", identity.GetProperty("workflowRunId").GetString());
        Assert.Equal(1, identity.GetProperty("workflowRunAttempt").GetInt32());
        Assert.Equal(4242, identity.GetProperty("processId").GetInt32());
        var copy = CoveragePerfReportJson.Deserialize(json).Identity;
        Assert.Equal(new string('b', 40), copy.AuthorizationCommitSha);
        Assert.Equal((new string('c', 40), "987654321", 1, 4242), (copy.WorkflowSha, copy.WorkflowRunId, copy.WorkflowRunAttempt, copy.ProcessId));
        string markdown = CoveragePerfReportJson.Markdown(report);
        Assert.Contains("Workload " + report.Identity.WorkloadSha + ", authorization commit " + new string('b', 40) +
            ", workflow " + new string('c', 40) + ", run 987654321 attempt 1, process 4242.", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Authorized_manifests_are_checked_out_byte_for_byte()
    {
        string root = CoveragePerfDiscoveryTests.Root();
        string attributes = CoveragePerfAuthorizationCommit.Git(root, "check-attr", "text", "eol", "--", CoveragePerfAuthorizationCommit.ManifestPath("any-unit"));
        string path = CoveragePerfAuthorizationCommit.ManifestPath("any-unit");
        Assert.Contains(path + ": text: unset", attributes, StringComparison.Ordinal);
        Assert.Contains(path + ": eol: unspecified", attributes, StringComparison.Ordinal);
    }

    static Dictionary<string, string> Environment(CoveragePerfFakeRun run, string workload) => new()
    {
        ["KE_COVERAGE_PERF_UNIT"] = run.Identity.UnitId,
        ["KE_COVERAGE_PERF_MANIFEST_SHA256"] = run.Identity.ManifestSha256,
        ["KE_COVERAGE_PERF_WORKLOAD_SHA"] = workload,
        ["KE_COVERAGE_PERF_PRESET"] = CoveragePerfManifest.Parse(run.Json).Preset,
        ["GITHUB_SHA"] = run.Identity.WorkflowSha,
        ["GITHUB_RUN_ID"] = run.Identity.WorkflowRunId,
        ["GITHUB_RUN_ATTEMPT"] = "1",
        ["GITHUB_WORKFLOW"] = run.Identity.Workflow,
        ["RUNNER_OS"] = "Linux",
        ["RUNNER_ENVIRONMENT"] = "github-hosted",
        ["KE_GRAPHICS_BACKEND"] = "vulkan-native",
    };

    sealed class Repository : IDisposable
    {
        readonly string temp = Path.Combine(Path.GetTempPath(), "khaozengine-ec-perf-8b-r1", Guid.NewGuid().ToString("N"));
        internal string Root => Path.Combine(temp, "repository");
        internal string Authorized => Path.Combine(temp, "output", "authorized");

        internal Repository()
        {
            Directory.CreateDirectory(Root);
            Git("-c", "init.defaultBranch=main", "init", "-q");
            Write("src/Workload.cs", "class Workload { }\n"u8.ToArray());
        }

        internal void Write(string relative, byte[] bytes)
        {
            string path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        internal void Output(byte[] manifest)
        {
            Directory.CreateDirectory(Authorized);
            File.WriteAllBytes(Path.Combine(Authorized, "inline-driver.json"), manifest);
        }

        // Plumbing commits run no hooks and take exact parents, including a merge.
        internal string Commit(string message, params string[] parents)
        {
            Git("add", "-A");
            var arguments = new List<string> { "-c", "user.name=Coverage Fixture", "-c", "user.email=fixture@example.invalid",
                "-c", "commit.gpgsign=false", "commit-tree", Git("write-tree").Trim(), "-m", message };
            foreach (string parent in parents) arguments.AddRange(new[] { "-p", parent });
            string sha = Git(arguments.ToArray()).Trim();
            Git("update-ref", "HEAD", sha);
            return sha;
        }

        string Git(params string[] arguments) => CoveragePerfAuthorizationCommit.Git(Root, arguments);

        public void Dispose()
        {
            if (!Directory.Exists(temp)) return;
            foreach (string file in Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(temp, true);
        }
    }
}
