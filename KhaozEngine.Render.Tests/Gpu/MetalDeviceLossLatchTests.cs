using System;
using System.Threading.Tasks;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Decisions M-G4 and M-F6: the command-buffer error latch and the liveness token it flips. All device-free,
    /// over a plain <see cref="MetalCommandBufferFault"/> snapshot, so the latch, the once-only rule, the
    /// liveness flip, the header string and the once-only publication all run on Linux and Windows where there is no Metal
    /// at all.
    /// <para>
    /// THERE IS NO INCUMBENT BEHAVIOUR TO PORT HERE, which makes this the one diagnostics surface in the phase
    /// that is net-new rather than reproduced. The vendored fork reads <c>MTLCommandBuffer.status</c> in exactly
    /// one place, to decide whether waiting is worth it, and never reads <c>.error</c>, so a Metal device loss is
    /// invisible to the engine and to telemetry today. #427 asks for the latch.
    /// </para>
    /// <para>
    /// AND PHASE 3'S <c>CheckResult</c> LESSON APPLIES WITHOUT ITS FIX BEING NEEDED. There, the incumbent's check
    /// was <c>[Conditional("DEBUG")]</c>, so a latch hanging off it would never fire in Release and the row had
    /// to build an unconditional check first. Here the read is two message sends this backend makes itself, so
    /// there is nothing that can compile away, and the equivalent hazard would be putting the read behind a knob.
    /// It is not behind one.
    /// </para>
    /// </summary>
    public sealed class MetalDeviceLossLatchTests
    {
        static MetalCommandBufferFault Failed(MTLCommandBufferError code, string description = "")
            => new(MTLCommandBufferStatus.Error, code, description);

        [Fact]
        public void AHealthyCommandBuffer_LatchesNothing()
        {
            var liveness = new DeviceLiveness();
            var latch = new MetalDeviceLossLatch(liveness, new RecordingLogger());

            Assert.False(latch.Check(MetalCommandBufferFault.Completed, "waitUntilCompleted (teardown drain)"));

            Assert.False(latch.IsLost);
            Assert.Null(latch.HeaderValue);
            Assert.True(liveness.IsAlive);
        }

        /// <summary>
        /// EVERY FAILURE LATCHES, including the codes that look recoverable, and that is M-G4's ruling rather
        /// than an accident. The Vulkan sibling latches only on <c>VK_ERROR_DEVICE_LOST</c> and lets an ordinary
        /// failure be the caller's to report. The same triage is not available here, because the GPU seam has no
        /// way to resubmit a Metal command buffer whose work was discarded, so a frame that failed is followed by
        /// one that reads its results. Stopping is the conservative direction. This row exists so that ruling is
        /// asserted rather than assumed, and so a later reader who thinks the codes should be triaged finds the
        /// decision instead of a gap.
        /// </summary>
        [Theory]
        [InlineData("Timeout")]
        [InlineData("DeviceRemoved")]
        [InlineData("OutOfMemory")]
        [InlineData("InvalidResource")]
        [InlineData("PageFault")]
        public void EveryCommandBufferError_Latches(string codeName)
        {
            var code = Enum.Parse<MTLCommandBufferError>(codeName);
            var liveness = new DeviceLiveness();
            var latch = new MetalDeviceLossLatch(liveness, new RecordingLogger());

            Assert.True(latch.Check(Failed(code), "commit (frame submit)"));

            Assert.True(latch.IsLost);
            Assert.True(liveness.IsDead);
            Assert.Contains("MTLCommandBufferError" + codeName, latch.HeaderValue, StringComparison.Ordinal);
        }

        /// <summary>
        /// A status of <c>Error</c> with a nil error still latches, because either signal alone is enough.
        /// Requiring BOTH would let a driver that reported only one of them slip a failure past the latch
        /// silently, and the two agree on every path anyone has observed, so requiring either costs nothing.
        /// </summary>
        [Fact]
        public void AnErrorStatusWithNoCode_StillLatchesWithASearchableToken()
        {
            var liveness = new DeviceLiveness();
            var latch = new MetalDeviceLossLatch(liveness, new RecordingLogger());

            Assert.True(latch.Check(new MetalCommandBufferFault(MTLCommandBufferStatus.Error,
                MTLCommandBufferError.None, ""), "commit (frame submit)"));

            Assert.Equal("MTLCommandBufferStatusError at commit (frame submit)", latch.HeaderValue);
        }

        /// <summary>
        /// A code this build does not name gets a token as its NUMBER rather than falling through to nothing,
        /// because an unrecognised code is exactly the case where a reader most needs something to search for.
        /// Apple appends to this enumeration and the engine's copy is transcribed for the codes it reports.
        /// </summary>
        [Fact]
        public void AnUnknownCode_StillProducesASearchableToken()
        {
            var liveness = new DeviceLiveness();
            var latch = new MetalDeviceLossLatch(liveness, new RecordingLogger());

            latch.Check(Failed((MTLCommandBufferError)9999), "commit (frame submit)");

            Assert.Contains("MTLCommandBufferError(9999)", latch.HeaderValue, StringComparison.Ordinal);
        }

        /// <summary>
        /// THE HEADER CARRIES THE SITE AND THE DRIVER'S OWN SENTENCE, which are the two things a post-mortem
        /// cannot reconstruct. The token groups across sessions, the site says which call noticed first, and the
        /// description is what the driver said about it.
        /// </summary>
        [Fact]
        public void TheHeader_CarriesTheToken_TheSite_AndTheDriversSentence()
        {
            var latch = new MetalDeviceLossLatch(new DeviceLiveness(), new RecordingLogger());

            latch.Check(Failed(MTLCommandBufferError.Timeout, "Execution of the command buffer was aborted"),
                "waitUntilCompleted (teardown drain)");

            Assert.Equal(
                "MTLCommandBufferErrorTimeout at waitUntilCompleted (teardown drain) "
                + "(Execution of the command buffer was aborted)",
                latch.HeaderValue);
        }

        /// <summary>
        /// THE FIRST SITE WINS AND THE SECOND CHANGES NOTHING. A device that has gone reports failures from every
        /// later call too, so which one saw it FIRST is the only ordering information a post-mortem gets out of a
        /// completion stream Metal delivers on an arbitrary internal thread in no guaranteed order.
        /// </summary>
        [Fact]
        public void TheFirstFailureWins_AndALaterOneDoesNotOverwriteIt()
        {
            var latch = new MetalDeviceLossLatch(new DeviceLiveness(), new RecordingLogger());

            Assert.True(latch.Check(Failed(MTLCommandBufferError.Timeout), "the first site"));
            Assert.True(latch.Check(Failed(MTLCommandBufferError.DeviceRemoved), "the second site"));

            Assert.Equal("the first site", latch.Site);
            Assert.Contains("Timeout", latch.HeaderValue, StringComparison.Ordinal);
        }

        /// <summary>
        /// The latch reports true for a healthy reading once it is already lost, because the answer to "should I
        /// stop" does not depend on what this particular buffer did. Worth pinning: a caller that read false
        /// after a loss would carry on submitting into a device nothing can execute.
        /// </summary>
        [Fact]
        public void OnceLost_EvenAHealthyReadingAnswersLost()
        {
            var latch = new MetalDeviceLossLatch(new DeviceLiveness(), new RecordingLogger());
            latch.Check(Failed(MTLCommandBufferError.Internal), "the first site");

            Assert.True(latch.Check(MetalCommandBufferFault.Completed, "a later drain"));
        }

        /// <summary>
        /// Concurrency, because the real caller is a driver callback on an arbitrary thread: many threads
        /// latching at once produce exactly ONE record, one liveness flip and one logged error. Two records would
        /// be a race over which one the session header carries.
        /// </summary>
        [Fact]
        public void ManyThreadsLatchingAtOnce_ProduceOneRecordAndOneLogLine()
        {
            var liveness = new DeviceLiveness();
            var logger = new RecordingLogger();
            var latch = new MetalDeviceLossLatch(liveness, logger);

            Parallel.For(0, 64, i => latch.Check(Failed(MTLCommandBufferError.Timeout), "site " + i));

            Assert.True(latch.IsLost);
            Assert.True(liveness.IsDead);
            Assert.Single(logger.Errors);
            Assert.StartsWith("site ", latch.Site, StringComparison.Ordinal);
        }

        /// <summary>
        /// A site nobody named still reads as something. An empty string in a session header is worse than a
        /// placeholder, because it looks like a field that failed to write rather than a caller that failed to
        /// name itself.
        /// </summary>
        [Fact]
        public void AnUnnamedSite_ReadsAsAPlaceholder()
        {
            var latch = new MetalDeviceLossLatch(new DeviceLiveness(), new RecordingLogger());

            latch.Check(Failed(MTLCommandBufferError.Internal), "   ");

            Assert.Equal("an unnamed site", latch.Site);
        }

        /// <summary>
        /// THE LOG LINE IS PART OF THE DELIVERABLE. A field crash report is read by somebody who has the log and
        /// not the code, so the error names what happened, what the driver said, and what the engine will now do
        /// about every later release.
        /// </summary>
        [Fact]
        public void TheLogLine_SaysWhatHappenedAndWhatItMeansForEverythingAfter()
        {
            var logger = new RecordingLogger();
            var latch = new MetalDeviceLossLatch(new DeviceLiveness(), logger);

            latch.Check(Failed(MTLCommandBufferError.DeviceRemoved, "The GPU was removed"), "present");

            string line = Assert.Single(logger.Errors);
            Assert.Contains("MTLCommandBufferErrorDeviceRemoved", line, StringComparison.Ordinal);
            Assert.Contains("The GPU was removed", line, StringComparison.Ordinal);
            Assert.Contains("no-op", line, StringComparison.Ordinal);
            Assert.Contains("telemetry session header", line, StringComparison.Ordinal);
        }

        /// <summary>
        /// The liveness token is one-way and idempotent. There is deliberately no un-kill: a device that has been
        /// torn down does not come back, and reviving one would turn a stale wrapper into a call against a
        /// released object.
        /// </summary>
        [Fact]
        public void Liveness_IsOneWayAndIdempotent()
        {
            var liveness = new DeviceLiveness();
            Assert.True(liveness.IsAlive);

            liveness.MarkDead();
            liveness.MarkDead();

            Assert.True(liveness.IsDead);
            Assert.False(liveness.IsAlive);
        }

        /// <summary>
        /// The default token says ALIVE, which is the safe direction rather than the convenient one. Defaulting
        /// to dead would make every fence read signalled and every drain a no-op before any death, so a pool
        /// would free resources the GPU is still reading and the corruption would surface somewhere else.
        /// </summary>
        [Fact]
        public void TheDefaultLivenessToken_SaysAlive()
            => Assert.False(LiveDevice.Instance.IsDead);
    }
}
