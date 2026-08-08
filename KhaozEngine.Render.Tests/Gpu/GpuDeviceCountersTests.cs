using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE ROUTE THE SOAK NUMBERS TAKE OUT OF THE DEVICE AND INTO A CAPTURE: from
    /// <see cref="IGpuDevice.Counters"/> through <see cref="GpuDeviceContext.Counters"/> and
    /// <see cref="GpuTelemetryChannels"/> into a telemetry session's sample rows. Device-free end to end, over a
    /// fake device, the same shape <see cref="GpuDeviceDiagnosticsTests"/> pins for the header fields.
    /// <para>
    /// THE TWO PROPERTIES WORTH PROTECTING. Absent is not zero, because zero stalls is the PASSING result and a
    /// backend that keeps no counters must not report the same row as one that counted and found nothing. And the
    /// two backpressure readings stay separate columns
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/499), because a non-zero off-timeline count beside a zero
    /// stall count is a specific diagnosis and adding them together destroys it.
    /// </para>
    /// </summary>
    public sealed class GpuDeviceCountersTests
    {
        // A device that reports whatever the test sets on it, right now. Everything else is inherited from the
        // counting fake, so this file adds one behaviour rather than a second device.
        sealed class CountingGpuDevice : IGpuDevice
        {
            readonly FakeGpuDevice _inner = new(GpuBackendKind.Direct3D11Native);

            internal GpuDeviceCounters Reported { get; set; }

            public GpuDeviceCounters Counters => Reported;

            public GpuBackendKind Backend => GpuBackendKind.Direct3D11Native;
            public GpuCapabilities Capabilities => _inner.Capabilities;
            public IGpuResourceFactory Factory => _inner.Factory;
            public IGpuFramebuffer? SwapchainFramebuffer => null;
            public IGpuSampler PointSampler => _inner.PointSampler;
            public IGpuSampler LinearSampler => _inner.LinearSampler;

            public void Submit(IGpuCommandList cl) { }
            public void Submit(IGpuCommandList cl, IGpuFence fence) { }
            public void WaitForIdle() { }
            public void UpdateBuffer<T>(IGpuBuffer b, uint o, ReadOnlySpan<T> d) where T : unmanaged { }
            public void UpdateBuffer<T>(IGpuBuffer b, uint o, T[] d) where T : unmanaged { }
            public void UpdateBuffer<T>(IGpuBuffer b, uint o, in T d) where T : unmanaged { }
            public void UpdateTexture(IGpuTexture t, byte[] d, uint x, uint y, uint w, uint h) { }
            public void UpdateTexture(IGpuTexture t, byte[] d, uint x, uint y, uint w, uint h, uint m, uint l) { }
            public MappedData Map(IGpuTexture s, GpuMapMode m) => throw new NotSupportedException();
            public void Unmap(IGpuTexture s) { }
            public MappedData Map(IGpuBuffer s, GpuMapMode m) => throw new NotSupportedException();
            public void Unmap(IGpuBuffer s) { }
            public void ResizeSwapchain(uint width, uint height) { }
            public void Present() { }
            public bool SyncToVerticalBlank { get; set; }
            public void Dispose() { }
        }

        static GpuBackendSelection Selection()
            => new(GpuBackendKind.Direct3D11Native, GpuBackendSource.UserPreference, null);

        // A soak window that stalled nothing, drained a little, and deferred one load-time write. The reading
        // gate 4 hopes to see.
        static GpuDeviceCounters CleanSoak()
            => new(framesBegun: 900_000, drainCount: 12, drainMs: 3.5, backpressureStallCount: 0,
                backpressureStallMs: 0d, offTimelineDeferred: 2, offTimelineOutstanding: 0,
                acquireWaitCount: 0, acquireWaitMs: 0d);

        // ---- absent is not zero ----------------------------------------------------------------------------

        /// <summary>
        /// THE DEFAULT VALUE ANSWERS "NOBODY COUNTED", which is the one distinction the whole gate turns on. A
        /// backend with no counters and a backend that counted a clean window both have zeros in every field, and
        /// only <see cref="GpuDeviceCounters.HasValue"/> tells them apart.
        /// </summary>
        [Fact]
        public void ADefaultCounterSetAnswersNothing()
        {
            var counters = default(GpuDeviceCounters);

            Assert.False(counters.HasValue);
            Assert.Equal(0L, counters.FramesBegun);
            Assert.Equal(0L, counters.DrainCount);
            Assert.Equal(0L, counters.BackpressureStallCount);
            Assert.Equal(0L, counters.OffTimelineDeferred);
        }

        /// <summary>A device that counted and found nothing is the PASSING soak result, and it has to be
        /// expressible. Same zeros, opposite meaning.</summary>
        [Fact]
        public void ACountedCleanWindowIsNotTheSameValueAsNoCountersAtAll()
        {
            var counted = new GpuDeviceCounters(1, 0, 0d, 0, 0d, 0, 0, 0, 0d);

            Assert.True(counted.HasValue);
            Assert.Equal(0L, counted.BackpressureStallCount);
            Assert.NotEqual(default(GpuDeviceCounters).HasValue, counted.HasValue);
        }

        /// <summary>The member was APPENDED with a default implementation, so every existing
        /// <see cref="IGpuDevice"/> kept compiling. Metal, Vulkan and the incumbent Direct3D11 path take it, which
        /// is correct rather than a gap: none of them has a fence drain or a segment ring to count.</summary>
        [Fact]
        public void ADeviceThatDoesNotOverrideTheMemberReportsNoCounters()
        {
            IGpuDevice device = new FakeGpuDevice();

            Assert.False(device.Counters.HasValue);
        }

        /// <summary>The context reads THROUGH to the device on every access rather than capturing at
        /// construction, because these numbers move on every frame and a captured copy would report the moment the
        /// context was built.</summary>
        [Fact]
        public void TheContextReadsTheDeviceLive()
        {
            var device = new CountingGpuDevice { Reported = CleanSoak() };
            using var ctx = new GpuDeviceContext(device, threadingCaps: null, threadingProbeFailure: null,
                Selection(), ownsDevice: true);

            Assert.Equal(0L, ctx.Counters.BackpressureStallCount);

            device.Reported = new GpuDeviceCounters(900_100, 12, 3.5, 4, 1.25, 2, 0, 0, 0d);

            Assert.Equal(4L, ctx.Counters.BackpressureStallCount);
            Assert.Equal(1.25, ctx.Counters.BackpressureStallMs);
        }

        // ---- the channel projection ------------------------------------------------------------------------

        /// <summary>
        /// A DEVICE THAT COUNTED NOTHING WRITES NO COLUMNS AT ALL. Emitting zeros for it would put a clean-looking
        /// stall count in every Metal and Vulkan capture, and an analyst comparing captures would read it as a
        /// backend that never stalls rather than one that never looked.
        /// </summary>
        [Fact]
        public void NoCountersProjectsToNoChannels()
        {
            Assert.Empty(GpuTelemetryChannels.For(default));

            var channels = new List<TelemetryChannel> { new("fps", 125) };
            GpuTelemetryChannels.AppendTo(channels, default);

            Assert.Single(channels);
        }

        /// <summary>
        /// THE SPELLINGS THEMSELVES, WRITTEN OUT ONCE. Every other assertion in this file looks a channel up BY
        /// the constant, so renaming the VALUE of one would leave the whole suite green while silently desyncing
        /// the three READMEs that quote these names and every capture already on disk. The names are a
        /// compatibility contract with those files: an analyst loading last month's jsonl beside today's has to
        /// find the same column in both. Changing one is a deliberate act, and this is the test it breaks first.
        /// </summary>
        [Fact]
        public void TheChannelSpellingsAreAContractWithCapturesOnDisk()
        {
            Assert.Equal("gpuFramesBegun", GpuTelemetryChannels.FramesBegun);
            Assert.Equal("gpuDrainCount", GpuTelemetryChannels.DrainCount);
            Assert.Equal("gpuDrainMs", GpuTelemetryChannels.DrainMs);
            Assert.Equal("gpuBackpressureStalls", GpuTelemetryChannels.BackpressureStalls);
            Assert.Equal("gpuBackpressureStallMs", GpuTelemetryChannels.BackpressureStallMs);
            Assert.Equal("gpuOffTimelineDeferred", GpuTelemetryChannels.OffTimelineDeferred);
            Assert.Equal("gpuOffTimelineOutstanding", GpuTelemetryChannels.OffTimelineOutstanding);
        }

        /// <summary>Every counter reaches a channel, under the spelling the constants name, and the count matches
        /// what <see cref="GpuTelemetryChannels.ChannelCount"/> promises so a caller can size its list
        /// once.</summary>
        [Fact]
        public void EveryCounterReachesAChannelUnderItsNamedSpelling()
        {
            IReadOnlyList<TelemetryChannel> channels = GpuTelemetryChannels.For(CleanSoak());

            Assert.Equal(GpuTelemetryChannels.ChannelCount, channels.Count);

            var byName = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (TelemetryChannel channel in channels) byName[channel.Name] = channel.Value;

            Assert.Equal(900_000d, byName[GpuTelemetryChannels.FramesBegun]);
            Assert.Equal(12d, byName[GpuTelemetryChannels.DrainCount]);
            Assert.Equal(3.5, byName[GpuTelemetryChannels.DrainMs]);
            Assert.Equal(0d, byName[GpuTelemetryChannels.BackpressureStalls]);
            Assert.Equal(0d, byName[GpuTelemetryChannels.BackpressureStallMs]);
            Assert.Equal(2d, byName[GpuTelemetryChannels.OffTimelineDeferred]);
            Assert.Equal(0d, byName[GpuTelemetryChannels.OffTimelineOutstanding]);
        }

        /// <summary>The append form joins a row a game already built rather than replacing it, which is how a
        /// frame sampler carrying its own numbers picks these up.</summary>
        [Fact]
        public void AppendingJoinsTheRowTheGameAlreadyBuilt()
        {
            var channels = new List<TelemetryChannel> { new("fps", 125), new("frameMs", 8.0) };

            GpuTelemetryChannels.AppendTo(channels, CleanSoak());

            Assert.Equal(2 + GpuTelemetryChannels.ChannelCount, channels.Count);
            Assert.Equal("fps", channels[0].Name);
            Assert.Equal(GpuTelemetryChannels.FramesBegun, channels[2].Name);
        }

        [Fact]
        public void AppendingToANullCollectionThrows()
            => Assert.Throws<ArgumentNullException>(() => GpuTelemetryChannels.AppendTo(null!, CleanSoak()));

        // ---- the two readings stay apart -------------------------------------------------------------------

        /// <summary>
        /// THE #499 REQUIREMENT, AS A ROW. A capture where the ring never stalled but a caller wrote off-timeline
        /// against in-flight work must report a zero stall count AND a non-zero off-timeline count, in two
        /// separately named columns. That combination is the diagnosis: the segment count is fine and a caller is
        /// writing against work still in flight. One folded column would read as a failed M3 and send the reader
        /// after the pipeline depth instead.
        /// </summary>
        [Fact]
        public void TheTwoBackpressureReadingsLandInSeparatelyNamedColumns()
        {
            var counters = new GpuDeviceCounters(
                framesBegun: 750_000, drainCount: 8, drainMs: 2.0, backpressureStallCount: 0,
                backpressureStallMs: 0d, offTimelineDeferred: 41, offTimelineOutstanding: 3,
                acquireWaitCount: 0, acquireWaitMs: 0d);

            string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".jsonl");
            try
            {
                using (var recorder = new TelemetryRecorder())
                {
                    recorder.Start(path);
                    recorder.Sample(30.0, GpuTelemetryChannels.For(counters));
                }

                string[] lines = File.ReadAllLines(path);
                using JsonDocument row = JsonDocument.Parse(lines[1]);

                Assert.Equal(0d, row.RootElement.GetProperty(GpuTelemetryChannels.BackpressureStalls).GetDouble());
                Assert.Equal(41d, row.RootElement.GetProperty(GpuTelemetryChannels.OffTimelineDeferred).GetDouble());
                Assert.Equal(3d, row.RootElement.GetProperty(GpuTelemetryChannels.OffTimelineOutstanding).GetDouble());
                Assert.NotEqual(
                    GpuTelemetryChannels.BackpressureStalls, GpuTelemetryChannels.OffTimelineDeferred);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        /// <summary>
        /// THE ACQUIRE-WAIT PAIR IS ITS OWN TWO COLUMNS (V-G6), and without it the acquire-model A/B has only
        /// mean frame time to read. On a machine pinned at its refresh rate both positions of that switch produce
        /// the same mean by construction, which is a gate that cannot read its own result, so these two are the
        /// numbers that actually see the stall.
        /// </summary>
        [Fact]
        public void TheAcquireWaitPairLandsInItsOwnTwoColumns()
        {
            var counters = new GpuDeviceCounters(
                framesBegun: 120_000, drainCount: 0, drainMs: 0d, backpressureStallCount: 0,
                backpressureStallMs: 0d, offTimelineDeferred: 0, offTimelineOutstanding: 0,
                acquireWaitCount: 119_998, acquireWaitMs: 780_000d);

            string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".jsonl");
            try
            {
                using (var recorder = new TelemetryRecorder())
                {
                    recorder.Start(path);
                    recorder.Sample(30.0, GpuTelemetryChannels.For(counters));
                }

                string[] lines = File.ReadAllLines(path);
                using JsonDocument row = JsonDocument.Parse(lines[1]);

                Assert.Equal(119_998d, row.RootElement.GetProperty(GpuTelemetryChannels.AcquireWaits).GetDouble());
                Assert.Equal(780_000d, row.RootElement.GetProperty(GpuTelemetryChannels.AcquireWaitMs).GetDouble());
                Assert.NotEqual(GpuTelemetryChannels.AcquireWaits, GpuTelemetryChannels.BackpressureStalls);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        /// <summary>A backend with no acquire to wait on passes ZERO rather than leaving the pair out, which is
        /// the honest reading on Direct3D 11 where a present hands the frame to the runtime and returns. The
        /// distinction between that and "nobody counted" is still <see cref="GpuDeviceCounters.HasValue"/>'s to
        /// make, for the whole set at once.</summary>
        [Fact]
        public void ABackendWithNoAcquireReportsZeroRatherThanNothing()
        {
            GpuDeviceCounters counted = CleanSoak();

            Assert.True(counted.HasValue);
            Assert.Equal(0L, counted.AcquireWaitCount);
            Assert.Equal(0d, counted.AcquireWaitMs);
            Assert.False(default(GpuDeviceCounters).HasValue);
        }

        /// <summary>
        /// A CUMULATIVE COUNTER SETTLES THE WINDOW BY SUBTRACTION, which is why the seam carries totals rather
        /// than the backend's per-frame rolls. Two rows sampled whenever the consumer felt like it still answer
        /// "how many stalls across this window" exactly, and M2's per-frame drain cost is the same subtraction
        /// over the frames between them.
        /// </summary>
        [Fact]
        public void TwoSampledRowsBracketTheWindowExactly()
        {
            var first = new GpuDeviceCounters(100_000, 400, 40d, 0, 0d, 2, 0, 0, 0d);
            var last = new GpuDeviceCounters(200_000, 900, 62d, 0, 0d, 2, 0, 0, 0d);

            long frames = last.FramesBegun - first.FramesBegun;
            double drainMsPerFrame = (last.DrainMs - first.DrainMs) / frames;

            Assert.Equal(0L, last.BackpressureStallCount - first.BackpressureStallCount);
            Assert.True(drainMsPerFrame < 0.2d, "M2's bar is under 0.2 ms of drain per frame.");
        }
    }
}
