using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Text.Json;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

[Collection("NativeDeviceLifecycle")]
public sealed class RendererPerfEntrypointRootTests
{
    [Fact]
    public void Nested_test_host_keeps_its_canonical_working_directory_when_given_dot_segments()
    {
        using var fixture = new EntryFixture(hostRelativePath: "tools/../tools/Renderer.Perf.Tests/bin/Release/net10.0");
        fixture.Refuse(RendererPerfEntrypointRefusal.RuntimeDiffers);
    }

    [Fact]
    public void Nested_test_host_reads_authorized_checkout_manifest_then_refuses_runtime_before_native_startup()
    {
        using var fixture = new EntryFixture();
        fixture.Refuse(RendererPerfEntrypointRefusal.RuntimeDiffers);
        using var runtime = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixture.Work.Reports, "runtime-admission.json")));
        Assert.Equal(Environment.Version.ToString(), runtime.RootElement.GetProperty("runtimeVersion").GetString());
        Assert.Equal("1", runtime.RootElement.GetProperty("environment").GetProperty("KE_GPU_TESTS").GetString());
        Assert.Equal(["authorization.json", "runtime-admission.json"],
            Directory.GetFiles(fixture.Work.Reports).Select(p => Path.GetFileName(p)!).Order().ToArray());
    }

    [Fact]
    public void Runtime_admitted_fixture_refuses_recipe_sources_from_another_checkout_before_startup()
    {
        using var fixture = new EntryFixture(matchRuntime: true);
        fixture.Refuse(RendererPerfEntrypointRefusal.RecipeSourceOutsideCheckout);
        Assert.True(File.Exists(Path.Combine(fixture.Work.Reports, "runtime-admission.json")));
        Assert.False(File.Exists(Path.Combine(fixture.Work.Reports, "native-admission.json")));
    }

    // The ordinary fixture passes every check before runtime admission (it refuses RuntimeDiffers), so each row
    // breaks only the rule its change targets.
    [Theory]
    [InlineData("second-attempt", RendererPerfEntrypointRefusal.NotFirstHostedAttempt)]
    [InlineData("unit-input", RendererPerfEntrypointRefusal.InputsDisagree)]
    [InlineData("unsafe-unit", RendererPerfEntrypointRefusal.UnsafeUnitToken)]
    [InlineData("nested-root", RendererPerfEntrypointRefusal.CheckoutRootNotGitTopLevel)]
    [InlineData("relative-root", RendererPerfEntrypointRefusal.CheckoutRootNotAbsolute)]
    [InlineData("dispatch-hash", RendererPerfEntrypointRefusal.DispatchHashesDisagree)]
    [InlineData("foreign-checkout", RendererPerfEntrypointRefusal.AuthorizationNotManifestOnly)]
    [InlineData("wrong-head", RendererPerfEntrypointRefusal.AuthorizationNotManifestOnly)]
    [InlineData("extra-change", RendererPerfEntrypointRefusal.AuthorizationNotManifestOnly)]
    [InlineData("manifest-unit", RendererPerfEntrypointRefusal.ManifestUnitMismatch)]
    public void Invalid_entrypoint_input_refuses_before_runtime_admission(string change, RendererPerfEntrypointRefusal refusal)
    {
        using var fixture = new EntryFixture();
        using var foreign = change == "foreign-checkout"
            ? new RendererPerfWorkflowContractTests.WorkflowFixture(EntryFixture.NewDirectory()) : null;
        string unit = "pilot-linux";
        switch (change)
        {
            case "second-attempt": fixture.Set("GITHUB_RUN_ATTEMPT", "2"); break;
            case "unit-input": fixture.Set("KE_RENDERER_PERF_UNIT", "pilot-other"); break;
            case "unsafe-unit":
                unit = "Pilot-linux";
                fixture.Set("RENDERER_PERF_UNIT", unit);
                fixture.Set("KE_RENDERER_PERF_UNIT", unit);
                break;
            case "nested-root": fixture.Set("GITHUB_WORKSPACE", fixture.Host); break;
            case "relative-root": fixture.Set("GITHUB_WORKSPACE", "."); break;
            case "dispatch-hash": fixture.Set("RENDERER_PERF_MANIFEST_SHA256", new string('0', 64)); break;
            case "foreign-checkout":
                // Keep the parent inspectable so refusal exercises checkout identity, not a missing Git object.
                foreign!.Git("fetch", "--quiet", fixture.Root, fixture.Work.Workload);
                fixture.Set("GITHUB_WORKSPACE", foreign.Git("rev-parse", "--show-toplevel").Trim());
                break;
            case "wrong-head": fixture.Set("TESTED_REF", fixture.Work.Workload); break;
            case "extra-change":
                File.WriteAllText(Path.Combine(fixture.Work.Directory, "extra.txt"), "not authorized");
                fixture.Work.Git("add", "--", "extra.txt");
                fixture.Work.Git("commit", "--quiet", "--amend", "--no-edit");
                fixture.AuthorizeCurrentHead();
                break;
            case "manifest-unit":
                // The authorization admits the other unit's manifest path, so only the entrypoint's unit check differs.
                fixture.Work.Manifest["unitId"] = "pilot-other";
                fixture.Work.SaveManifest();
                fixture.Work.Git("add", "--", "tools/Renderer.Perf.Tests/authorized/pilot-linux.json");
                fixture.Work.Git("commit", "--quiet", "--amend", "--no-edit");
                fixture.AuthorizeCurrentHead("pilot-other");
                fixture.Set("RENDERER_PERF_MANIFEST_SHA256", fixture.Work.Environment["RENDERER_PERF_MANIFEST_SHA256"]);
                break;
        }
        fixture.Refuse(refusal, unit);
        Assert.False(File.Exists(Path.Combine(fixture.Work.Reports, "runtime-admission.json")));
    }

    [Fact]
    public void Missing_checkout_root_refuses_before_runtime_admission()
    {
        // Every other variable stays explicit, so the only unset name is the one refused.
        using var fixture = new EntryFixture();
        fixture.Set("GITHUB_WORKSPACE", null);
        fixture.Refuse<InvalidOperationException>();
        Assert.False(File.Exists(Path.Combine(fixture.Work.Reports, "runtime-admission.json")));
    }

    [Fact]
    public void Changed_manifest_bytes_refuse_before_runtime_admission()
    {
        using var fixture = new EntryFixture();
        File.AppendAllText(fixture.Work.ManifestPath, " ");
        var error = fixture.Refuse<RendererPerfProtocolException>();
        Assert.Equal("Hash", error.Code);
        Assert.Null(error.EntrypointRefusal);
        Assert.False(File.Exists(Path.Combine(fixture.Work.Reports, "runtime-admission.json")));
    }

    [Fact]
    public void Missing_checkout_manifest_does_not_fall_back_to_a_foreign_manifest_under_test_host()
    {
        using var fixture = new EntryFixture();
        string foreign = Path.Combine(fixture.Host, "tools/Renderer.Perf.Tests/authorized/pilot-linux.json");
        Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
        File.Move(fixture.Work.ManifestPath, foreign);
        var error = Assert.Throws<FileNotFoundException>(() => RendererPerfEntrypoint.Run("pilot-linux", fixture.Work.Reports));
        Assert.Equal(Path.GetFullPath("tools/Renderer.Perf.Tests/authorized/pilot-linux.json", fixture.Root), error.FileName);
        Assert.False(File.Exists(Path.Combine(fixture.Work.Reports, "runtime-admission.json")));
    }

    sealed class EntryFixture : IDisposable
    {
        internal readonly RendererPerfWorkflowContractTests.WorkflowFixture Work = new(NewDirectory());
        readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);
        readonly string _previousDirectory = Environment.CurrentDirectory;
        internal readonly string Host;
        internal readonly string Root;

        internal EntryFixture(bool matchRuntime = false, string hostRelativePath = "tools/Renderer.Perf.Tests/bin/Release/net10.0")
        {
            Root = Work.Git("rev-parse", "--show-toplevel").Trim();
            if (matchRuntime)
            {
                Work.Manifest["runtime"]!["gcMode"] = GCSettings.IsServerGC ? "server" : "workstation";
                Work.Manifest["runtime"]!["environment"] = new System.Text.Json.Nodes.JsonObject
                { ["KE_GPU_TESTS"] = "1", ["DOTNET_TieredCompilation"] = "1" };
            }
            // The ordinary fixture deliberately cannot pass runtime admission on any installed runtime.
            Work.Manifest["runtime"]!["runtimeVersion"] = matchRuntime ? Environment.Version.ToString() : "10.0.999";
            Work.SaveManifest();
            Work.Git("add", "--", "tools/Renderer.Perf.Tests/authorized/pilot-linux.json");
            Work.Git("commit", "--quiet", "--amend", "--no-edit");
            AuthorizeCurrentHead();
            Host = Path.GetFullPath(Path.Combine(Root, hostRelativePath));
            Directory.CreateDirectory(Host);
            if (matchRuntime)
            {
                // Only this serialized fixture changes globals. It admits runtime, never recipe or native work.
                foreach (DictionaryEntry pair in Environment.GetEnvironmentVariables())
                {
                    string name = (string)pair.Key;
                    if (name.StartsWith("KE_", StringComparison.Ordinal) || name.StartsWith("DOTNET_", StringComparison.Ordinal)
                        || name.StartsWith("COMPlus_", StringComparison.Ordinal) || name.StartsWith("VK_", StringComparison.Ordinal))
                        Set(name, null);
                }
                Set("DOTNET_TieredCompilation", "1");
            }
            foreach (var pair in Work.Environment) Set(pair.Key, pair.Value);
            Set("GITHUB_WORKSPACE", Root);
            Set("GITHUB_ACTIONS", "true");
            Set("RUNNER_ENVIRONMENT", "github-hosted");
            Set("GITHUB_RUN_ATTEMPT", "1");
            Set("KE_GPU_TESTS", "1");
            Set("KE_RENDERER_PERF_UNIT", "pilot-linux");
            Environment.CurrentDirectory = Host;
        }

        internal void AuthorizeCurrentHead(string authorizedUnit = "pilot-linux")
        {
            Work.Environment["TESTED_REF"] = Work.Git("rev-parse", "HEAD").Trim();
            var authorization = new RendererPerfAuthorization(Work.Environment["RENDERER_PERF_MANIFEST_SHA256"],
                Work.Workload, Work.Environment["TESTED_REF"], Work.Workload, 1,
                [$"tools/Renderer.Perf.Tests/authorized/{authorizedUnit}.json"], 1, "linux-vulkan", "gpu-t4-linux", "gpu-hosted-bench.yml");
            File.WriteAllBytes(Path.Combine(Work.Reports, "authorization.json"),
                JsonSerializer.SerializeToUtf8Bytes(authorization, RendererPerfManifest.JsonOptions));
            Set("TESTED_REF", Work.Environment["TESTED_REF"]);
        }

        internal static string NewDirectory() => Path.Combine(Path.GetTempPath(), "khaozengine-renderer-entry-root-fix", Guid.NewGuid().ToString("N"));
        internal void Set(string name, string? value)
        {
            _previous.TryAdd(name, Environment.GetEnvironmentVariable(name));
            Environment.SetEnvironmentVariable(name, value);
        }
        internal T Refuse<T>(string unit = "pilot-linux") where T : Exception
        {
            var error = Assert.Throws<T>(() => RendererPerfEntrypoint.Run(unit, Work.Reports));
            Assert.Equal(Host, Environment.CurrentDirectory);
            return error;
        }
        internal void Refuse(RendererPerfEntrypointRefusal refusal, string unit = "pilot-linux")
        {
            var error = Refuse<RendererPerfProtocolException>(unit);
            Assert.Equal("Entrypoint", error.Code);
            Assert.Equal(refusal, error.EntrypointRefusal);
        }
        public void Dispose()
        {
            Environment.CurrentDirectory = _previousDirectory;
            foreach (var pair in _previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            Work.Dispose();
        }
    }
}
