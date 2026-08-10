using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The native Metal timeline against a REAL <c>MTLSharedEvent</c>, which is the one thing no
    /// <c>[Fact]</c> can reach: a value signalled because GPU work finished rather than because a test set a
    /// property. Row 5 of <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>.
    /// <para>
    /// WHAT A RED RUN MEANS. Everything the timeline decides is covered device-free by
    /// <see cref="MetalTimelineTests"/> and <see cref="MetalGpuFenceTests"/>, so a failure here is about the
    /// four native calls underneath and the block on top of them: <c>newSharedEvent</c>,
    /// <c>encodeSignalEvent:value:</c> on a committed command buffer, <c>signaledValue</c> reading back what the
    /// GPU reached, <c>waitUntilSignaledValue:timeoutMS:</c>, and the <c>[UnmanagedCallersOnly]</c> completion
    /// handler delivering a real <c>status</c> and <c>error</c>. Row 8's ring recycles segments against exactly
    /// this, so a red run here is a corruption there.
    /// </para>
    /// <para>
    /// DORMANT OFF macOS RATHER THAN SKIPPED. Under <c>KE_GPU_TESTS=1</c> the Vulkan and Direct3D 11 legs run
    /// the whole assembly in strict mode, where a skip is a failure, so this returns early with the platform
    /// recorded rather than skipping. That is phase 3's row-19 lesson: a dormant row is not a skip, and a
    /// zero-skipped gate satisfied by rows asserting nothing is worth nothing.
    /// </para>
    /// <para>
    /// IT SITS IN <c>NativeDeviceLifecycle</c> FOR BOTH OF THAT COLLECTION'S REASONS. It builds a whole
    /// <c>MTLDevice</c> and queue beside the suite's own, which is the collection's original reason, and it
    /// registers a real queue into the same four-slot process-static table
    /// <see cref="MetalCompletionHandlerTests"/> fills, which is why that class moved here too. A class can only
    /// be in one collection, so a separate registry collection would have forced a choice between the two, and
    /// the device-creating half is the one row 19 requires.
    /// </para>
    /// </summary>
    [Collection("NativeDeviceLifecycle")]
    public sealed class MetalTimelineGpuTests
    {
        readonly ITestOutputHelper _output;

        public MetalTimelineGpuTests(ITestOutputHelper output) => _output = output;

        [GpuFact]
        public void TheTimelineSignalsFencesFromRealGpuCompletions()
        {
            if (!KhaozEngineMetal.IsPlatformSupported)
            {
                _output.WriteLine("dormant: not macOS, so there is no Metal device to signal against.");
                return;
            }

            MetalTimelineProbeResult result = MetalTimelineProbe.Run();
            string report = result.Report();
            _output.WriteLine(report);

            Assert.True(result.DeviceCreated,
                "MTLCreateSystemDefaultDevice returned nil on a machine the platform guard said was macOS. "
                + "Everything below is unmeasured:\n" + report);
            Assert.True(result.QueueCreated, "newCommandQueue returned nil:\n" + report);
            Assert.True(result.SharedEventCreated, "newSharedEvent returned nil:\n" + report);

            // THE ROUTING KEY, on the hardware that decided it. The pointer-equality reading is RECORDED rather
            // than asserted, because it is a fact about the machine: on this one MTLCreateSystemDefaultDevice is
            // a per-GPU process singleton, and a machine where it is not would still route correctly on the
            // queue. What IS asserted is the consequence, which holds either way: two engine devices on one GPU
            // each register their own latch. A device-keyed table fails this outright.
            _output.WriteLine("routing key measurement: MTLCreateSystemDefaultDevice twice returns the same "
                + "pointer = " + result.DefaultDeviceIsProcessSingleton);
            Assert.True(result.TwoQueuesOnOneDeviceBothRegistered,
                "two queues on one device could not both register a completion latch, so a second engine device "
                + "on this GPU could not be created:\n" + report);

            // The values, which is the shared event being created AT 0: the first submission takes 1, which is
            // what makes 0 usable as a fence's unarmed marker.
            Assert.Equal(1UL, result.FirstValue);
            Assert.Equal(2UL, result.SecondValue);

            // The seam's requirement, on a real fence rather than a fake one.
            Assert.False(result.FenceSignaledBeforeArming,
                "a fence straight from the timeline read signaled:\n" + report);

            // THE LOAD-BEARING PAIR. signaledValue read back exactly the value the command buffer encoded, and
            // the seam fence armed with it reads signaled. That is a real GPU completion reaching IGpuFence.
            Assert.Equal(1UL, result.SignaledAfterFirstDrain);
            Assert.True(result.FenceSignaledAfterFirstDrain,
                "the fence armed with the first submission's value did not read signaled after the drain "
                + "returned:\n" + report);

            // Reset re-arms rather than merely existing, and the second target is strictly higher.
            Assert.False(result.FenceSignaledAfterReset, "Reset left the fence signaled:\n" + report);
            Assert.Equal(2UL, result.SignaledAfterSecondDrain);
            Assert.True(result.FenceSignaledAfterSecondDrain,
                "the re-armed fence did not read signaled after the second drain:\n" + report);

            // M-F2 and M-G4: the handler fires for every submitted buffer, on Metal's own thread, and what it
            // reports is status and error. On a healthy device it reports Completed with no error, which is the
            // path that has to work before the failure path can mean anything.
            Assert.Equal(2, result.CompletionsSeen);
            Assert.Equal(MetalCommandBufferStatus.Completed, result.FirstCompletionStatus);
            Assert.True(result.AllCompletionsCompleted,
                "a command buffer completed with a status other than Completed:\n" + report);
            Assert.True(result.AllCompletionsErrorFree,
                "a command buffer completed carrying an error:\n" + report);

            // The slice loop's release, measured against the real event on a value nothing will ever signal. A
            // false here is a hang rather than a wrong number, which is why the probe times out the flipper
            // thread rather than trusting it. The COUNT is deliberately not pinned to exactly one: the flipper
            // is gated on an event set immediately before the drain, but on a loaded runner it can still win
            // the race to the drain's first liveness check, in which case the drain legitimately never starts
            // and counts nothing. Both outcomes satisfy the property this test exists for (a dead device never
            // hangs the drain), and the exact counting semantics are pinned deterministically by the
            // device-free MetalTimelineTests against the fake, where the race cannot occur. This assertion went
            // red on the hosted macos-26 leg for exactly that race before it was relaxed.
            Assert.True(result.DrainReleasedByDeviceDeath,
                "the drain did not return after liveness flipped underneath it:\n" + report);
            Assert.True(result.CountedDrains <= 1,
                "a single drain counted more than once:\n" + report);
            Assert.True(result.CountedDrains == 0 || result.CountedDrainMs > 0,
                "a drain that really blocked recorded no time:\n" + report);
            Assert.True(result.FenceSignaledAfterDeviceDeath,
                "a fence did not read signaled after the device died, which is what strands a retire pool "
                + "forever:\n" + report);
        }
    }
}
