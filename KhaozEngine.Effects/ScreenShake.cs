using System;
using System.Numerics;

namespace KhaozEngine.Effects
{
    /// <summary>
    /// Trauma-based screen shake. Add trauma on impacts; the shake magnitude falls off as
    /// <c>trauma^2</c> and decays over time. A pure offset generator - it does not touch a camera; the game
    /// composes <see cref="Offset"/>/<see cref="Angle"/> onto its render camera. Deterministic: the noise is
    /// seeded smooth (sine-sum) noise, no <see cref="System.Random"/> or wall-clock, so it is reproducible
    /// and headless-testable.
    /// </summary>
    public sealed class ScreenShake
    {
        private readonly float _phaseX;
        private readonly float _phaseY;
        private readonly float _phaseA;
        private float _time;
        private float _trauma;

        /// <summary>Creates a shake; <paramref name="seed"/> fixes the per-channel noise phases.</summary>
        public ScreenShake(uint seed = 1)
        {
            _phaseX = seed * 1.0f;
            _phaseY = seed * 2.0f + 1.3f;
            _phaseA = seed * 3.0f + 2.7f;
        }

        /// <summary>Current trauma, 0..1.</summary>
        public float Trauma => _trauma;

        /// <summary>Positional offset magnitude (world units) at trauma 1.</summary>
        public float MaxOffset { get; set; } = 30f;

        /// <summary>Rotational offset magnitude (radians) at trauma 1; set 0 for positional-only shake.</summary>
        public float MaxAngle { get; set; } = 0.1f;

        /// <summary>Trauma drained per second by <see cref="Update"/>.</summary>
        public float DecayPerSecond { get; set; } = 1f;

        /// <summary>Oscillation speed (higher = faster shaking).</summary>
        public float Frequency { get; set; } = 25f;

        /// <summary>Adds trauma (e.g. on an explosion/hit); the result is clamped to [0,1]. Non-positive
        /// amounts are ignored.</summary>
        public void Add(float amount)
        {
            if (amount <= 0f) return;
            _trauma = MathF.Min(1f, _trauma + amount);
        }

        /// <summary>Drains trauma by <see cref="DecayPerSecond"/>*<paramref name="dt"/> (floored at 0) and
        /// advances the internal noise time by <paramref name="dt"/>*<see cref="Frequency"/>.</summary>
        public void Update(float dt)
        {
            _trauma = MathF.Max(0f, _trauma - DecayPerSecond * dt);
            _time += dt * Frequency;
        }

        /// <summary>Positional offset this frame: <c>trauma^2 * MaxOffset * noise</c>, per axis.</summary>
        public Vector2 Offset
        {
            get
            {
                float m = _trauma * _trauma * MaxOffset;
                return new Vector2(m * Noise(_phaseX), m * Noise(_phaseY));
            }
        }

        /// <summary>Rotational offset this frame (radians): <c>trauma^2 * MaxAngle * noise</c>.</summary>
        public float Angle => _trauma * _trauma * MaxAngle * Noise(_phaseA);

        // Smooth deterministic noise in [-1,1] from the internal time at a per-channel phase.
        private float Noise(float phase)
            => 0.6f * MathF.Sin(_time + phase) + 0.4f * MathF.Sin(2.13f * _time + 1.7f * phase);
    }
}
