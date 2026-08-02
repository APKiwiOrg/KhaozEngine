using System;

namespace KhaozEngine.Windowing.Rumble
{
    /// <summary>
    /// Game-facing gamepad rumble (vibration) OUTPUT seam. This is the mirror image of the input snapshot rule:
    /// input flows IN through the immutable <see cref="InputState"/> snapshot, and rumble flows OUT through this
    /// interface. Only <see cref="AppWindow"/> (via its <see cref="AppWindow.Rumble"/>) touches the Silk.NET/GLFW
    /// vibration motors; games call this seam, a headless <see cref="NoopRumble"/> backs tests and servers, and no
    /// other class reaches the Silk gamepad devices.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two ways to drive a motor pair (low-frequency = heavy/left motor, high-frequency = light/right motor, each in
    /// [0,1]):
    /// </para>
    /// <list type="bullet">
    /// <item><description><see cref="SetRumble"/> - a sustained level you own. It holds until you set it again (or to
    /// zero). Nothing decays it for you.</description></item>
    /// <item><description><see cref="Pulse"/> - a fire-and-forget envelope: it ramps to the requested intensity and
    /// decays to zero over <c>duration</c>, auto-stopping. The engine loop ticks it each frame via
    /// <see cref="Tick"/>.</description></item>
    /// </list>
    /// <para>
    /// The two layers COMPOSE: the effective motor level sent to the device is the per-motor MAX of the sustained
    /// level and every live pulse (documented stacking policy, so a strong sustained rumble is never cut short by a
    /// weaker pulse ending, and overlapping pulses take the strongest rather than summing past 1). See
    /// <see cref="RumbleMixer"/> for the pure logic.
    /// </para>
    /// <para>
    /// Physical-device verification is impossible in CI and on this machine; this seam is compile-verified and
    /// headless-tested against a recording sink. Whether a pulse is FELT depends on the backend and the pad: the
    /// current GLFW input backend enumerates zero vibration motors (GLFW has no haptics API), so all output is a
    /// graceful no-op there. A future SDL-backed window gets rumble for free through this same seam. See the on-device
    /// caveat in the docs.
    /// </para>
    /// </remarks>
    public interface IRumble
    {
        /// <summary>
        /// Set a SUSTAINED vibration level for a player's pad. <paramref name="lowFrequency"/> drives the heavy
        /// (left) motor, <paramref name="highFrequency"/> the light (right) motor; both are clamped to [0,1]. The
        /// level holds until changed (set both to 0 to stop the sustained layer). Pulses compose on top via MAX.
        /// A disconnected / motor-less pad is a graceful no-op.
        /// </summary>
        void SetRumble(PlayerIndex player, float lowFrequency, float highFrequency);

        /// <summary>
        /// Fire a fire-and-forget rumble pulse on a player's pad: it reaches <paramref name="intensity"/> (applied to
        /// both motors, or scaled per motor via <paramref name="highFrequencyScale"/>) and decays to zero over
        /// <paramref name="duration"/>, then auto-stops. Ticked by the engine loop. Multiple live pulses and the
        /// sustained level compose by per-motor MAX. A non-positive duration or a zero intensity is a no-op.
        /// </summary>
        /// <param name="player">Which pad.</param>
        /// <param name="intensity">Peak intensity in [0,1], applied to the low-frequency (heavy) motor.</param>
        /// <param name="duration">How long the pulse lasts before it auto-stops.</param>
        /// <param name="highFrequencyScale">Scales the peak for the high-frequency (light) motor relative to
        /// <paramref name="intensity"/> (default 1 = same peak on both). Clamped so the resulting peak stays in [0,1].</param>
        /// <param name="shape">Decay shape over the pulse's lifetime.</param>
        void Pulse(PlayerIndex player, float intensity, TimeSpan duration,
            float highFrequencyScale = 1f, RumbleDecay shape = RumbleDecay.Linear);

        /// <summary>
        /// Advance all live pulses by <paramref name="dt"/> seconds, recompute each player's effective motor levels
        /// (sustained MAX live pulses), and push them to the device. The engine loop calls this once per frame; a
        /// game driving <see cref="AppWindow.Run(System.Action{Frame})"/> directly should call it once per frame too. Idempotent for
        /// <paramref name="dt"/> &lt;= 0 (pushes current levels without advancing time).
        /// </summary>
        void Tick(float dt);

        /// <summary>Immediately stop everything on every player (clears sustained levels and all live pulses, pushes zero).</summary>
        void StopAll();

        /// <summary>Immediately stop everything on one player (clears its sustained level and pulses, pushes zero for it).</summary>
        void Stop(PlayerIndex player);
    }

    /// <summary>Decay shape of a rumble <see cref="IRumble.Pulse"/> over its lifetime.</summary>
    public enum RumbleDecay
    {
        /// <summary>Hold the peak intensity flat for the whole duration, then cut to zero (a square pulse).</summary>
        Constant = 0,
        /// <summary>Ramp linearly from the peak down to zero across the duration (the default).</summary>
        Linear = 1,
        /// <summary>Quadratic ease-out: starts at the peak and falls off faster early, tailing to zero (a sharp "hit").</summary>
        EaseOut = 2,
    }
}
