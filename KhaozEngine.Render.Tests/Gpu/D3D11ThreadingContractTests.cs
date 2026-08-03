using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
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
    /// A RACE TEST THAT PASSES PROVES LESS THAN ONE THAT FAILS, and both are written so removing the lock they
    /// pin makes them fail rather than flake. The update race asserts on a torn PATTERN rather than on timing,
    /// and the resize race asserts on an invariant of the packed size rather than on which size won.
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
        /// not corrupt the ring's bookkeeping. Two writers filling the SAME range with two different repeated
        /// bytes make a lost lock observable as CONTENT: with the short lock the range is all
        /// <see cref="PatternA"/> or all <see cref="PatternB"/> at every instant, and without it a sample catches
        /// a half-copied range. The sampler runs under the submit lock, so it can only ever observe a tear that a
        /// writer left behind while not holding it.
        /// </para>
        /// <para>
        /// THE SEGMENT ASSERTION IS THE OTHER HALF, and it is what distinguishes this from a plain mutual-exclusion
        /// test. The frame is advanced twice before the race, so the current segment is 2, and every one of these
        /// writes has to land there: the write goes to the segment the NEXT submit will bind rather than the one
        /// the GPU is executing, and segments 0 and 1 have to come out untouched.
        /// </para>
        /// <para>
        /// The ring memory fake refuses a double map and a double unmap by name, so a lost lock around the
        /// mapping itself surfaces as a named exception from a writer rather than as a subtle miscount, and the
        /// map and unmap tallies are checked against each other afterwards for the same reason.
        /// </para>
        /// </summary>
        [Fact]
        public void AForeignThreadUpdate_RacingASubmit_IsSerializedAndLandsWholeInTheCurrentSegment()
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
            using var stop = new ManualResetEventSlim(false);
            using var writing = new CountdownEvent(2);

            Thread writerA = ForeignWriter(harness, PatternA, writing, stop, failures);
            Thread writerB = ForeignWriter(harness, PatternB, writing, stop, failures);
            writerA.Start();
            writerB.Start();
            Assert.True(writing.Wait(JoinBudget), "The foreign writer threads never started.");

            int tornSamples = 0;
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < SubmitIterations && clock.Elapsed < RaceBudget; i++)
            {
                D3D11CommandDrivers.Submit(
                    harness.SubmitLock, list, ref emitter, signal, fence: null, rings: harness.Allocator);

                lock (harness.SubmitLock)
                {
                    if (!IsUniform(harness.Memory.Segment(segmentBase, PatternBytes))) tornSamples++;
                }
            }

            stop.Set();
            Assert.True(writerA.Join(JoinBudget) && writerB.Join(JoinBudget),
                "A foreign writer thread never finished after the race was stopped.");

            Assert.True(failures.IsEmpty,
                $"{failures.Count} thread(s) failed: {string.Join(" | ", failures)}");
            Assert.Equal(0, tornSamples);

            // The writers really did write, and what they left behind is one pattern rather than a mixture.
            ReadOnlySpan<byte> landed = harness.Memory.Segment(segmentBase, PatternBytes);
            Assert.True(landed[0] == PatternA || landed[0] == PatternB,
                "Neither foreign writer reached the ring, so the race proved nothing.");
            Assert.True(IsUniform(landed), "The final segment contents are a mixture of both writers' patterns.");

            // The other two segments are untouched, which is the "current segment" half of decision U5.
            Assert.True(IsAllZero(harness.Memory.Segment(harness.Ring.FrameBaseBytes(0), PatternBytes)));
            Assert.True(IsAllZero(harness.Memory.Segment(harness.Ring.FrameBaseBytes(1), PatternBytes)));

            // And the mapping bookkeeping survived the race: every map was released except at most the one the
            // ring is holding now. A lost lock around the map shows up here as well as in the fake's own refusal.
            int outstanding = harness.Memory.MapCount - harness.Memory.UnmapCount;
            Assert.Equal(harness.Ring.IsMapped ? 1 : 0, outstanding);
        }

        /// <summary>
        /// AND THE SUBMIT ORDER IS THE OBSERVABLE ORDER while that race runs. The submit lock is taken ONCE
        /// around the unmap, the replay, the end-of-replay signal and the segment bookkeeping, so two submits
        /// cannot interleave and the value a submission signals orders the same way its commands reached the
        /// device. Asserted as a dense, gapless timeline across a submitter racing a foreign writer, since a
        /// submit path that released the lock between its steps would let the foreign write land inside a
        /// submission rather than between two.
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
            Thread writer = ForeignWriter(harness, PatternA, writing, stop, failures);
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
        /// THE SIZES CARRY THEIR OWN TEAR DETECTOR. Every queued size is <c>640 + n</c> by <c>480 + n</c> for the
        /// same n, so a half-applied size (a width from one request with a height from another) fails the
        /// arithmetic rather than needing a judgement about which request should have won. That is the property
        /// the packed-long queue buys, and it is invisible in a single-threaded test because there is nothing to
        /// tear against.
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

            // Everything the surface was asked to do arrived under the submit lock. The queue is the one caller
            // that must NOT hold it, and it reaches the surface not at all.
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
        /// THE ORDERING RULE, WHICH IS THE ONLY THING TWO LOCKS IN ONE BACKEND NEED: the submit lock is the OUTER
        /// lock and the creation gate is a STRICT LEAF, acquiring nothing while held. Pinned by holding the submit
        /// lock on one thread for the whole of a creation on another: if the gate ever waited on the submit lock,
        /// or the submit lock's holder ever waited on the gate, this deadlocks instead of returning.
        /// </summary>
        [Fact]
        public void TheCreationGate_NeverWaitsOnTheSubmitLock()
        {
            object submitLock = new();
            var gate = new D3D11CreationGate(driverConcurrentCreates: false);
            using var holding = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            using var created = new ManualResetEventSlim(false);

            var holder = new Thread(() =>
            {
                lock (submitLock)
                {
                    holding.Set();
                    release.Wait(JoinBudget);
                }
            })
            { IsBackground = true, Name = "submit-lock-holder" };

            holder.Start();
            Assert.True(holding.Wait(JoinBudget), "The holder thread never took the submit lock.");

            using (gate.Enter())
            {
                Assert.False(Monitor.IsEntered(submitLock));
                created.Set();
            }

            Assert.True(created.IsSet);
            release.Set();
            Assert.True(holder.Join(JoinBudget));
        }

        // ---- nothing waits under the submit lock ---------------------------------------------------------

        /// <summary>
        /// THE DRAIN REFUSES A CALLER THAT ALREADY HOLDS THE SUBMIT LOCK. <c>WaitForIdle</c> signals and flushes
        /// under the lock and then RELEASES it to wait, so the submission it is waiting for can still be made. A
        /// caller holding the lock re-enters rather than acquires, the release inside frees nothing, and the drain
        /// waits for work no other thread can submit. That is a nameless hang at teardown, so it is a named
        /// exception instead.
        /// </summary>
        [Fact]
        public void WaitForIdle_RefusesACallerHoldingTheSubmitLock()
        {
            object submitLock = new();
            var timeline = new FakeD3D11FenceTimeline { AutoCompleteAfterPolls = 1 };
            using var fences = new D3D11FenceSubsystem(timeline, submitLock);

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
        // countdown once it is running, so the racing test never measures a thread that never started.
        static Thread ForeignWriter(D3D11RingHarness harness, byte pattern, CountdownEvent running,
            ManualResetEventSlim stop, ConcurrentBag<Exception> failures)
            => new(() =>
            {
                byte[] payload = new byte[PatternBytes];
                Array.Fill(payload, pattern);
                try
                {
                    running.Signal();
                    while (!stop.IsSet) harness.Allocator.UpdateBuffer(harness.Ring, WriteOffset, payload);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            })
            { IsBackground = true, Name = FormattableString.Invariant($"foreign-writer-{pattern:X2}") };

        static bool IsUniform(ReadOnlySpan<byte> bytes)
        {
            for (int i = 1; i < bytes.Length; i++)
            {
                if (bytes[i] != bytes[0]) return false;
            }
            return true;
        }

        static bool IsAllZero(ReadOnlySpan<byte> bytes)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != 0) return false;
            }
            return true;
        }

        // A size is WHOLE when its width and height came from the same request, which the test's own numbering
        // makes checkable: width 640 + n and height 480 + n for one n. A width from one request paired with a
        // height from another is the half-applied resize this exists to catch.
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
