using System;

namespace KhaozEngine.Windowing.Rumble
{
    /// <summary>
    /// A fully inert <see cref="IRumble"/>: every call is a no-op. Backs headless servers and any code path with no
    /// window/gamepad. (A <see cref="RumbleDriver"/> over <see cref="NoopRumbleOutput"/> is equivalent but keeps its
    /// envelope state; this one keeps nothing.) <see cref="AppWindow.Rumble"/> hands this out on a backend that has
    /// no motors, so a game can call rumble unconditionally.
    /// </summary>
    public sealed class NoopRumble : IRumble
    {
        /// <summary>Shared instance (stateless).</summary>
        public static readonly NoopRumble Instance = new();

        /// <inheritdoc/>
        public void SetRumble(PlayerIndex player, float lowFrequency, float highFrequency) { }

        /// <inheritdoc/>
        public void Pulse(PlayerIndex player, float intensity, TimeSpan duration,
            float highFrequencyScale = 1f, RumbleDecay shape = RumbleDecay.Linear) { }

        /// <inheritdoc/>
        public void Tick(float dt) { }

        /// <inheritdoc/>
        public void StopAll() { }

        /// <inheritdoc/>
        public void Stop(PlayerIndex player) { }
    }
}
