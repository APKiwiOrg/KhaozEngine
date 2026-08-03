using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The real <c>WaitForIdle</c> on the native Direct3D 11 backend (decision C6): the drain loop, the
    /// <c>KE_D3D11_REAL_DRAIN</c> kill switch that restores the no-op, and the per-frame counters that are the
    /// M2 measurement.
    /// <para>
    /// WHY THIS IS A CHANGE AT ALL. Veldrid's <c>WaitForIdleCore</c> on Direct3D 11 is an empty method body, so
    /// every drain in the engine currently does nothing there, including one half of the only ordering guarantee
    /// the seam offers. It has never caused a known bug, because Direct3D 11 tracks hazards itself, but a
    /// primitive that does nothing on one backend makes the seam's guarantees backend-dependent in an
    /// undocumented way, and it makes <c>OceanFftProducer.LastStallMs</c> a measurement of an empty call.
    /// </para>
    /// </summary>
    public sealed class D3D11DrainTests
    {
        static D3D11FenceSubsystem Subsystem(
            ID3D11FenceTimeline timeline, ID3D11DeviceLiveness? liveness = null, bool realDrain = true)
            => new(timeline, new object(), liveness, realDrain);

        /// <summary>
        /// THE DRAIN: signal a fresh point, then poll until the GPU reaches it. Both halves asserted, because a
        /// drain that polled without signalling would return the moment the last submission happened to complete
        /// and would prove nothing about the work issued since.
        /// </summary>
        [Fact]
        public void TheDrain_SignalsAFreshPointAndPollsUntilTheGpuReachesIt()
        {
            var timeline = new FakeD3D11FenceTimeline { AutoCompleteAfterPolls = 3 };
            using D3D11FenceSubsystem fences = Subsystem(timeline);
            fences.SignalEndOfReplay(null);

            fences.WaitForIdle();

            Assert.Equal(2, timeline.SignalCount);          // the submission, then the drain's own point
            Assert.Equal(3, timeline.PollCount);
            Assert.Equal(2UL, timeline.Completed);
        }

        /// <summary>
        /// THE DRAIN FLUSHES, EXACTLY ONCE, AND BEFORE IT POLLS. The immediate context buffers commands, so a
        /// signal the driver has never been handed is a point the GPU may never reach and a drain waiting on it
        /// would never return. Both halves are asserted: once, because a flush per poll would turn a drain into a
        /// submission storm, and before the first poll, because a flush that arrived after the loop had started
        /// leaves the first polls asking about a signal nobody has issued yet.
        /// </summary>
        [Fact]
        public void TheDrain_FlushesTheContextOnceBeforeItPolls()
        {
            var timeline = new FakeD3D11FenceTimeline { AutoCompleteAfterPolls = 5 };
            using D3D11FenceSubsystem fences = Subsystem(timeline);

            fences.WaitForIdle();

            Assert.Equal(1, timeline.FlushCount);
            Assert.Equal(0, timeline.PollCountAtFirstFlush);
        }

        /// <summary>
        /// A SUBMIT DOES NOT FLUSH, and neither does a fence poll. Only the drain has decided to wait, so only
        /// the drain pays to have the work handed over. A flush on the seam's <c>Signaled</c> would give the
        /// cheapest member of the fence contract a cost that grows with how often a consumer looks at it, which
        /// is the same trap <c>DO_NOT_FLUSH</c> exists to avoid on the fallback mechanism.
        /// </summary>
        [Fact]
        public void TheReplayTailSignalAndTheFencePoll_DoNotFlush()
        {
            var timeline = new FakeD3D11FenceTimeline();
            using D3D11FenceSubsystem fences = Subsystem(timeline);
            IGpuFence fence = fences.CreateFence();

            fences.SignalEndOfReplay(fence);
            fences.SignalEndOfReplay(null);
            _ = fence.Signaled;
            _ = fences.CompletedValue;

            Assert.Equal(0, timeline.FlushCount);
        }

        /// <summary>A drain that finds the GPU already caught up still polls once and returns, and it still
        /// counts: it is a real drain that happened to be cheap, which is exactly what the M2 measurement wants
        /// to see a lot of.</summary>
        [Fact]
        public void ADrainOnAnIdleGpu_PollsOnceAndCounts()
        {
            var timeline = new FakeD3D11FenceTimeline { AutoCompleteAfterPolls = 1 };
            using D3D11FenceSubsystem fences = Subsystem(timeline);

            fences.WaitForIdle();
            fences.BeginFrame();

            Assert.Equal(1, timeline.PollCount);
            Assert.Equal(1, fences.LastFrameDrain.Count);
        }

        /// <summary>
        /// THE KILL SWITCH. With the drain off the call returns without signalling, without polling and without
        /// counting, which is the empty body the Veldrid Direct3D 11 path has always had. Not counting is
        /// deliberate: counting them would report a run with the switch down as having drained a few hundred
        /// times for zero milliseconds, which reads as a drain that costs nothing rather than as a drain that
        /// never ran.
        /// </summary>
        [Fact]
        public void WithTheKillSwitchDown_TheDrainIsTheNoOpItReplaces()
        {
            var timeline = new FakeD3D11FenceTimeline();
            using D3D11FenceSubsystem fences = Subsystem(timeline, realDrain: false);

            fences.WaitForIdle();
            fences.WaitForIdle();
            fences.BeginFrame();

            Assert.False(fences.RealDrainEnabled);
            Assert.Equal(0, timeline.SignalCount);
            Assert.Equal(0, timeline.PollCount);
            Assert.Equal(0, timeline.FlushCount);
            Assert.Equal(0, fences.LastFrameDrain.Count);
            Assert.Equal(0d, fences.LastFrameDrain.TotalMs);
        }

        /// <summary>The kill switch does not touch the rest of the subsystem: fences keep working, because the
        /// switch is about <c>WaitForIdle</c> and not about C5.</summary>
        [Fact]
        public void TheKillSwitch_DoesNotDisableFences()
        {
            var timeline = new FakeD3D11FenceTimeline();
            using D3D11FenceSubsystem fences = Subsystem(timeline, realDrain: false);
            IGpuFence fence = fences.CreateFence();

            fences.SignalEndOfReplay(fence);
            Assert.False(fence.Signaled);

            timeline.Completed = 1UL;
            Assert.True(fence.Signaled);
            Assert.True(fences.SupportsCompletionFences);
        }

        /// <summary>Decision X3: after the device is dead the drain is a no-op. A destroyed device has nothing to
        /// wait for, and a spin on a counter nothing can advance any more would never return.</summary>
        [Fact]
        public void AfterDeviceDeath_TheDrainIsANoOpAndCountsNothing()
        {
            var timeline = new FakeD3D11FenceTimeline();
            var liveness = new FakeD3D11DeviceLiveness { IsDead = true };
            using D3D11FenceSubsystem fences = Subsystem(timeline, liveness);

            fences.WaitForIdle();
            fences.BeginFrame();

            Assert.Equal(0, timeline.SignalCount);
            Assert.Equal(0, timeline.PollCount);
            Assert.Equal(0, timeline.FlushCount);
            Assert.Equal(0, fences.LastFrameDrain.Count);
        }

        /// <summary>
        /// A device that dies MID-DRAIN releases the caller. The spin deliberately has no timeout, since a GPU
        /// that never reaches the point has hung and silently proceeding over work that has not happened is
        /// worse, so the liveness check inside the loop is the one escape. Direct3D's own reset after a hang is
        /// what flips it.
        /// </summary>
        [Fact]
        public void ADeviceThatDiesMidDrain_ReleasesTheDrain()
        {
            var liveness = new FakeD3D11DeviceLiveness();
            var timeline = new DyingTimeline(liveness, killAfterPolls: 4);
            using D3D11FenceSubsystem fences = Subsystem(timeline, liveness);

            fences.WaitForIdle();

            Assert.True(liveness.IsDead);
            Assert.Equal(4, timeline.PollCount);
        }

        // ---- The M2 counters ----

        /// <summary>
        /// The counters describe the frame that has ENDED, so a reader never sees a half-accumulated total. Same
        /// shape as <c>WaterFrameStats</c> and <c>Scene3D.LastFrameStats</c>.
        /// </summary>
        [Fact]
        public void TheCounters_DescribeTheFrameThatEnded()
        {
            var timeline = new FakeD3D11FenceTimeline { AutoCompleteAfterPolls = 1 };
            using D3D11FenceSubsystem fences = Subsystem(timeline);

            fences.WaitForIdle();
            fences.WaitForIdle();
            Assert.Equal(0, fences.LastFrameDrain.Count);   // still accumulating, nothing rolled yet

            fences.BeginFrame();
            Assert.Equal(2, fences.LastFrameDrain.Count);

            fences.WaitForIdle();
            Assert.Equal(2, fences.LastFrameDrain.Count);   // last frame's number, unchanged mid-frame

            fences.BeginFrame();
            Assert.Equal(1, fences.LastFrameDrain.Count);
        }

        /// <summary>A frame that drained nothing reports zero on both numbers rather than repeating the previous
        /// frame's, which is what makes "the drain stopped happening" visible at all.</summary>
        [Fact]
        public void AFrameWithNoDrains_ReportsZero()
        {
            var timeline = new FakeD3D11FenceTimeline { AutoCompleteAfterPolls = 1 };
            using D3D11FenceSubsystem fences = Subsystem(timeline);

            fences.WaitForIdle();
            fences.BeginFrame();
            Assert.Equal(1, fences.LastFrameDrain.Count);

            fences.BeginFrame();

            Assert.Equal(0, fences.LastFrameDrain.Count);
            Assert.Equal(0d, fences.LastFrameDrain.TotalMs);
        }

        /// <summary>
        /// The duration is real wall-clock time and never negative, AND A LOOP OF HUNDREDS OF POLLS IS NOT
        /// MILLISECONDS OF SLEEP. This ran against a plain <c>SpinWait.SpinOnce()</c> before, whose default
        /// <c>sleep1Threshold</c> is 20, so a 200-poll fake drain spent about 207 ms sleeping and the test passed
        /// anyway, because "at least zero" is true of any number. One such sleep is more than the entire 0.2 ms
        /// per-frame budget M2 measures against, so a drain that escalated would settle decision C6 on a
        /// measurement of the scheduler.
        /// <para>
        /// The bound below is loose on purpose, and it is chosen by arithmetic rather than by feel. A loop that
        /// sleeps a millisecond per iteration cannot finish all 400 polls in under 400 ms, because
        /// <c>Thread.Sleep(1)</c> never returns in under a millisecond on any platform, so a 350 ms bound still
        /// discriminates that failure shape with room to spare. Typical is a fraction of a millisecond here on an
        /// idle machine, since the fake completes on a poll count and nothing in the loop waits for anything
        /// real, but this also has to clear shared, contended CI hardware: a loaded 2-core GitHub ubuntu runner
        /// measured 171.2 ms for this same 400-poll yielding spin (CI run 30783329046) against the previous 100 ms
        /// bound, which is a real scheduling cost, not a sleep, and a 350 ms bound passes it with real margin.
        /// </para>
        /// <para>The fallback shape is the one under test (no blocking wait), because that is the path that
        /// spins. The monotonic path blocks on the fence and never reaches the spin at all.</para>
        /// </summary>
        [Fact]
        public void TheDrainDuration_IsRealElapsedTimeAndTheSpinNeverSleeps()
        {
            var timeline = new FakeD3D11FenceTimeline
            {
                AutoCompleteAfterPolls = 400,
                BlockingWaitAvailable = false,
                PollIsFreeThreaded = false,
            };
            using D3D11FenceSubsystem fences = Subsystem(timeline);

            fences.WaitForIdle();
            fences.BeginFrame();

            Assert.Equal(1, fences.LastFrameDrain.Count);
            Assert.Equal(400, timeline.PollCount);
            Assert.True(fences.LastFrameDrain.TotalMs >= 0d);
            Assert.True(fences.LastFrameDrain.TotalMs < 350d,
                $"A 400-poll fake drain took {fences.LastFrameDrain.TotalMs:F1} ms, which is the shape of a loop "
                + "that sleeps a millisecond per iteration rather than one that spins: Thread.Sleep(1) never "
                + "returns in under a millisecond, so 400 sleeping polls cannot finish under 400 ms, and this "
                + "350 ms bound still catches that shape with margin. SpinOnce must be called with "
                + "sleep1Threshold: -1, and a mechanism with a blocking wait must use it instead.");
        }

        /// <summary>
        /// ON A MECHANISM WITH A BLOCKING WAIT, THE DRAIN BLOCKS RATHER THAN SPINNING. That is the primary path,
        /// the Direct3D 11.4 fence, where <c>SetEventOnCompletion</c> wakes the drain on the GPU's own signal
        /// with no granularity cost. The spin is what is left for the fallback, which has no such primitive.
        /// <para>
        /// The count is one wait per iteration that did not find the value reached, which is the poll count minus
        /// the poll that ended the loop. Asserting the exact number, rather than "more than zero", is what pins
        /// that the drain does not ALSO spin between waits.
        /// </para>
        /// </summary>
        [Fact]
        public void OnAMechanismWithABlockingWait_TheDrainWaitsOncePerUnsatisfiedPoll()
        {
            var timeline = new FakeD3D11FenceTimeline { AutoCompleteAfterPolls = 4 };
            using D3D11FenceSubsystem fences = Subsystem(timeline);

            fences.WaitForIdle();

            Assert.Equal(4, timeline.PollCount);
            Assert.Equal(3, timeline.WaitCallCount);
        }

        /// <summary>A drain that finds the value already reached never waits at all, which is the common case on
        /// an idle device and the reason the loop polls before it waits.</summary>
        [Fact]
        public void ADrainOnAnIdleGpu_NeverReachesTheWait()
        {
            var timeline = new FakeD3D11FenceTimeline { AutoCompleteAfterPolls = 1 };
            using D3D11FenceSubsystem fences = Subsystem(timeline);

            fences.WaitForIdle();

            Assert.Equal(0, timeline.WaitCallCount);
        }

        // ---- The kill switch's environment parse ----

        [Fact]
        public void TheKillSwitchVariable_FollowsTheEngineNamingConvention()
        {
            Assert.StartsWith("KE_", D3D11RealDrain.EnvVarName, StringComparison.Ordinal);
            Assert.Equal("KE_D3D11_REAL_DRAIN", D3D11RealDrain.EnvVarName);
        }

        /// <summary>Unset is ON, which is the direction that matters: the drain ships enabled and the variable
        /// exists to turn it OFF for the soak window.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("1")]
        [InlineData("true")]
        [InlineData("YES")]
        [InlineData(" On ")]
        public void TheDrainIsOnByDefaultAndOnEveryOnValue(string? value)
        {
            Assert.True(D3D11RealDrain.Resolve(value, out string? unrecognized));
            Assert.Null(unrecognized);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("false")]
        [InlineData("NO")]
        [InlineData(" off ")]
        public void EveryOffValue_RestoresTheNoOp(string value)
        {
            Assert.False(D3D11RealDrain.Resolve(value, out string? unrecognized));
            Assert.Null(unrecognized);
        }

        /// <summary>
        /// A value that was set and understood as nothing comes back verbatim so the caller can WARN. It matters
        /// more here than for an ordinary setting: a mistyped OFF that silently left the drain ON produces a
        /// measurement saying the drain is innocent, from a run that never turned it off.
        /// </summary>
        [Fact]
        public void AnUnrecognizedValue_ComesBackVerbatimAndKeepsTheDefault()
        {
            Assert.True(D3D11RealDrain.Resolve("disabled", out string? unrecognized));
            Assert.Equal("disabled", unrecognized);
            Assert.Contains("disabled", D3D11RealDrain.UnrecognizedWarning("disabled"), StringComparison.Ordinal);
        }

        /// <summary>The INFO line exists for the OFF run only, so a capture proves the lever was down rather than
        /// resting on the tester believing they set it. A line on every session is a line nobody reads.</summary>
        [Fact]
        public void TheDisabledDescription_NamesTheVariableThatCausedIt()
            => Assert.Contains(D3D11RealDrain.EnvVarName, D3D11RealDrain.DisabledDescription, StringComparison.Ordinal);

        // A timeline that kills the device partway through a drain, standing in for a Direct3D reset after a
        // hang. Completion never arrives, so the only way out of the loop is the liveness check inside it.
        sealed class DyingTimeline : ID3D11FenceTimeline
        {
            readonly FakeD3D11DeviceLiveness _liveness;
            readonly int _killAfterPolls;
            ulong _issued;

            internal DyingTimeline(FakeD3D11DeviceLiveness liveness, int killAfterPolls)
            {
                _liveness = liveness;
                _killAfterPolls = killAfterPolls;
            }

            internal int PollCount { get; private set; }

            public D3D11FenceMechanism Mechanism => D3D11FenceMechanism.MonotonicFence;

            public bool PollIsFreeThreaded => true;

            public ulong Signal() => ++_issued;

            public void Flush() { }

            // No wait, so the drain spins and reaches the liveness check every iteration. A fake wait that
            // returned true would be a wait of zero length, which is the same loop with an extra call in it.
            public bool TryWaitForValue(ulong value, int timeoutMilliseconds) => false;

            public ulong CompletedValue
            {
                get
                {
                    PollCount++;
                    if (PollCount >= _killAfterPolls) _liveness.IsDead = true;
                    return 0UL;
                }
            }

            public void Dispose() { }
        }
    }
}
