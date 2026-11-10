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
}

internal sealed record CoverageRecessionFactoryFailure(string Operation, string Type, ShaderValidationRefusal? Reason,
    string? Label, GpuShaderStages? Stage, string Message)
{
    internal static CoverageRecessionFactoryFailure Capture(string operation, Exception error)
        => new(operation, error.GetType().FullName!, (error as ShaderValidationException)?.Reason,
            (error as ShaderValidationException)?.Label, (error as ShaderValidationException)?.Stage, error.Message);
}

internal sealed record CoverageRecessionRowDetails(CoverageRecessionRowRefusal Reason, int BracketsSeen, int BracketsExpected,
    int DispatchCount, IReadOnlyList<CoverageFailureCode> CoverageFailures,
    IReadOnlyList<CoverageRecessionFactoryFailure> FactoryFailures, CoverageRecessionFactoryFailure? Error);

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
    public CoverageRecessionRowDetails Details => new(Reason, BracketsSeen, BracketsExpected, DispatchCount, CoverageFailures, FactoryFailures,
        InnerException is null ? null : CoverageRecessionFactoryFailure.Capture("row", InnerException));

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
