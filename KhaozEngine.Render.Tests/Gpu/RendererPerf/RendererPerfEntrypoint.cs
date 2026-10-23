using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal static class RendererPerfEntrypoint
{
    internal static void Run(string unit, string directory)
    {
        Require(Env("GITHUB_ACTIONS") == "true" && Env("RUNNER_ENVIRONMENT") == "github-hosted" && Env("KE_GPU_TESTS") == "1"
            && Env("WORKFLOW_RUN_ATTEMPT") == "1" && Env("GITHUB_RUN_ATTEMPT") == "1",
            "Only a first-attempt GitHub-hosted authorization can open the native device.");
        Require(unit == Env("RENDERER_PERF_UNIT") && unit == Env("KE_RENDERER_PERF_UNIT")
            && directory == Env("KE_RENDERER_PERF_REPORTS"), "Explicit entrypoint inputs disagree with the workflow.");
        Require(unit.Length is >= 1 and <= 64 && unit[0] != '-' && unit.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'), "Unsafe unit token.");
        string path = $"tools/Renderer.Perf.Tests/authorized/{unit}.json";
        byte[] bytes = File.ReadAllBytes(path);
        var authorization = RendererPerfManifest.ReadJson<RendererPerfAuthorization>(
            File.ReadAllBytes(Path.Combine(directory, "authorization.json")), 64 * 1024);
        Require(authorization.ManifestSha256 == Env("RENDERER_PERF_MANIFEST_SHA256")
            && authorization.WorkloadSha == Env("RENDERER_PERF_WORKLOAD_SHA"), "Dispatch hashes disagree with independent authorization facts.");
        string head = RendererPerfHostProcess.Run("git", "rev-parse", "HEAD");
        string parents = RendererPerfHostProcess.Run("git", "rev-list", "--parents", "-n", "1", "HEAD");
        string changes = RendererPerfHostProcess.Run("git", "diff", "--name-status", "--no-renames", authorization.WorkloadSha, "HEAD");
        Require(head == Env("TESTED_REF") && head == authorization.AuthorizationSha
            && parents == head + " " + authorization.WorkloadSha && changes == "A\t" + path,
            "Authorization must add only the manifest to its immutable workload parent.");
        var manifest = RendererPerfManifest.Parse(bytes, authorization);
        Require(manifest.UnitId == unit, "Manifest names a different unit.");
        var environment = RuntimeEnvironment();
        string gc = GCSettings.IsServerGC ? "server" : "workstation";
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "runtime-admission.json"), JsonSerializer.SerializeToUtf8Bytes(new
        { runtimeVersion = Environment.Version.ToString(), gcMode = gc, environment }, RendererPerfManifest.JsonOptions));
        Require(typeof(RendererPerfEntrypoint).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration == "Release"
            && Environment.Version.ToString() == manifest.Runtime.RuntimeVersion && gc == manifest.Runtime.GcMode
            && Env("DOTNET_TieredCompilation") == (manifest.Runtime.TieredCompilation ? "1" : "0")
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COMPlus_TieredCompilation"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VK_INSTANCE_LAYERS"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VK_LOADER_LAYERS_ENABLE"))
            && environment.Count == manifest.Runtime.Environment.Count
            && environment.All(p => manifest.Runtime.Environment.TryGetValue(p.Key, out var value) && value == p.Value),
            "Observed runtime, GC, tiering or relevant environment differs from approval.");
        long coldStart = Stopwatch.GetTimestamp();
        RendererPerfExecutable.ValidateWork(manifest);
        var layouts = manifest.Recipes.ToDictionary(r => r.SceneId, RendererPerfExecutable.Layout, StringComparer.Ordinal);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "manifest.json"), bytes);
        Write("shader-sources.json", RendererPerfExecutable.ShaderHashes);
        Write("control-corpus.json", RendererPerfControlShaders.Corpus);
        Write("control-definitions.json", manifest.Controls.Select(c => RendererPerfControls.Definition(c.Id)).ToArray());
        Write("frozen-layouts.json", layouts);
        foreach (var recipe in manifest.Recipes)
            File.Copy(RendererPerfRecipe.RecipePath(recipe.Version.Split('/')[0]), Path.Combine(directory, recipe.SceneId + "-recipe.json"), overwrite: true);

        // Assembly references alone do not run the test-support module initializers.
        if (manifest.Execution.Backend == "vulkan") KhaozEngine.Gpu.Vulkan.KhaozEngineVulkan.Register();
        else KhaozEngine.Gpu.D3D11.KhaozEngineD3D11.Register();
        // Cold recipes, compiler metadata, layouts, native creation and calibration count as startup.
        using var context = GpuDeviceContext.CreateHeadless();
        Require(context.InjectedModules is null || context.InjectedModules.Count == 0, "An injected capture or overlay module was detected.");
        IGpuDevice device = context.GpuDevice;
        var facts = RendererPerfNativeIdentity.Read(device);
        string backend = device.Backend == GpuBackendKind.VulkanNative ? "vulkan" : "d3d11";
        var identity = new RendererPerfRunIdentity(unit, authorization.ManifestSha256, manifest.WorkloadSha,
            manifest.BaselineMainSha, head, Env("GITHUB_WORKFLOW_SHA"), Env("GITHUB_RUN_ID"), 1, Environment.ProcessId,
            RendererPerfRecipe.Hash(File.ReadAllBytes(typeof(RendererPerfEntrypoint).Assembly.Location)),
            RuntimeInformation.OSDescription, Image(), Cpu(), Environment.Version.ToString(), gc,
            manifest.Runtime.TieredCompilation, environment, device.Capabilities.DeviceName,
            facts.Vendor, facts.Device, facts.Driver, backend, facts.Recording, device.Diagnostics.SoftwareAdapter,
            facts.TimestampBits, facts.TimestampPeriodNs, device.Capabilities.SupportsTimestamps, "none", "none", manifest.Execution.CompletionPolicy);
        RendererPerfAdmission.Identity(manifest, identity);
        Write("native-admission.json", new
        {
            identity,
            facts,
            coldStartTicks = coldStart,
            frequency = Stopwatch.Frequency,
            clockProbe = backend == "d3d11" ? "one cold two-query submission, no scene frame, no retry" : "selected physical-device timestamp limits"
        });
        var telemetry = new RendererPerfHostTelemetry(directory);
        var clock = new RendererPerfClock(Stopwatch.GetTimestamp, Stopwatch.Frequency);
        RendererPerfReport report = RendererPerfDriver.Run(manifest, identity, CreateSession, clock, new EvidencePublication(directory));
        Require(report.Publication.State == RendererPerfPublicationState.Completed && report.Publication.ReturnedByRun
            && report.Publication.FinalUnitPublished && report.Assessment.ValidForPromotion,
            report.Publication.OriginalFailure ?? string.Join("\n", report.Assessment.Reasons));
        // Re-read the stamped bytes through the strict metric-vectors/2 codec before the fact can pass.
        var final = RendererPerfReportJson.Parse(File.ReadAllBytes(Path.Combine(directory, unit + ".json")));
        Require((final.Identity with { Environment = report.Identity.Environment }) == report.Identity
            && final.Identity.Environment.Count == report.Identity.Environment.Count
            && final.Identity.Environment.All(p => report.Identity.Environment.TryGetValue(p.Key, out var value) && value == p.Value)
            && final.Publication.State == report.Publication.State && final.Publication.FinalUnitPublished
            && final.Publication.ReturnedByRun && final.Publication.Errors.IsEmpty
            && final.Publication.ElapsedMilliseconds == report.Publication.ElapsedMilliseconds
            && final.Publication.EstimatedCostUsd == report.Publication.EstimatedCostUsd,
            "The final file differs from the returned publication.");

        IRendererPerfSession CreateSession(RendererPerfManifest approved, RendererPerfBlock block, RendererPerfClock unitClock)
        {
            bool conditioning = block.SceneId == "conditioning:S3";
            var info = new RendererPerfSessionInfo(identity, true, true, null, ImmutableDictionary<string, RendererPerfMetric>.Empty,
                Startup: conditioning ? new(block.SceneId, new("startup.admission", null, coldStart, coldStart), clock.Frequency,
                    ImmutableDictionary<string, RendererPerfMetric>.Empty) : null);
            if (block.SceneId.StartsWith("control:", StringComparison.Ordinal))
            {
                int id = int.Parse(block.SceneId.AsSpan(8), System.Globalization.CultureInfo.InvariantCulture);
                var control = approved.Controls.Single(c => c.Id == id);
                return new RendererPerfNativeSession(device, info, block, [], unitClock,
                    (wrapped, _) => RendererPerfControls.Create(wrapped, control), telemetry.Read);
            }
            string scene = conditioning ? "S3" : block.SceneId;
            var recipe = approved.Recipes.Single(r => r.SceneId == scene);
            return new RendererPerfNativeSession(device, info, block, layouts[scene], unitClock,
                (wrapped, observer) => new RendererPerfSceneWorkload(wrapped, observer, recipe), telemetry.Read);
        }
        void Write<T>(string name, T value) => File.WriteAllBytes(Path.Combine(directory, name), JsonSerializer.SerializeToUtf8Bytes(value, RendererPerfManifest.JsonOptions));
    }

    static ImmutableDictionary<string, string> RuntimeEnvironment()
    {
        var values = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry pair in Environment.GetEnvironmentVariables())
        {
            string name = (string)pair.Key, value = (string)pair.Value!;
            if ((name.StartsWith("KE_", StringComparison.Ordinal) || name.StartsWith("DOTNET_", StringComparison.Ordinal)
                || name.StartsWith("COMPlus_", StringComparison.Ordinal) || name.StartsWith("VK_", StringComparison.Ordinal))
                && !name.StartsWith("KE_RENDERER_PERF_", StringComparison.Ordinal) && value.Length != 0)
                values.Add(name, value);
        }
        return values.ToImmutable();
    }
    static string Image() => Env("RUNNER_OS") + "/" + (Environment.GetEnvironmentVariable("ImageOS") ?? "image-id-unavailable")
        + "/" + (Environment.GetEnvironmentVariable("ImageVersion") ?? RuntimeInformation.OSDescription);
    static string Cpu()
    {
        if (OperatingSystem.IsLinux())
            return File.ReadLines("/proc/cpuinfo").First(s => s.StartsWith("model name", StringComparison.Ordinal)).Split(':', 2)[1].Trim();
        return Env("PROCESSOR_IDENTIFIER");
    }
    static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException(name + " must be explicit.");
    static void Require(bool condition, string reason) => RendererPerfManifest.Require(condition, "Entrypoint", reason);

    sealed class EvidencePublication(string directory) : IRendererPerfPublication
    {
        public RendererPerfReport Publish(RendererPerfReport report, RendererPerfClock clock, long deadline)
            => RendererPerfPublicationWriter.Files(directory, Prepare).Publish(report, clock, deadline);

        RendererPerfReport Prepare(RendererPerfReport report)
        {
            // Preserve per-block model context. Task 14 owns canonical aggregation and comparisons.
            var models = report.Blocks.Select(block => new
            {
                block.Block,
                models = block.Measured.IsEmpty ? [] : RendererPerfWorkModels.ForFrame(block.Measured[0],
                    block.Block.SceneId + "/" + block.Block.Layout + ", first retained measured frame")
            }).ToArray();
            File.WriteAllBytes(Path.Combine(directory, "work-models.json"), JsonSerializer.SerializeToUtf8Bytes(models, RendererPerfManifest.JsonOptions));
            var sidecars = Directory.EnumerateFiles(directory).Where(path => !path.EndsWith(".trx", StringComparison.Ordinal)
                && !path.EndsWith(".log", StringComparison.Ordinal) && !path.EndsWith(".partial.json", StringComparison.Ordinal))
                .Select(path => new RendererPerfSidecar(Path.GetFileName(path), RendererPerfRecipe.Hash(File.ReadAllBytes(path)))).ToImmutableArray();
            return report with { Sidecars = sidecars };
        }
    }
}
