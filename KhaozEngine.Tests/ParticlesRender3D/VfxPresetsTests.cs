using System;
using System.Numerics;
using KhaozEngine.Particles;
using Xunit;

namespace KhaozEngine.Tests.ParticlesRender3D
{
    // Headless tests for the modern VFX preset library: shape (one look per phase, positive pools), determinism
    // (the sim under a preset is a pure function of the ctor seed + call sequence), and that every preset actually
    // emits within its authored schedule. No GPU: the adapter presentation side is covered by the GpuFact tests.
    public sealed class VfxPresetsTests
    {
        public static TheoryData<string> PresetNames => new()
        {
            "FireBurst", "FrostShatter", "HealMotes", "EmberDrift",
            "SparkShower", "Shockwave", "SmokePlume", "ArcaneSparkle",
        };

        static VfxPreset Resolve(string name) => name switch
        {
            "FireBurst" => VfxPresets.FireBurst,
            "FrostShatter" => VfxPresets.FrostShatter,
            "HealMotes" => VfxPresets.HealMotes,
            "EmberDrift" => VfxPresets.EmberDrift,
            "SparkShower" => VfxPresets.SparkShower,
            "Shockwave" => VfxPresets.Shockwave,
            "SmokePlume" => VfxPresets.SmokePlume,
            "ArcaneSparkle" => VfxPresets.ArcaneSparkle,
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown preset"),
        };

        [Theory]
        [MemberData(nameof(PresetNames))]
        public void Preset_HasEffectWithOneLookPerPhase(string name)
        {
            VfxPreset preset = Resolve(name);

            Assert.NotNull(preset.Effect);
            Assert.True(preset.Effect.PhaseCount > 0, "expected at least one phase");
            Assert.Equal(preset.Effect.PhaseCount, preset.Looks.Count);
        }

        [Theory]
        [MemberData(nameof(PresetNames))]
        public void Preset_PhasePoolCapacitiesArePositive(string name)
        {
            VfxPreset preset = Resolve(name);
            var player = new ParticleEffectPlayer(preset.Effect, maxInstances: 4, seed: 1);

            for (int ph = 0; ph < player.PhaseCount; ph++)
            {
                Assert.True(player.PhaseSystem(ph).Capacity > 0, $"phase {ph} pool capacity must be positive");
            }
        }

        [Theory]
        [MemberData(nameof(PresetNames))]
        public void Preset_IsDeterministicUnderSameSeed(string name)
        {
            VfxPreset a = Resolve(name);
            VfxPreset b = Resolve(name);
            var pa = new ParticleEffectPlayer(a.Effect, maxInstances: 4, seed: 4242);
            var pb = new ParticleEffectPlayer(b.Effect, maxInstances: 4, seed: 4242);

            var origin = new Vector3(1f, 0f, 2f);
            var dir = Vector3.Normalize(new Vector3(0.2f, 1f, 0.1f));
            pa.Play(origin, dir);
            pb.Play(origin, dir);
            for (int i = 0; i < 60; i++)
            {
                pa.Update(1f / 60f);
                pb.Update(1f / 60f);
            }

            for (int ph = 0; ph < pa.PhaseCount; ph++)
            {
                ParticleSystem sa = pa.PhaseSystem(ph);
                ParticleSystem sb = pb.PhaseSystem(ph);
                Assert.Equal(sa.ActiveCount, sb.ActiveCount);
                if (sa.ActiveCount > 0)
                {
                    Assert.Equal(sa.Active[0].Position, sb.Active[0].Position);
                }
            }
        }

        [Theory]
        [MemberData(nameof(PresetNames))]
        public void Preset_EmitsWithinItsSchedule(string name)
        {
            VfxPreset preset = Resolve(name);
            var player = new ParticleEffectPlayer(preset.Effect, maxInstances: 4, seed: 9);

            float end = 0f;
            for (int ph = 0; ph < preset.Effect.PhaseCount; ph++)
            {
                ParticleEffectPhase phase = preset.Effect.GetPhase(ph);
                end = MathF.Max(end, phase.Delay + phase.Duration);
            }

            const float dt = 1f / 60f;
            int steps = (int)(end / dt) + 30;
            player.Play(Vector3.Zero, Vector3.UnitY);

            int maxAlive = 0;
            for (int s = 0; s < steps; s++)
            {
                player.Update(dt);
                int alive = 0;
                for (int ph = 0; ph < player.PhaseCount; ph++)
                {
                    alive += player.PhaseSystem(ph).ActiveCount;
                }
                if (alive > maxAlive)
                {
                    maxAlive = alive;
                }
            }

            Assert.True(maxAlive > 0, $"{name} emitted no particles across its schedule");
        }
    }
}
