using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Particles;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.ParticlesRender3D
{
    // Headless tests for the modern VFX preset library: shape (one look per phase, positive pools), determinism
    // (the sim under a preset is a pure function of the ctor seed + call sequence), and that every preset actually
    // emits within its authored schedule. No GPU: the adapter presentation side is covered by the GpuFact tests.
    public sealed class VfxPresetsTests
    {
        // The roster is discovered by reflection over VfxPresets' public static VfxPreset properties, instead of
        // a hand-typed list, so a newly added preset is automatically covered by every theory below. The roster
        // guard rejects empty discovery without pinning how many presets the library must publish.

        private static readonly PropertyInfo[] PresetProperties = typeof(VfxPresets)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(VfxPreset) && p.GetIndexParameters().Length == 0)
            .ToArray();

        public static TheoryData<string> PresetNames
        {
            get
            {
                var data = new TheoryData<string>();
                foreach (PropertyInfo property in PresetProperties)
                {
                    data.Add(property.Name);
                }
                return data;
            }
        }

        static VfxPreset Resolve(string name)
        {
            PropertyInfo? property = PresetProperties.FirstOrDefault(p => p.Name == name);
            if (property is null)
            {
                throw new ArgumentOutOfRangeException(nameof(name), name, "unknown preset");
            }

            return (VfxPreset)property.GetValue(null)!;
        }

        // Every validator below reads the discovered list, so a preset the discovery missed would lose all of them.
        [Fact]
        public void PresetRoster_IsEveryShippedPreset()
        {
            string[] shipped =
            {
                "ArcaneSparkle", "EmberDrift", "EssenceMotes", "FireBurst", "FrostShatter",
                "HealMotes", "HeatHaze", "Shockwave", "SmokePlume", "SparkShower",
            };

            Assert.Equal(shipped, PresetProperties.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        }

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

            Assert.True(player.PhaseCount > 0);
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
            Assert.True(pa.PhaseCount > 0);
            Assert.Equal(pa.PhaseCount, pb.PhaseCount);
            float end = 0f;
            for (int ph = 0; ph < a.Effect.PhaseCount; ph++)
            {
                ParticleEffectPhase phase = a.Effect.GetPhase(ph);
                end = MathF.Max(end, phase.Delay + phase.Duration);
            }
            const float dt = 1f / 60f;
            int steps = (int)MathF.Ceiling(end / dt) + 1;
            bool observedParticles = false;
            for (int i = 0; i < steps; i++)
            {
                pa.Update(dt);
                pb.Update(dt);
                for (int ph = 0; ph < pa.PhaseCount; ph++)
                {
                    ParticleSystem sa = pa.PhaseSystem(ph);
                    ParticleSystem sb = pb.PhaseSystem(ph);
                    observedParticles |= sa.ActiveCount > 0;
                    Assert.Equal(sa.ActiveCount, sb.ActiveCount);
                    Assert.Equal(sa.Active.ToArray(), sb.Active.ToArray());
                }
            }
            Assert.True(observedParticles);
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

        [Fact]
        public void EssenceMotes_HasTwoPhases_OneLookEach()
        {
            VfxPreset preset = VfxPresets.EssenceMotes;

            Assert.Equal(2, preset.Effect.PhaseCount);
            Assert.Equal(2, preset.Looks.Count);
        }

        [Fact]
        public void EssenceMotes_HazePhase_IgnoresAttractor()
        {
            VfxPreset preset = VfxPresets.EssenceMotes;

            Assert.False(preset.Effect.GetPhase(0).Config.IgnoreAttractor);
            Assert.True(preset.Effect.GetPhase(1).Config.IgnoreAttractor);
        }

        [Fact]
        public void EssenceMotes_MotePhase_UsesSoftGlow()
        {
            VfxPreset preset = VfxPresets.EssenceMotes;

            Assert.Equal(ParticleShape.SoftGlow, preset.Looks[0].Shape);
            Assert.Equal(ParticleShape.Wisp, preset.Looks[1].Shape);
        }
    }
}
