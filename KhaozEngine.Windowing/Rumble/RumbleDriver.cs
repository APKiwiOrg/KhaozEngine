using System;

namespace KhaozEngine.Windowing.Rumble
{
    /// <summary>
    /// The concrete <see cref="IRumble"/>: a pure <see cref="RumbleMixer"/> (state + envelopes) driving an
    /// <see cref="IRumbleOutput"/> sink (the device). Device-free itself, so it is fully headless-testable against a
    /// recording <see cref="IRumbleOutput"/>. <see cref="AppWindow"/> builds one over its Silk output; a headless
    /// server or a test builds one over <see cref="NoopRumbleOutput"/> (see <see cref="NoopRumble"/> for the shared
    /// no-op instance).
    /// </summary>
    public sealed class RumbleDriver : IRumble
    {
        readonly RumbleMixer _mixer = new();
        readonly IRumbleOutput _output;

        /// <summary>Build a driver over a rumble output sink.</summary>
        public RumbleDriver(IRumbleOutput output)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
        }

        /// <inheritdoc/>
        public void SetRumble(PlayerIndex player, float lowFrequency, float highFrequency)
        {
            _mixer.SetSustained(player, lowFrequency, highFrequency);
            Push(player);
        }

        /// <inheritdoc/>
        public void Pulse(PlayerIndex player, float intensity, TimeSpan duration,
            float highFrequencyScale = 1f, RumbleDecay shape = RumbleDecay.Linear)
        {
            _mixer.AddPulse(player, intensity, (float)duration.TotalSeconds, highFrequencyScale, shape);
            Push(player);
        }

        /// <inheritdoc/>
        public void Tick(float dt)
        {
            _mixer.Advance(dt);
            for (int i = 0; i < 4; i++) Push((PlayerIndex)i);
        }

        /// <inheritdoc/>
        public void StopAll()
        {
            _mixer.ClearAll();
            for (int i = 0; i < 4; i++) _output.Set((PlayerIndex)i, 0f, 0f);
        }

        /// <inheritdoc/>
        public void Stop(PlayerIndex player)
        {
            _mixer.Clear(player);
            _output.Set(player, 0f, 0f);
        }

        void Push(PlayerIndex player)
        {
            (float low, float high) = _mixer.Effective(player);
            _output.Set(player, low, high);
        }
    }
}
