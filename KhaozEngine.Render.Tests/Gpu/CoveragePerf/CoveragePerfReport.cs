using System.Collections.Immutable;
using System.Globalization;

namespace KhaozEngine.Tests.Gpu;

internal enum CoveragePerfRunStatus { Completed, Refused, Failed }
internal enum CoveragePerfBudgetStatus { Unproved, Pass, Fail }

/// <summary>
/// Caller-verified identities, not values inferred from a manifest or GITHUB_SHA alone. A different workflow attempt
/// requires a new audit record and exact byte binding. Process identity distinguishes reports within that attempt.
/// </summary>
internal sealed record CoveragePerfRunIdentity(string WorkloadSha, string WorkflowSha, string UnitId, string ManifestSha256,
    string Runner, string Workflow, string Leg, string WorkflowRunId, int WorkflowRunAttempt, int ProcessId,
    string AuthorizationIdentity)
{
    internal CoveragePerfExecutionIdentity Execution { get; init; } = new();
    // The checked-out commit whose single parent is WorkloadSha and which adds only the approved manifest.
    internal string AuthorizationCommitSha { get; init; } = "unavailable";
    internal string RunId => string.Create(CultureInfo.InvariantCulture, $"{WorkflowRunId}-{WorkflowRunAttempt}-{ProcessId}");
}

internal sealed record CoveragePerfEnvironmentValue(string Name, string Value);

/// <summary>
/// Completed unit envelope. Missing or synthetic evidence stays Unproved through independent validity checks.
/// Partial blocks survive failure, and a completed block does not establish final unit completion.
/// </summary>
internal sealed record CoveragePerfReport(CoveragePerfRunStatus Status, string Reason, CoveragePerfManifest? Manifest,
    string ManifestSha256, CoveragePerfRunIdentity Identity, ImmutableArray<CoveragePerfBlockResult> Blocks,
    CoveragePerfBudgetStatus CpuStatus = CoveragePerfBudgetStatus.Unproved,
    CoveragePerfBudgetStatus GpuStatus = CoveragePerfBudgetStatus.Unproved)
{
    internal bool ReturnedByRun { get; init; }
    internal bool FinalUnitPublished { get; init; }
    internal CoveragePerfAllocationProof AllocationProof { get; init; } = new("unavailable", false, CoveragePerfBudgetStatus.Unproved);
    internal const string Schema = "khaozengine.coverage-perf-report/1";
    internal ImmutableArray<string> Errors { get; init; } = [];
}
