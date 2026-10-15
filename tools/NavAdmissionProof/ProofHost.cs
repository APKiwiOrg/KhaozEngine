using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Movement;

namespace KhaozEngine.Tests.Movement.Research;

internal static class ProofHost
{
    internal const string Project = "tools/NavAdmissionProof/NavAdmissionProof.csproj";
    internal const string Filter = "FullyQualifiedName=KhaozEngine.Tests.Movement.Research.StableLegacyAdmissionProofTests.SingleUnprofiledPair";

    internal static string Required(string key) => Environment.GetEnvironmentVariable(key)
        is { Length: > 0 } value ? value : throw new InvalidOperationException("Missing required environment variable " + key);

    internal static void RequireHostedSelection()
    {
        if (!OperatingSystem.IsLinux() || IntPtr.Size != 8)
            throw new InvalidOperationException("This manual proof requires 64-bit Linux on GitHub-hosted Actions.");
        Require("GITHUB_ACTIONS", "true");
        Require("RUNNER_ENVIRONMENT", "github-hosted");
        Require("RUNNER_OS", "Linux");
        Require("GITHUB_EVENT_NAME", "workflow_dispatch");
        Require("GITHUB_WORKFLOW", "stress-test");
        Require("PROJECT", Project);
        Require("FILTER", Filter);
        Require("ITERATIONS", "1");
        Require("LOAD", "0");
        Require("CONFIGURATION", "Release");
        Require("SUPPORT_COST", "true");
        Require("DOTNET_TieredCompilation", "0");
        if (Metadata("BuildConfiguration") != "Release")
            throw new InvalidOperationException("Only the Release binary may execute the proof.");
        string? alternative = Environment.GetEnvironmentVariable("COMPlus_TieredCompilation");
        if (alternative is not null && alternative != "0")
            throw new InvalidOperationException("Conflicting compilation environment.");
        foreach (string key in new[] { "CORECLR_ENABLE_PROFILING", "COR_ENABLE_PROFILING", "DOTNET_EnableEventPipe",
            "COMPlus_EnableEventPipe", "DOTNET_DiagnosticPorts", "COMPlus_DiagnosticPorts", "DOTNET_StartupHooks" })
        {
            string? value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(value) && value != "0")
                throw new InvalidOperationException("Profiler or diagnostic injection refused via " + key);
        }
        _ = Required("KHAOZ_SUPPORT_COST_ARTIFACT");
        string commit = Required("GITHUB_SHA");
        if (commit.Length != 40 || MovementVersion().Split('+').Last() != commit)
            throw new InvalidOperationException("The Movement binary must match the checked-out Actions commit.");
    }

    internal static ProofEnvironment Describe() => new(
        RuntimeInformation.FrameworkDescription, Environment.Version.ToString(), RuntimeInformation.OSDescription,
        RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount,
        Metadata("BuildSdk"), Metadata("BuildConfiguration"), Required("GITHUB_SHA"), MovementVersion(), BinarySetHash(),
        Required("DOTNET_TieredCompilation"), Environment.GetEnvironmentVariable("COMPlus_TieredCompilation"),
        Environment.GetEnvironmentVariable("DOTNET_ReadyToRun"), Environment.GetEnvironmentVariable("COMPlus_ReadyToRun"),
        Environment.GetEnvironmentVariable("DOTNET_gcServer"), Environment.GetEnvironmentVariable("DOTNET_GCHeapCount"),
        Environment.GetEnvironmentVariable("DOTNET_EnableDiagnostics"), CompilationEnvironment(), Required("RUNNER_ENVIRONMENT"),
        Required("RUNNER_OS"), Required("RUNNER_ARCH"), Required("GITHUB_WORKFLOW"), Required("GITHUB_EVENT_NAME"),
        Required("GITHUB_RUN_ID"), Required("GITHUB_RUN_ATTEMPT"));

    private static string Metadata(string key) => typeof(ProofHost).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == key).Value
        ?? throw new InvalidOperationException("Missing build metadata " + key);

    private static string MovementVersion() => typeof(GroundMoveContext).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    private static string CompilationEnvironment() => string.Join("\n", new[]
    {
        "DOTNET_TieredCompilation", "COMPlus_TieredCompilation", "DOTNET_TieredPGO", "COMPlus_TieredPGO",
        "DOTNET_TC_QuickJit", "COMPlus_TC_QuickJit", "DOTNET_TC_QuickJitForLoops", "COMPlus_TC_QuickJitForLoops",
        "DOTNET_ReadyToRun", "COMPlus_ReadyToRun", "DOTNET_JitMinOpts", "COMPlus_JitMinOpts",
        "DOTNET_JitNoInline", "COMPlus_JitNoInline", "DOTNET_EnableHWIntrinsic", "COMPlus_EnableHWIntrinsic",
        "DOTNET_gcServer", "COMPlus_gcServer", "DOTNET_GCHeapCount", "COMPlus_GCHeapCount",
    }.Select(key => key + "=" + (Environment.GetEnvironmentVariable(key) ?? "<unset>")));

    private static void Require(string key, string expected)
    {
        if (Required(key) != expected) throw new InvalidOperationException("This proof requires " + key + "=" + expected);
    }

    private static string BinarySetHash()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string path in Directory.GetFiles(AppContext.BaseDirectory).Where(path => path.EndsWith(".dll", StringComparison.Ordinal) ||
            path.EndsWith(".deps.json", StringComparison.Ordinal) || path.EndsWith(".runtimeconfig.json", StringComparison.Ordinal))
            .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(path) + "\n"));
            using var stream = File.OpenRead(path);
            hash.AppendData(SHA256.HashData(stream));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
