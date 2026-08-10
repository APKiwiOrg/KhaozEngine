using System;
using System.IO;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// DECISION M-G5: <c>MetalFrameCapture</c> takes the native backend's command-queue POINTER instead of
    /// reflecting into Veldrid's private <c>_commandQueue</c> field, and the reflection that survives for the
    /// Veldrid Metal leg is its own named thing that a test can ask about directly.
    ///
    /// <para><b>WHAT WAS ACTUALLY WRONG WITH THE OLD SHAPE.</b> It was not that reflection is distasteful. It is
    /// that the reflection's failure mode is a silent one: a Veldrid field rename returns zero, the capture is
    /// skipped, and the session that armed it finds an empty output directory, which is indistinguishable from a
    /// missing <c>MTL_CAPTURE_ENABLED</c> and from an unarmed run. The native backend owns its queue, so on that
    /// path the failure mode is deleted rather than handled, and on the Veldrid path
    /// <see cref="VeldridMetalCommandQueue"/> is a member the row below asks a live device about.</para>
    ///
    /// <para><b>THE CAPTURE PATH IS SAFE TO EXECUTE ON AN ORDINARY RUN, WHICH IS A MEASUREMENT RATHER THAN AN
    /// ASSUMPTION.</b> <c>-startCaptureWithDescriptor:error:</c> in a process where capture was never enabled
    /// raises an Objective-C exception rather than answering false, and that is a process abort no managed
    /// <c>catch</c> can intercept. Measured on an Apple M2 Max under macOS 26:
    /// <c>-[MTLCaptureManager supportsDestination:]</c> answers NO for BOTH destinations without
    /// <c>MTL_CAPTURE_ENABLED=1</c>, so guarding on it means the start call is unreachable in exactly the case
    /// that would abort. That guard is what makes these rows runnable at all rather than a fourth thing the
    /// design has to take on trust.</para>
    ///
    /// <para><b>IN <c>NativeDeviceLifecycle</c> BECAUSE THE ARM IS PROCESS-GLOBAL.</b> <see cref="GpuFrameCapture"/>
    /// holds one armed path for the whole process, so two classes arming concurrently would consume each other's.
    /// <c>GpuFrameCaptureTests</c> moved into the same collection for that reason, and the collection's own
    /// definition names the arm as its third piece of shared state.</para>
    /// </summary>
    [Collection("NativeDeviceLifecycle")]
    public sealed class MetalFrameCaptureTests
    {
        readonly ITestOutputHelper _out;
        public MetalFrameCaptureTests(ITestOutputHelper o) => _out = o;

        /// <summary>
        /// A ZERO QUEUE IS REFUSED BEFORE ANY OBJECTIVE-C CALL, which is what makes this row runnable on Linux and
        /// Windows as a plain <c>[Fact]</c>. Zero is exactly what a Veldrid layout change produces, so this is the
        /// shape of the failure the reflection has always had, asserted rather than described.
        /// </summary>
        [Fact]
        public void StartWithNoQueue_IsRefusedBeforeTouchingMetal()
        {
            Assert.False(MetalFrameCapture.Start(IntPtr.Zero, Path.Combine(Path.GetTempPath(), "never.gputrace")));
        }

        /// <summary>
        /// STOP DOES NOT DRAIN WHEN NOTHING IS CAPTURING, which is what makes an unconditional call at a present
        /// boundary free. The drain is the caller's own <c>WaitForIdle</c> now that the capture holds no device,
        /// and calling it on every present would be a full GPU stall per frame for a debug feature nobody armed.
        /// </summary>
        [Fact]
        public void StopWithNothingCapturing_DoesNotDrain()
        {
            bool drained = false;
            MetalFrameCapture.Stop(() => drained = true);
            Assert.False(drained);
        }

        /// <summary>
        /// THE REFLECTION THE VELDRID LEG STILL NEEDS, ASKED OF A LIVE VELDRID METAL DEVICE. This is the row that
        /// turns the next Veldrid field rename into a red test instead of an empty output directory, and it is the
        /// reason the read is a named type rather than a step inside the capture. Dormant when the incumbent did
        /// not come up on Metal, which is every non-macOS leg.
        /// </summary>
        [GpuFact]
        public void VeldridsPrivateCommandQueueField_IsStillWhereTheReflectionLooks()
        {
            using GpuDeviceContext incumbent = GpuDeviceContext.CreateHeadless();
            if (incumbent.Backend != GpuBackendKind.Metal)
            {
                _out.WriteLine($"dormant: the incumbent device came up on {incumbent.Backend}, not Metal, so it "
                    + "has no Metal command queue to find.");
                return;
            }

            var wrapper = (VeldridGpuDevice)incumbent.GpuDevice;
            IntPtr queue = VeldridMetalCommandQueue.TryRead(wrapper.GraphicsDevice);

            _out.WriteLine($"Veldrid {VeldridMetalCommandQueue.FieldName} -> {queue}");
            Assert.NotEqual(IntPtr.Zero, queue);
        }

        /// <summary>A null device reads zero rather than throwing, because the capture path answers false on zero
        /// and a throw here would break a frame for a debug feature.</summary>
        [Fact]
        public void TheVeldridQueueRead_AnswersZeroForNothingRatherThanThrowing()
            => Assert.Equal(IntPtr.Zero, VeldridMetalCommandQueue.TryRead(null));

        /// <summary>
        /// THE NATIVE PATH, WITH A REAL QUEUE POINTER AND NO REFLECTION ANYWHERE. Both arms are honest: on an
        /// ordinary run Metal refuses the destination and <c>Start</c> answers false having started nothing, and
        /// on a run launched with <c>MTL_CAPTURE_ENABLED=1</c> it really starts, which is the arm that writes a
        /// trace. Asserting only the first would pass on a leg where capture works and prove nothing about it.
        /// </summary>
        [GpuFact]
        public void TheNativeQueuePointer_DrivesTheCaptureWithNoReflectionInIt()
        {
            if (!MetalDormancy.NativeDeviceAvailable(_out)) return;

            using GpuDeviceContext context = GpuDeviceContext.CreateHeadless(GpuBackendKind.MetalNative);
            var device = (MetalGpuDevice)context.GpuDevice;

            bool enabled = MetalFrameCapture.CaptureIsEnabledForThisProcess();
            _out.WriteLine($"MTL_CAPTURE_ENABLED reaches MTLCaptureManager: {enabled}");

            string path = Path.Combine(Path.GetTempPath(), $"ke-metal-{Guid.NewGuid():N}.gputrace");
            try
            {
                bool started = MetalFrameCapture.Start(device.Queue.Handle, path);
                _out.WriteLine($"start -> {started}");

                if (!enabled)
                {
                    Assert.False(started,
                        "Metal said it does not support the GPU-trace destination, so a capture must not have "
                        + "started. A true here means the destination guard and the start call disagree, which is "
                        + "the state the guard exists to make impossible.");
                    Assert.False(File.Exists(path));
                }
                else
                {
                    // THE ARM THAT WOULD OTHERWISE ASSERT NOTHING. Without this, a run launched with
                    // MTL_CAPTURE_ENABLED=1 where Start had broken would skip the block above (enabled is true)
                    // and the block below (started is false), so the one leg that can prove the capture path
                    // works would pass having checked nothing at all.
                    Assert.True(started,
                        "Metal supports the GPU-trace destination in this process, so the capture had to start. A "
                        + "false here is the queue pointer, the descriptor or the start call itself being wrong, "
                        + "and it is the regression this row exists to catch.");
                }

                MetalFrameCapture.Stop(device.WaitForIdle);

                if (started)
                {
                    Assert.True(File.Exists(path) || Directory.Exists(path),
                        "a capture that started and stopped wrote no .gputrace bundle at " + path);
                }
            }
            finally
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else if (File.Exists(path)) File.Delete(path);
            }
        }

        /// <summary>
        /// THE PRESENT BOUNDARY THE SWAPCHAIN ROW WILL CALL
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/581), driven here against a real device so it is a
        /// path that has executed rather than an Objective-C route nobody has ever taken. The arm is CONSUMED,
        /// which is the behaviour the append audit's third silent site was about: before this row an arm taken on
        /// a native Metal session stayed armed for ever and wrote nothing.
        /// <para>
        /// The trace itself is only written on a run launched with <c>MTL_CAPTURE_ENABLED=1</c>, and the assertion
        /// is written to hold either way for the reason the row above is.
        /// </para>
        /// </summary>
        [GpuFact]
        public void ThePresentBoundary_ConsumesAnArmedCapture()
        {
            if (!MetalDormancy.NativeDeviceAvailable(_out)) return;

            using GpuDeviceContext context = GpuDeviceContext.CreateHeadless(GpuBackendKind.MetalNative);
            var device = (MetalGpuDevice)context.GpuDevice;

            string path = Path.Combine(Path.GetTempPath(), $"ke-metal-{Guid.NewGuid():N}.gputrace");
            try
            {
                GpuFrameCapture.ArmNext(path);
                Assert.True(GpuFrameCapture.IsArmed);

                // The present that consumes the arm. On an ordinary run the start is refused and nothing is
                // capturing afterwards, but the arm is gone either way, which is the half this row is about.
                device.ServiceFrameCaptureAtPresentBoundary();
                Assert.False(GpuFrameCapture.IsArmed,
                    "the native present boundary left the arm set, so a capture armed on this backend would "
                    + "never be consumed by anything, which is the third silent site the Metal append had.");

                // And the next present, which is where a started capture ends. A no-op when nothing started.
                device.ServiceFrameCaptureAtPresentBoundary();
            }
            finally
            {
                // Never leave the process armed for whatever runs next, whichever way the rows above went.
                GpuFrameCapture.TryConsume(out _);
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
