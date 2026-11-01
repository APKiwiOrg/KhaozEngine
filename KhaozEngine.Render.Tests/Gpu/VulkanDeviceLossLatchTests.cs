using System;
using System.Threading.Tasks;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Decision V-G4 and V-F10: the device-loss latch and the liveness token it flips. All device-free, over a
    /// <c>VkResult</c> value and a fake fault source, so the latch, the once-only rule, the liveness flip, the
    /// header string and the fault append all run on a machine with no Vulkan loader.
    /// <para>
    /// THE REASON THIS IS NOT BUILT ON THE INCUMBENT'S SHAPE is one attribute: its <c>VulkanUtil.CheckResult</c>
    /// is <c>[Conditional("DEBUG")]</c>, so a Release build checks nothing and a latch hanging off it would never
    /// fire in the only configuration anybody ships. Issue #427 asks for exactly that latch, and it can only be
    /// honest if the check underneath it is unconditional.
    /// </para>
    /// </summary>
    public sealed class VulkanDeviceLossLatchTests
    {
        /// <summary>A healthy device reports nothing anywhere: no latch, no header field, and a liveness token
        /// that still says alive.</summary>
        [Fact]
        public void AHealthyDevice_LatchesNothing()
        {
            var liveness = new DeviceLiveness();
            var latch = new VulkanDeviceLossLatch(liveness, logger: new RecordingLogger());

            Assert.False(latch.Check(Result.Success, "vkDeviceWaitIdle"));

            Assert.False(latch.IsLost);
            Assert.Null(latch.HeaderValue);
            Assert.True(liveness.IsAlive);
        }

        /// <summary>
        /// AN ORDINARY FAILURE IS NOT A DEVICE LOSS and is deliberately not latched. It is the caller's to report
        /// or throw, because only the caller knows whether its own failed call is recoverable, and a latch that
        /// fired on an out-of-memory would turn a recoverable allocation failure into a dead device.
        /// </summary>
        [Theory]
        [InlineData(Result.ErrorOutOfHostMemory)]
        [InlineData(Result.ErrorOutOfDeviceMemory)]
        [InlineData(Result.ErrorInitializationFailed)]
        [InlineData(Result.Timeout)]
        [InlineData(Result.SuboptimalKhr)]
        public void AnOrdinaryResult_IsNotLatched(Result result)
        {
            var liveness = new DeviceLiveness();
            var latch = new VulkanDeviceLossLatch(liveness, logger: new RecordingLogger());

            Assert.False(latch.Check(result, "vkQueueSubmit"));

            Assert.False(latch.IsLost);
            Assert.True(liveness.IsAlive);
        }

        /// <summary>
        /// <c>VK_ERROR_DEVICE_LOST</c> latches AT THE FAULT SITE, with the site's own name, and flips liveness in
        /// the same breath. The site is carried because a device loss is reported by every later call too, so
        /// saying which one saw it FIRST is the only ordering information a post-mortem gets.
        /// </summary>
        [Fact]
        public void ADeviceLoss_LatchesAtTheSite_AndFlipsLiveness()
        {
            var liveness = new DeviceLiveness();
            var log = new RecordingLogger();
            var latch = new VulkanDeviceLossLatch(liveness, logger: log);

            Assert.True(latch.Check(Result.ErrorDeviceLost, "vkQueueSubmit (Submit)"));

            Assert.True(latch.IsLost);
            Assert.Equal(Result.ErrorDeviceLost, latch.ObservedResult);
            Assert.Equal("vkQueueSubmit (Submit)", latch.Site);
            Assert.True(liveness.IsDead);
            Assert.Single(log.Errors);
        }

        /// <summary>The persisted session header carries the result token and a useful site, even when unnamed.</summary>
        [Theory]
        [InlineData("vkDeviceWaitIdle (WaitForIdle)", "VK_ERROR_DEVICE_LOST at vkDeviceWaitIdle (WaitForIdle)")]
        [InlineData("  ", "VK_ERROR_DEVICE_LOST at an unnamed site")]
        public void TheHeaderValue_IsTheTokenAndTheSite(string site, string expected)
        {
            var latch = new VulkanDeviceLossLatch(new DeviceLiveness(), logger: new RecordingLogger());

            latch.Check(Result.ErrorDeviceLost, site);

            Assert.Equal(expected, latch.HeaderValue);
        }

        /// <summary>
        /// THE LATCH IS TAKEN EXACTLY ONCE, and every later site answers true without overwriting. One recorded
        /// reason with one recorded site is the only useful post-mortem: the FIRST site is the informative one,
        /// because a lost device reports itself from every call after it.
        /// </summary>
        [Fact]
        public void TheFirstSiteWins_AndLaterOnesStillAnswerTrue()
        {
            var log = new RecordingLogger();
            var latch = new VulkanDeviceLossLatch(new DeviceLiveness(), logger: log);

            Assert.True(latch.Check(Result.ErrorDeviceLost, "vkQueueSubmit"));
            Assert.True(latch.Check(Result.ErrorDeviceLost, "vkAcquireNextImageKHR"));
            Assert.True(latch.Check(Result.ErrorDeviceLost, "vkMapMemory"));

            Assert.Equal("vkQueueSubmit", latch.Site);
            // Logged once, not three times. A dead device is not three problems.
            Assert.Single(log.Errors);
        }

        /// <summary>Concurrent faults on different threads produce ONE record, which is what the interlocked claim
        /// is for: two threads can notice a loss in the same instant and two recorded reasons would be a race over
        /// which one the header carries.</summary>
        [Fact]
        public void ConcurrentFaults_ProduceOneRecord()
        {
            var log = new RecordingLogger();
            var latch = new VulkanDeviceLossLatch(new DeviceLiveness(), logger: log);

            Parallel.For(0, 64, i => latch.Check(Result.ErrorDeviceLost, "site" + i));

            Assert.True(latch.IsLost);
            Assert.Single(log.Errors);
            Assert.NotNull(latch.Site);
        }

        /// <summary>
        /// <c>VK_EXT_device_fault</c>'s detail is APPENDED to the header when a driver has one. The seam is here
        /// from the start rather than retrofitted, because a fault source added later must not change the latch,
        /// and because retrofitting the reporting after the first field crash wastes the crash.
        /// </summary>
        [Fact]
        public void AFaultDetail_IsAppendedToTheHeader()
        {
            var latch = new VulkanDeviceLossLatch(new DeviceLiveness(),
                new FakeFault("faulting address 0x7ffd0000, vendor fault 0x21"), new RecordingLogger());

            latch.Check(Result.ErrorDeviceLost, "vkQueueSubmit");

            Assert.Equal("VK_ERROR_DEVICE_LOST at vkQueueSubmit (faulting address 0x7ffd0000, vendor fault 0x21)",
                latch.HeaderValue);
            Assert.Equal("faulting address 0x7ffd0000, vendor fault 0x21", latch.FaultDetail);
        }

        /// <summary>
        /// A FAULT SOURCE THAT THROWS DOES NOT REPLACE THE DIAGNOSTIC. It is called during a device loss, against
        /// a device that has just died, so a second failure there would take away the first one's report at
        /// exactly the moment it mattered. The latch still records, the liveness still flips, and the reason is
        /// simply thinner.
        /// </summary>
        [Fact]
        public void AThrowingFaultSource_DoesNotBreakTheLatch()
        {
            var liveness = new DeviceLiveness();
            var log = new RecordingLogger();
            var latch = new VulkanDeviceLossLatch(liveness, new ThrowingFault(), log);

            Assert.True(latch.Check(Result.ErrorDeviceLost, "vkQueueSubmit"));

            Assert.True(liveness.IsDead);
            Assert.Equal("VK_ERROR_DEVICE_LOST at vkQueueSubmit", latch.HeaderValue);
            Assert.Single(log.Warns);
        }

        /// <summary>A fault source with nothing to say reads as no detail rather than as an empty parenthesis in
        /// the header.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void AnEmptyFaultDetail_IsNoDetail(string? detail)
        {
            var latch = new VulkanDeviceLossLatch(new DeviceLiveness(), new FakeFault(detail),
                new RecordingLogger());

            latch.Check(Result.ErrorDeviceLost, "vkQueueSubmit");

            Assert.Null(latch.FaultDetail);
            Assert.Equal("VK_ERROR_DEVICE_LOST at vkQueueSubmit", latch.HeaderValue);
        }

        /// <summary>
        /// V-F10's one-way contract, asserted on its own: the token flips once and there is deliberately no way
        /// back. A device that has been destroyed does not come back, and an un-kill would turn a stale wrapper
        /// into a call against freed memory.
        /// </summary>
        [Fact]
        public void Liveness_FlipsOnceAndOneWay()
        {
            var liveness = new DeviceLiveness();

            Assert.True(liveness.IsAlive);
            Assert.False(liveness.IsDead);

            liveness.MarkDead();
            liveness.MarkDead();

            Assert.False(liveness.IsAlive);
            Assert.True(liveness.IsDead);
        }

        /// <summary>
        /// The safe default is ALIVE. Defaulting to dead would make every fence read signalled and every drain a
        /// no-op, which is the failure V-F10 exists to produce only after death and is silent before it: a pool
        /// would free resources the GPU is still reading.
        /// </summary>
        [Fact]
        public void TheDefaultLivenessToken_IsAlive()
            => Assert.False(LiveDevice.Instance.IsDead);

        /// <summary>
        /// The unconditional result check, which is the whole point of <see cref="VulkanResultCodes"/>: it must
        /// throw in EVERY configuration, and this assembly is built the same way the shipped one is.
        /// </summary>
        [Fact]
        public void RequireThrowsForFailure_AndAcceptsSuccess()
        {
            VulkanResultCodes.Require(Result.Success, "vkCreateDevice");
            // A positive result is a SUCCESS code in Vulkan, which is the one that catches people out.
            VulkanResultCodes.Require(Result.Incomplete, "vkEnumeratePhysicalDevices");

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => VulkanResultCodes.Require(Result.ErrorIncompatibleDriver, "vkCreateInstance"));

            // VulkanResultCodes promises the call name verbatim and the stable result token in the message.
            Assert.Contains("vkCreateInstance", ex.Message, StringComparison.Ordinal);
            Assert.Contains("VK_ERROR_INCOMPATIBLE_DRIVER", ex.Message, StringComparison.Ordinal);
        }

        /// <summary><c>VK_SUBOPTIMAL_KHR</c> is a SUCCESS, which the swapchain row depends on and which is the
        /// Vulkan result-code fact most often got wrong.</summary>
        [Fact]
        public void SuboptimalIsASuccess()
        {
            Assert.False(VulkanResultCodes.IsFailure(Result.SuboptimalKhr));
            Assert.True(VulkanResultCodes.IsFailure(Result.ErrorDeviceLost));
            Assert.True(VulkanResultCodes.IsDeviceLoss(Result.ErrorDeviceLost));
            Assert.False(VulkanResultCodes.IsDeviceLoss(Result.ErrorOutOfDeviceMemory));
        }

        sealed class FakeFault(string? detail) : IVulkanDeviceFault
        {
            public string? DescribeFault() => detail;
        }

        sealed class ThrowingFault : IVulkanDeviceFault
        {
            public string? DescribeFault() => throw new InvalidOperationException("the device is gone");
        }
    }
}
