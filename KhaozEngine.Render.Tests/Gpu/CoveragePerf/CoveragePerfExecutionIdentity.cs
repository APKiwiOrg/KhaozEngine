using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;

namespace KhaozEngine.Tests.Gpu;

// Captured or supplied outside measurement. Unknown values remain explicit and cannot prove a gate.
internal sealed record CoveragePerfExecutionIdentity
{
    internal string HarnessAssembly { get; init; } = "unavailable";
    internal string Runtime { get; init; } = "unavailable";
    internal string GcSettings { get; init; } = "unavailable";
    internal string TieringSettings { get; init; } = "unavailable";
    internal long ClockFrequency { get; init; }
    internal string HostCpu { get; init; } = "unavailable";
    internal string HostIdentity { get; init; } = "unavailable";
    internal string RunnerImage { get; init; } = "unavailable";
    internal string LegacyBaselineSha { get; init; } = "unavailable";
    internal string LegacySequenceFingerprint { get; init; } = "unavailable";
    internal string BatchCompletionPolicy { get; init; } = "unavailable";
    internal string TimestampCompletionPolicy { get; init; } = "unavailable";
    internal string ProductionCompletionPolicy { get; init; } = "unavailable";
    internal string AllocationObservationPolicy { get; init; } = "ordinary GC, informational only";
    internal ImmutableArray<CoveragePerfEnvironmentValue> Environment { get; init; } = [];

    internal static CoveragePerfExecutionIdentity Capture(CoveragePerfExecutionIdentity supplied, CoveragePerfClock clock,
        CoveragePerfManifest manifest)
    {
        var assembly = typeof(CoveragePerfDriver).Assembly;
        var settings = new[] { "DOTNET_TieredCompilation", "DOTNET_TieredPGO", "DOTNET_TC_QuickJit",
            "DOTNET_ReadyToRun", "COMPlus_TieredCompilation", "COMPlus_TieredPGO", "COMPlus_ReadyToRun" }
            .Select(key => new CoveragePerfEnvironmentValue(key, System.Environment.GetEnvironmentVariable(key) ?? "unset"))
            .ToImmutableArray();
        return supplied with
        {
            HarnessAssembly = $"{assembly.FullName}, MVID={assembly.ManifestModule.ModuleVersionId}, informational={assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}",
            Runtime = RuntimeInformation.FrameworkDescription + ", " + RuntimeInformation.RuntimeIdentifier,
            GcSettings = $"server={GCSettings.IsServerGC}, latency={GCSettings.LatencyMode}",
            TieringSettings = string.Join(",", settings.Select(s => s.Name + "=" + s.Value)),
            ClockFrequency = clock.Frequency,
            HostIdentity = supplied.HostIdentity == "unavailable" ? System.Environment.MachineName : supplied.HostIdentity,
            HostCpu = supplied.HostCpu == "unavailable" ? System.Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unavailable" : supplied.HostCpu,
            RunnerImage = supplied.RunnerImage == "unavailable" ? System.Environment.GetEnvironmentVariable("ImageOS") is { } image
                ? image + "/" + System.Environment.GetEnvironmentVariable("ImageVersion") : "unavailable" : supplied.RunnerImage,
            LegacyBaselineSha = CoveragePerfLegacyHost.BaselineSha,
            LegacySequenceFingerprint = CoveragePerfLegacyHost.SequenceFingerprint,
            BatchCompletionPolicy = "three-frame batches, partial final batch at block boundary, one harness WaitForIdle",
            TimestampCompletionPolicy = manifest.AdapterPolicy == CoveragePerfAdapterPolicy.LavapipeCpuOnly
                ? "timestamps disabled" : "one nonblocking poll per slot after each batch drain, no retry",
            ProductionCompletionPolicy = "legacy direct submit or production shared retention and nonblocking polls, no measured waits",
            Environment = supplied.Environment.AddRange(settings),
        };
    }
}

internal sealed record CoveragePerfAllocationProof(string NativeProofSha, bool Reconciled, CoveragePerfBudgetStatus Status);
