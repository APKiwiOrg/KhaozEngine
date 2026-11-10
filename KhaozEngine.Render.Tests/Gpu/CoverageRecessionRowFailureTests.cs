using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("AllocSensitive")]
public sealed class CoverageRecessionRowFailureTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1, "covered-scene")]
    [InlineData(32, "covered-scene")]
    [InlineData(1, "covered-scene-passes")]
    public void A_diagnostic_shader_refusal_keeps_the_baseline_result_and_names_the_unavailable_row(int actors, string layout)
    {
        using var compute = RefusingGraphics();
        using var device = CoverageRecessionWarmAllocationTests.InertCostDevice(compute);
        var measured = CoverageRecessionCostMeasurement.Measure(device, new(4, 4), actors, 3, Deadline(),
            [layout, "baseline-scene"]);
        Assert.Equal("baseline-scene", Assert.Single(measured.Rows).Layout);
        var unavailable = Assert.Single(measured.UnavailableRows);
        Assert.Equal(layout, unavailable.Layout);
        Assert.Equal(actors, unavailable.Actors);
        Assert.Equal("conditioning", unavailable.Phase);
        Assert.Equal(0, unavailable.Frame);
        Assert.Equal(CoverageRecessionRowRefusal.CoverageNotPublished, unavailable.Reason.Reason);
        Assert.Equal(new[] { CoverageFailureCode.UnsupportedAdapter }, unavailable.Reason.CoverageFailures);
        var refusal = Assert.Single(unavailable.Reason.FactoryFailures);
        Assert.Equal(ShaderValidationRefusal.MetalLibraryRejected, refusal.Reason);
        Assert.Equal("synthetic-coverage", refusal.Label);
        Assert.Equal(GpuShaderStages.Vertex, refusal.Stage);
        Assert.Null(CoverageRecessionCostMeasurement.DiagnosticDifference(measured.Rows, actors, layout, "baseline-scene"));
        Assert.Equal(0, CoverageRecessionCostMeasurement.DiagnosticDifference(measured.Rows, actors, "baseline-scene", "baseline-scene"));
        Assert.All(device.Pools, pool => Assert.Equal(1, pool.Disposals));
    }

    [Fact]
    public void An_acceptance_shader_refusal_ends_the_unit_with_the_original_refusal_and_frame_context()
    {
        using var compute = RefusingGraphics();
        using var device = CoverageRecessionWarmAllocationTests.InertCostDevice(compute);
        var error = Assert.Throws<CoverageRecessionRowException>(() =>
            CoverageRecessionCostMeasurement.Measure(device, new(4, 4), 1, 3, Deadline(), ["covered-scene-outer"]));
        Assert.Equal("covered-scene-outer", error.Layout);
        Assert.Equal(1, error.Actors);
        Assert.Equal("conditioning", error.Phase);
        Assert.Equal(0, error.Frame);
        Assert.Equal(CoverageRecessionRowRefusal.CoverageNotPublished, error.Reason);
        Assert.Equal((0, 0, 0), (error.BracketsSeen, error.BracketsExpected, error.DispatchCount));
        Assert.Equal(new[] { CoverageFailureCode.UnsupportedAdapter }, error.CoverageFailures);
        var failure = Assert.Single(error.FactoryFailures);
        Assert.Equal(typeof(ShaderValidationException).FullName, failure.Type);
        Assert.Equal(ShaderValidationRefusal.MetalLibraryRejected, failure.Reason);
        Assert.Equal("synthetic-coverage", failure.Label);
        Assert.Equal(GpuShaderStages.Vertex, failure.Stage);
        Assert.All(device.Pools, pool => Assert.Equal(0, pool.Begins));
    }

    [Theory]
    [InlineData("carry-only")]
    [InlineData("covered-scene")]
    public void An_empty_staged_recording_reports_its_bracket_and_dispatch_counts_and_can_be_discarded(string layout)
    {
        using var compute = new CoveragePublicationDevice();
        using var device = CoverageRecessionWarmAllocationTests.InertCostDevice(compute);
        using (var row = new CoverageRecessionCostMeasurement.Driver(device, new(4, 4), 1, layout, 3)
        { OmitCarryRecordingForTests = true })
        {
            var failure = Assert.Throws<CoverageRecessionRowException>(() => row.Frame(0, retain: false));
            Assert.Equal(CoverageRecessionRowRefusal.CarryBracketsIncomplete, failure.Reason);
            Assert.Equal((0, 10, 0), (failure.BracketsSeen, failure.BracketsExpected, failure.DispatchCount));
        }
        Assert.All(device.Pools, pool => Assert.Equal(1, pool.Disposals));
        Assert.All(device.Fences, fence => Assert.Equal(1, fence.DisposeCalls));
    }

    [Theory]
    [InlineData("construction")]
    [InlineData("scene-construction")]
    [InlineData("opening")]
    [InlineData("harvest")]
    [InlineData("observed")]
    public void Failed_diagnostic_resources_are_released_and_the_remaining_schedule_finishes(string point)
    {
        using var compute = new CoveragePublicationDevice();
        using var device = CoverageRecessionWarmAllocationTests.InertCostDevice(compute);
        int pools = 0;
        var buffers = new List<SubmissionTestBuffer>();
        bool refused = false;
        device.BufferFactory = description =>
        {
            if (point == "scene-construction" && buffers.Count == 3 && !refused)
            { refused = true; throw new NotSupportedException("Synthetic scene construction refusal."); }
            var buffer = new SubmissionTestBuffer(description.SizeInBytes, description.Usage);
            buffers.Add(buffer); return buffer;
        };
        device.PoolFactory = capacity =>
        {
            bool first = pools++ == 0;
            if (first && point == "construction") throw new NotSupportedException("Synthetic pool refusal.");
            var pool = new Performance.GpuPerfTimestampTestPool(capacity) { Owners = device.Fences };
            if (first && point == "opening") pool.OnBegin = () => throw new InvalidOperationException("Synthetic timestamp opening failure.");
            if (first && point == "observed") pool.OnBegin = () =>
            {
                if (pool.Begins == CoverageRecessionCostMeasurement.ConditioningFrames / CoverageRecessionCostMeasurement.BatchFrames)
                    throw new InvalidOperationException("Synthetic observed-frame opening failure.");
            };
            if (first && point == "harvest") pool.ResolveFailure = new InvalidOperationException("Synthetic timestamp harvest failure.");
            device.Pools.Add(pool); return pool;
        };
        var measured = CoverageRecessionCostMeasurement.Measure(device, new(4, 4), 1, 3, Deadline(),
            ["baseline-scene-passes", "baseline-scene"]);
        Assert.Equal("baseline-scene", Assert.Single(measured.Rows).Layout);
        var row = Assert.Single(measured.UnavailableRows);
        Assert.Equal("baseline-scene-passes", row.Layout);
        Assert.Equal(point is "construction" or "scene-construction" ? -1 : point == "harvest" ? 2 : 0, row.Frame);
        Assert.Equal(CoverageRecessionRowRefusal.OperationFailed, row.Reason.Reason);
        Assert.Equal(point == "observed" ? "observed" : "conditioning", row.Phase);
        Assert.All(device.Pools, pool => Assert.Equal(1, pool.Disposals));
        Assert.All(device.Fences, fence => Assert.Equal(1, fence.DisposeCalls));
        Assert.NotEmpty(buffers);
        Assert.All(buffers, buffer => Assert.True(buffer.DisposeCalls > 0));
    }

    // #1762. A lost observed window prevents the oracle, without identifying the historical cause.
    [Fact]
    public void An_observed_collection_makes_the_diagnostic_row_unavailable_before_the_oracle()
    {
        using var compute = new CoveragePublicationDevice();
        using var device = CoverageRecessionWarmAllocationTests.InertCostDevice(compute);
        bool first = true, collected = false;
        device.PoolFactory = capacity =>
        {
            var pool = new Performance.GpuPerfTimestampTestPool(capacity) { Owners = device.Fences };
            if (first) pool.OnBegin = () =>
            {
                if (pool.Begins == CoverageRecessionCostMeasurement.ConditioningFrames / CoverageRecessionCostMeasurement.BatchFrames)
                {
                    collected = true;
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                }
            };
            first = false;
            device.Pools.Add(pool); return pool;
        };
        var measured = CoverageRecessionCostMeasurement.Measure(device, new(4, 4), 1, 3, Deadline(),
            ["covered-scene-passes", "baseline-scene"]);
        Assert.True(collected);
        Assert.Equal("baseline-scene", Assert.Single(measured.Rows).Layout);
        var row = Assert.Single(measured.UnavailableRows);
        output.WriteLine(JsonSerializer.Serialize(row));
        Assert.Equal(("covered-scene-passes", "observed", 0), (row.Layout, row.Phase, row.Frame));
        Assert.Equal(CoverageRecessionRowRefusal.OperationFailed, row.Reason.Reason);
        Assert.Equal(typeof(AllocationMeasurementException).FullName, row.Reason.Error!.Type);
        var allocation = Assert.IsType<CoverageRecessionAllocationFailure>(row.Reason.Error.Allocation);
        Assert.Equal(AllocationMeasurementRefusal.CollectionInterrupted, allocation.Reason);
        Assert.Equal("coverage recording begin", allocation.Description);
        Assert.Equal(AllocationRegion.DefaultBudget, allocation.Budget);
        Assert.NotNull(allocation.ProcessBytes);
        Assert.NotNull(allocation.ThreadBytes);
        Assert.Equal(allocation.ProcessBytes - allocation.ThreadBytes, allocation.OtherThreadBytes);
        Assert.NotNull(allocation.ReportedMessages);
        Assert.True(allocation.Collections!.Gen2 > 0);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(row));
        var recorded = json.RootElement.GetProperty("Reason").GetProperty("Error").GetProperty("Allocation");
        Assert.Equal((int)allocation.Reason, recorded.GetProperty("Reason").GetInt32());
        Assert.Equal(allocation.Collections.Gen2, recorded.GetProperty("Collections").GetProperty("Gen2").GetInt32());
    }

    // #1762. Preserve observer wrappers and every allocation field in both failure policies.
    [Theory]
    [InlineData("baseline-scene-passes")]
    [InlineData("baseline-scene")]
    public void Nested_operation_failures_keep_their_chain_and_typed_allocation_diagnostics(string layout)
    {
        var allocation = new AllocationMeasurementException(AllocationMeasurementRefusal.RuntimeStartFailed,
            "synthetic window", "Synthetic start refusal.", new InvalidOperationException("Synthetic runtime refusal."))
        {
            Budget = 4096,
            ProcessBytes = 128,
            ThreadBytes = 96,
            ReportedMessages = 3,
            Collections = (1, 2, 3),
            RuntimeReason = "synthetic-runtime",
        };
        using var compute = new CoveragePublicationDevice();
        using var device = CoverageRecessionWarmAllocationTests.InertCostDevice(compute);
        bool first = true;
        device.PoolFactory = capacity =>
        {
            if (first)
            {
                first = false;
                throw new InvalidOperationException("Synthetic observer wrapper.",
                    new AggregateException(allocation, new NotSupportedException("Synthetic sibling refusal.")));
            }
            var pool = new Performance.GpuPerfTimestampTestPool(capacity) { Owners = device.Fences };
            device.Pools.Add(pool); return pool;
        };
        CoverageRecessionRowDetails details;
        if (CoverageRecessionCostMeasurement.IsAcceptance(layout))
        {
            var error = Assert.Throws<CoverageRecessionRowException>(() =>
                CoverageRecessionCostMeasurement.Measure(device, new(4, 4), 1, 3, Deadline(), [layout]));
            details = error.Details;
        }
        else
        {
            var measured = CoverageRecessionCostMeasurement.Measure(device, new(4, 4), 1, 3, Deadline(),
                [layout, "baseline-scene"]);
            Assert.Equal("baseline-scene", Assert.Single(measured.Rows).Layout);
            details = Assert.Single(measured.UnavailableRows).Reason;
        }
        output.WriteLine(JsonSerializer.Serialize(details));
        Assert.Equal(CoverageRecessionRowRefusal.OperationFailed, details.Reason);
        var outer = Assert.IsType<CoverageRecessionFactoryFailure>(details.Error);
        Assert.Equal(typeof(InvalidOperationException).FullName, outer.Type);
        Assert.NotNull(outer.StackTrace);
        var aggregate = Assert.Single(outer.InnerExceptions);
        Assert.Equal(typeof(AggregateException).FullName, aggregate.Type);
        Assert.Equal(new[] { typeof(AllocationMeasurementException).FullName, typeof(NotSupportedException).FullName },
            aggregate.InnerExceptions.Select(error => error.Type));
        var refusal = aggregate.InnerExceptions[0];
        Assert.Equal(typeof(InvalidOperationException).FullName, Assert.Single(refusal.InnerExceptions).Type);
        var snapshot = Assert.IsType<CoverageRecessionAllocationFailure>(refusal.Allocation);
        Assert.Equal(allocation.Reason, snapshot.Reason);
        Assert.Equal(allocation.Description, snapshot.Description);
        Assert.Equal(allocation.Budget, snapshot.Budget);
        Assert.Equal(allocation.ProcessBytes, snapshot.ProcessBytes);
        Assert.Equal(allocation.ThreadBytes, snapshot.ThreadBytes);
        Assert.Equal(allocation.OtherThreadBytes, snapshot.OtherThreadBytes);
        Assert.Equal(allocation.ReportedMessages, snapshot.ReportedMessages);
        Assert.Equal(new CoverageRecessionCollectionCounts(1, 2, 3), snapshot.Collections);
        Assert.Equal(allocation.RuntimeReason, snapshot.RuntimeReason);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(details));
        var nested = json.RootElement.GetProperty("Error").GetProperty("InnerExceptions")[0].GetProperty("InnerExceptions");
        Assert.Equal((int)allocation.Reason, nested[0].GetProperty("Allocation").GetProperty("Reason").GetInt32());
        Assert.Equal(3, nested[0].GetProperty("Allocation").GetProperty("Collections").GetProperty("Gen2").GetInt32());
    }

    // #1762. Keep the underlying refusal visible if a pass-row mismatch check fails.
    [Theory]
    [InlineData("carry-only")]
    [InlineData("covered-scene")]
    [InlineData("covered-scene-passes")]
    [InlineData("carry-only-outer")]
    [InlineData("covered-scene-outer")]
    public void Evidence_mismatch_is_unavailable_for_diagnostic_rows_and_fatal_for_acceptance(string layout)
    {
        using var compute = new CoveragePublicationDevice();
        using var device = CoverageRecessionWarmAllocationTests.InertCostDevice(compute);
        GCHandle pinned = default;
        device.BufferMap = (buffer, _) =>
        {
            pinned = GCHandle.Alloc(((SubmissionTestBuffer)buffer).Bytes, GCHandleType.Pinned);
            return new(pinned.AddrOfPinnedObject(), 0, buffer.SizeInBytes);
        };
        device.BufferUnmap = _ => pinned.Free();
        // Inert dispatch leaves zero words. The real evidence readback and exact oracle must reject them.
        if (CoverageRecessionCostMeasurement.IsAcceptance(layout))
        {
            var failure = Assert.Throws<CoverageRecessionRowException>(() =>
                CoverageRecessionCostMeasurement.Measure(device, new(4, 4), 1, 3, Deadline(), [layout]));
            output.WriteLine(JsonSerializer.Serialize(failure.Details));
            Assert.Equal(CoverageRecessionRowRefusal.ArithmeticMismatch, failure.Reason);
        }
        else
        {
            var result = CoverageRecessionCostMeasurement.Measure(device, new(4, 4), 1, 3, Deadline(), [layout, "baseline-scene"]);
            Assert.Equal("baseline-scene", Assert.Single(result.Rows).Layout);
            var unavailable = Assert.Single(result.UnavailableRows);
            output.WriteLine(JsonSerializer.Serialize(unavailable));
            Assert.Equal(CoverageRecessionRowRefusal.ArithmeticMismatch, unavailable.Reason.Reason);
            Assert.Equal(("observed", 2), (unavailable.Phase, unavailable.Frame));
        }
    }

    static CoveragePublicationDevice RefusingGraphics() => new()
    {
        RefuseVertex = (vertex, _) => vertex == ShaderSources.CoverageModelMotionVert
            ? throw new ShaderValidationException(ShaderValidationRefusal.MetalLibraryRejected, "Synthetic shader refusal.")
            { Label = "synthetic-coverage", Stage = GpuShaderStages.Vertex }
            : false,
    };

    [Fact]
    public void A_later_discovery_refusal_has_no_dispatches_from_the_preceding_frame()
    {
        using var compute = new CoveragePublicationDevice
        {
            RefuseVertex = (vertex, _) => vertex == ShaderSources.CoveragePointVert
                ? throw new ShaderValidationException(ShaderValidationRefusal.MetalLibraryRejected, "Synthetic late discovery refusal.")
                { Label = "synthetic-point", Stage = GpuShaderStages.Vertex }
                : false,
        };
        using var device = CoverageRecessionWarmAllocationTests.InertCostDevice(compute);
        var failure = Assert.Throws<CoverageRecessionRowException>(() =>
            new CoverageRecessionCostMeasurement.Driver(device, new(4, 4), 1, "covered-scene-passes", 3));
        Assert.Equal(("conditioning", 1), (failure.Phase, failure.Frame));
        Assert.Equal(CoverageRecessionRowRefusal.CoverageNotPublished, failure.Reason);
        Assert.Equal(0, failure.DispatchCount);
        Assert.Equal("synthetic-point", Assert.Single(failure.FactoryFailures).Label);
    }

    // #1762. A row diagnostic is taken while a failure is being reported, so a deep chain or a getter that
    // throws must cost only the unreadable part and never the report.
    [Theory]
    [InlineData(40, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void Deep_or_unreadable_failures_still_leave_a_serialisable_row_diagnostic(int wrappers, bool unreadable)
    {
        Exception thrown = unreadable ? new UnreadableException() : new NotSupportedException("Synthetic innermost refusal.");
        for (int i = 0; i < wrappers; i++) thrown = new InvalidOperationException("Synthetic wrapper.", thrown);
        using var compute = new CoveragePublicationDevice();
        using var device = CoverageRecessionWarmAllocationTests.InertCostDevice(compute);
        bool first = true;
        device.PoolFactory = capacity =>
        {
            if (first) { first = false; throw thrown; }
            var pool = new Performance.GpuPerfTimestampTestPool(capacity) { Owners = device.Fences };
            device.Pools.Add(pool); return pool;
        };
        var measured = CoverageRecessionCostMeasurement.Measure(device, new(4, 4), 1, 3, Deadline(),
            ["baseline-scene-passes", "baseline-scene"]);
        Assert.Equal("baseline-scene", Assert.Single(measured.Rows).Layout);
        var row = Assert.Single(measured.UnavailableRows);
        Assert.Equal(CoverageRecessionRowRefusal.OperationFailed, row.Reason.Reason);
        var chain = new List<CoverageRecessionFactoryFailure>();
        for (var level = row.Reason.Error; level is not null; level = level.InnerExceptions.FirstOrDefault()) chain.Add(level);
        Assert.Equal(Math.Min(wrappers + 1, CoverageRecessionFactoryFailure.MaxDepth), chain.Count);
        Assert.Equal(wrappers + 1 > CoverageRecessionFactoryFailure.MaxDepth ? 1 : 0, chain[^1].OmittedInnerExceptions);
        if (unreadable)
        {
            Assert.Equal(typeof(UnreadableException).FullName, chain[^1].Type);
            Assert.Equal(new[] { "Message", "StackTrace" }, chain[^1].UnreadableMembers);
        }
        // The cost unit writes unavailable rows with these options before it asserts anything.
        string json = JsonSerializer.Serialize(new { unavailableRows = measured.UnavailableRows },
            new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(1, parsed.RootElement.GetProperty("unavailableRows").GetArrayLength());
    }

    // #1762. Finalizer work left by conditioning must be finished before the first measured window opens,
    // because a finalizer allocating inside a window loses its no-GC region.
    [Fact]
    public void Finalizers_left_by_conditioning_complete_before_the_first_observed_frame()
    {
        using var compute = new CoveragePublicationDevice();
        using var device = CoverageRecessionWarmAllocationTests.InertCostDevice(compute);
        var finalized = new StrongBox<bool>();
        bool first = true, dropped = false;
        bool? finishedBeforeObservation = null;
        int batches = CoverageRecessionCostMeasurement.ConditioningFrames / CoverageRecessionCostMeasurement.BatchFrames;
        device.PoolFactory = capacity =>
        {
            var pool = new Performance.GpuPerfTimestampTestPool(capacity) { Owners = device.Fences };
            if (first) pool.OnBegin = () =>
            {
                if (pool.Begins == batches - 1) { DropFinalizable(finalized); dropped = true; }
                else if (pool.Begins == batches) finishedBeforeObservation ??= Volatile.Read(ref finalized.Value);
            };
            first = false;
            device.Pools.Add(pool); return pool;
        };
        var measured = CoverageRecessionCostMeasurement.Measure(device, new(4, 4), 1, 3, Deadline(), ["baseline-scene"]);
        Assert.Equal("baseline-scene", Assert.Single(measured.Rows).Layout);
        Assert.True(dropped);
        Assert.True(finishedBeforeObservation);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void DropFinalizable(StrongBox<bool> finalized) => _ = new Finalizable(finalized);

    sealed class Finalizable(StrongBox<bool> finalized)
    {
        ~Finalizable() => Volatile.Write(ref finalized.Value, true);
    }

    sealed class UnreadableException : Exception
    {
        public override string Message => throw new InvalidOperationException("Synthetic getter failure.");
        public override string? StackTrace => throw new InvalidOperationException("Synthetic getter failure.");
    }

    static long Deadline() => Stopwatch.GetTimestamp() + 900 * Stopwatch.Frequency;
}
