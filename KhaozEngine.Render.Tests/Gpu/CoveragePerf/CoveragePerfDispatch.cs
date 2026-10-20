using System;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace KhaozEngine.Tests.Gpu;

internal sealed record CoveragePerfRequest(byte[] Bytes, CoveragePerfManifest Manifest, string WorkloadSha, string AuthorizationSha);

// Authorization and runtime facts are resolved before the entrypoint creates a clock or a device.
internal static class CoveragePerfDispatch
{
    internal static CoveragePerfRequest Load(string authorized, Func<string, string?> environment, Func<string, string, string> authorizationCommit)
    {
        string unit = Required(environment, "KE_COVERAGE_PERF_UNIT");
        Match(unit, "^[a-z0-9-]{1,64}$", "KE_COVERAGE_PERF_UNIT");
        string hash = Required(environment, "KE_COVERAGE_PERF_MANIFEST_SHA256");
        Match(hash, "^[0-9a-f]{64}$", "KE_COVERAGE_PERF_MANIFEST_SHA256");
        string expected = Required(environment, "KE_COVERAGE_PERF_WORKLOAD_SHA");
        Match(expected, "^[0-9a-f]{40}$", "KE_COVERAGE_PERF_WORKLOAD_SHA");
        string file = Path.Combine(authorized, unit + ".json");
        if (!File.Exists(file)) throw new InvalidDataException("Selected authorized manifest is missing: " + unit);
        if (new FileInfo(file).Length > CoveragePerfManifest.MaxManifestBytes)
            throw new InvalidDataException("Selected manifest exceeds the protocol byte limit.");
        byte[] bytes = File.ReadAllBytes(file);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != hash)
            throw new InvalidDataException("Selected manifest SHA-256 differs from KE_COVERAGE_PERF_MANIFEST_SHA256.");
        var manifest = CoveragePerfManifest.Parse(bytes);
        if (manifest.UnitId != unit) throw new InvalidDataException("Manifest unitId differs from KE_COVERAGE_PERF_UNIT.");
        if (manifest.WorkloadSha != expected)
            throw new InvalidDataException("Manifest workloadSha differs from the dispatched KE_COVERAGE_PERF_WORKLOAD_SHA.");
        // The checkout is the authorization commit, verified against the dispatched workload commit by git.
        string authorization = authorizationCommit(unit, expected);
        Match(authorization, "^[0-9a-f]{40}$", "authorization commit SHA");
        return new(bytes, manifest, expected, authorization);
    }

    internal static CoveragePerfRunIdentity Identity(CoveragePerfRequest request, Func<string, string?> environment)
    {
        string workflow = Required(environment, "GITHUB_WORKFLOW");
        string os = Required(environment, "RUNNER_OS");
        string backend = Required(environment, "KE_GRAPHICS_BACKEND");
        string runner, leg;
        if (workflow == "stress-test" && os == "Linux" && backend == "vulkan-native")
        {
            runner = "ubuntu-26.04";
            leg = "ubuntu-26.04-vulkan-lavapipe";
            if (Required(environment, "RUNNER_ENVIRONMENT") != "github-hosted")
                throw new InvalidDataException("CPU measurement requires the GitHub-hosted runner.");
        }
        else if (workflow == "gpu-hosted-bench")
        {
            leg = Required(environment, "KE_COVERAGE_PERF_LEG");
            (runner, string expectedOs, string expectedBackend) = leg switch
            {
                "linux-vulkan" => ("gpu-t4-linux", "Linux", "vulkan-native"),
                "windows-d3d11" => ("gpu-t4-windows", "Windows", "direct3d11-native"),
                "windows-vulkan" => ("gpu-t4-windows", "Windows", "vulkan-native"),
                _ => throw new InvalidDataException("Measurement requires one named hosted GPU leg."),
            };
            if (os != expectedOs || backend != expectedBackend)
                throw new InvalidDataException("Runner OS or selected backend differs from the named leg.");
        }
        else throw new InvalidDataException("Measurement requires the approved workflow and runner route.");
        string workflowSha = Required(environment, "GITHUB_SHA");
        Match(workflowSha, "^[0-9a-f]{40}$", "GITHUB_SHA");
        if (!int.TryParse(Required(environment, "GITHUB_RUN_ATTEMPT"), NumberStyles.None, CultureInfo.InvariantCulture, out int attempt) || attempt <= 0)
            throw new InvalidDataException("GITHUB_RUN_ATTEMPT must be positive.");
        var identity = new CoveragePerfRunIdentity(request.WorkloadSha, workflowSha, request.Manifest.UnitId,
            request.Manifest.Sha256, runner, workflow, leg, Required(environment, "GITHUB_RUN_ID"), attempt,
            Environment.ProcessId, request.Manifest.Authorization.Identity)
        {
            AuthorizationCommitSha = request.AuthorizationSha,
            Execution = new()
            {
                HostCpu = HostCpu(environment),
                Environment = new[] { "RUNNER_NAME", "RUNNER_OS", "RUNNER_ARCH", "RUNNER_ENVIRONMENT", "GITHUB_JOB",
                    "VK_DRIVER_FILES", "VK_ICD_FILENAMES", "KE_GRAPHICS_BACKEND", "KE_D3D11_RECORDING_MODE" }
                    .Select(key => new CoveragePerfEnvironmentValue(key, environment(key) ?? "unset")).ToImmutableArray(),
            },
        };
        request.Manifest.ValidateAuthorization(identity);
        return identity;
    }

    internal static string RepositoryRoot(string directory)
    {
        for (DirectoryInfo? current = new(directory); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "KhaozEngine.slnx"))) return current.FullName;
        throw new InvalidDataException("Cannot locate the checked-out workload repository.");
    }

    static string HostCpu(Func<string, string?> environment)
    {
        if (environment("PROCESSOR_IDENTIFIER") is { Length: > 0 } windows) return windows;
        if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
        {
            string? model = File.ReadLines("/proc/cpuinfo").FirstOrDefault(line => line.StartsWith("model name", StringComparison.Ordinal));
            if (model is not null) return model[(model.IndexOf(':') + 1)..].Trim();
        }
        return "unavailable";
    }

    static string Required(Func<string, string?> environment, string key) =>
        environment(key) is { Length: > 0 } value ? value : throw new InvalidDataException(key + " is required.");
    static void Match(string value, string pattern, string key)
    {
        if (!Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant) || value.Contains('\n'))
            throw new InvalidDataException(key + " has an invalid identity format.");
    }
}
