using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE PRESENT BOUNDARY, DEVICE-FREE: M-W2 to M-W7, every one of them, on every leg. Work-breakdown row 15
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/581).
    ///
    /// <para><b>THIS IS THE ROW MM7 IS ABOUT.</b> The design records that not one line of the incumbent's
    /// swapchain runs in CI anywhere, ever, and that the four regressions row 15 answers (a nil-drawable frame
    /// silently discarded, a depth texture recreated with no drain, an uncounted CPU throttle on
    /// <c>nextDrawable</c>, and a vsync toggle applied only inside three values of a deprecated enum) were all
    /// shipped for years with nothing able to see them. Three of the four are decisions rather than native calls,
    /// and this file is where they are now checked. The fourth (the unconditional <c>displaySyncEnabled</c>) is
    /// checked here as "the boundary always writes it" and on a device as "the layer reads back what was
    /// written".</para>
    ///
    /// <para><b>THE ORDERED LOG IS THE INSTRUMENT, and half these rows would pass on a transposition without
    /// it.</b> Present before apply, drain before the drawable-size write, capture after the present, acquire
    /// last: each is a relation between two calls, and a per-call counter cannot fail on a wrong order.</para>
    /// </summary>
    public sealed class MetalPresentBoundaryTests
    {
        const int FramesInFlight = 3;

        /// <summary>
        /// M-W1 AND M-W4's CONFIGURATION, WRITTEN ONCE AND BEFORE THE FIRST ACQUIRE. The order matters and is the
        /// incumbent's: a drawable acquired before the pixel format was written would be the wrong format, and the
        /// layer reports no error for it.
        /// </summary>
        [Fact]
        public void ConstructionConfiguresTheLayerAndThenTakesTheFirstDrawable()
        {
            Harness h = Harness.Windowed(scriptDrawables: 1);

            Assert.Equal(new MetalDrawableSize(1280u, 720u), h.Api.ConfiguredSize);
            Assert.False(h.Api.ConfiguredSrgb);
            Assert.True(h.Api.ConfiguredSync);

            // M-W4: the drawable queue and the uniform ring are ONE number, and this is the consumer of
            // KE_METAL_FRAMES_IN_FLIGHT that was not live until row 15.
            Assert.Equal(FramesInFlight, h.Api.ConfiguredMaximumDrawableCount);

            Assert.Equal(new[] { "configure", "acquire" }, h.Api.Log);
        }

        /// <summary>
        /// A ZERO-SIZED WINDOW IS CLAMPED BEFORE THE LAYER EVER SEES IT. A minimised window reports (0, 0) through
        /// the framebuffer-resize callback on the sibling backends and reaches this one the same way, and a layer
        /// configured at zero would take a drawable of a size no pipeline can rasterise into.
        /// </summary>
        [Fact]
        public void AZeroSizedHostIsClampedBeforeTheLayerIsConfigured()
        {
            Harness h = Harness.Windowed(scriptDrawables: 1, size: new MetalDrawableSize(0u, 0u));

            Assert.Equal(new MetalDrawableSize(1u, 1u), h.Api.ConfiguredSize);
            Assert.Equal(1u, h.Boundary.Framebuffer.Width);
            Assert.Equal(1u, h.Boundary.Framebuffer.Height);
        }

        /// <summary>
        /// THE ORDER OF A BOUNDARY, WHICH IS THE WHOLE OF 11.2 AND 11.4 IN ONE ASSERTION: take M-W6's present
        /// buffer, present the drawable the frame rendered into, service the capture, then acquire the next
        /// frame's. The capture is between them because a trace brackets whole frames (M-G5), and the acquire is
        /// last because it BLOCKS and holds no lock (M-W4, M-W8). The buffer is FIRST for the other half of
        /// M-W8: <c>-commandBuffer</c> blocks at the queue's own cap and every commit that could clear it takes
        /// the submit lock, so taking it inside that lock is a deadlock rather than a stall.
        /// </summary>
        [Fact]
        public void ABoundaryTakesItsBufferThenPresentsThenServicesTheCaptureThenAcquires()
        {
            Harness h = Harness.Windowed(scriptDrawables: 2);
            h.Api.Log.Clear();

            h.Boundary.Present();

            Assert.Equal(new[] { "acquirePresentBuffer", "present", "releaseDrawable", "capture", "acquire" },
                h.Api.Log);

            // AND THE PRESENT RODE THE BUFFER THAT ACQUIRE HANDED OUT, which is what says the two halves of the
            // split seam are still one present rather than a buffer taken and dropped.
            Assert.Equal(h.Api.PresentBuffers, h.Api.PresentedOn);
        }

        /// <summary>
        /// THE PRESENTED DRAWABLE IS THE ONE THE FRAME RENDERED INTO, and the next frame binds the next one. A
        /// boundary that presented the drawable it had just acquired would present a frame nothing had drawn into,
        /// which is a black window rather than an error, and is exactly the failure the acquire-at-the-boundary
        /// timing (M-W4) exists to avoid.
        /// </summary>
        [Fact]
        public void ThePresentedDrawableIsTheOneTheFrameRenderedInto()
        {
            Harness h = Harness.Windowed(scriptDrawables: 3);

            IntPtr firstTexture = h.Boundary.Framebuffer.Attachment.Texture;
            h.Boundary.Present();
            IntPtr secondTexture = h.Boundary.Framebuffer.Attachment.Texture;

            Assert.Single(h.Api.Presented);
            Assert.NotEqual(firstTexture, secondTexture);

            // The drawable presented is the one whose texture the framebuffer was pointing at, which is the
            // relation this whole timing exists to keep. Read off what the fake actually handed out rather than
            // off handle arithmetic, so the assertion says the relation instead of encoding the fake.
            Assert.Equal(h.Api.Handed[0].Drawable, h.Api.Presented[0]);
            Assert.Equal(h.Api.Handed[0].Texture, firstTexture);
            Assert.Equal(h.Api.Handed[1].Texture, secondTexture);
        }

        /// <summary>
        /// EVERY ACQUIRE PUBLISHES A NEW <c>Id</c>, WHICH IS ROW 12's CORRECTION ACTED ON. M-A6's guard is
        /// <c>if (framebuffer.Id == _framebuffer.Id) return;</c> and it returns BEFORE copying the incoming
        /// record, so a source whose texture moves under a stable number leaves the schedule describing the
        /// drawable the present has already moved past, with nothing anywhere reporting it.
        /// </summary>
        [Fact]
        public void EveryAcquirePublishesAFreshAttachmentSetId()
        {
            Harness h = Harness.Windowed(scriptDrawables: 4);
            var ids = new List<ulong> { h.Bound.Id };

            for (int i = 0; i < 3; i++)
            {
                h.Boundary.Present();
                ids.Add(h.Bound.Id);
            }

            Assert.Equal(4, ids.Distinct().Count());

            // AND THE OBJECT IS THE SAME ONE THROUGHOUT (M-W7). Stable object identity and a moving attachment-set
            // Id are two different claims, and the whole of row 12's correction is that they are.
            Assert.Equal(4uL, h.Boundary.Framebuffer.Generation);
        }

        /// <summary>
        /// M-W5, THE HEADLINE REGRESSION: a nil drawable binds the device-owned orphan target, the frame still
        /// COUNTS, and only its present is skipped. The incumbent builds the whole frame and throws every draw in
        /// it away with nothing logged and nothing counted.
        /// </summary>
        [Fact]
        public void ANilDrawableBindsTheOrphanTargetAndSkipsOnlyThePresent()
        {
            Harness h = Harness.Windowed(scriptDrawables: 1);
            h.Api.ScriptNoDrawable();
            h.Api.Log.Clear();

            h.Boundary.Present();

            // The frame that HAD a drawable was presented, and the acquire for the next one came back nil.
            Assert.Single(h.Api.Presented);
            Assert.True(h.Boundary.IsOrphanBound);
            Assert.False(h.Boundary.HasDrawable);
            Assert.Equal(1, h.Orphan.EnsureCount);
            Assert.Equal(h.Orphan.Handle, h.Boundary.Framebuffer.Attachment.Texture);

            // AND THE ORPHAN MATCHES THE FRAMEBUFFER'S PUBLISHED FORMAT, or every pipeline bound while it is up is
            // validated against the wrong output description on its first draw.
            Assert.Equal(GpuPixelFormat.B8G8R8A8UNorm, h.Orphan.LastFormat);
            Assert.Equal(h.Boundary.Framebuffer.Outputs.Colour[0], h.Orphan.LastFormat);

            // THE NEXT BOUNDARY IS THE ONE THAT SKIPS, because the nil arrived at an ACQUIRE and a present always
            // presents what the last acquire produced.
            h.Boundary.Present();

            Assert.Single(h.Api.Presented);
            Assert.Equal(2L, h.Boundary.FramesBegun);
            Assert.Equal(1L, h.Boundary.SkippedPresents);
        }

        /// <summary>
        /// A SKIPPED PRESENT IS NOT A SKIPPED FRAME, stated as the counter identity it is: <c>FramesBegun</c>
        /// counts EVERY boundary, because it is the denominator every per-frame figure in
        /// <c>GpuDeviceCounters</c> is divided by.
        /// </summary>
        [Fact]
        public void EveryBoundaryCountsIntoFramesBegunIncludingASkippedOne()
        {
            Harness h = Harness.Windowed(scriptDrawables: 1);

            // One good drawable, then nothing but nil for the rest.
            for (int i = 0; i < 5; i++) h.Boundary.Present();

            Assert.Equal(5L, h.Boundary.FramesBegun);
            Assert.Single(h.Api.Presented);
            Assert.Equal(4L, h.Boundary.SkippedPresents);
        }

        /// <summary>
        /// AND IT IS LOGGED ONCE, WHICH THE INCUMBENT DOES NOT DO AT ALL. Once per device rather than once per
        /// frame, because a minimised window produces one of these per frame for as long as it is down and a line
        /// each would bury the session log in the one state a reader most wants to search it.
        /// </summary>
        [Fact]
        public void TheFirstSkippedPresentWarnsExactlyOnce()
        {
            Harness h = Harness.Windowed(scriptDrawables: 1);

            for (int i = 0; i < 5; i++) h.Boundary.Present();

            Assert.Single(h.Logger.Warns);
            Assert.Contains("orphan target", h.Logger.Warns[0], StringComparison.Ordinal);
        }

        /// <summary>
        /// THE ORPHAN DIES AT THE NEXT SUCCESSFUL ACQUIRE AND NOT BEFORE, and it dies AFTER the framebuffer has
        /// been repointed. Releasing it first would leave the wrapper naming a destroyed texture for the length of
        /// one statement, which is the exact failure M-W5's device-owned lifetime rule exists to prevent.
        /// </summary>
        [Fact]
        public void TheOrphanIsReleasedAfterTheDrawableHasBeenPublished()
        {
            Harness h = Harness.Windowed(scriptDrawables: 1);
            h.Api.ScriptNoDrawable();
            h.Boundary.Present();

            Assert.True(h.Orphan.IsLive);

            h.Api.ScriptDrawable();
            h.Api.Log.Clear();
            h.Boundary.Present();

            Assert.False(h.Boundary.IsOrphanBound);
            Assert.False(h.Orphan.IsLive);

            // THE ORDER, WHICH IS THE CLAIM. The acquire republishes and the release follows it.
            int acquire = h.Api.Log.IndexOf("acquire");
            int release = h.Api.Log.IndexOf("orphanRelease");
            Assert.True(acquire >= 0 && release > acquire,
                "the orphan target was released before the acquire that replaced it had published, so the "
                + "swapchain framebuffer named a destroyed texture in between. Log: "
                + string.Join(", ", h.Api.Log));
        }

        /// <summary>
        /// M-W4's MEASUREMENT: every acquire is counted, because <c>nextDrawable</c> has no zero-timeout probe and
        /// a boundary cannot tell an instant acquire from a blocked one except by timing it. The seam's own doc
        /// says a backend that blocks the CPU on the acquire reports one per frame, and this is that backend.
        /// </summary>
        [Fact]
        public void EveryAcquireIsCountedAndRunsOneAheadOfTheFrames()
        {
            Harness h = Harness.Windowed(scriptDrawables: 4);

            for (int i = 0; i < 3; i++) h.Boundary.Present();

            Assert.Equal(3L, h.Boundary.FramesBegun);

            // ONE AHEAD, and the offset is a fact rather than an off-by-one: the first drawable is taken at
            // CREATION, before any frame exists, which is the incumbent's timing that M-W4 keeps.
            Assert.Equal(4L, h.Boundary.AcquireTotals.Count);
            Assert.Equal(4, h.Api.AcquireCount);
        }

        /// <summary>
        /// AND THE MILLISECONDS ARE REAL, driven off zero rather than asserted at it. A count with no time against
        /// it cannot be weighed, and MM4's exit criterion is stated as acquire wait PER FRAME, which is this
        /// figure over the count.
        /// </summary>
        [Fact]
        public void TheAcquireWaitCarriesTheTimeItSpent()
        {
            Harness h = Harness.Windowed(scriptDrawables: 2);
            h.Api.AcquireDelay = TimeSpan.FromMilliseconds(20);

            h.Boundary.Present();

            Assert.True(h.Boundary.AcquireTotals.TotalMs >= 15d,
                "a 20 ms acquire recorded " + h.Boundary.AcquireTotals.TotalMs.ToString("F3",
                    System.Globalization.CultureInfo.InvariantCulture)
                + " ms, so the pair is counting acquires without timing them.");
        }

        /// <summary>
        /// M-W6's PRESENT BUFFER OCCUPIES THE PLUS ONE. Section 6.1's bound is the frame depth PLUS ONE and the
        /// one has existed for this buffer since row 7 with nothing in it, so until this row the peak observed was
        /// one below the bound. This is the row that makes the bound tight.
        /// </summary>
        [Fact]
        public void ThePresentBufferIsCountedIntoTheUncommittedBound()
        {
            Harness h = Harness.Windowed(scriptDrawables: 3);

            Assert.Equal(0, h.Uncommitted.Peak);

            h.Boundary.Present();

            Assert.Equal(1, h.Uncommitted.Peak);
            Assert.Equal(0, h.Uncommitted.Outstanding);
            Assert.False(h.Uncommitted.ExceededBound);

            // A SKIPPED PRESENT TAKES NO BUFFER, which is the other half: the bound is about buffers HELD, and a
            // frame with no drawable never asks the queue for one. Three drawables were scripted, so the fourth
            // boundary is the one that skips.
            h.Api.ScriptNoDrawable();
            h.Boundary.Present();
            h.Boundary.Present();

            Assert.Equal(3, h.Api.PresentBuffers.Count);

            h.Boundary.Present();

            Assert.Equal(1L, h.Boundary.SkippedPresents);
            Assert.Equal(3, h.Api.PresentBuffers.Count);
            Assert.Equal(1, h.Uncommitted.Peak);
        }

        /// <summary>
        /// A QUEUE THAT WILL NOT MAKE A PRESENT BUFFER SKIPS THE PRESENT AND STILL RELEASES THE DRAWABLE, which
        /// is a device already in trouble rather than a state to throw out of a frame loop: whatever went wrong
        /// has already been latched by the buffer that saw it. The drawable release is the half worth pinning,
        /// because holding it would leak one per frame for as long as the queue stayed in that state.
        /// </summary>
        [Fact]
        public void APresentBufferTheQueueRefusesSkipsThePresentAndStillReleasesTheDrawable()
        {
            Harness h = Harness.Windowed(scriptDrawables: 2);
            h.Api.PresentBufferIsRefused = true;
            h.Api.Log.Clear();

            h.Boundary.Present();

            Assert.Empty(h.Api.Presented);
            Assert.Equal(new[] { "acquirePresentBuffer", "releaseDrawable", "capture", "acquire" }, h.Api.Log);

            // AND NOTHING WAS COUNTED AS HELD, because nothing was handed out to hold.
            Assert.Equal(0, h.Uncommitted.Peak);
            Assert.Equal(0, h.Uncommitted.Outstanding);
        }

        /// <summary>
        /// M-W7: A RESIZE APPLIES AT THE BOUNDARY, AFTER A DRAIN. The incumbent applies it inline on the calling
        /// thread with no drain anywhere. The drain is asserted to come FIRST, because a drain after the write is
        /// a drain that protected nothing.
        /// </summary>
        [Fact]
        public void AResizeAppliesAtTheBoundaryAfterADrain()
        {
            Harness h = Harness.Windowed(scriptDrawables: 2);

            h.Boundary.QueueResize(640u, 480u);

            // NOTHING HAS HAPPENED YET, which is the queueing half: the call stored a number and returned.
            Assert.Equal(new MetalDrawableSize(1280u, 720u), h.Api.LastDrawableSize);
            Assert.Equal(0, h.Drains);

            h.Api.Log.Clear();
            h.Boundary.Present();

            Assert.Equal(new MetalDrawableSize(640u, 480u), h.Api.LastDrawableSize);
            Assert.Equal(1, h.Drains);
            Assert.Equal(new[] { "acquirePresentBuffer", "present", "releaseDrawable", "drain",
                "drawableSize=640x480", "capture", "acquire" }, h.Api.Log);

            // AND THE FRAMEBUFFER FOLLOWS IT, under the same object.
            Assert.Equal(640u, h.Boundary.Framebuffer.Width);
            Assert.Equal(480u, h.Boundary.Framebuffer.Height);
        }

        /// <summary>
        /// A RESIZE TO ZERO IS CLAMPED AT THE APPLY, not refused. A minimised window is a state to survive rather
        /// than an error, and one by one is a layer that still answers every property.
        /// </summary>
        [Fact]
        public void AResizeToZeroIsClampedAtTheApply()
        {
            Harness h = Harness.Windowed(scriptDrawables: 2);

            h.Boundary.QueueResize(0u, 0u);
            h.Boundary.Present();

            Assert.Equal(new MetalDrawableSize(1u, 1u), h.Api.LastDrawableSize);
            Assert.Equal(new MetalDrawableSize(1u, 1u), h.Boundary.Size);
        }

        /// <summary>
        /// M-W2: A VSYNC CHANGE QUEUES AND IS THEN WRITTEN UNCONDITIONALLY. The incumbent writes
        /// <c>displaySyncEnabled</c> only when <c>MetalFeatures.MaxFeatureSet</c> equals one of three values of an
        /// enum deprecated since macOS 10.15, so on a machine outside that set the toggle silently does nothing.
        /// There is no condition here to assert the absence of, so what is asserted is that the write HAPPENS.
        /// </summary>
        [Fact]
        public void AVsyncChangeAppliesAtTheBoundaryUnconditionally()
        {
            Harness h = Harness.Windowed(scriptDrawables: 2);

            Assert.True(h.Boundary.AppliedSyncToVerticalBlank);
            h.Boundary.SyncToVerticalBlank = false;

            // THE GETTER ANSWERS THE REQUEST AND THE LAYER STILL HAS THE OLD VALUE, which is what "queued" means
            // and what a consumer reading back what it just set has to see.
            Assert.False(h.Boundary.SyncToVerticalBlank);
            Assert.True(h.Boundary.AppliedSyncToVerticalBlank);
            Assert.Null(h.Api.LastDisplaySync);

            h.Boundary.Present();

            Assert.False(h.Api.LastDisplaySync);
            Assert.False(h.Boundary.AppliedSyncToVerticalBlank);
        }

        /// <summary>
        /// AND A BOUNDARY WITH NOTHING QUEUED PAYS FOR NO DRAIN AT ALL. A drain per frame would put M-F5's counted
        /// wait on every single frame and report as a drain cost the design never intended to spend, which would
        /// also make the M2 criterion (under 0.2 ms of drain per frame) unreadable.
        /// </summary>
        [Fact]
        public void AnOrdinaryBoundaryDrainsNothing()
        {
            Harness h = Harness.Windowed(scriptDrawables: 5);

            for (int i = 0; i < 4; i++) h.Boundary.Present();

            Assert.Equal(0, h.Drains);
            Assert.DoesNotContain("drain", h.Api.Log);
        }

        /// <summary>
        /// A RESIZE AND A VSYNC CHANGE IN ONE BOUNDARY COST ONE DRAIN AND BOTH WRITES. Two applies in one frame
        /// would be two drains for one instant of ownership, and neither write may be dropped because the queue
        /// carries them independently.
        /// </summary>
        [Fact]
        public void AResizeAndAVsyncChangeShareOneDrain()
        {
            Harness h = Harness.Windowed(scriptDrawables: 2);

            h.Boundary.QueueResize(320u, 200u);
            h.Boundary.SyncToVerticalBlank = false;
            h.Boundary.Present();

            Assert.Equal(1, h.Drains);
            Assert.Equal(new MetalDrawableSize(320u, 200u), h.Api.LastDrawableSize);
            Assert.False(h.Api.LastDisplaySync);
        }

        /// <summary>
        /// M-W7's IDENTITY CLAIM: the framebuffer OBJECT survives every resize and every acquire, so anything
        /// holding it may cache it. On this API that is free rather than built, because there is no per-image view
        /// object for a resize to invalidate.
        /// </summary>
        [Fact]
        public void TheFramebufferObjectSurvivesEveryResizeAndEveryAcquire()
        {
            Harness h = Harness.Windowed(scriptDrawables: 4);
            MetalSwapchainFramebuffer first = h.Boundary.Framebuffer;

            h.Boundary.QueueResize(100u, 100u);
            h.Boundary.Present();
            h.Boundary.Present();
            h.Api.ScriptNoDrawable();
            h.Boundary.Present();

            Assert.Same(first, h.Boundary.Framebuffer);

            // AND ITS Outputs NEVER MOVED, which is the half that makes the identity useful: a resize changes the
            // size and never the format or the sample count, so every pipeline built against the window stays
            // valid across every one of them.
            Assert.Equal(GpuPixelFormat.B8G8R8A8UNorm, first.Outputs.Colour[0]);
            Assert.Null(first.Outputs.Depth);
            Assert.Equal(1, first.Outputs.SampleCount);
        }

        /// <summary>
        /// TEARDOWN RELEASES THE DRAWABLE, THE ORPHAN AND THE LAYER, in that order and once each. The device calls
        /// this after its own drain, which is what makes releasing a drawable a present may still be running safe.
        /// </summary>
        [Fact]
        public void DisposeReleasesTheHeldDrawableTheOrphanAndTheLayer()
        {
            Harness h = Harness.Windowed(scriptDrawables: 1);
            h.Api.ScriptNoDrawable();
            h.Boundary.Present();
            h.Api.ScriptDrawable();
            h.Boundary.Present();

            int releasedBefore = h.Api.Released.Count;
            h.Api.Log.Clear();

            h.Boundary.Dispose();

            Assert.Equal(releasedBefore + 1, h.Api.Released.Count);
            Assert.True(h.Api.IsDisposed);
            Assert.Equal(new[] { "releaseDrawable", "orphanRelease", "disposeApi" }, h.Api.Log);

            // AND A SECOND DISPOSE DOES NOTHING, because a device's teardown and a consumer's can both reach it.
            h.Api.Log.Clear();
            h.Boundary.Dispose();
            Assert.Empty(h.Api.Log);
        }

        /// <summary>A present after teardown is a no-op rather than a throw, matching every other member on this
        /// backend's dead-device posture: the frame loop above is not written to handle a failure here.</summary>
        [Fact]
        public void APresentAfterDisposeDoesNothing()
        {
            Harness h = Harness.Windowed(scriptDrawables: 2);
            h.Boundary.Dispose();
            h.Api.Log.Clear();

            h.Boundary.Present();

            Assert.Empty(h.Api.Log);
            Assert.Equal(0L, h.Boundary.FramesBegun);
        }

        sealed class Harness
        {
            internal FakeMetalSwapchainApi Api { get; private init; } = null!;
            internal FakeMetalOrphanTarget Orphan { get; private init; } = null!;
            internal MetalUncommittedBuffers Uncommitted { get; private init; } = null!;
            internal MetalPresentBoundary Boundary { get; private set; } = null!;
            internal RecordingLogger Logger { get; private init; } = null!;
            internal int Drains { get; private set; }

            /// <summary>The one submit lock, KEPT rather than passed inline, so the fake can be handed the same
            /// object and asked whether the caller was inside it. That is the whole instrument behind M-W8's
            /// rows.</summary>
            internal object SubmitLock { get; } = new();

            internal MetalBoundFramebuffer Bound
                => ((IMetalBoundFramebufferSource)Boundary.Framebuffer).AsBound;

            internal static Harness Windowed(int scriptDrawables, MetalDrawableSize? size = null,
                bool syncToVerticalBlank = true)
            {
                var api = new FakeMetalSwapchainApi();
                var orphan = new FakeMetalOrphanTarget(api.Log);
                var uncommitted = new MetalUncommittedBuffers(FramesInFlight, new RecordingLogger());
                var logger = new RecordingLogger();

                for (int i = 0; i < scriptDrawables; i++) api.ScriptDrawable();

                var harness = new Harness
                {
                    Api = api,
                    Orphan = orphan,
                    Uncommitted = uncommitted,
                    Logger = logger,
                };

                // BEFORE THE BOUNDARY IS BUILT, because the constructor takes the first drawable and that
                // acquire is one of the ones M-W8's claim covers.
                api.LockToWatch = harness.SubmitLock;

                harness.Boundary = new MetalPresentBoundary(api, orphan, uncommitted, new MetalAcquireWaits(),
                    harness.SubmitLock, harness.Drain, () => api.Note("capture"),
                    size ?? new MetalDrawableSize(1280u, 720u), MetalSwapchainPolicy.ColourSrgbRequested,
                    syncToVerticalBlank, FramesInFlight, logger);

                return harness;
            }

            void Drain()
            {
                Drains++;
                Api.Note("drain");
            }
        }
    }
}
