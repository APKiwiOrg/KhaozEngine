using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE NATIVE DIRECT3D 11 THREADING CONTRACT (decision W4, and the W5 boundary): work-breakdown row 15 of
    /// <c>docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md</c>. The contract itself is written down in the
    /// package README's "Threading: the shipped contract" section, and this file is what makes each clause
    /// something other than a promise.
    /// <para>
    /// THE TWO RACES ARE THE POINT OF THE FILE. A foreign-thread device-level update racing a submit, and a
    /// concurrent resize racing a present, are the two interleavings the whole design is shaped around: they are
    /// the pair that issue #415 records as a frame-long monitor exited from the wrong thread, and neither is
    /// visible in a single-threaded test of either side. Both run device-free here, against the shipped machinery
    /// rather than a copy of it, so they run on macOS and Linux as well as Windows.
    /// <see cref="GpuDeviceLifecycleTests"/> is the real-device sibling and covers the process-wide create and
    /// dispose gate instead.
    /// </para>
    /// <para>
    /// A RACE TEST THAT PASSES PROVES LESS THAN ONE THAT FAILS, so both were run against a deliberately unlocked
    /// build to find out WHICH assertion fails there, rather than assuming the one that reads like the detector
    /// is it. The update race fails because its writers crash on the map pointer a submit's unmap withdraws
    /// mid-copy. The resize race fails because the surface starts receiving calls with no submit lock held. The
    /// torn-content and packed-size assertions are the ones that read like detectors and are not, and each is
    /// documented at its own test as what it actually is.
    /// </para>
    /// </summary>
    public sealed class D3D11ThreadingContractTests
    {
        // Long enough that a non-serialized copy would be caught mid-write rather than being a single store the
        // hardware happens to make atomic, short enough to fit a 256-byte uniform buffer's segment.
        const int PatternBytes = 64;
        const uint WriteOffset = 0;
        const byte PatternA = 0xA1;
        const byte PatternB = 0xB2;

        // The race loops are bounded twice: an iteration count that is plenty on a fast machine, and a wall clock
        // so a loaded CI runner ends the test rather than the test ending the runner.
        const int SubmitIterations = 400;
        static readonly TimeSpan RaceBudget = TimeSpan.FromSeconds(5);
        static readonly TimeSpan JoinBudget = TimeSpan.FromSeconds(30);

        // The update race WAITS for its writers instead of hoping they were scheduled. A first write has to land
        // before the measured window opens, and this many have to land inside it, with the window extending itself
        // in short extra rounds until they do or until RaceBudget above ends the whole race. A round is small on
        // purpose: the pause between rounds is what lets a starved writer through, and a hammering submit loop
        // that never pauses is the thing being starved by.
        const int MinimumRacingWrites = 8;
        const int ExtraRoundSubmits = 16;
        static readonly TimeSpan FirstWriteBudget = TimeSpan.FromSeconds(10);

        // The resize race's sizes, which carry their own consistency check: every width is 640 + n and every
        // height is 480 + n for the SAME n, so a half-applied size is arithmetic rather than a judgement call.
        const uint WidthBase = 640;
        const uint HeightBase = 480;
        const uint SizeCount = 64;

        // ---- the foreign-thread device-level update, racing a submit (W4) --------------------------------

        /// <summary>
        /// A DEVICE-LEVEL <c>UpdateBuffer</c> ON A FOREIGN THREAD, RACING A SUBMIT, IS SERIALIZED AGAINST IT,
        /// LANDS WHOLE, AND LANDS IN THE CURRENT SEGMENT. That is the off-timeline write of section 6.4 under
        /// exactly the conditions it is documented to be legal in: any thread, no recording required, one may be
        /// open.
        /// <para>
        /// TWO WRITERS AND ONE SUBMITTER, because one writer against a submit can only ever prove the write did
        /// not corrupt the ring's bookkeeping.
        /// </para>
        /// <para>
        /// THE PRIMARY DETECTOR IS THE WRITERS CRASHING, NOT THE CONTENT. Take the lock away and a submit's unmap
        /// withdraws the ring's mapping while a writer is copying through the pointer it just read, so the writer
        /// dies on the nulled pointer and lands in <c>failures</c>. That is deterministic: 5 unlocked runs out of
        /// 5. The two writers filling the SAME range with two different repeated bytes are the SECONDARY guard,
        /// and they are weaker than they read. Measured on its own, the under-lock sampler catches a half-copied
        /// range in roughly 40% of unlocked runs, because a tear is only visible for as long as it takes the
        /// writer to finish the copy. It is kept because it is the detector that would still work on a future
        /// path where the ring stays MAPPED across the submit: there a lost lock tears content without ever
        /// producing a null pointer to crash on, and the crash detector goes quiet.
        /// </para>
        /// <para>
        /// THE WINDOW WAITS FOR THE RACE INSTEAD OF HOPING FOR IT, which is what stops the whole thing passing
        /// vacuously without turning the guard itself into a coin flip. Both writers being scheduled once at
        /// startup and then starved for the entire window on a loaded runner would leave every assertion below
        /// true with nothing having raced at all, so the shared iteration counter is read on either side of the
        /// submit loop and enough writes have to have landed in between.
        /// </para>
        /// <para>
        /// READING THE COUNTER WAS NOT ENOUGH ON ITS OWN. The planned <see cref="SubmitIterations"/> submits are
        /// under a millisecond of work, and on one full-suite run the writers were simply not scheduled inside
        /// that millisecond: a 24 ms test failed on "No foreign write landed while the submit loop was running",
        /// then passed on the immediate rerun and 3 times out of 3 in isolation. A guard that fires on scheduling
        /// luck reports the runner rather than the code, so the window is waited for at both ends now. The writers
        /// have to land a FIRST write before it opens, which puts thread startup latency outside the measurement
        /// entirely, and it then extends itself in short rounds until <see cref="MinimumRacingWrites"/> writes have
        /// landed WHILE submits were running, bounded by the same wall clock as the race. Only writes counted
        /// inside a round of submits count, so the pause between rounds cannot satisfy the guard. Failing it now
        /// means the writers went seconds without being scheduled against a submit loop hammering the same lock,
        /// which is an environment worth failing on rather than a timing coincidence.
        /// </para>
        /// <para>
        /// THE SEGMENT ASSERTION IS THE OTHER HALF, and it is what distinguishes this from a plain mutual-exclusion
        /// test. An off-timeline write reaches EVERY segment (the resolution of
        /// https://github.com/APKiwiOrg/KhaozEngine/issues/484), and it does all of those copies inside ONE hold
        /// of the submit lock, so the ring has to come out carrying one writer's pattern in all three segments
        /// rather than a per-segment mixture. Two writers filling the same range with different bytes is what
        /// makes that checkable: a replication split across several holds of the lock would leave segment 2 as one
        /// writer and segment 0 as the other.
        /// </para>
        /// <para>
        /// The ring memory fake refuses a double map and a double unmap by name, so a lost lock around the
        /// mapping itself surfaces as a named exception from a writer rather than as a subtle miscount, and the
        /// map and unmap tallies are checked against each other afterwards for the same reason.
        /// </para>
        /// </summary>
        [Fact]
        public void AForeignThreadUpdate_RacingASubmit_IsSerializedAndLandsWholeInEverySegment()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: 3);
            var log = new D3D11EmitterCallLog();
            var emitter = new D3D11CountingEmitter(log);
            var signal = new D3D11SubmitSignalTests.FakeD3D11SubmitSignal();

            // Two frames in, so the current segment is 2 and a write landing in segment 0 is a visible failure
            // rather than the default. Nothing has submitted yet, so neither BeginFrame waits on anything.
            harness.Allocator.BeginFrame();
            harness.Allocator.BeginFrame();
            Assert.Equal(2, harness.Allocator.CurrentSegment);
            uint segmentBase = harness.Ring.FrameBaseBytes(harness.Allocator.CurrentSegment);

            using IGpuCommandList list = D3D11CommandDrivers.CreateDeferred();
            list.Begin();
            list.Draw(3);
            list.End();

            var failures = new ConcurrentBag<Exception>();
            var progress = new WriterProgress();
            using var stop = new ManualResetEventSlim(false);
            using var writing = new CountdownEvent(2);

            Thread writerA = ForeignWriter(harness, PatternA, writing, stop, failures, progress);
            Thread writerB = ForeignWriter(harness, PatternB, writing, stop, failures, progress);
            writerA.Start();
            writerB.Start();
            Assert.True(writing.Wait(JoinBudget), "The foreign writer threads never started.");

            // Thread startup happens BEFORE the measured window rather than inside it. The countdown above is
            // signalled on the way into the writer loop, so it means "running" and not yet "writing", and the
            // window that follows is the one whose racing has to be real.
            Assert.True(SpinWait.SpinUntil(() => progress.Iterations > 0, FirstWriteBudget),
                "No foreign writer landed a single device-level write in "
                + FirstWriteBudget.TotalSeconds.ToString(CultureInfo.InvariantCulture)
                + " seconds, so the writer threads never ran at all.");

            long writesDuring = 0;
            int tornSamples = 0;
            int submits = 0;
            var clock = Stopwatch.StartNew();
            for (int round = 0; ; round++)
            {
                int iterations = round == 0 ? SubmitIterations : ExtraRoundSubmits;
                long writesBefore = progress.Iterations;

                for (int i = 0; i < iterations && clock.Elapsed < RaceBudget; i++)
                {
                    D3D11CommandDrivers.Submit(
                        harness.SubmitLock, list, ref emitter, signal, fence: null, rings: harness.Allocator);
                    submits++;

                    lock (harness.SubmitLock)
                    {
                        if (!IsUniform(harness.Memory.Segment(segmentBase, PatternBytes))) tornSamples++;
                    }
                }

                // Only what landed while the submits were running is counted, so the pause below is outside the
                // measurement rather than a way to satisfy it.
                writesDuring += progress.Iterations - writesBefore;
                if (writesDuring >= MinimumRacingWrites || clock.Elapsed >= RaceBudget) break;

                // The planned round found nothing, so extend the race. The pause is what a writer starved by the
                // submit loop needs to get scheduled at all, and the round after it is where that shows up.
                Thread.Sleep(1);
            }

            clock.Stop();
            stop.Set();
            Assert.True(writerA.Join(JoinBudget) && writerB.Join(JoinBudget),
                "A foreign writer thread never finished after the race was stopped.");

            // THE PRIMARY DETECTOR: an unlocked build kills the writers on the map pointer the unmap withdraws.
            Assert.True(failures.IsEmpty,
                $"{failures.Count} thread(s) failed: {string.Join(" | ", failures)}");

            // And they really were writing WHILE the submits ran, so the assertion above is about a race rather
            // than about two threads that started and were then starved for the whole window. The window waited
            // for this rather than assuming it, so reaching the failure means seconds of starvation.
            Assert.True(writesDuring >= MinimumRacingWrites,
                "Only " + writesDuring.ToString(CultureInfo.InvariantCulture)
                + " foreign write(s) landed while the submit loop was running, across "
                + submits.ToString(CultureInfo.InvariantCulture) + " submits and "
                + clock.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)
                + " seconds of racing. The writers were starved rather than raced, so every assertion in this "
                + "test is vacuous.");

            // The secondary guard. See the class doc: this one catches roughly 40% of unlocked runs, and is here
            // for the future path where the ring stays mapped and there is no pointer to crash on.
            Assert.Equal(0, tornSamples);

            // The writers really did write, and what they left behind is one pattern rather than a mixture.
            ReadOnlySpan<byte> landed = harness.Memory.Segment(segmentBase, PatternBytes);
            Assert.True(landed[0] == PatternA || landed[0] == PatternB,
                "Neither foreign writer reached the ring, so the race proved nothing.");
            Assert.True(IsUniform(landed), "The final segment contents are a mixture of both writers' patterns.");

            // And so is every OTHER segment, with the SAME pattern: an off-timeline write replicates into all of
            // them inside one hold of the lock, so a per-segment mixture would mean the replication is not atomic.
            byte winner = landed[0];
            for (int segment = 0; segment < 3; segment++)
            {
                ReadOnlySpan<byte> bytes = harness.Memory.Segment(harness.Ring.FrameBaseBytes(segment), PatternBytes);
                Assert.True(IsUniform(bytes) && bytes[0] == winner,
                    "Segment " + segment.ToString(CultureInfo.InvariantCulture)
                    + " did not come out of the race carrying one writer's whole pattern, so the replicated "
                    + "off-timeline write is not one critical section.");
            }

            // And the mapping bookkeeping survived the race: every map was released except at most the one the
            // ring is holding now. A lost lock around the map shows up here as well as in the fake's own refusal.
            int outstanding = harness.Memory.MapCount - harness.Memory.UnmapCount;
            Assert.Equal(harness.Ring.IsMapped ? 1 : 0, outstanding);
        }

        /// <summary>
        /// THE TIMELINE BOOKKEEPING SURVIVES A CONCURRENT FOREIGN WRITER: one signalled value per submission,
        /// issued in order with nothing spent in between, and the current segment ending owned by the last of
        /// them. That is what the assertions below check, and the whole of it.
        /// <para>
        /// IT DOES NOT PIN THE SUBMIT LOCK, and claiming it did would be worse than claiming nothing. It passes
        /// with the lock removed entirely, 3 runs out of 3, because a foreign device-level write advances no
        /// timeline value and takes no segment, so nothing it can do to an unlocked submit is visible in a count.
        /// There is no interleaving detection here. What is left is still worth having, just smaller: the
        /// bookkeeping is not corrupted by concurrent ring traffic.
        /// </para>
        /// <para>
        /// THE SUBMIT LOCK AROUND A SUBMIT IS PINNED ELSEWHERE, by four tests that predate this file.
        /// <c>D3D11RingRecyclingTests.ASubmit_AlreadyHoldsTheSubmitLockWhenItUnmapsTheRings</c> has the unmap
        /// running NESTED in the submit's own acquisition rather than taking the lock for itself, which is the
        /// assertion that the bracket is one critical section and not three. Both theories of
        /// <c>D3D11SubmitSignalTests.TheSignal_IsRaisedWhileTheSubmitLockIsHeld</c> put the end-of-replay signal
        /// inside it, on both drivers. <c>D3D11RecordingDriverTests.SubmitTakesTheLockAroundTheReplay</c> covers
        /// the replay itself. A reader looking for the lock's coverage wants those four, not this.
        /// </para>
        /// </summary>
        [Fact]
        public void SubmitOrder_IsTheObservableOrder_EvenWithAForeignWriterRunning()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: 3);
            var log = new D3D11EmitterCallLog();
            var emitter = new D3D11CountingEmitter(log);
            var timeline = new FakeD3D11FenceTimeline();
            using var fences = new D3D11FenceSubsystem(timeline, harness.SubmitLock);

            using IGpuCommandList list = D3D11CommandDrivers.CreateDeferred();
            list.Begin();
            list.Draw(1);
            list.End();

            var failures = new ConcurrentBag<Exception>();
            using var stop = new ManualResetEventSlim(false);
            using var writing = new CountdownEvent(1);
            Thread writer = ForeignWriter(harness, PatternA, writing, stop, failures, new WriterProgress());
            writer.Start();
            Assert.True(writing.Wait(JoinBudget), "The foreign writer thread never started.");

            const int Submits = 32;
            for (int i = 0; i < Submits; i++)
            {
                D3D11CommandDrivers.Submit(
                    harness.SubmitLock, list, ref emitter, fences, fence: null, rings: harness.Allocator);
            }

            stop.Set();
            Assert.True(writer.Join(JoinBudget), "The foreign writer thread never finished.");
            Assert.True(failures.IsEmpty, $"{failures.Count} thread(s) failed: {string.Join(" | ", failures)}");

            // One value per submission, in order, with nothing spent in between: the foreign writer takes the
            // same lock and never advances the timeline.
            Assert.Equal(Submits, timeline.SignalCount);
            Assert.Equal((ulong)Submits, timeline.Issued);
            Assert.Equal((ulong)Submits, harness.Allocator.SegmentOwner(harness.Allocator.CurrentSegment));
        }

        // ---- the concurrent resize, racing a present (W3 under W4) ---------------------------------------

        /// <summary>
        /// A RESIZE QUEUED FROM A FOREIGN THREAD WHILE THE SUBMIT THREAD PRESENTS APPLIES WHOLE, AT A BOUNDARY,
        /// AND ONLY THERE. This is decision W3's queue, coalesce and apply under decision W4's one lock, driven
        /// by the interleaving it exists for: a window callback arriving at an arbitrary point of a present.
        /// <para>
        /// THE DETERMINISTIC DETECTOR IS THE LOCK ASSERTION AT THE END, where every call the surface receives
        /// after the constructor's own is checked to have arrived with the submit lock held. That is the one that
        /// failed 3 runs out of 3 against a deliberately unlocked build. The two assertions that read like
        /// detectors are forward-guards instead, and are labelled as such below rather than left to look like
        /// coverage they do not provide.
        /// </para>
        /// <para>
        /// <see cref="AssertWholeSize"/> IS A FORWARD-GUARD. Every queued size is <c>640 + n</c> by <c>480 + n</c>
        /// for the same n, so a width from one request paired with a height from another is arithmetic rather
        /// than a judgement about which request should have won. Against the CURRENT design it cannot fail: the
        /// pending size is ONE packed long, written whole and read whole, so no interleaving produces a mixed
        /// pair. It is kept for the day the queue becomes two fields, or grows a third value that has to agree
        /// with them, which is the exact change that makes a mixed pair reachable and the exact moment nobody
        /// would think to add the check.
        /// </para>
        /// <para>
        /// THE APPLIES-BOUNDED-BY-PRESENTS ASSERTION IS THE SAME KIND OF GUARD. Coalescing to the LAST requested
        /// size is what makes a drag-resize burst cost one <c>ResizeBuffers</c> per frame rather than one per
        /// event, but one packed slot cannot hold more than one pending size, so the bound holds by construction
        /// and no interleaving can break it. It becomes falsifiable the day the queue ACCUMULATES (a list of
        /// pending sizes, an apply per event), and that is the regression it is here to catch.
        /// </para>
        /// <para>
        /// THE APPLY IS PINNED AS A FOUR-CALL SEQUENCE, not merely as "a resize happened": every
        /// <c>ResizeBuffers</c> in the trace is preceded by the present it rode and by the release of the old
        /// views, and followed by the creation of the new ones at the same size. The release-before-resize half
        /// is the ordering rule <c>IDXGISwapChain::ResizeBuffers</c> enforces and the incumbent depends on
        /// silently, and the present-before-resize half is why a drag-resize does not present an undefined
        /// backbuffer.
        /// </para>
        /// <para>
        /// The fake surface keeps a plain list and is not thread-safe, which is deliberate: every call it
        /// receives arrives under the submit lock, so the test would fail on a corrupted trace if the queue ever
        /// touched it. <c>QueueResize</c> touches nothing native, which is the whole of W3.
        /// </para>
        /// <para>
        /// THE LOCK ASSERTION SKIPS THE FIRST CALL, AND THAT DEPENDS ON THE CONSTRUCTOR. <c>D3D11Swapchain</c>
        /// creates its initial attachments while it is being built, before there is a frame or a lock holder, so
        /// <c>calls[0]</c> is a <c>CreateAttachments</c> that legitimately owes no lock and every call after it
        /// owes one. If the constructor ever makes a second surface call, or none, the <c>Skip(1)</c> below moves
        /// with it, and forgetting to move it turns the strongest assertion in this test into a weaker one
        /// silently.
        /// </para>
        /// </summary>
        [Fact]
        public void AConcurrentResize_RacingAPresent_AppliesWholeAndOnlyAtTheBoundary()
        {
            object submitLock = new();
            var surface = new FakeD3D11SwapchainSurface(WidthBase, HeightBase) { SubmitLock = submitLock };
            using var swapchain = new D3D11Swapchain(
                surface, submitLock, WidthBase, HeightBase, syncToVerticalBlank: false);

            var failures = new ConcurrentBag<Exception>();
            using var stop = new ManualResetEventSlim(false);
            using var queueing = new CountdownEvent(1);

            var resizer = new Thread(() =>
            {
                try
                {
                    uint n = 0;
                    queueing.Signal();
                    while (!stop.IsSet)
                    {
                        n = (n + 1) % SizeCount;
                        swapchain.QueueResize(WidthBase + n, HeightBase + n);
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            })
            { IsBackground = true, Name = "resize-racer" };

            resizer.Start();
            Assert.True(queueing.Wait(JoinBudget), "The resizing thread never started.");

            int presents = 0;
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < SubmitIterations && clock.Elapsed < RaceBudget; i++)
            {
                Assert.Equal(0, swapchain.Present());
                presents++;
            }

            stop.Set();
            Assert.True(resizer.Join(JoinBudget), "The resizing thread never finished after the race was stopped.");
            Assert.True(failures.IsEmpty, $"{failures.Count} thread(s) failed: {string.Join(" | ", failures)}");

            IReadOnlyList<FakeSwapchainCall> calls = surface.Calls;
            int applied = calls.Count(c => c.Name == "ResizeBuffers");
            Assert.True(applied > 0, "No queued resize was ever applied, so the race proved nothing.");

            // COALESCED: a burst of thirty requests between two presents costs one ResizeBuffers, never thirty.
            // A forward-guard against an accumulating queue rather than a live detector: one packed slot holds
            // one pending size, so today this bound cannot be broken. See the doc above.
            Assert.True(applied <= presents,
                $"{applied} resizes were applied across {presents} presents, so more than one landed at a "
                + "boundary.");

            for (int i = 0; i < calls.Count; i++)
            {
                if (calls[i].Name != "ResizeBuffers") continue;

                Assert.Equal("ReleaseAttachments", calls[i - 1].Name);
                Assert.Equal("Present", calls[i - 2].Name);
                Assert.Equal("CreateAttachments", calls[i + 1].Name);
                Assert.Equal(calls[i].Detail, calls[i + 1].Detail);
                AssertWholeSize(calls[i].Detail);
            }

            // THE DETERMINISTIC DETECTOR (3 of 3 unlocked). Everything the surface was asked to do arrived under
            // the submit lock. The queue is the one caller that must NOT hold it, and it reaches the surface not
            // at all. The skipped first call is the constructor's own CreateAttachments, per the doc above.
            Assert.All(calls.Skip(1), call => Assert.True(call.HeldTheSubmitLock,
                $"{call} arrived without the submit lock held."));

            // The framebuffer ends on a size that was actually requested, whole, and matching the backbuffer.
            swapchain.ApplyPendingResize();
            Assert.Equal(surface.BackbufferWidth, swapchain.Framebuffer.Width);
            Assert.Equal(surface.BackbufferHeight, swapchain.Framebuffer.Height);
            AssertWholeSize(FormattableString.Invariant(
                $"{swapchain.Framebuffer.Width}x{swapchain.Framebuffer.Height}"));
        }

        // ---- the creation gate (W4's creation clause) ----------------------------------------------------

        /// <summary>
        /// A DRIVER THAT CREATES CONCURRENTLY GETS NO LOCK AT ALL, which is the "free-threaded" half of the
        /// clause and the reason the gate is a type rather than an unconditional <c>lock</c>: paying a monitor
        /// per creation on every machine to protect the drivers that need it is exactly the shape decision W4
        /// removes everywhere else.
        /// </summary>
        [Fact]
        public void TheCreationGate_TakesNoLockWhenTheDriverCreatesConcurrently()
        {
            var gate = new D3D11CreationGate(driverConcurrentCreates: true);

            Assert.False(gate.Serializes);
            using (gate.Enter()) Assert.False(gate.IsEnteredByCurrentThread);
        }

        /// <summary>A driver that does not gets the short lock, held for the creation and nothing longer.
        /// </summary>
        [Fact]
        public void TheCreationGate_SerializesWhenTheDriverDoesNot()
        {
            var gate = new D3D11CreationGate(driverConcurrentCreates: false);

            Assert.True(gate.Serializes);
            using (gate.Enter()) Assert.True(gate.IsEnteredByCurrentThread);
            Assert.False(gate.IsEnteredByCurrentThread);
        }

        /// <summary>
        /// AN UNKNOWN THREADING ANSWER SERIALIZES. The probe behind <c>DriverConcurrentCreates</c> is a
        /// diagnostic that degrades to "unknown" on every failure path, so reading its silence as a yes would bet
        /// a driver's stability on whether a log line came back. The safe direction costs one uncontended monitor
        /// per creation.
        /// </summary>
        [Theory]
        [InlineData(null, true)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void TheCreationGate_TreatsAnUnknownProbeAnswerAsNo(bool? concurrentCreates, bool expectSerializes)
        {
            GpuThreadingCaps? caps = concurrentCreates is bool yes
                ? new GpuThreadingCaps(DriverCommandLists: false, DriverConcurrentCreates: yes)
                : null;

            Assert.Equal(expectSerializes, D3D11CreationGate.For(caps).Serializes);
        }

        /// <summary>
        /// TWO THREADS CREATING AT ONCE ARE SERIALIZED, asserted the only way a lock scope can be observed from
        /// outside: hold it and watch the other thread fail to finish, then watch it finish once the hold is
        /// released. The wait after the release is what proves it was blocked rather than broken.
        /// </summary>
        [Fact]
        public void TheCreationGate_BlocksASecondCreatorWhileOneIsInside()
        {
            var gate = new D3D11CreationGate(driverConcurrentCreates: false);
            using var started = new ManualResetEventSlim(false);
            using var finished = new ManualResetEventSlim(false);

            var second = new Thread(() =>
            {
                started.Set();
                using (gate.Enter()) finished.Set();
            })
            { IsBackground = true, Name = "second-creator" };

            bool blocked;
            using (gate.Enter())
            {
                second.Start();
                Assert.True(started.Wait(JoinBudget), "The second creating thread never started.");
                blocked = !finished.Wait(TimeSpan.FromMilliseconds(250));
            }

            Assert.True(finished.Wait(JoinBudget), "The second creation never completed after the gate released.");
            Assert.True(blocked,
                "Two creations ran at once on a driver reporting DriverConcurrentCreates false.");
            Assert.True(second.Join(JoinBudget));
        }

        /// <summary>
        /// THE GATE HOLDS NOTHING BUT ITS OWN LOCK, which is the leaf property the ordering rule rests on: the
        /// submit lock is OUTER, the gate is INNER, and the gate is a STRICT LEAF that acquires nothing and waits
        /// on nothing while held. A type whose entire instance state is one <c>object</c> it created itself
        /// cannot reach the submit lock, or anything that could, so there is no state through which the leaf
        /// claim can be violated. Asserted over the type's instance fields, which is where that change would
        /// first show up.
        /// <para>
        /// THE NEVER-WAITS HALF IS STRUCTURAL, AND IT IS REVIEWED RATHER THAN EXECUTABLE. <c>D3D11CreationGate</c>
        /// is 99 lines that enter one monitor and exit it: no wait, no second lock, no call out of the type. The
        /// threaded test that used to sit here asserted NOTHING, because the gate holds no reference to the
        /// test's own lock object, so "the gate did not wait on it" was true by construction and would have
        /// stayed true however the gate was written. This assertion is smaller and real: it fails on the change
        /// that would actually make the leaf claim false, which is this type growing a second thing to hold.
        /// </para>
        /// </summary>
        [Fact]
        public void TheCreationGate_IsAStrictLeaf_WithNoFieldButItsOwnLock()
        {
            FieldInfo[] fields = typeof(D3D11CreationGate)
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            FieldInfo only = Assert.Single(fields);
            Assert.Equal(typeof(object), only.FieldType);
            Assert.True(only.IsInitOnly,
                $"{only.Name} is not readonly, so the gate's one lock can be swapped after construction.");
        }

        // ---- nothing waits under the submit lock ---------------------------------------------------------

        /// <summary>
        /// THE DRAIN REFUSES A CALLER THAT ALREADY HOLDS THE SUBMIT LOCK. <c>WaitForIdle</c> signals and flushes
        /// under the lock and then RELEASES it to wait, so the submission it is waiting for can still be made. A
        /// caller holding the lock re-enters rather than acquires, the release inside frees nothing, and the drain
        /// waits for work no other thread can submit. That is a nameless hang at teardown, so it is a named
        /// exception instead.
        /// <para>
        /// THIS IS THE LIVE CASE, and it is the only one that throws. The two cases where the drain is not a
        /// drain at all return quietly even from under the lock, which is the ordering the theory below pins.
        /// </para>
        /// </summary>
        [Fact]
        public void WaitForIdle_RefusesACallerHoldingTheSubmitLock()
        {
            object submitLock = new();
            var timeline = new FakeD3D11FenceTimeline { AutoCompleteAfterPolls = 1 };
            using var fences = new D3D11FenceSubsystem(timeline, submitLock);

            // A live device with the real drain on, which is what makes the guard the reachable check here.
            Assert.False(fences.IsDeviceDead);
            Assert.True(fences.RealDrainEnabled);

            lock (submitLock)
            {
                InvalidOperationException ex = Assert.Throws<InvalidOperationException>(fences.WaitForIdle);
                Assert.Contains("submit lock", ex.Message, StringComparison.Ordinal);
            }

            // And it still drains normally from outside, so the guard costs the ordinary path nothing.
            fences.WaitForIdle();
            Assert.Equal(1, timeline.FlushCount);
        }

        /// <summary>
        /// THE REFUSAL IS THE LIVE DRAIN'S ONLY, AND THAT IS AN ORDERING RULE. The dead-device return (X3) and the
        /// <c>KE_D3D11_REAL_DRAIN</c> return are both checked BEFORE the submit-lock guard, so the two calls that
        /// do nothing stay quiet even from a caller holding the lock. Teardown is exactly where the two meet: a
        /// caller draining inside the frame's critical section is the same shape that runs against a device which
        /// has just died, and X3 promises that caller a no-op rather than an exception. The kill switch has the
        /// same claim on it, since it is documented to restore the empty method body and not to swap one
        /// behaviour for a different throw. Neither return touches anything, so neither can be harmed by running
        /// under the lock.
        /// </summary>
        [Theory]
        [InlineData(true, true)]    // dead device, real drain on
        [InlineData(false, false)]  // live device, kill switch down
        [InlineData(true, false)]   // both at once, which is what a torn-down process looks like
        public void WaitForIdle_UnderTheSubmitLock_ReturnsQuietlyWhereItIsNotADrain(bool dead, bool realDrain)
        {
            object submitLock = new();
            var timeline = new FakeD3D11FenceTimeline { AutoCompleteAfterPolls = 1 };
            var liveness = new FakeD3D11DeviceLiveness { IsDead = dead };
            using var fences = new D3D11FenceSubsystem(timeline, submitLock, liveness, realDrain);

            lock (submitLock) fences.WaitForIdle();

            // Quiet means quiet, and it is the same nothing the call makes from outside the lock: no signal, no
            // flush, no poll, and nothing counted.
            Assert.Equal(0, timeline.SignalCount);
            Assert.Equal(0, timeline.FlushCount);
            Assert.Equal(0, timeline.PollCount);

            fences.BeginFrame();
            Assert.Equal(0, fences.LastFrameDrain.Count);
        }

        /// <summary>
        /// OPENING A FRAME REFUSES THE SAME CALLER, for the same reason with a different mechanism.
        /// <c>BeginFrame</c> waits for the GPU to finish with the segment it opens, which is up to a frame, and
        /// decision W4 caps the submit lock at microseconds. On the event-query fence mechanism it is worse than
        /// slow, because every completion poll re-enters that lock, so the wait would also shut out the
        /// submission that would end it.
        /// </summary>
        [Fact]
        public void RingBeginFrame_RefusesACallerHoldingTheSubmitLock()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: 3);

            lock (harness.SubmitLock)
            {
                InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                    harness.Allocator.BeginFrame);
                Assert.Contains("submit lock", ex.Message, StringComparison.Ordinal);
            }

            harness.Allocator.BeginFrame();
            Assert.Equal(1, harness.Allocator.CurrentSegment);
        }

        // ---- fixtures ------------------------------------------------------------------------------------

        // One foreign thread doing device-level writes of a single repeated byte until told to stop. Signals the
        // countdown once it is running, so the racing test never measures a thread that never started, and counts
        // its own iterations so the test can tell "raced and found nothing" from "never got scheduled".
        // THE COUNTDOWN IS SIGNALLED BEFORE THE FIRST WRITE, on the way into the loop, so it means running and
        // not writing. A caller that needs the writer to be actually WRITING waits on the counter as well, which
        // is what the update race does before it opens its measured window.
        static Thread ForeignWriter(D3D11RingHarness harness, byte pattern, CountdownEvent running,
            ManualResetEventSlim stop, ConcurrentBag<Exception> failures, WriterProgress progress)
            => new(() =>
            {
                byte[] payload = new byte[PatternBytes];
                Array.Fill(payload, pattern);
                try
                {
                    running.Signal();
                    while (!stop.IsSet)
                    {
                        harness.Allocator.UpdateBuffer(harness.Ring, WriteOffset, payload);
                        progress.Advance();
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            })
            { IsBackground = true, Name = FormattableString.Invariant($"foreign-writer-{pattern:X2}") };

        // How many device-level writes the foreign writers have made, shared by however many of them a test
        // starts. Interlocked on both ends because the point of reading it is to compare two instants across
        // threads, and a torn long would make the vacuity check itself unreliable.
        sealed class WriterProgress
        {
            long _iterations;

            internal long Iterations => Interlocked.Read(ref _iterations);

            internal void Advance() => Interlocked.Increment(ref _iterations);
        }

        static bool IsUniform(ReadOnlySpan<byte> bytes)
        {
            for (int i = 1; i < bytes.Length; i++)
            {
                if (bytes[i] != bytes[0]) return false;
            }
            return true;
        }

        // A size is WHOLE when its width and height came from the same request, which the test's own numbering
        // makes checkable: width 640 + n and height 480 + n for one n. A FORWARD-GUARD, not a live detector: the
        // pending size is one packed long today, so a mixed pair is unreachable and this cannot fail. It is here
        // for the day the queue becomes two fields or grows a value that has to agree with them.
        static void AssertWholeSize(string detail)
        {
            string[] parts = detail.Split('x');
            Assert.Equal(2, parts.Length);
            uint width = uint.Parse(parts[0], CultureInfo.InvariantCulture);
            uint height = uint.Parse(parts[1], CultureInfo.InvariantCulture);

            Assert.True(width >= WidthBase && width < WidthBase + SizeCount, $"unexpected width in {detail}");
            Assert.True(width - WidthBase == height - HeightBase,
                $"{detail} pairs a width and a height from two different resize requests.");
        }
    }
}
