using System.Linq;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class ShadowInputObservationGpuTests(MetalRecordingCapacityFixture fixture, ITestOutputHelper log)
    : IClassFixture<MetalRecordingCapacityFixture>
{
    [GpuTheory]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(6, false)]
    [InlineData(6, true)]
    public void InputObservationPreservesPipelinedPixelsCapturesAndCommands(int slots, bool split)
    {
        var scene = slots == 3 ? fixture.Control : fixture.Candidate;
        Render(scene, split, ShadowStageProbeMode.Off);
        var off = Render(scene, split, ShadowStageProbeMode.Off);
        Assert.All(scene.ProbeSnapshots, snapshot => Assert.Null(snapshot));
        var offOrder = scene.ProbeDrawOrder.ToArray();
        var counted = Render(scene, split, ShadowStageProbeMode.Counts);
        var counts = scene.ProbeSnapshots.ToArray();
        var countOrder = scene.ProbeDrawOrder.ToArray();
        var waits = scene.WaitCallbacks.ToArray();
        var timed = Render(scene, split, ShadowStageProbeMode.Timings);
        Assert.Equal(8, off.Length);
        long firstSceneFrame = counts[0]!.Value.Inputs!.Value.SceneFrameIndex;
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(off[i], counted[i]); Assert.Equal(off[i], timed[i]);
            Assert.Equal(offOrder[i], countOrder[i]); Assert.Equal(offOrder[i], scene.ProbeDrawOrder[i]);
            var count = counts[i]!.Value;
            var timing = scene.ProbeSnapshots[i]!.Value;
            Assert.Equal(count.Counters, timing.Counters);
            foreach (var snapshot in new[] { count, timing })
            {
                var inputs = snapshot.Inputs!.Value;
                Assert.True(inputs.Complete);
                Assert.Equal(snapshot.Request, inputs.Request);
                Assert.Equal(i + 1, inputs.Request.Sequence);
                Assert.Equal(ShadowInputAvailability.Active, inputs.Availability);
                Assert.InRange(inputs.Fits.Count, 1, 4);
                Assert.Equal(snapshot.Counters!.Value.DirectionalCascadeCount, inputs.Fits.Count);
                Assert.Equal(i / 60f, inputs.PrefixEffectTimeSeconds);
                Assert.Equal((64, 40), (inputs.View.Width, inputs.View.Height));
            }
            Assert.Equal(firstSceneFrame + i, count.Inputs!.Value.SceneFrameIndex);
            Assert.Equal(slots, waits[i].Main.Receipt!.Value.Depth);
            if (split) Assert.Equal(slots, waits[i].Prefix.Receipt!.Value.Depth);
        }
        log.WriteLine($"slots={slots}, split={split}, eight pending outputs and same-frame captures match Off/Counts/Timings, active input snapshots complete.");
        log.WriteLine(scene.ValidationProof);
    }

    [GpuTheory]
    [InlineData(ShadowStageProbeMode.Off)]
    [InlineData(ShadowStageProbeMode.Counts)]
    [InlineData(ShadowStageProbeMode.Timings)]
    public void SharedInputObservationScenesMatchFreshScenesAfterModeChanges(ShadowStageProbeMode mode)
    {
        foreach (var (slots, aged) in fixture.Modes)
        {
            Render(aged, false, ShadowStageProbeMode.Counts);
            Render(aged, true, ShadowStageProbeMode.Timings);
            var images = Render(aged, true, mode);
            using var fresh = new PreparePhaseSplitFixture(slots);
            // Match the complete jitter-cycle priming of point-shadow and foliage motion state.
            Render(fresh, false, ShadowStageProbeMode.Counts);
            Render(fresh, true, ShadowStageProbeMode.Timings);
            var isolated = Render(fresh, true, mode);
            for (int i = 0; i < 8; i++) Assert.Equal(isolated[i], images[i]);
            log.WriteLine(aged.ValidationProof);
        }
    }

    [GpuTheory]
    [InlineData(3)]
    [InlineData(6)]
    public void ColdNativeOutputMatchesWithOffCountsAndTimings(int slots)
    {
        byte[][] Capture(ShadowStageProbeMode mode)
        {
            using var fresh = new PreparePhaseSplitFixture(slots);
            var images = Render(fresh, true, mode);
            log.WriteLine(fresh.ValidationProof);
            return images;
        }
        var off = Capture(ShadowStageProbeMode.Off);
        var counted = Capture(ShadowStageProbeMode.Counts);
        var timed = Capture(ShadowStageProbeMode.Timings);
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(off[i], counted[i]);
            Assert.Equal(off[i], timed[i]);
        }
    }

    static byte[][] Render(PreparePhaseSplitFixture fixture, bool split, ShadowStageProbeMode mode)
        => fixture.Render(split, false, true, true, true, probeMode: mode, pointShadows: true,
            ringWaitProbe: true, directionalMode: ShadowMode.ShadowMap);
}
