using System;
using System.Diagnostics;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu.Performance;
using KhaozEngine.Tests.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("AllocSensitive")]
public sealed class CoverageRecessionWarmAllocationTests(ITestOutputHelper output)
{
    [Fact]
    public void Warm_metal_clear_provenance_records_without_managed_allocation()
    {
        using var harness = new MetalRingHarness();
        using var native = harness.NewList(new object());
        using var target = new RecordedFramebuffer();
        var provenance = new CoverageRecessionDeferredClears(static () => 0);
        native.Begin(); native.SetFramebuffer(target); provenance.Discover();
        native.ClearColorTarget(0, Color.Black); provenance.Cleared(native, 0);
        native.Passes.PrepareDraw(); provenance.Observe(native); native.End(); provenance.Freeze(1);
        native.Begin(); native.SetFramebuffer(target); provenance.BeginFrame(native, 0);
        native.ClearColorTarget(0, Color.Black);
        long cleared, consumed;
        // The existing native fakes allocate trace strings. Only the actual annotation work is protected.
        using (AllocationRegion.Start("Metal clear request provenance"))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            provenance.Cleared(native, 0);
            cleared = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        native.Passes.PrepareDraw();
        using (AllocationRegion.Start("Metal clear consumption provenance"))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            provenance.Observe(native);
            consumed = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        native.End();
        Assert.Single(provenance.For(0).ToArray());
        Assert.Equal((0L, 0L), (cleared, consumed));
    }

    [Fact]
    public void Warm_outline_queue_stays_allocation_free_after_a_collection()
    {
        // #1749. Enum metadata caches may be collected between warm-up and the protected measurement.
        using var device = new FakeGpuDevice();
        using var scene = new Scene3D(device, new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm));
        scene.Begin(); scene.BeginMeshOutline(Color.White, 2);
        scene.Begin(); scene.BeginMeshOutline(Color.White, 2);
        scene.Begin();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long allocated;
        using (AllocationRegion.Start("outline queue after collection"))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            scene.BeginMeshOutline(Color.White, 2);
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        output.WriteLine($"Outline queue after collection allocated {allocated} bytes.");
        Assert.Equal(0, allocated);
    }

    [Theory]
    [InlineData("carry-only", 1)]
    [InlineData("baseline-scene", 1)]
    [InlineData("covered-scene", 1)]
    [InlineData("carry-only", 32)]
    [InlineData("baseline-scene", 32)]
    [InlineData("covered-scene", 32)]
    [InlineData("carry-only-outer", 1)]
    [InlineData("carry-only-outer", 32)]
    [InlineData("covered-scene-outer", 1)]
    [InlineData("covered-scene-outer", 32)]
    [InlineData("carry-only-inline-outer", 1)]
    [InlineData("carry-only-inline-outer", 32)]
    [InlineData("covered-scene-inline-outer", 1)]
    [InlineData("covered-scene-inline-outer", 32)]
    [InlineData("baseline-scene-passes", 1)]
    [InlineData("baseline-scene-passes", 32)]
    [InlineData("covered-scene-passes", 1)]
    [InlineData("covered-scene-passes", 32)]
    [InlineData("covered-scene-inline-passes", 1)]
    [InlineData("covered-scene-inline-passes", 32)]
    public void Warm_cost_frame_regions_allocate_zero_for_each_actor_population(string layout, int actors)
    {
        // Inert device and counters exercise the real cost driver, without establishing GPU timing or output.
        using var compute = new CoveragePublicationDevice();
        using var device = InertCostDevice(compute);
        using var driver = new CoverageRecessionCostMeasurement.Driver(device, new(4, 4), actors, layout, 1);
        long deadline = Stopwatch.GetTimestamp() + 900 * Stopwatch.Frequency;
        // Use the real unit's conditioning, including both rows and holes on every recording slot.
        for (int frame = 0; frame < CoverageRecessionCostMeasurement.ConditioningFrames; frame++)
        {
            driver.Frame(frame, retain: false);
            if (frame % 3 == 2) { device.WaitForIdle(); driver.Harvest(deadline); }
        }
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        driver.Frame(CoverageRecessionCostMeasurement.ConditioningFrames, retain: true);
        var allocation = driver.LastAllocation;
        device.WaitForIdle(); driver.Harvest(deadline);
        output.WriteLine($"{layout}, actors={actors}, preparation={allocation.Preparation}, opening={allocation.Opening}, encoding={allocation.Encoding}, submission={allocation.Submission}");
        Assert.Equal((0L, 0L, 0L, 0L), allocation);
    }

    /// <summary>An inert device with strict timestamp pools that runs the real cost driver without a GPU.</summary>
    internal static GpuPerfTimestampTestDevice InertCostDevice(CoveragePublicationDevice compute) => new()
    {
        ProgramFactory = compute,
        CaptureCommandTrace = false,
        Features = new(false, true, "coverage-cost-allocation", supportsCompute: true,
            supportsCompletionFences: true, supportsShadowMaps: true, maxMsaaSampleCount: 4)
        { SupportsTimestamps = true },
        OnSubmittedFence = fence => ((SubmissionTestFence)fence).Signaled = true,
    };
}
