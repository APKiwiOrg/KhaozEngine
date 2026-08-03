using System;
using System.Threading;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE END-OF-REPLAY SIGNAL WHERE THE TWO ROWS MEET (decision C5, section 10.3): the fence subsystem defines
    /// the signal, the drivers own the submit path, and these pin that a submit raises it ONCE, AFTER the last
    /// command of that submission, UNDER the device's submit lock, on both drivers.
    /// <para>
    /// All three of those are invisible from either side alone. A fence-lifecycle test drives the subsystem
    /// directly, so it can say what a signal does and never where one happens, and a driver test has no fence in
    /// it at all. Getting the placement wrong is silent in the way that matters most: a signal raised before the
    /// replay names a point the GPU reaches before the submission is issued, so a fence polled there reports work
    /// complete that has not happened and a retire pool frees resources the GPU is still reading.
    /// </para>
    /// <para>
    /// Device-free on every operating system, because both halves already are: the submit path is written in
    /// engine-owned handle types and the timeline behind the subsystem is an interface.
    /// </para>
    /// </summary>
    public sealed class D3D11SubmitSignalTests
    {
        // ---- once per submit, after the last op, under the lock ----

        /// <summary>
        /// ONE SUBMIT IS ONE SIGNAL, on both drivers. The constant-buffer ring reads a dense timeline, so a
        /// submission that signalled twice or not at all would leave the counter describing a submission stream
        /// that never happened.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void EverySubmit_RaisesExactlyOneSignal(bool immediate)
        {
            var log = new D3D11EmitterCallLog();
            var emitter = new D3D11CountingEmitter(log);
            var signal = new FakeD3D11SubmitSignal();
            object submitLock = new();

            using IGpuCommandList list = Recorder(immediate, emitter);
            list.Begin();
            list.Draw(1);
            list.End();

            D3D11CommandDrivers.Submit(submitLock, list, ref emitter, signal);
            Assert.Equal(1, signal.SignalCount);
            Assert.Null(signal.LastFence);

            list.Begin();
            list.Draw(2);
            list.End();
            D3D11CommandDrivers.Submit(submitLock, list, ref emitter, signal);

            Assert.Equal(2, signal.SignalCount);
        }

        /// <summary>
        /// AND IT LANDS AFTER THE LAST COMMAND OF THAT SUBMISSION, which is the placement decision C5 spells out
        /// and the one a reader cannot check by looking at either row. Asserted against the emitter call log
        /// itself: the signal records how many calls had been made when it arrived, and that has to be all of
        /// them.
        /// <para>
        /// The two drivers reach it from opposite directions and the assertion is the same for both, which is the
        /// point. The deferred one has just replayed the whole stream into the emitter, and the immediate one
        /// emitted during record and has nothing left to emit at submit.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheSignal_LandsAfterTheLastEmittedCommand(bool immediate)
        {
            var log = new D3D11EmitterCallLog();
            var emitter = new D3D11CountingEmitter(log);
            var signal = new FakeD3D11SubmitSignal(log);
            object submitLock = new();

            using IGpuCommandList list = Recorder(immediate, emitter);
            list.Begin();
            list.Draw(1);
            list.Draw(2);
            list.End();

            D3D11CommandDrivers.Submit(submitLock, list, ref emitter, signal);

            Assert.Equal(new[] { "Begin()", "Draw(1,1,0,0)", "Draw(2,1,0,0)", "End()" }, log.Trace);
            Assert.Equal(log.TotalCalls, signal.EmitterCallsAtLastSignal);
        }

        /// <summary>
        /// THE SIGNAL IS RAISED UNDER THE SUBMIT LOCK, on both drivers. Signalling is a context call, so a signal
        /// outside the lock races the next submission's replay and the two can reach the immediate context at
        /// once. Same <c>Monitor.IsEntered</c> shape the fence timeline's own lock assertions use.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheSignal_IsRaisedWhileTheSubmitLockIsHeld(bool immediate)
        {
            var log = new D3D11EmitterCallLog();
            var emitter = new D3D11CountingEmitter(log);
            object submitLock = new();
            var signal = new FakeD3D11SubmitSignal(submitLock: submitLock);

            using IGpuCommandList list = Recorder(immediate, emitter);
            list.Begin();
            list.Draw(1);
            list.End();

            D3D11CommandDrivers.Submit(submitLock, list, ref emitter, signal);

            Assert.True(signal.LastSignalHeldTheSubmitLock);
        }

        /// <summary>
        /// A SUBMIT THAT NAMES NO SINK SIGNALS NOTHING AND REPLAYS EXACTLY AS IT DID, which is what keeps every
        /// existing call site working while no shipped path constructs a device. The trailing arguments are
        /// optional for that reason and not for convenience.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ASubmitWithNoSignalSink_ReplaysAndSignalsNothing(bool immediate)
        {
            var log = new D3D11EmitterCallLog();
            var emitter = new D3D11CountingEmitter(log);

            using IGpuCommandList list = Recorder(immediate, emitter);
            list.Begin();
            list.Draw(7);
            list.End();

            D3D11CommandDrivers.Submit(new object(), list, ref emitter);

            Assert.Equal(new[] { "Begin()", "Draw(7,1,0,0)", "End()" }, log.Trace);
        }

        /// <summary>
        /// SUBMITTING THE SAME SEALED LIST TWICE SIGNALS TWICE on the deferred driver, because a stream is
        /// replayable and each replay is its own submission with its own point on the timeline. Deferred only:
        /// the immediate driver's second submit replays nothing, so the two are not the same claim.
        /// </summary>
        [Fact]
        public void ADoubleSubmitOfOneSealedList_SignalsTwiceOnTheDeferredDriver()
        {
            var log = new D3D11EmitterCallLog();
            var emitter = new D3D11CountingEmitter(log);
            var timeline = new FakeD3D11FenceTimeline();
            using var fences = new D3D11FenceSubsystem(timeline, new object());
            object submitLock = new();

            using IGpuCommandList list = D3D11CommandDrivers.CreateDeferred();
            list.Begin();
            list.Draw(1);
            list.End();

            D3D11CommandDrivers.Submit(submitLock, list, ref emitter, fences);
            D3D11CommandDrivers.Submit(submitLock, list, ref emitter, fences);

            Assert.Equal(2, timeline.SignalCount);
            Assert.Equal(2UL, timeline.Issued);
            Assert.Equal(new[] { "Begin()", "Draw(1,1,0,0)", "End()", "Begin()", "Draw(1,1,0,0)", "End()" },
                log.Trace);
        }

        /// <summary>A submit the drivers REJECT signals nothing, because the replay throws before the signal is
        /// reached. An unsealed list emitted no commands, so there is no point on the timeline for it to
        /// name.</summary>
        [Fact]
        public void ARejectedSubmit_SignalsNothing()
        {
            var log = new D3D11EmitterCallLog();
            var emitter = new D3D11CountingEmitter(log);
            var signal = new FakeD3D11SubmitSignal();

            using IGpuCommandList halfRecorded = D3D11CommandDrivers.CreateDeferred();
            halfRecorded.Begin();
            halfRecorded.Draw(1);

            Assert.Throws<InvalidOperationException>(
                () => D3D11CommandDrivers.Submit(new object(), halfRecorded, ref emitter, signal));
            Assert.Equal(0, signal.SignalCount);
        }

        // ---- through the real subsystem: the timeline and the fence ----

        /// <summary>
        /// THE WIRING END TO END, through the shipped subsystem rather than a fake sink: a fenceless submit
        /// advances the timeline. The counter has to track the submission stream whether or not anyone asked to
        /// observe it, because a later fence's value only covers the earlier work if the earlier work took a
        /// value too, and the constant-buffer ring's segment recycling reads the same counter.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AFencelessSubmit_AdvancesTheTimeline(bool immediate)
        {
            var log = new D3D11EmitterCallLog();
            var emitter = new D3D11CountingEmitter(log);
            var timeline = new FakeD3D11FenceTimeline();
            using var fences = new D3D11FenceSubsystem(timeline, new object());
            object submitLock = new();

            using IGpuCommandList list = Recorder(immediate, emitter);
            list.Begin();
            list.Draw(1);
            list.End();

            D3D11CommandDrivers.Submit(submitLock, list, ref emitter, fences);

            Assert.Equal(1, timeline.SignalCount);
            Assert.Equal(1UL, timeline.Issued);
        }

        /// <summary>
        /// AND A SUBMITTED FENCE IS ARMED WITH THAT SUBMISSION'S VALUE, not with the timeline's position at some
        /// other moment. Two submissions deep, so a fence armed with the wrong one is visible: the fence belongs
        /// to the second, so it reads unsignalled while the GPU has only reached the first.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ASubmittedFence_IsArmedWithThatSubmissionsValue(bool immediate)
        {
            var log = new D3D11EmitterCallLog();
            var emitter = new D3D11CountingEmitter(log);
            var timeline = new FakeD3D11FenceTimeline();
            using var fences = new D3D11FenceSubsystem(timeline, new object());
            object submitLock = new();
            IGpuFence fence = fences.CreateFence();

            using IGpuCommandList earlier = Recorder(immediate, emitter);
            earlier.Begin();
            earlier.Draw(1);
            earlier.End();
            D3D11CommandDrivers.Submit(submitLock, earlier, ref emitter, fences);

            using IGpuCommandList ours = Recorder(immediate, emitter);
            ours.Begin();
            ours.Draw(2);
            ours.End();
            D3D11CommandDrivers.Submit(submitLock, ours, ref emitter, fences, fence);

            Assert.Equal(2UL, ((D3D11GpuFence)fence).Target);

            timeline.Completed = 1UL;
            Assert.False(fence.Signaled);

            timeline.Completed = 2UL;
            Assert.True(fence.Signaled);
        }

        /// <summary>
        /// A FENCE WITH NO SINK TO ARM IT IS REFUSED, which is the one combination of the two optional arguments
        /// that is always a defect. Accepting it would leave the fence unarmed, so it reads unsignalled forever
        /// and whatever waits on it hangs, and the hang surfaces in the retire pool rather than at the submit
        /// that caused it.
        /// </summary>
        [Fact]
        public void AFenceWithNoSignalSink_IsRefusedAtTheSubmit()
        {
            var log = new D3D11EmitterCallLog();
            var emitter = new D3D11CountingEmitter(log);
            var timeline = new FakeD3D11FenceTimeline();
            using var fences = new D3D11FenceSubsystem(timeline, new object());

            using IGpuCommandList list = D3D11CommandDrivers.CreateDeferred();
            list.Begin();
            list.End();

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => D3D11CommandDrivers.Submit(new object(), list, ref emitter, null, fences.CreateFence()));

            Assert.Contains("fence subsystem", ex.Message, StringComparison.Ordinal);
            Assert.Equal(0, timeline.SignalCount);
            Assert.Equal(0, log.TotalCalls);
        }

        // ---- Fixtures ----

        static IGpuCommandList Recorder(bool immediate, D3D11CountingEmitter emitter) => immediate
            ? D3D11CommandDrivers.Create(D3D11RecordMode.Immediate, emitter)
            : D3D11CommandDrivers.CreateDeferred();

        /// <summary>
        /// A signal sink with no timeline behind it, which is what lets a test ask WHERE in a replay the signal
        /// arrived rather than what it did. It records the emitter's call count at the moment it was raised, so
        /// "after the last emitted command" is an assertion about the shipped submit path rather than about a
        /// helper standing in for it.
        /// </summary>
        internal sealed class FakeD3D11SubmitSignal : ID3D11SubmitSignal
        {
            readonly D3D11EmitterCallLog? _log;
            readonly object? _submitLock;

            internal FakeD3D11SubmitSignal(D3D11EmitterCallLog? log = null, object? submitLock = null)
            {
                _log = log;
                _submitLock = submitLock;
            }

            /// <summary>How many submissions have signalled through this sink.</summary>
            internal int SignalCount { get; private set; }

            /// <summary>The fence the last submission carried, or null if it was fenceless.</summary>
            internal IGpuFence? LastFence { get; private set; }

            /// <summary>What the emitter call log stood at when the last signal arrived. Null log leaves it at
            /// zero, so a test that cares wires the log in.</summary>
            internal int EmitterCallsAtLastSignal { get; private set; }

            /// <summary>Whether the last signal ran with the submit lock held. Null until something signals, and
            /// always null while no lock was wired in, so a test cannot read a false negative out of a fake it
            /// forgot to set up.</summary>
            internal bool? LastSignalHeldTheSubmitLock { get; private set; }

            /// <inheritdoc/>
            public ulong SignalEndOfReplay(IGpuFence? fence)
            {
                SignalCount++;
                LastFence = fence;
                if (_log is not null) EmitterCallsAtLastSignal = _log.TotalCalls;
                if (_submitLock is object submitLock) LastSignalHeldTheSubmitLock = Monitor.IsEntered(submitLock);
                return (ulong)SignalCount;
            }
        }
    }
}
