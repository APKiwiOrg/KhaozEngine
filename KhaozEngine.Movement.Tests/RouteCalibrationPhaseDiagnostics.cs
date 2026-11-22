using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;

namespace KhaozEngine.Tests.Movement;

public sealed partial class RouteCalibrationDiagnosticsTests
{
    void ReportPhases<TClock>(List<string> invalidWindows) where TClock : struct, ISampleClock
    {
        var fixture = RouteCalibrationFixture.LongReconstruction();
        using var pool = fixture.CreatePool();
        foreach (var slice in RouteCalibrationPhases.Reconstruction)
        {
            var window = MeasureWindow(1,
                samples => RunReconstruction<UntimedClock>(fixture, pool, slice, samples),
                samples => RunReconstruction<TClock>(fixture, pool, slice, samples));
            ReportPhase(slice.Phase.ToString(), slice.Phase.ToString(), slice.Units, 1, window);
        }

        var rows = RouteCalibrationPhases.ProjectedRows();
        ReportPhase("projected-rows", "FootprintRow", PathWorkWeights.FootprintRow, rows.Length,
            MeasureWindow(rows.Length,
                samples => RunRows<UntimedClock>(rows, samples),
                samples => RunRows<TClock>(rows, samples)));

        var crossings = RouteCalibrationPhases.Crossings();
        ReportCrossings("crossings-128", RouteCalibrationPhases.ShortCrossingBudget);
        ReportCrossings("crossings-256", RouteCalibrationPhases.LongCrossingBudget);

        void ReportCrossings(string name, int quota)
        {
            ReportPhase(name, "CellCrossing", quota, 1, MeasureWindow(1,
                samples => RunCrossings<UntimedClock>(crossings, quota, samples),
                samples => RunCrossings<TClock>(crossings, quota, samples)));
        }

        void ReportPhase(string name, string kind, int units, int samplesPerBatch, SampleWindow window)
        {
            string key = $"name={name} mode=phase";
            string? rawTicks = TClock.IsTimed ? string.Join(",", window.Samples) : null;
            string timing = TClock.IsTimed ? TimingFields(window.Samples) : "";
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{key} kind={kind} units_per_sample={units} samples_per_batch={samplesPerBatch} " +
                $"{timing}{window.ValidityFields()} units={kind}={units * samplesPerBatch}"));
            if (rawTicks is not null)
                output.WriteLine($"{key} raw_ticks={rawTicks}");
            if (!window.IsValid && !TClock.IsTimed)
                invalidWindows.Add(name);
        }
    }

    static void RunReconstruction<TClock>(RouteCalibrationFixture fixture, PathPlanWorkspaces pool,
        RouteCalibrationPhases.ReconstructionSlice slice, Span<long> samples) where TClock : struct, ISampleClock
    {
        using var request = fixture.Begin(pool);
        slice.Prepare(request);
        var budget = slice.Budget;
        long prior = request.WorkSpent;
        long start = TClock.GetTimestamp();
        request.Advance(budget);
        long end = TClock.GetTimestamp();
        samples[0] = end - start;

        if (request.NextPhaseForTests != slice.Next || request.WorkSpent - prior != slice.Units)
            throw new InvalidOperationException($"The {slice.Phase} advance did not spend exactly its phase.");
    }

    static void RunRows<TClock>(NavSegmentRequest[] rows, Span<long> samples) where TClock : struct, ISampleClock
    {
        for (int i = 0; i < rows.Length; i++)
        {
            var cursor = rows[i];
            long start = TClock.GetTimestamp();
            cursor.Step();
            long end = TClock.GetTimestamp();
            samples[i] = end - start;

            if (cursor.NextOperation != NavSegmentOperation.FootprintColumn)
                throw new InvalidOperationException("The projected row did not lead to its column scan.");
        }
    }

    static void RunCrossings<TClock>(NavSegmentRequest cursor, int quota, Span<long> samples)
        where TClock : struct, ISampleClock
    {
        long prior = cursor.WorkSpent;
        long start = TClock.GetTimestamp();
        cursor.Advance(quota);
        long end = TClock.GetTimestamp();
        samples[0] = end - start;

        if (cursor.NextOperation != NavSegmentOperation.CellCrossing || cursor.WorkSpent - prior != quota)
            throw new InvalidOperationException("The crossing advance did not stay within its phase.");
    }
}
