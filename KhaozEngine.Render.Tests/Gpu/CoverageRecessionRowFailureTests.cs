using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

[Collection("AllocSensitive")]
public sealed class CoverageRecessionRowFailureTests
{
    [Theory]
    [InlineData(1, "covered-scene-outer")]
    [InlineData(32, "covered-scene-outer")]
    [InlineData(1, "covered-scene-inline-passes")]
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
            CoverageRecessionCostMeasurement.Measure(device, new(4, 4), 1, 3, Deadline(), ["covered-scene"]));
        Assert.Equal("covered-scene", error.Layout);
        Assert.Equal(1, error.Actors);
        Assert.Equal("conditioning", error.Phase);
        Assert.Equal(0, error.Frame);
        Assert.Equal(CoverageRecessionRowRefusal.CoverageNotPublished, error.Reason);
        Assert.Equal((0, 10, 0), (error.BracketsSeen, error.BracketsExpected, error.DispatchCount));
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

    [Theory]
    [InlineData("carry-only-inline-outer")]
    [InlineData("covered-scene-inline-outer")]
    [InlineData("covered-scene-inline-passes")]
    [InlineData("carry-only")]
    public void Evidence_mismatch_is_unavailable_for_inline_rows_and_fatal_for_acceptance(string layout)
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
        if (layout == "carry-only")
        {
            var failure = Assert.Throws<CoverageRecessionRowException>(() =>
                CoverageRecessionCostMeasurement.Measure(device, new(4, 4), 1, 3, Deadline(), [layout]));
            Assert.Equal(CoverageRecessionRowRefusal.ArithmeticMismatch, failure.Reason);
        }
        else
        {
            var result = CoverageRecessionCostMeasurement.Measure(device, new(4, 4), 1, 3, Deadline(), [layout, "baseline-scene"]);
            Assert.Equal("baseline-scene", Assert.Single(result.Rows).Layout);
            var unavailable = Assert.Single(result.UnavailableRows);
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

    static long Deadline() => Stopwatch.GetTimestamp() + 900 * Stopwatch.Frequency;
}
