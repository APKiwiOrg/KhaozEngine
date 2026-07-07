using System;
using System.Collections.Generic;

namespace KhaozEngine.Windowing.Rumble
{
    /// <summary>
    /// Pure, device-free rumble state + envelope logic: holds each player's sustained level and its live pulses,
    /// advances pulse time, and computes the effective per-motor level to send to the device. No Silk.NET, no I/O,
    /// no clock; time is fed in as <c>dt</c> seconds. This is the headless-testable core the <see cref="RumbleDriver"/>
    /// wraps around an <see cref="IRumbleOutput"/>.
    /// </summary>
    /// <remarks>
    /// <para>Stacking policy (documented + tested): the effective level per motor is the MAX of the sustained level
    /// and every live pulse's current level. MAX (not sum) was chosen so overlapping effects never clip past 1 and a
    /// weaker effect ending never audibly drops a stronger one that is still going. A pulse retires (is removed) once
    /// its elapsed time reaches its duration.</para>
    /// <para>Player count is fixed at the four <see cref="PlayerIndex"/> slots.</para>
    /// </remarks>
    public sealed class RumbleMixer
    {
        const int PlayerCount = 4;

        readonly (float Low, float High)[] _sustained = new (float, float)[PlayerCount];
        readonly List<Pulse>[] _pulses;

        /// <summary>Create an empty mixer (all motors at rest).</summary>
        public RumbleMixer()
        {
            _pulses = new List<Pulse>[PlayerCount];
            for (int i = 0; i < PlayerCount; i++) _pulses[i] = new List<Pulse>();
        }

        /// <summary>Set a player's sustained (held-until-changed) motor levels, clamped to [0,1].</summary>
        public void SetSustained(PlayerIndex player, float low, float high)
        {
            int i = Slot(player);
            _sustained[i] = (Clamp01(low), Clamp01(high));
        }

        /// <summary>
        /// Add a live pulse to a player. <paramref name="intensity"/> is the peak on the low motor (clamped to [0,1]);
        /// the high motor peaks at <c>intensity * highScale</c> (clamped). A non-positive duration or a zero peak on
        /// both motors is dropped (no-op).
        /// </summary>
        public void AddPulse(PlayerIndex player, float intensity, float durationSeconds, float highScale, RumbleDecay shape)
        {
            if (durationSeconds <= 0f) return;
            float low = Clamp01(intensity);
            float high = Clamp01(intensity * highScale);
            if (low <= 0f && high <= 0f) return;
            _pulses[Slot(player)].Add(new Pulse(low, high, durationSeconds, shape));
        }

        /// <summary>
        /// Advance every live pulse by <paramref name="dt"/> seconds and drop any that have run their full duration.
        /// <paramref name="dt"/> &lt;= 0 leaves pulse time untouched (levels are still recomputable). Negative dt is
        /// treated as zero.
        /// </summary>
        public void Advance(float dt)
        {
            if (dt <= 0f) return;
            for (int p = 0; p < PlayerCount; p++)
            {
                List<Pulse> list = _pulses[p];
                for (int k = list.Count - 1; k >= 0; k--)
                {
                    Pulse pulse = list[k];
                    pulse.Elapsed += dt;
                    if (pulse.Elapsed >= pulse.Duration) list.RemoveAt(k);
                    else list[k] = pulse;
                }
            }
        }

        /// <summary>Compute a player's current effective motor levels: sustained MAX every live pulse (each in [0,1]).</summary>
        public (float Low, float High) Effective(PlayerIndex player)
        {
            int i = Slot(player);
            (float low, float high) = _sustained[i];
            List<Pulse> list = _pulses[i];
            for (int k = 0; k < list.Count; k++)
            {
                (float pl, float ph) = list[k].Current();
                if (pl > low) low = pl;
                if (ph > high) high = ph;
            }
            return (low, high);
        }

        /// <summary>True if the player has any sustained level or any live pulse (i.e. the mixer would push non-zero, roughly).</summary>
        public bool IsActive(PlayerIndex player)
        {
            int i = Slot(player);
            return _sustained[i].Low > 0f || _sustained[i].High > 0f || _pulses[i].Count > 0;
        }

        /// <summary>Clear a player's sustained level and all its pulses.</summary>
        public void Clear(PlayerIndex player)
        {
            int i = Slot(player);
            _sustained[i] = default;
            _pulses[i].Clear();
        }

        /// <summary>Clear every player's sustained level and all pulses.</summary>
        public void ClearAll()
        {
            for (int i = 0; i < PlayerCount; i++)
            {
                _sustained[i] = default;
                _pulses[i].Clear();
            }
        }

        static int Slot(PlayerIndex player)
        {
            int i = (int)player;
            return i < 0 ? 0 : (i >= PlayerCount ? PlayerCount - 1 : i);
        }

        static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        struct Pulse
        {
            public readonly float Low;
            public readonly float High;
            public readonly float Duration;
            public readonly RumbleDecay Shape;
            public float Elapsed;

            public Pulse(float low, float high, float duration, RumbleDecay shape)
            {
                Low = low;
                High = high;
                Duration = duration;
                Shape = shape;
                Elapsed = 0f;
            }

            /// <summary>The pulse's motor levels right now, given its elapsed fraction and decay shape.</summary>
            public (float Low, float High) Current()
            {
                float t = Duration > 0f ? Elapsed / Duration : 1f;
                if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
                float env = Shape switch
                {
                    RumbleDecay.Constant => 1f,
                    RumbleDecay.EaseOut => (1f - t) * (1f - t),
                    _ => 1f - t, // Linear
                };
                return (Low * env, High * env);
            }
        }
    }
}
