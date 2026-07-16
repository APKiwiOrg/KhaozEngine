using System;
using System.Numerics;
using KhaozEngine.Particles;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Particles;

/// <summary>
/// Pins the legacy per-particle RNG draw sequence (life, direction, speed) against a hand-rolled copy of
/// <see cref="XorRng"/>. A zero-default config must consume exactly this historical sequence so the four
/// games keep their same-build determinism after the modernisation.
/// </summary>
public class LegacyRngSequenceTests
{
    // A byte-for-byte re-implementation of XorRng so the test is independent of the engine copy.
    private struct HandRng
    {
        private uint _state;

        public HandRng(uint seed) => _state = seed != 0u ? seed : 0x9E3779B9u;

        public uint NextUInt()
        {
            uint x = _state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _state = x;
            return x;
        }

        public float NextFloat() => (NextUInt() >> 8) * (1.0f / 16777216.0f);

        public float Range(float min, float max) => max <= min ? min : min + (max - min) * NextFloat();
    }

    [Fact]
    public void LegacyConfig_ConsumesLifeDirectionSpeed_InOrder()
    {
        // Omni direction (Direction ~zero) routes through SampleSphere: 2 draws for the direction.
        var cfg = new EmitterConfig
        {
            LifetimeMin = 0.5f,
            LifetimeMax = 2.0f,
            SpeedMin = 3f,
            SpeedMax = 8f,
            Direction = Vector3.Zero,
            SpreadDegrees = 180f,
            StartSize = 1f,
            EndSize = 1f,
            StartColor = Color.White,
            EndColor = Color.White,
        };

        var origin = new Vector3(4f, 5f, 6f);
        const uint seed = 1234u;

        var sys = new ParticleSystem(16, seed: seed);
        sys.Emit(cfg, origin, 3);

        var hand = new HandRng(seed);
        for (int i = 0; i < 3; i++)
        {
            float life = hand.Range(cfg.LifetimeMin, cfg.LifetimeMax);

            // SampleSphere: z then phi.
            float z = 1f - 2f * hand.NextFloat();
            float r = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
            float phi = (MathF.PI * 2f) * hand.NextFloat();
            Vector3 dir = new(r * MathF.Cos(phi), r * MathF.Sin(phi), z);

            float speed = hand.Range(cfg.SpeedMin, cfg.SpeedMax);
            Vector3 expectedVel = dir * speed;

            Particle p = sys.Active[i];
            Assert.Equal(life, p.Life);
            Assert.Equal(origin, p.Position); // Point shape: spawn at origin exactly
            Assert.Equal(expectedVel.X, p.Velocity.X);
            Assert.Equal(expectedVel.Y, p.Velocity.Y);
            Assert.Equal(expectedVel.Z, p.Velocity.Z);
        }
    }

    [Fact]
    public void LegacyConeConfig_ConsumesLifeConeSpeed_InOrder()
    {
        // A finite cone routes through the cone sampler: still exactly 2 direction draws (cosTheta, phi).
        var cfg = new EmitterConfig
        {
            LifetimeMin = 1f,
            LifetimeMax = 1f,
            SpeedMin = 2f,
            SpeedMax = 10f,
            Direction = Vector3.UnitZ,
            SpreadDegrees = 40f,
            StartSize = 1f,
            EndSize = 1f,
            StartColor = Color.White,
            EndColor = Color.White,
        };

        var origin = Vector3.Zero;
        const uint seed = 777u;

        var sys = new ParticleSystem(16, seed: seed);
        sys.Emit(cfg, origin, 3);

        var hand = new HandRng(seed);
        for (int i = 0; i < 3; i++)
        {
            float life = hand.Range(cfg.LifetimeMin, cfg.LifetimeMax);

            // SampleConeDirection around +Z with a 40 degree half-angle.
            float cosHalf = MathF.Cos(40f * (MathF.PI / 180f));
            float cosTheta = cosHalf + (1f - cosHalf) * hand.NextFloat();
            float sinTheta = MathF.Sqrt(MathF.Max(0f, 1f - cosTheta * cosTheta));
            float phi = (MathF.PI * 2f) * hand.NextFloat();
            Vector3 local = new(MathF.Cos(phi) * sinTheta, MathF.Sin(phi) * sinTheta, cosTheta);
            // Basis for axis +Z: t = +X, b = +Y, so world dir == local.
            Vector3 dir = Vector3.Normalize(local);

            float speed = hand.Range(cfg.SpeedMin, cfg.SpeedMax);
            Vector3 expectedVel = dir * speed;

            Particle p = sys.Active[i];
            Assert.Equal(life, p.Life);
            Assert.Equal(expectedVel.X, p.Velocity.X, 5);
            Assert.Equal(expectedVel.Y, p.Velocity.Y, 5);
            Assert.Equal(expectedVel.Z, p.Velocity.Z, 5);
        }
    }
}
