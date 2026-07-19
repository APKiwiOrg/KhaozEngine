using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Particles;
using Xunit;

namespace KhaozEngine.Tests.ParticlesRender3D
{
    // Headless tests for the modern VFX preset library: shape (one look per phase, positive pools), determinism
    // (the sim under a preset is a pure function of the ctor seed + call sequence), and that every preset actually
    // emits within its authored schedule. No GPU: the adapter presentation side is covered by the GpuFact tests.
    public sealed class VfxPresetsTests
    {
        // The roster is discovered by reflection over VfxPresets' public static VfxPreset properties, instead of
        // a hand-typed list, so a newly added preset is automatically covered by every theory below with nothing
        // else to update. As of this write there are 9: FireBurst, FrostShatter, HealMotes, EmberDrift,
        // SparkShower, Shockwave, SmokePlume, ArcaneSparkle, HeatHaze. That count is also the floor
        // MinimumPresetCount checks in PresetRoster_HasAtLeastTheKnownPresetCount below, so an accidental
        // emptying of the roster (a broken filter, a renamed type) fails loudly instead of quietly running the
        // theories below over zero cases.
        private const int MinimumPresetCount = 9;

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

        [Fact]
        public void PresetRoster_HasAtLeastTheKnownPresetCount()
        {
            Assert.True(
                PresetProperties.Length >= MinimumPresetCount,
                $"expected at least {MinimumPresetCount} VfxPresets properties, found {PresetProperties.Length}. " +
                "An emptied or misdiscovered preset roster would otherwise run every theory below over zero cases.");
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
