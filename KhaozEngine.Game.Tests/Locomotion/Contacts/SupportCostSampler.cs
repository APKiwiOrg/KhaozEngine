using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime;
using KhaozEngine.Physics;
using KhaozEngine.Locomotion;
using Xunit.Sdk;

namespace KhaozEngine.Tests.Locomotion.Contacts;

/// <summary>Each row samples first use, primes once outside measurement, then samples warm execution.
/// Preallocated callbacks bracket complete actual ground substeps, including rejected attempts.</summary>
internal sealed class SupportCostSampler
{
    readonly record struct Window(long Bytes, double Microseconds, SupportQueryCounts Counts,
        SupportGroundObservation Ground, System.Numerics.Vector3 Motion, QueryCounts Queries, int FindStart, int FindEnd);
    readonly Window[] coldSteps = new Window[1024], warmSteps = new Window[1024];
    readonly bool timing;
    readonly SupportFindObservation[] coldFinds = new SupportFindObservation[16384], warmFinds = new SupportFindObservation[16384];
    SupportFindObservation[] finds;
    int findCount, stepFindStart;
    CountingQueryView? view;
    QueryCounts stepQueries;
    readonly Action<SupportFindObservation> observeFind;
    Window[] steps;
    int stepCount;
    long stepBytes, stepStart;
    SupportQueryCounts stepCounts;
    System.Numerics.Vector3 stepFeet;
    readonly Action begin, end;

    internal SupportCostSampler(bool timing)
    {
        this.timing = timing;
        steps = coldSteps;
        finds = coldFinds;
        observeFind = observation => finds[findCount++] = observation;
        begin = Begin;
        end = End;
    }

    void Begin()
    {
        stepFeet = SupportQueryDiagnostics.Ground.Feet;
        stepQueries = view!.Counts;
        stepFindStart = findCount;
        stepCounts = SupportQueryDiagnostics.Snapshot();
        stepBytes = GC.GetAllocatedBytesForCurrentThread();
        stepStart = timing ? Stopwatch.GetTimestamp() : 0;
    }

    void End()
    {
        long elapsed = timing ? Stopwatch.GetTimestamp() - stepStart : 0;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - stepBytes;
        steps[stepCount++] = new(bytes, elapsed * 1_000_000d / Stopwatch.Frequency,
            SupportQueryDiagnostics.Snapshot() - stepCounts, SupportQueryDiagnostics.Ground,
            SupportQueryDiagnostics.Ground.Feet - stepFeet, view!.Counts - stepQueries, stepFindStart, findCount);
    }

    internal Action? BeforeWarm { get; init; }
    internal bool IsolateAllocations { get; init; } = true;

    internal IReadOnlyList<SupportCostSample> Measure(SupportCostRow row, string operation)
    {
        using var execution = new SupportCostExecution(row, operation);
        view = execution.View;
        using var observers = new ObserverScope(begin, end, observeFind);
        finds = coldFinds;
        findCount = 0;
        steps = coldSteps;
        stepCount = 0;
        execution.View.Reset();
        SupportQueryDiagnostics.Reset();
        // Dense first use exceeds 40 MB. Reserve room for it and the unmeasured scratch inspection.
        using var allocationRegion = SupportAllocationRegion.Start(IsolateAllocations, totalSize: 64 * 1024 * 1024);
        long before = GC.GetAllocatedBytesForCurrentThread();
        long start = timing ? Stopwatch.GetTimestamp() : 0;
        SupportCostOutput cold;
        using (ContactAllocationDiagnostics.Begin(ContactAllocationPhase.Route)) cold = execution.Invoke();
        long coldTicks = timing ? Stopwatch.GetTimestamp() - start : 0;
        long coldBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        bool coldIsolated = GCSettings.LatencyMode == GCLatencyMode.NoGCRegion;
        SupportQueryCounts coldCounts = SupportQueryDiagnostics.Snapshot();
        QueryCounts coldQueries = execution.View.Counts;
        int coldStepCount = stepCount;
        int coldFindCount = findCount;
        long inspectionBefore = GC.GetAllocatedBytesForCurrentThread();
        long coldScratch = execution.ScratchPayloadBytes;
        long inspectionBytes = GC.GetAllocatedBytesForCurrentThread() - inspectionBefore;
        bool inspectionIsolated = GCSettings.LatencyMode == GCLatencyMode.NoGCRegion;

        steps = warmSteps;
        finds = warmFinds;
        findCount = 0;
        stepCount = 0;
        // A hit-only branch can first JIT on the second call even with tiering disabled.
        // One unmeasured call prepares it. Reset its observations before the warm window.
        long primingBefore = GC.GetAllocatedBytesForCurrentThread();
        using (ContactAllocationDiagnostics.Begin(ContactAllocationPhase.Route)) execution.Invoke();
        long primingBytes = GC.GetAllocatedBytesForCurrentThread() - primingBefore;
        bool primingIsolated = GCSettings.LatencyMode == GCLatencyMode.NoGCRegion;
        findCount = 0;
        stepCount = 0;
        execution.View.Reset();
        SupportQueryDiagnostics.Reset();
        BeforeWarm?.Invoke();
        // The checked region spans cold, priming and warm calls. Reflection stays outside the byte windows.
        before = GC.GetAllocatedBytesForCurrentThread();
        start = timing ? Stopwatch.GetTimestamp() : 0;
        SupportCostOutput warm;
        using (ContactAllocationDiagnostics.Begin(ContactAllocationPhase.Route)) warm = execution.Invoke();
        long warmTicks = timing ? Stopwatch.GetTimestamp() - start : 0;
        long warmBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        try { allocationRegion.Dispose(); }
        catch (XunitException error)
        {
            throw new XunitException($"{row.Name} {operation} no-GC region failed. " +
                $"Cold {coldBytes} B (isolated {coldIsolated}), scratch inspection {inspectionBytes} B " +
                $"(isolated {inspectionIsolated}), priming {primingBytes} B (isolated {primingIsolated}), warm {warmBytes} B.", error);
        }
        SupportQueryCounts counts = SupportQueryDiagnostics.Snapshot();
        QueryCounts queries = execution.View.Counts;
        int warmStepCount = stepCount;
        SupportQueryDiagnostics.SetGroundSubstepObserver(null, null);
        SupportQueryDiagnostics.SetFindObserver(null);
        SupportRouteCounts routes = SupportQueryDiagnostics.Routes;
        bool coldMatches = CheckReads(execution, coldFinds.AsSpan(0, coldFindCount));
        bool warmMatches = CheckReads(execution, warmFinds.AsSpan(0, findCount));
        cold = execution.CheckGeometry(cold, coldMatches);
        warm = execution.CheckGeometry(warm, warmMatches);
        long scratch = Math.Max(coldScratch, execution.ScratchPayloadBytes);
        var samples = new List<SupportCostSample>
        {
            new(operation, row.Name, cold.GeometryPassed && warm.GeometryPassed, warm.StepOutcome?.ToString() ?? warm.Outcome,
                warm.Height, warm.Error, warm.Motion, warmStepCount, counts, coldBytes, warmBytes,
                coldTicks * 1_000_000d / Stopwatch.Frequency, warmTicks * 1_000_000d / Stopwatch.Frequency,
                scratch, GroundScenarioTests.SupportFindStackBytes())
            {
                Input = row, Queries = queries, ColdQueries = coldQueries, ColdCounts = coldCounts,
                OracleOutcome = execution.Expected.Status.ToString(), OracleHeight = execution.Expected.Height,
                GeometryMatchesOracle = cold.GeometryMatchesOracle && warm.GeometryMatchesOracle,
                SupportReads = warmFinds.AsSpan(0, findCount).ToArray(), Routes = routes,
                ColdSupportReads = coldFinds.AsSpan(0, coldFindCount).ToArray(),
                InputDetails = operation == "LegacyTreadDownPass"
                    ? "Centre (0.175, 0.9, 0), previous centre (0.175, 1.15, 0), terrain centre 0.9, steppedUp true. Conditional tread route, not comparable to the main row."
                    : "Row.Input is reused unchanged for first use, one unmeasured priming call and warm execution in this operation's separate selected scene.",
                Notes = warmBytes == 0 ? [] : operation == "Find"
                    ? ["Warm Find allocated. Preserve measured bytes for Task 8 or 9. This row cannot pass the allocation gate."]
                    : ["Whole acquisition-through-disposal allocation includes fresh lifetime tokens. Compare with the named runtime inventory. Any excess requires phase attribution."],
                ColdGeometryPassed = cold.GeometryPassed, ColdOutcome = cold.StepOutcome?.ToString() ?? cold.Outcome,
                ColdHeight = cold.Height, ColdHeightError = cold.Error, ColdAchievedMotion = cold.Motion,
                EndState = warm.State, ColdEndState = cold.State,
                Dense = row.Dense, Refusal = warm.Refused,
                Comparable = !row.Explicit && row.Grounded && row.Direction != System.Numerics.Vector2.Zero && row.Normal.Y == 1 && !row.ExpectedRefusal &&
                    !row.KnownIncomplete && operation != "LegacyTreadDownPass",
            },
        };
        if (operation is "GroundTickBorrowed" or "DryTick" or "ExplicitTick")
            for (int i = 0; i < warmStepCount; i++)
            {
                Window c = i < coldStepCount ? coldSteps[i] : default, w = warmSteps[i];
                samples.Add(new($"GroundSubstep/{operation}/{i}", row.Name,
                    cold.GeometryPassed && warm.GeometryPassed && w.Ground.Supported,
                    w.Ground.Supported ? "SupportedSubstep" : "UnsupportedSubstep", w.Ground.Height, w.Ground.HeightError,
                    w.Motion, 1, w.Counts with { GroundSubsteps = 1 }, c.Bytes, w.Bytes,
                    c.Microseconds, w.Microseconds, scratch, GroundScenarioTests.SupportFindStackBytes())
                {
                    Input = row,
                    Dense = row.Dense,
                    Refusal = warm.Refused,
                    Comparable = false,
                    GeometryMatchesOracle = CheckReads(execution, warmFinds.AsSpan(w.FindStart, w.FindEnd - w.FindStart)),
                    Queries = w.Queries,
                    ColdQueries = c.Queries,
                    ColdCounts = c.Counts,
                    SupportReads = warmFinds.AsSpan(w.FindStart, w.FindEnd - w.FindStart).ToArray(),
                    ColdSupportReads = coldFinds.AsSpan(c.FindStart, c.FindEnd - c.FindStart).ToArray(),
                    Notes = w.Bytes > 0 ? ["Whole actual shell/seat/placement substep allocation. Unexpected warm bytes require phase attribution beyond support."] : [],
                    ColdGeometryPassed = cold.GeometryPassed && c.Ground.Supported,
                    ColdOutcome = c.Ground.Supported ? "SupportedSubstep" : "UnsupportedSubstep",
                    ColdHeight = c.Ground.Height,
                    ColdHeightError = c.Ground.HeightError,
                    ColdAchievedMotion = c.Motion
                });
            }
        return samples;
    }

    readonly struct ObserverScope : IDisposable
    {
        internal ObserverScope(Action begin, Action end, Action<SupportFindObservation> find)
        {
            SupportQueryDiagnostics.SetGroundSubstepObserver(begin, end);
            SupportQueryDiagnostics.SetFindObserver(find);
        }

        public void Dispose()
        {
            SupportQueryDiagnostics.SetGroundSubstepObserver(null, null);
            SupportQueryDiagnostics.SetFindObserver(null);
        }
    }

    static bool CheckReads(SupportCostExecution execution, ReadOnlySpan<SupportFindObservation> observations)
    {
        bool matches = true;
        foreach (SupportFindObservation observation in observations)
        {
            execution.CheckFind(observation, out bool matched);
            matches &= matched;
        }
        return matches;
    }
}
