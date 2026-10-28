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
        var checkout = new RendererPerfCheckout(Env("GITHUB_WORKSPACE"));
        string path = $"tools/Renderer.Perf.Tests/authorized/{unit}.json";
        byte[] bytes = File.ReadAllBytes(checkout.ManifestPath(unit));
        byte[] authorizationBytes = File.ReadAllBytes(Path.Combine(directory, "authorization.json"));
        var authorization = RendererPerfManifest.ReadJson<RendererPerfAuthorization>(authorizationBytes, 64 * 1024);
        Require(authorization.ManifestSha256 == Env("RENDERER_PERF_MANIFEST_SHA256")
            && authorization.WorkloadSha == Env("RENDERER_PERF_WORKLOAD_SHA"), "Dispatch hashes disagree with independent authorization facts.");
        string head = checkout.Git("rev-parse", "HEAD");
        string parents = checkout.Git("rev-list", "--parents", "-n", "1", "HEAD");
        string changes = checkout.Git("diff", "--name-status", "--no-renames", authorization.WorkloadSha, "HEAD");
        Require(head == Env("TESTED_REF") && head == authorization.AuthorizationSha
            && parents == head + " " + authorization.WorkloadSha && changes == "A\t" + path,
            "Authorization must add only the manifest to its immutable workload parent.");
        var manifest = RendererPerfManifest.Parse(bytes, authorization);
        Require(manifest.UnitId == unit, "Manifest names a different unit.");
        var environment = RuntimeEnvironment();
        string gc = GCSettings.IsServerGC ? "server" : "workstation";
        Directory.CreateDirectory(directory);
        byte[] runtimeBytes = JsonSerializer.SerializeToUtf8Bytes(new
        { runtimeVersion = Environment.Version.ToString(), gcMode = gc, environment }, RendererPerfManifest.JsonOptions);
        File.WriteAllBytes(Path.Combine(directory, "runtime-admission.json"), runtimeBytes);
        Require(typeof(RendererPerfEntrypoint).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration == "Release"
            && Environment.Version.ToString() == manifest.Runtime.RuntimeVersion && gc == manifest.Runtime.GcMode
            && Env("DOTNET_TieredCompilation") == (manifest.Runtime.TieredCompilation ? "1" : "0")
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COMPlus_TieredCompilation"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VK_INSTANCE_LAYERS"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VK_LOADER_LAYERS_ENABLE"))
            && environment.Count == manifest.Runtime.Environment.Count
            && environment.All(p => manifest.Runtime.Environment.TryGetValue(p.Key, out var value) && value == p.Value),
            "Observed runtime, GC, tiering or relevant environment differs from approval.");
        checkout.ValidateRecipePaths(manifest);
        long coldStart = Stopwatch.GetTimestamp();
        var clock = new RendererPerfClock(Stopwatch.GetTimestamp, Stopwatch.Frequency);
        var budget = new RendererPerfUnitBudget(clock, coldStart,
            checked(coldStart + manifest.Limits.UnitSeconds * clock.Frequency), default);
        RendererPerfHostTelemetry? telemetry = null;
        RendererPerfPublicationWriter writer = null!;
        writer = RendererPerfPublicationWriter.Files(directory, value =>
        {
            telemetry?.Register(writer, budget);
            return value;
        });
        IRendererPerfUnitPublication publication = writer;
        writer.RegisterExisting("authorization.json", RendererPerfArtifactProducers.Schema("authorization.json"), authorizationBytes);
        writer.RegisterExisting("runtime-admission.json", RendererPerfArtifactProducers.Schema("runtime-admission.json"), runtimeBytes);
        Dictionary<string, RendererPerfPassDefinition[]> layouts;
        IGpuHeadlessDiagnosticSettings settings;
        GpuBackendKind backendKind = manifest.Execution.Backend == "vulkan"
            ? GpuBackendKind.VulkanNative : GpuBackendKind.Direct3D11Native;
        try
        {
            budget.Check("renderer.startup.before");
            RendererPerfExecutable.ValidateWork(manifest);
            layouts = manifest.Recipes.ToDictionary(r => r.SceneId, RendererPerfExecutable.Layout, StringComparer.Ordinal);
            Directory.CreateDirectory(directory);
            writer.Produce("manifest.json", RendererPerfArtifactProducers.Schema("manifest.json"), bytes, budget);
            Write("shader-sources.json", RendererPerfExecutable.ShaderHashes);
            Write("control-corpus.json", RendererPerfControlShaders.Corpus);
            Write("control-definitions.json", manifest.Controls.Select(c => RendererPerfControls.Definition(c.Id)).ToArray());
            Write("frozen-layouts.json", layouts);
            foreach (var recipe in manifest.Recipes)
            {
                budget.Check("renderer.startup.recipe.before");
                string name = recipe.SceneId + "-recipe.json";
                writer.Produce(name, RendererPerfArtifactProducers.Schema(name),
                    File.ReadAllBytes(RendererPerfRecipe.RecipePath(RendererPerfRecipe.IdFromVersion(recipe.Version))), budget);
                budget.Check("renderer.startup.recipe.after");
            }

            // Assembly references alone do not run the test-support module initializers.
            if (manifest.Execution.Backend == "vulkan") KhaozEngine.Gpu.Vulkan.KhaozEngineVulkan.Register();
            else KhaozEngine.Gpu.D3D11.KhaozEngineD3D11.Register();
            budget.Check("renderer.settings.before");
            settings = GpuDeviceContext.ResolveHeadlessDiagnosticSettings(backendKind, new(false, false));
            budget.Check("renderer.settings.after");
        }
        catch (Exception error)
        {
            var refused = RendererPerfUnit.RefuseBeforeCreation(authorization, budget, "gpu.context.provider", error, publication!);
            throw new GpuDiagnosticFailureException(refused.Failures);
        }
        telemetry = new RendererPerfHostTelemetry(directory);
        RendererPerfRunIdentity identity = null!;
        int operationOrdinal = 0;
        var outcome = RendererPerfUnit.Run(manifest, authorization, budget, settings,
            GpuDeviceContext.CreateHeadlessAttempt, IdentifyProduction,
            (device, capture) => (approved, block, unitClock) => CreateSession(device, capture, approved, block, unitClock), publication!);
        RendererPerfReport report = outcome.Report ?? throw new InvalidOperationException(outcome.EarlyFailure?.OriginalFailure
            ?? "No trustworthy native run identity or partial report was produced.");
        RendererPerfVerdict.RequireSuccess(outcome);
        // The typed writer verifies files before sampling completion. Its stamp changes only the
        // two reserved accounting slots. Received workflow and offline directories are verified again.

        RendererPerfRunIdentity IdentifyProduction(IGpuDevice device)
        {
            var facts = RendererPerfNativeIdentity.Read(device, manifest.ExecutionClass);
            string backend = device.Backend == GpuBackendKind.VulkanNative ? "vulkan" : "d3d11";
            identity = new RendererPerfRunIdentity(unit, authorization.ManifestSha256, manifest.WorkloadSha,
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
            return identity;
        }

        IRendererPerfSession CreateSession(IGpuDevice device, RendererPerfConstructionCapture capture,
            RendererPerfManifest approved, RendererPerfBlock block, RendererPerfClock unitClock)
        {
            RendererPerfSchedule schedule = approved.Schedule;
            bool conditioning = block.SceneId == schedule.ConditioningBlockId;
            var info = new RendererPerfSessionInfo(identity, true, true, null, ImmutableDictionary<string, RendererPerfMetric>.Empty,
                Startup: conditioning ? new(block.SceneId, new("startup.admission", null, coldStart, coldStart), clock.Frequency,
                    ImmutableDictionary<string, RendererPerfMetric>.Empty) : null);
            if (block.SceneId.StartsWith("control:", StringComparison.Ordinal))
            {
                int id = int.Parse(block.SceneId.AsSpan(8), System.Globalization.CultureInfo.InvariantCulture);
                var control = approved.Controls.Single(c => c.Id == id);
                return new RendererPerfNativeSession(device, info, block, [], unitClock,
                    (wrapped, _) => RendererPerfControls.Create(wrapped, control), telemetry.Read,
                    capture, operationOrdinal++, approved.ExecutionClass, budget.Deadline);
            }
            string scene = conditioning ? schedule.ConditioningSceneId : block.SceneId;
            var recipe = approved.Recipes.Single(r => r.SceneId == scene);
            return new RendererPerfNativeSession(device, info, block, layouts[scene], unitClock,
                (wrapped, observer) => new RendererPerfSceneWorkload(wrapped, observer, recipe), telemetry.Read,
                capture, operationOrdinal++, approved.ExecutionClass, budget.Deadline);
        }
        void Write<T>(string name, T value)
        {
            budget.Check("renderer.startup.write.before");
            writer.Produce(name, RendererPerfArtifactProducers.Schema(name),
                JsonSerializer.SerializeToUtf8Bytes(value, RendererPerfManifest.JsonOptions), budget);
            budget.Check("renderer.startup.write.after");
        }
    }

    sealed class EvidencePublication(string directory) : IRendererPerfPublication
    {
        public RendererPerfReport Publish(RendererPerfReport report, RendererPerfClock clock, long deadline)
        {
            RendererPerfPublicationWriter? writer = null;
            writer = RendererPerfPublicationWriter.Files(directory, value =>
            {
                writer!.Produce("work-models.json", RendererPerfArtifactProducers.Schema("work-models.json"), RendererPerfArtifactProducers.WorkModels(value));
                return value with { Sidecars = writer.ExpectedSidecars };
            });
            return writer.Publish(report, clock, deadline);
        }
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

}
