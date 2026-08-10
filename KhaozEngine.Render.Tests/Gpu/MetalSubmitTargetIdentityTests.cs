using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// WHAT THE NATIVE METAL SUBMIT PATH WILL ACCEPT, device-free. Row 7 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>.
    ///
    /// <para><b>THE QUESTION IS IDENTITY AND NOT TYPE, AND A PROCESS REALLY DOES HOLD MORE THAN ONE OF THESE.</b>
    /// <see cref="MetalCompletionHandler.MaxRegisteredQueues"/> is four because a test assembly creates and
    /// disposes headless devices, and the design expects that many live at once. A type check therefore passes a
    /// command list belonging to a DIFFERENT native Metal device, and both members' exception messages have always
    /// claimed otherwise. What a cross-device submit would actually do is commit another queue's buffer while
    /// holding this device's submit lock, with this device's shared event encoded into it, so
    /// <see cref="MetalTimeline.LastSubmitted"/> would stop describing either device. A cross-device FENCE is
    /// worse, because it is silent: it polls the wrong counter and reports signalled for work this device never
    /// ran.</para>
    ///
    /// <para><b>THE DECIDING HALF IS STATIC, WHICH IS WHY THIS RUNS EVERYWHERE.</b> Both members take the identity
    /// they compare against rather than reading it off an instance, so two owners and two timelines are all a
    /// plain <c>[Fact]</c> needs and no <c>MTLDevice</c> is involved. The same split
    /// <see cref="MetalCompletionHandler.Deliver"/> takes for the completion path.</para>
    /// </summary>
    public sealed class MetalSubmitTargetIdentityTests : IDisposable
    {
        readonly MetalRingHarness _harness = new();

        /// <inheritdoc/>
        public void Dispose() => _harness.Dispose();

        MetalCommandList NewList(object owner) => _harness.NewList(owner);

        [Fact]
        public void AListThisDeviceCreatedIsAccepted()
        {
            object device = new();
            MetalCommandList list = NewList(device);

            Assert.Same(list, MetalGpuDevice.RequireList(list, device));
        }

        /// <summary>THE ONE THE TYPE CHECK MISSED: a real <see cref="MetalCommandList"/>, from another native
        /// Metal device.</summary>
        [Fact]
        public void AListFromAnotherNativeMetalDeviceIsRefusedByName()
        {
            object mine = new();
            object theirs = new();

            ArgumentException thrown =
                Assert.Throws<ArgumentException>(() => MetalGpuDevice.RequireList(NewList(theirs), mine));

            Assert.Equal("cl", thrown.ParamName);
            Assert.Contains("not created by this native Metal device", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("DIFFERENT native Metal device", thrown.Message, StringComparison.Ordinal);
        }

        /// <summary>And the arm that always worked, in the shape it actually turns up in: a decorator that WRAPS
        /// this device's own list is not this device's list, which is why the suite's own wrappers document
        /// submitting the inner one.</summary>
        [Fact]
        public void AListThatOnlyWrapsThisDevicesListIsStillRefused()
        {
            object device = new();
            using MetalCommandList real = NewList(device);
            using RecordingGpuCommandList wrapper = new(real);

            Assert.Throws<ArgumentException>(() => MetalGpuDevice.RequireList(wrapper, device));
        }

        [Fact]
        public void AFenceOnThisDevicesTimelineIsAccepted()
        {
            MetalTimeline timeline = new(new FakeMetalSharedEvent());
            using MetalGpuFence fence = timeline.CreateFence();

            Assert.Same(fence, MetalGpuDevice.RequireFence(fence, timeline));
        }

        /// <summary>
        /// The silent one. A fence from another device's timeline names a value on the wrong counter, so arming it
        /// here would leave it reporting signalled the moment THAT device reached the number, and a consumer
        /// polling it frees resources this device's GPU is still reading.
        /// </summary>
        [Fact]
        public void AFenceFromAnotherNativeMetalDeviceIsRefusedByName()
        {
            MetalTimeline mine = new(new FakeMetalSharedEvent());
            MetalTimeline theirs = new(new FakeMetalSharedEvent());
            using MetalGpuFence foreign = theirs.CreateFence();

            ArgumentException thrown =
                Assert.Throws<ArgumentException>(() => MetalGpuDevice.RequireFence(foreign, mine));

            Assert.Equal("fence", thrown.ParamName);
            Assert.Contains("names no value on this device's timeline", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("DIFFERENT native Metal device", thrown.Message, StringComparison.Ordinal);

            // And it was never armed, so the wrong counter was never written into it.
            Assert.Equal(0UL, foreign.Target);
        }

        [Fact]
        public void AFenceFromAnotherBackendIsStillRefused()
        {
            MetalTimeline timeline = new(new FakeMetalSharedEvent());

            Assert.Throws<ArgumentException>(
                () => MetalGpuDevice.RequireFence(new AlwaysSignaledFence(), timeline));
        }

        /// <summary>An <see cref="IGpuFence"/> that is not this backend's at all, which is the arm the type check
        /// already covered.</summary>
        sealed class AlwaysSignaledFence : IGpuFence
        {
            public bool Signaled => true;

            public void Reset()
            {
            }

            public void Dispose()
            {
            }
        }
    }
}
