using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

internal enum CoverageRecessionRowRefusal
{
    OperationFailed,
    CoverageNotPublished,
    CarryBracketsIncomplete,
    ArithmeticMismatch,
    CleanupFailed,
}

internal sealed record CoverageRecessionFactoryFailure(string Operation, string Type, ShaderValidationRefusal? Reason,
    string? Label, GpuShaderStages? Stage, string Message)
{
    public string? StackTrace { get; init; }
    public CoverageRecessionAllocationFailure? Allocation { get; init; }
    public IReadOnlyList<CoverageRecessionFactoryFailure> InnerExceptions { get; init; } = ImmutableArray<CoverageRecessionFactoryFailure>.Empty;

    internal static CoverageRecessionFactoryFailure Capture(string operation, Exception error)
    {
        IEnumerable<Exception> nested = error is AggregateException aggregate ? aggregate.InnerExceptions
            : error.InnerException is { } inner ? [inner] : [];
        return new(operation, error.GetType().FullName!, (error as ShaderValidationException)?.Reason,
            (error as ShaderValidationException)?.Label, (error as ShaderValidationException)?.Stage, error.Message)
        {
            StackTrace = error.StackTrace,
            Allocation = error is AllocationMeasurementException allocation ? CoverageRecessionAllocationFailure.Capture(allocation) : null,
            InnerExceptions = ImmutableArray.CreateRange(nested.Select(inner => Capture(operation, inner))),
        };
    }
}

internal sealed record CoverageRecessionCollectionCounts(int Gen0, int Gen1, int Gen2);

internal sealed record CoverageRecessionAllocationFailure(AllocationMeasurementRefusal Reason, string Description,
    long? Budget, long? ProcessBytes, long? ThreadBytes, long? OtherThreadBytes, long? ReportedMessages,
    CoverageRecessionCollectionCounts? Collections, string? RuntimeReason)
{
    internal static CoverageRecessionAllocationFailure Capture(AllocationMeasurementException error)
        => new(error.Reason, error.Description, error.Budget, error.ProcessBytes, error.ThreadBytes, error.OtherThreadBytes,
            error.ReportedMessages, error.Collections is { } counts ? new(counts.Gen0, counts.Gen1, counts.Gen2) : null, error.RuntimeReason);
}

internal sealed record CoverageRecessionRowDetails(CoverageRecessionRowRefusal Reason, int BracketsSeen, int BracketsExpected,
    int DispatchCount, IReadOnlyList<CoverageFailureCode> CoverageFailures,
    IReadOnlyList<CoverageRecessionFactoryFailure> FactoryFailures, CoverageRecessionFactoryFailure? Error,
    IReadOnlyList<CoverageRecessionFactoryFailure> CleanupFailures, bool DrainFailed,
    IReadOnlyList<CoverageRecessionUnavailableRow> AdditionalRowFailures);

/// <summary>One failed synthetic cost row. The JSON carries Details rather than serializing Exception internals.</summary>
internal sealed class CoverageRecessionRowException : InvalidOperationException
{
    public string Layout { get; }
    public int Actors { get; }
    public string Phase { get; }
    public int Frame { get; }
    public CoverageRecessionRowRefusal Reason { get; }
    public int BracketsSeen { get; }
    public int BracketsExpected { get; }
    public int DispatchCount { get; }
    public IReadOnlyList<CoverageFailureCode> CoverageFailures { get; }
    public IReadOnlyList<CoverageRecessionFactoryFailure> FactoryFailures { get; }
    public IReadOnlyList<CoverageRecessionFactoryFailure> CleanupFailures { get; private set; } = ImmutableArray<CoverageRecessionFactoryFailure>.Empty;
    public bool DrainFailed { get; private set; }
    public IReadOnlyList<CoverageRecessionUnavailableRow> AdditionalRowFailures { get; private set; } = ImmutableArray<CoverageRecessionUnavailableRow>.Empty;
    object? undrainedOwner;
    public override string Message => base.Message
        + (DrainFailed ? " Fatal teardown: submitted work could not be drained. GPU dependants remain retained." : "")
        + string.Concat(CleanupFailures.Select(f => $" Cleanup {f.Operation}: {f.Type}, reason={f.Reason}, label={f.Label}, stage={f.Stage}: {f.Message}"))
        + string.Concat(AdditionalRowFailures.Select(f => $" Additional teardown row {f.Layout}, actors={f.Actors}, {f.Phase} frame={f.Frame}: "
            + string.Join(" ", f.Reason.CleanupFailures.Select(c => $"{c.Operation}: {c.Type}, reason={c.Reason}, label={c.Label}, stage={c.Stage}: {c.Message}"))));
    public CoverageRecessionRowDetails Details => new(Reason, BracketsSeen, BracketsExpected, DispatchCount, CoverageFailures, FactoryFailures,
        InnerException is null ? null : CoverageRecessionFactoryFailure.Capture("row", InnerException), CleanupFailures, DrainFailed, AdditionalRowFailures);

    internal void AttachCleanup(CoverageRecessionCleanup cleanup, object owner)
    {
        CleanupFailures = ImmutableArray.CreateRange(cleanup.Snapshot());
        DrainFailed |= cleanup.DrainFailed;
        // Keep native owners rooted while the device has unproven completion. No dependent Dispose may run.
        if (DrainFailed) undrainedOwner = owner;
    }

    internal void AddRowFailure(CoverageRecessionRowException error)
    {
        AdditionalRowFailures = ImmutableArray.CreateRange(AdditionalRowFailures.Append(CoverageRecessionUnavailableRow.From(error)).ToArray());
        DrainFailed |= error.DrainFailed;
        if (error.undrainedOwner is not null) undrainedOwner = (undrainedOwner, error.undrainedOwner);
    }

    internal CoverageRecessionRowException(string layout, int actors, string phase, int frame,
        CoverageRecessionRowRefusal reason, int bracketsSeen, int bracketsExpected, int dispatchCount,
        IReadOnlyList<CoverageFailureCode> coverageFailures, IReadOnlyList<CoverageRecessionFactoryFailure> factoryFailures,
        Exception? inner = null)
        : base($"Coverage cost row {layout}, actors={actors}, {phase} frame={frame}, {reason}, "
            + $"brackets={bracketsSeen}/{bracketsExpected}, dispatches={dispatchCount}, coverage=[{string.Join(", ", coverageFailures)}]. "
            + string.Join(" ", factoryFailures.Select(f => $"Factory {f.Operation}: {f.Type}, reason={f.Reason}, label={f.Label}, stage={f.Stage}: {f.Message}"))
            + (inner is null ? "" : " Row error: " + inner.Message), inner)
    {
        Layout = layout; Actors = actors; Phase = phase; Frame = frame; Reason = reason;
        BracketsSeen = bracketsSeen; BracketsExpected = bracketsExpected; DispatchCount = dispatchCount;
        CoverageFailures = ImmutableArray.CreateRange(coverageFailures.ToArray());
        FactoryFailures = ImmutableArray.CreateRange(factoryFailures.ToArray());
    }
}

internal sealed record CoverageRecessionUnavailableRow(string Layout, int Actors, string Phase, int Frame, CoverageRecessionRowDetails Reason)
{
    internal static CoverageRecessionUnavailableRow From(CoverageRecessionRowException error)
        => new(error.Layout, error.Actors, error.Phase, error.Frame, error.Details);
}
