using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu.Performance;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

[Collection("AllocSensitive")]
public sealed class CoverageRecessionCleanupTests
{
    [Theory]
    [InlineData("baseline-scene-passes")]
    [InlineData("baseline-scene")]
    public void Timestamp_cleanup_preserves_the_frame_failure_and_diagnostic_survivors(string layout)
    {
        using var compute = new CoveragePublicationDevice();
        using var device = CoverageRecessionWarmAllocationTests.InertCostDevice(compute);
        device.PoolFactory = capacity =>
        {
            bool first = device.Pools.Count == 0;
            var pool = new GpuPerfTimestampTestPool(capacity) { Owners = device.Fences };
            if (first)
            {
                pool.OnBegin = () => throw new NotSupportedException("Synthetic frame refusal.");
                pool.OnDispose = () => throw new ShaderValidationException(ShaderValidationRefusal.MetalLibraryRejected,
                    "Synthetic cleanup refusal.")
                { Label = "synthetic-cleanup", Stage = GpuShaderStages.Compute };
            }
            device.Pools.Add(pool); return pool;
        };
        if (layout == "baseline-scene")
        {
            var failure = Assert.Throws<CoverageRecessionRowException>(() =>
                CoverageRecessionCostMeasurement.Measure(device, new(4, 4), 1, 3, Deadline(), [layout]));
            Assert.Equal("baseline-scene", failure.Layout);
            Assert.Equal((1, "conditioning", 0), (failure.Actors, failure.Phase, failure.Frame));
            Assert.IsType<NotSupportedException>(failure.InnerException);
            AssertCleanup(failure.Details);
        }
        else
        {
            var measured = CoverageRecessionCostMeasurement.Measure(device, new(4, 4), 1, 3, Deadline(),
                [layout, "baseline-scene"]);
            Assert.Equal("baseline-scene", Assert.Single(measured.Rows).Layout);
            var unavailable = Assert.Single(measured.UnavailableRows);
            Assert.Equal(typeof(NotSupportedException).FullName, unavailable.Reason.Error!.Type);
            AssertCleanup(unavailable.Reason);
            var json = JsonSerializer.SerializeToNode(unavailable)!;
            Assert.Equal("synthetic-cleanup", json["Reason"]!["CleanupFailures"]![0]!["Label"]!.GetValue<string>());
        }
        Assert.All(device.Pools, pool => Assert.Equal(1, pool.Disposals));
        Assert.NotEmpty(device.Commands);
        Assert.All(device.Commands, commands => Assert.Equal(1, commands.Disposals));
    }

    [Fact]
    public void Covered_final_cleanup_preserves_context_before_the_scene_is_disposed()
    {
        using var compute = new CoveragePublicationDevice();
        using var device = CoverageRecessionWarmAllocationTests.InertCostDevice(compute);
        device.PoolFactory = capacity =>
        {
            var pool = new GpuPerfTimestampTestPool(capacity) { Owners = device.Fences };
            if (device.Pools.Count == 0) pool.OnDispose = () => throw new NotSupportedException("Synthetic covered teardown refusal.");
            device.Pools.Add(pool); return pool;
        };
        var row = new CoverageRecessionCostMeasurement.Driver(device, new(4, 4), 1, "covered-scene", 3);
        row.Frame(0, retain: false);
        var error = Assert.Throws<CoverageRecessionRowException>(row.Dispose);
        Assert.Equal(("covered-scene", 1, "conditioning", 0), (error.Layout, error.Actors, error.Phase, error.Frame));
        Assert.Equal(CoverageRecessionRowRefusal.CleanupFailed, error.Reason);
        Assert.Equal(new[] { CoverageFailureCode.None }, error.CoverageFailures);
        Assert.Equal(typeof(NotSupportedException).FullName, Assert.Single(error.CleanupFailures).Type);
        Assert.NotEmpty(device.Pools);
        Assert.All(device.Pools, pool => Assert.Equal(1, pool.Disposals));
        Assert.NotEmpty(device.Commands);
        Assert.All(device.Commands, commands => Assert.Equal(1, commands.Disposals));
        row.Dispose();
    }

    [Fact]
    public void Timestamp_ring_construction_keeps_the_refusal_and_its_rollback_failure()
    {
        using var compute = new CoveragePublicationDevice();
        using var device = CoverageRecessionWarmAllocationTests.InertCostDevice(compute);
        bool refused = false;
        device.PoolFactory = capacity =>
        {
            if (device.Pools.Count == 1 && !refused)
            { refused = true; throw new NotSupportedException("Synthetic timestamp construction refusal."); }
            var pool = new GpuPerfTimestampTestPool(capacity) { Owners = device.Fences };
            if (device.Pools.Count == 0) pool.OnDispose = () => throw new ShaderValidationException(
                ShaderValidationRefusal.MetalLibraryRejected, "Synthetic timestamp construction cleanup refusal.")
            { Label = "synthetic-cleanup", Stage = GpuShaderStages.Compute };
            device.Pools.Add(pool); return pool;
        };
        var measured = CoverageRecessionCostMeasurement.Measure(device, new(4, 4), 1, 3, Deadline(),
            ["baseline-scene-passes", "baseline-scene"]);
        Assert.Equal("baseline-scene", Assert.Single(measured.Rows).Layout);
        var unavailable = Assert.Single(measured.UnavailableRows);
        Assert.Equal(("baseline-scene-passes", "conditioning", -1), (unavailable.Layout, unavailable.Phase, unavailable.Frame));
        Assert.Equal(typeof(NotSupportedException).FullName, unavailable.Reason.Error!.Type);
        AssertCleanup(unavailable.Reason);
        Assert.NotEmpty(device.Pools);
        Assert.All(device.Pools, pool => Assert.Equal(1, pool.Disposals));
        Assert.NotEmpty(device.Commands);
        Assert.All(device.Commands, commands => Assert.Equal(1, commands.Disposals));
    }

    [Fact]
    public void An_undrained_diagnostic_is_fatal_and_retains_its_submitted_dependants()
    {
        using var compute = new CoveragePublicationDevice();
        using var device = CoverageRecessionWarmAllocationTests.InertCostDevice(compute);
        var buffers = new List<SubmissionTestBuffer>();
        SubmissionTestBuffer[] rowBuffers = [], liveBuffers = [];
        GpuPerfTimestampTestCommands[] submittedAtFailure = [];
        GpuPerfTimestampTestCommands? recordingAtFailure = null;
        bool failDrain = false;
        device.BufferFactory = description =>
        {
            var buffer = new SubmissionTestBuffer(description.SizeInBytes, description.Usage);
            buffers.Add(buffer); return buffer;
        };
        device.Trace = operation =>
        {
            if (operation != "drain" || !failDrain) return;
            failDrain = false;
            throw new InvalidOperationException("Synthetic drain refusal.");
        };
        device.PoolFactory = capacity =>
        {
            if (device.Pools.Count == 0) rowBuffers = buffers.ToArray();
            var pool = new GpuPerfTimestampTestPool(capacity) { Owners = device.Fences };
            if (device.Pools.Count == 1) pool.OnBegin = () =>
            {
                liveBuffers = rowBuffers.Where(buffer => buffer.DisposeCalls == 0).ToArray();
                submittedAtFailure = device.Commands.Where(commands => commands.Submitted && commands.Disposals == 0).ToArray();
                recordingAtFailure = device.Commands.Single(commands => ReferenceEquals(commands.Pool, pool));
                failDrain = true;
                throw new NotSupportedException("Synthetic frame refusal after submission.");
            };
            device.Pools.Add(pool); return pool;
        };
        var error = Assert.Throws<CoverageRecessionRowException>(() =>
            CoverageRecessionCostMeasurement.Measure(device, new(4, 4), 1, 3, Deadline(),
                ["carry-only-outer", "baseline-scene"]));
        Assert.Equal(("carry-only-outer", 1, "conditioning", 1), (error.Layout, error.Actors, error.Phase, error.Frame));
        Assert.Equal(CoverageRecessionRowRefusal.OperationFailed, error.Reason);
        Assert.IsType<NotSupportedException>(error.InnerException);
        Assert.True(error.DrainFailed);
        var cleanup = Assert.Single(error.CleanupFailures);
        Assert.Equal("renderer.session.dispose.submission.drain", cleanup.Operation);
        Assert.Equal(typeof(InvalidOperationException).FullName, cleanup.Type);
        Assert.NotEmpty(liveBuffers);
        Assert.All(liveBuffers, buffer => Assert.Equal(0, buffer.DisposeCalls));
        Assert.Equal(6, device.Pools.Count);
        Assert.All(device.Pools.Take(3), pool => Assert.Equal(0, pool.Disposals));
        Assert.All(device.Pools.Skip(3), pool => Assert.Equal(1, pool.Disposals));
        Assert.NotEmpty(submittedAtFailure);
        Assert.All(submittedAtFailure, commands => Assert.Equal(0, commands.Disposals));
        Assert.NotNull(recordingAtFailure);
        Assert.False(recordingAtFailure.Submitted);
        Assert.Equal(1, recordingAtFailure.Disposals);
    }

    [Fact]
    public void Construction_cleanup_releases_remaining_handles_and_preserves_both_refusals()
    {
        using var compute = new CoveragePublicationDevice();
        using var device = CoverageRecessionWarmAllocationTests.InertCostDevice(compute);
        var buffers = new List<SubmissionTestBuffer>();
        bool refused = false;
        device.BufferFactory = description =>
        {
            if (buffers.Count == 3 && !refused)
            { refused = true; throw new NotSupportedException("Synthetic scene construction refusal."); }
            var buffer = new SubmissionTestBuffer(description.SizeInBytes, description.Usage);
            if (buffers.Count == 1) buffer.OnDispose = () => throw new ShaderValidationException(
                ShaderValidationRefusal.MetalLibraryRejected, "Synthetic construction cleanup refusal.")
            { Label = "synthetic-cleanup", Stage = GpuShaderStages.Compute };
            buffers.Add(buffer); return buffer;
        };
        var measured = CoverageRecessionCostMeasurement.Measure(device, new(4, 4), 1, 3, Deadline(),
            ["baseline-scene-passes", "baseline-scene"]);
        Assert.Equal("baseline-scene", Assert.Single(measured.Rows).Layout);
        var failure = Assert.Single(measured.UnavailableRows);
        Assert.Equal(("baseline-scene-passes", 1, "conditioning", -1), (failure.Layout, failure.Actors, failure.Phase, failure.Frame));
        Assert.Equal(typeof(NotSupportedException).FullName, failure.Reason.Error!.Type);
        AssertCleanup(failure.Reason);
        Assert.Contains(failure.Reason.FactoryFailures, error => error.Operation == "CreateBuffer" && error.Type == typeof(NotSupportedException).FullName);
        Assert.True(buffers.Count > 3);
        Assert.All(buffers, buffer => Assert.True(buffer.DisposeCalls > 0));
    }

    [Theory]
    [InlineData("baseline-scene-inline-passes")]
    [InlineData("baseline-scene")]
    public void Final_disposal_reports_its_row_and_still_disposes_later_rows(string layout)
    {
        using var compute = new CoveragePublicationDevice();
        using var device = CoverageRecessionWarmAllocationTests.InertCostDevice(compute);
        device.PoolFactory = capacity =>
        {
            var pool = new GpuPerfTimestampTestPool(capacity) { Owners = device.Fences };
            if (device.Pools.Count == 3) pool.OnDispose = () => throw new NotSupportedException("Synthetic final cleanup refusal.");
            device.Pools.Add(pool); return pool;
        };
        if (layout == "baseline-scene")
        {
            var error = Assert.Throws<CoverageRecessionRowException>(() =>
                CoverageRecessionCostMeasurement.Measure(device, new(4, 4), 1, 3, Deadline(),
                    ["baseline-scene-passes", layout, "baseline-scene-outer"]));
            Assert.Equal((layout, 1, "observed", 2), (error.Layout, error.Actors, error.Phase, error.Frame));
            Assert.Equal(CoverageRecessionRowRefusal.CleanupFailed, error.Reason);
            Assert.Equal(typeof(NotSupportedException).FullName, Assert.Single(error.CleanupFailures).Type);
        }
        else
        {
            var measured = CoverageRecessionCostMeasurement.Measure(device, new(4, 4), 1, 3, Deadline(),
                ["baseline-scene-passes", layout, "baseline-scene-outer"]);
            Assert.Equal(new[] { "baseline-scene-passes", "baseline-scene-outer" }, measured.Rows.Select(row => row.Layout));
            var unavailable = Assert.Single(measured.UnavailableRows);
            Assert.Equal((layout, "observed", 2), (unavailable.Layout, unavailable.Phase, unavailable.Frame));
            Assert.Equal(CoverageRecessionRowRefusal.CleanupFailed, unavailable.Reason.Reason);
            Assert.Equal(typeof(NotSupportedException).FullName, Assert.Single(unavailable.Reason.CleanupFailures).Type);
        }
        Assert.Equal(9, device.Pools.Count);
        Assert.All(device.Pools, pool => Assert.Equal(1, pool.Disposals));
        Assert.NotEmpty(device.Commands);
        Assert.All(device.Commands, commands => Assert.Equal(1, commands.Disposals));
    }

    [Fact]
    public void Final_disposal_attaches_other_row_failures_without_replacing_the_acceptance_failure()
    {
        using var compute = new CoveragePublicationDevice();
        using var device = CoverageRecessionWarmAllocationTests.InertCostDevice(compute);
        device.PoolFactory = capacity =>
        {
            var pool = new GpuPerfTimestampTestPool(capacity) { Owners = device.Fences };
            if (device.Pools.Count == 0) pool.OnBegin = () => throw new NotSupportedException("Synthetic acceptance refusal.");
            if (device.Pools.Count == 3) pool.OnDispose = () => throw new InvalidOperationException("Synthetic other-row cleanup refusal.");
            device.Pools.Add(pool); return pool;
        };
        var error = Assert.Throws<CoverageRecessionRowException>(() =>
            CoverageRecessionCostMeasurement.Measure(device, new(4, 4), 1, 3, Deadline(),
                ["baseline-scene", "baseline-scene-passes", "baseline-scene-outer"]));
        Assert.Equal(("baseline-scene", "conditioning", 0), (error.Layout, error.Phase, error.Frame));
        Assert.Equal(CoverageRecessionRowRefusal.OperationFailed, error.Reason);
        Assert.IsType<NotSupportedException>(error.InnerException);
        var other = Assert.Single(error.AdditionalRowFailures);
        Assert.Equal(("baseline-scene-passes", "conditioning", -1), (other.Layout, other.Phase, other.Frame));
        Assert.Equal(typeof(InvalidOperationException).FullName, Assert.Single(other.Reason.CleanupFailures).Type);
        Assert.Equal(9, device.Pools.Count);
        Assert.All(device.Pools, pool => Assert.Equal(1, pool.Disposals));
        Assert.NotEmpty(device.Commands);
        Assert.All(device.Commands, commands => Assert.Equal(1, commands.Disposals));
    }

    static void AssertCleanup(CoverageRecessionRowDetails details)
    {
        Assert.False(details.DrainFailed);
        var cleanup = Assert.Single(details.CleanupFailures);
        Assert.Equal(typeof(ShaderValidationException).FullName, cleanup.Type);
        Assert.Equal(ShaderValidationRefusal.MetalLibraryRejected, cleanup.Reason);
        Assert.Equal("synthetic-cleanup", cleanup.Label);
        Assert.Equal(GpuShaderStages.Compute, cleanup.Stage);
    }

    static long Deadline() => Stopwatch.GetTimestamp() + 900 * Stopwatch.Frequency;
}
