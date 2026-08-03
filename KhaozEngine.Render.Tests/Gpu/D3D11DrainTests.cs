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

        /// <summary>The duration is real wall-clock time and is never negative, which is the only claim a test on
        /// a shared machine can honestly make about it. The GATE on the number is M2's soak measurement, not a
        /// unit test.</summary>
        [Fact]
        public void TheDrainDuration_IsRealElapsedTime()
        {
            var timeline = new FakeD3D11FenceTimeline { AutoCompleteAfterPolls = 200 };
            using D3D11FenceSubsystem fences = Subsystem(timeline);

            fences.WaitForIdle();
            fences.BeginFrame();

            Assert.Equal(1, fences.LastFrameDrain.Count);
            Assert.True(fences.LastFrameDrain.TotalMs >= 0d);
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

            public ulong Signal() => ++_issued;

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
