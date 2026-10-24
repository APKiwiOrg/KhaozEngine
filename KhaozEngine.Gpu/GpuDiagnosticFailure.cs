using System;
using System.Collections.Immutable;

namespace KhaozEngine.Gpu;

/// <summary>One caught or deliberately raised event, retaining its actual exception reference.</summary>
public sealed record GpuDiagnosticFailure(string Stage, Exception Error);
/// <summary>The sole ordered failure transport for throwing boundaries. The wrapper is not itself a failure event.</summary>
public sealed class GpuDiagnosticFailureException : Exception
{
    public GpuDiagnosticFailureException(ImmutableArray<GpuDiagnosticFailure> failures)
        : base("GPU diagnostic operation failed.")
    {
        if (failures.IsDefaultOrEmpty) throw new ArgumentException("At least one failure is required.", nameof(failures));
        foreach (var failure in failures)
            if (failure is null || string.IsNullOrWhiteSpace(failure.Stage) || failure.Error is null)
                throw new ArgumentException("Every event requires an originating stage and actual exception.", nameof(failures));
        Failures = failures;
    }

    public ImmutableArray<GpuDiagnosticFailure> Failures { get; }
    internal static void Append(ImmutableArray<GpuDiagnosticFailure>.Builder failures, string stage, Exception error)
    {
        if (error is GpuDiagnosticFailureException transport) failures.AddRange(transport.Failures);
        else failures.Add(new(stage, error));
    }

    internal static GpuDiagnosticFailureException DuringCleanup(string stage, Exception original,
        params (string Stage, Action Release)[] cleanup)
    {
        var failures = ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
        Append(failures, stage, original);
        foreach (var release in cleanup)
            try { release.Release(); }
            catch (Exception error) { Append(failures, release.Stage, error); }
        return new(failures.ToImmutable());
    }

}
