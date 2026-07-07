using System;
using System.Collections.Generic;
using KhaozEngine.Windowing;
using KhaozEngine.Windowing.Rumble;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    /// <summary>
    /// Headless rumble: the pure <see cref="RumbleMixer"/> envelope/stacking logic and the <see cref="RumbleDriver"/>
    /// seam driven against a recording <see cref="IRumbleOutput"/>. No device, no window - the AppWindow-owned Silk
    /// sink is the only untested piece and is covered by the on-device smoke caveat, not by CI.
    /// </summary>
    public class RumbleTests
    {
        /// <summary>Records every motor write so a test can assert what reached the "device".</summary>
        sealed class RecordingOutput : IRumbleOutput
        {
            public readonly List<(PlayerIndex Player, float Low, float High)> Writes = new();
            public void Set(PlayerIndex player, float low, float high) => Writes.Add((player, low, high));
            public (float Low, float High) Last(PlayerIndex p)
            {
                for (int i = Writes.Count - 1; i >= 0; i--)
                    if (Writes[i].Player == p) return (Writes[i].Low, Writes[i].High);
                return (0f, 0f);
            }
        }

        const float Eps = 1e-4f;

        // ---- RumbleMixer: sustained + clamping ----

        [Fact]
        public void Sustained_HoldsUntilChanged_AndClampsToUnit()
        {
            var m = new RumbleMixer();
            m.SetSustained(PlayerIndex.One, 0.4f, 0.7f);
            Assert.Equal((0.4f, 0.7f), m.Effective(PlayerIndex.One));

            m.Advance(10f); // time passing does not touch a sustained level
            Assert.Equal((0.4f, 0.7f), m.Effective(PlayerIndex.One));

            m.SetSustained(PlayerIndex.One, 5f, -2f); // out-of-range clamps to [0,1]
            Assert.Equal((1f, 0f), m.Effective(PlayerIndex.One));
        }

        [Fact]
        public void Sustained_IsPerPlayer_Isolated()
        {
            var m = new RumbleMixer();
            m.SetSustained(PlayerIndex.One, 0.5f, 0.5f);
            Assert.Equal((0f, 0f), m.Effective(PlayerIndex.Two));
            Assert.Equal((0f, 0f), m.Effective(PlayerIndex.Four));
        }

        // ---- RumbleMixer: pulse decay shapes ----

        [Fact]
        public void Pulse_LinearDecay_RampsToZeroOverDuration()
        {
            var m = new RumbleMixer();
            m.AddPulse(PlayerIndex.One, 1f, durationSeconds: 1f, highScale: 1f, RumbleDecay.Linear);
            Assert.Equal(1f, m.Effective(PlayerIndex.One).Low, 3);       // t=0 -> peak
            m.Advance(0.5f);
            Assert.Equal(0.5f, m.Effective(PlayerIndex.One).Low, 3);     // t=0.5 -> half
            m.Advance(0.25f);
            Assert.Equal(0.25f, m.Effective(PlayerIndex.One).Low, 3);    // t=0.75 -> quarter
        }

        [Fact]
        public void Pulse_ConstantShape_HoldsPeakThenRetires()
        {
            var m = new RumbleMixer();
            m.AddPulse(PlayerIndex.One, 0.8f, durationSeconds: 1f, highScale: 1f, RumbleDecay.Constant);
            m.Advance(0.9f);
            Assert.Equal(0.8f, m.Effective(PlayerIndex.One).Low, 3); // flat until it ends
            m.Advance(0.2f);
            Assert.Equal(0f, m.Effective(PlayerIndex.One).Low, 3);   // fully elapsed -> retired
        }

        [Fact]
        public void Pulse_EaseOut_FallsFasterThanLinearEarly()
        {
            var m = new RumbleMixer();
            m.AddPulse(PlayerIndex.One, 1f, durationSeconds: 1f, highScale: 1f, RumbleDecay.EaseOut);
            m.Advance(0.5f);
            // EaseOut at t=0.5 = (1-0.5)^2 = 0.25, less than linear's 0.5.
            Assert.Equal(0.25f, m.Effective(PlayerIndex.One).Low, 3);
        }

        [Fact]
        public void Pulse_HighScale_ScalesHighMotorPeak_AndClamps()
        {
            var m = new RumbleMixer();
            m.AddPulse(PlayerIndex.One, 0.5f, durationSeconds: 1f, highScale: 2f, RumbleDecay.Constant);
            (float low, float high) = m.Effective(PlayerIndex.One);
            Assert.Equal(0.5f, low, 3);
            Assert.Equal(1f, high, 3); // 0.5 * 2 = 1.0, clamped
        }

        [Fact]
        public void Pulse_AutoStops_ExactlyAtDuration()
        {
            var m = new RumbleMixer();
            m.AddPulse(PlayerIndex.One, 1f, durationSeconds: 0.5f, highScale: 1f, RumbleDecay.Linear);
            Assert.True(m.IsActive(PlayerIndex.One));
            m.Advance(0.5f); // elapsed == duration -> retired
            Assert.False(m.IsActive(PlayerIndex.One));
            Assert.Equal((0f, 0f), m.Effective(PlayerIndex.One));
        }

        [Fact]
        public void Pulse_ZeroOrNegativeDuration_IsDropped()
        {
            var m = new RumbleMixer();
            m.AddPulse(PlayerIndex.One, 1f, durationSeconds: 0f, highScale: 1f, RumbleDecay.Linear);
            m.AddPulse(PlayerIndex.One, 1f, durationSeconds: -1f, highScale: 1f, RumbleDecay.Linear);
            Assert.False(m.IsActive(PlayerIndex.One));
        }

        [Fact]
        public void Pulse_ZeroIntensityBothMotors_IsDropped()
        {
            var m = new RumbleMixer();
            m.AddPulse(PlayerIndex.One, 0f, durationSeconds: 1f, highScale: 0f, RumbleDecay.Linear);
            Assert.False(m.IsActive(PlayerIndex.One));
        }

        // ---- RumbleMixer: stacking policy (MAX) ----

        [Fact]
        public void Stacking_SustainedMaxPulse_TakesTheStronger()
        {
            var m = new RumbleMixer();
            m.SetSustained(PlayerIndex.One, 0.3f, 0.3f);
            m.AddPulse(PlayerIndex.One, 0.9f, durationSeconds: 1f, highScale: 1f, RumbleDecay.Constant);
            Assert.Equal((0.9f, 0.9f), m.Effective(PlayerIndex.One)); // pulse wins

            m.Advance(1.01f); // pulse retires, sustained remains
            Assert.Equal((0.3f, 0.3f), m.Effective(PlayerIndex.One)); // sustained survives the pulse ending
        }

        [Fact]
        public void Stacking_OverlappingPulses_TakeMaxNotSum()
        {
            var m = new RumbleMixer();
            m.AddPulse(PlayerIndex.One, 0.6f, durationSeconds: 1f, highScale: 1f, RumbleDecay.Constant);
            m.AddPulse(PlayerIndex.One, 0.8f, durationSeconds: 1f, highScale: 1f, RumbleDecay.Constant);
            // MAX, not 0.6+0.8=1.4-clamped-to-1: proves it is genuinely max, not a saturated sum.
            Assert.Equal(0.8f, m.Effective(PlayerIndex.One).Low, 3);
        }

        [Fact]
        public void Advance_NonPositiveDt_DoesNotAdvanceTime()
        {
            var m = new RumbleMixer();
            m.AddPulse(PlayerIndex.One, 1f, durationSeconds: 1f, highScale: 1f, RumbleDecay.Linear);
            m.Advance(0f);
            m.Advance(-5f);
            Assert.Equal(1f, m.Effective(PlayerIndex.One).Low, 3); // still at peak
        }

        [Fact]
        public void Clear_And_ClearAll_ResetState()
        {
            var m = new RumbleMixer();
            m.SetSustained(PlayerIndex.One, 0.5f, 0.5f);
            m.AddPulse(PlayerIndex.Two, 1f, durationSeconds: 1f, highScale: 1f, RumbleDecay.Constant);
            m.Clear(PlayerIndex.One);
            Assert.False(m.IsActive(PlayerIndex.One));
            Assert.True(m.IsActive(PlayerIndex.Two));
            m.ClearAll();
            Assert.False(m.IsActive(PlayerIndex.Two));
        }

        // ---- RumbleDriver: seam over a recording sink ----

        [Fact]
        public void Driver_SetRumble_PushesLevelImmediately()
        {
            var sink = new RecordingOutput();
            var driver = new RumbleDriver(sink);
            driver.SetRumble(PlayerIndex.One, 0.4f, 0.6f);
            Assert.Equal((0.4f, 0.6f), sink.Last(PlayerIndex.One));
        }

        [Fact]
        public void Driver_Pulse_ThenTick_DecaysAndAutoStops()
        {
            var sink = new RecordingOutput();
            var driver = new RumbleDriver(sink);
            driver.Pulse(PlayerIndex.One, 1f, TimeSpan.FromSeconds(1), highFrequencyScale: 1f, RumbleDecay.Linear);
            Assert.Equal(1f, sink.Last(PlayerIndex.One).Low, 3); // peak pushed on Pulse

            driver.Tick(0.5f);
            Assert.Equal(0.5f, sink.Last(PlayerIndex.One).Low, 3);

            driver.Tick(0.6f); // total 1.1s > duration -> retired, zero pushed
            Assert.Equal(0f, sink.Last(PlayerIndex.One).Low, 3);
        }

        [Fact]
        public void Driver_Tick_PushesEveryPlayerEachFrame()
        {
            var sink = new RecordingOutput();
            var driver = new RumbleDriver(sink);
            sink.Writes.Clear();
            driver.Tick(0.016f);
            // One push per player slot each tick so a just-stopped player reliably gets its zero.
            var players = new HashSet<PlayerIndex>();
            foreach (var w in sink.Writes) players.Add(w.Player);
            Assert.Equal(4, players.Count);
        }

        [Fact]
        public void Driver_StopAll_ZeroesEveryPlayer()
        {
            var sink = new RecordingOutput();
            var driver = new RumbleDriver(sink);
            driver.SetRumble(PlayerIndex.One, 1f, 1f);
            driver.SetRumble(PlayerIndex.Two, 1f, 1f);
            sink.Writes.Clear();
            driver.StopAll();
            Assert.Equal((0f, 0f), sink.Last(PlayerIndex.One));
            Assert.Equal((0f, 0f), sink.Last(PlayerIndex.Two));
        }

        [Fact]
        public void Driver_Stop_ZeroesOnlyThatPlayer()
        {
            var sink = new RecordingOutput();
            var driver = new RumbleDriver(sink);
            driver.SetRumble(PlayerIndex.One, 1f, 1f);
            driver.SetRumble(PlayerIndex.Two, 0.5f, 0.5f);
            driver.Stop(PlayerIndex.One);
            Assert.Equal((0f, 0f), sink.Last(PlayerIndex.One));
            // Player Two's sustained still there: a fresh tick re-pushes it.
            driver.Tick(0f);
            Assert.Equal((0.5f, 0.5f), sink.Last(PlayerIndex.Two));
        }

        [Fact]
        public void Driver_NullOutput_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new RumbleDriver(null!));
        }

        // ---- Headless no-op backs servers/tests ----

        [Fact]
        public void NoopRumble_AllCalls_AreInert()
        {
            IRumble r = NoopRumble.Instance;
            r.SetRumble(PlayerIndex.One, 1f, 1f);
            r.Pulse(PlayerIndex.One, 1f, TimeSpan.FromSeconds(1));
            r.Tick(0.5f);
            r.Stop(PlayerIndex.One);
            r.StopAll();
            // No throw, no state: the point is it is safe to call unconditionally.
        }

        [Fact]
        public void NoopRumbleOutput_Set_IsInert()
        {
            IRumbleOutput o = NoopRumbleOutput.Instance;
            o.Set(PlayerIndex.One, 1f, 1f); // no throw
        }
    }
}
