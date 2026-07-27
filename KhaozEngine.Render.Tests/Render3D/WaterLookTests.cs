using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// The per-plane water look: what a <see cref="WaterLook"/> may override, what it inherits, and what it is
    /// structurally unable to touch. All headless - the whole feature is a different set of numbers written into a
    /// UBO slot that was being written anyway, so none of it needs a device.
    /// </summary>
    public sealed class WaterLookTests
    {
        // ---- The overridable set -----------------------------------------------------------------------------

        /// <summary>One overridable field: how a look sets it, and where its value lands in the packed slot.
        /// Adding a field to <see cref="WaterLook"/> is one row here, and
        /// <see cref="Every_overridable_field_is_covered_by_the_table"/> fails until it is added.</summary>
        sealed record LookField(string Name, Action<WaterLook> Set, Func<WaterRenderer.WaterUbo, float> Read);

        // Every value differs from the WaterSettings default, so "the slot moved" is unambiguous. WaveSource is
        // deliberately absent: it does not reach PackUbo at all (the shader reads the ocean maps' own live flag),
        // so its two tests are the demand pair at the bottom of this file.
        static readonly LookField[] Table =
        {
            // Body colour.
            new(nameof(WaterLook.DeepColor), l => l.DeepColor = new Color(0.7f, 0.11f, 0.02f, 0.5f), u => u.DeepColor.X),
            new(nameof(WaterLook.ShallowColor), l => l.ShallowColor = new Color(0.02f, 0.11f, 0.7f, 0.5f), u => u.ShallowColor.Y),
            new(nameof(WaterLook.AbsorptionPerMetre), l => l.AbsorptionPerMetre = new Color(0.03f, 0.05f, 0.07f, 0f), u => u.Absorption.X),
            new(nameof(WaterLook.ShallowDepth), l => l.ShallowDepth = 7.5f, u => u.DetailParams.W),
            new(nameof(WaterLook.Opacity), l => l.Opacity = 0.42f, u => u.ShoreGlint.W),

            // Swell.
            new(nameof(WaterLook.SwellAmplitude), l => l.SwellAmplitude = 0.04f, u => u.SwellParams.X),
            new(nameof(WaterLook.SwellWavelength), l => l.SwellWavelength = 6f, u => u.SwellParams.Y),
            new(nameof(WaterLook.SwellDirectionDegrees), l => l.SwellDirectionDegrees = 90f, u => u.SwellParams.Z),
            new(nameof(WaterLook.SwellSpreadDegrees), l => l.SwellSpreadDegrees = 10f, u => u.SwellParams.W),
            new(nameof(WaterLook.SwellSteepness), l => l.SwellSteepness = 0.15f, u => u.SwellShape.X),
            new(nameof(WaterLook.SwellSpeed), l => l.SwellSpeed = 0.2f, u => u.SwellShape.Y),
            new(nameof(WaterLook.SwellComponents), l => l.SwellComponents = 2, u => u.SwellShape.Z),
            new(nameof(WaterLook.SwellSeed), l => l.SwellSeed = 3.5f, u => u.SwellShape.W),

            // Depth response. The FIELD stays scene-wide, only how hard this body reacts to it is per plane.
            new(nameof(WaterLook.ShoalingStrength), l => l.ShoalingStrength = 0.25f, u => u.BathyParams.Y),
            new(nameof(WaterLook.SurfStrength), l => l.SurfStrength = 0f, u => u.SurfParams.X),

            // Ripple and detail.
            new(nameof(WaterLook.WaveScale), l => l.WaveScale = 0.9f, u => u.WaveParams.X),
            new(nameof(WaterLook.WaveSpeed), l => l.WaveSpeed = 0.08f, u => u.WaveParams.Y),
            new(nameof(WaterLook.NormalStrength), l => l.NormalStrength = 0.05f, u => u.WaveParams.Z),
            new(nameof(WaterLook.WaveWarpStrength), l => l.WaveWarpStrength = 0.1f, u => u.DetailParams.X),
            new(nameof(WaterLook.RippleComponents), l => l.RippleComponents = 3, u => u.RippleSpectrum.X),
            new(nameof(WaterLook.RippleLacunarity), l => l.RippleLacunarity = 1.9f, u => u.RippleSpectrum.Y),
            new(nameof(WaterLook.RippleGain), l => l.RippleGain = 0.4f, u => u.RippleSpectrum.Z),
            new(nameof(WaterLook.RippleSeed), l => l.RippleSeed = 7.25f, u => u.RippleSpectrum.W),
            new(nameof(WaterLook.VarianceToRoughness), l => l.VarianceToRoughness = 0.3f, u => u.FootprintParams.Y),
            new(nameof(WaterLook.DetailFadeDistance), l => l.DetailFadeDistance = 18f, u => u.DetailParams.Y),
            new(nameof(WaterLook.DistantDetailScale), l => l.DistantDetailScale = 0.6f, u => u.DetailParams.Z),

            // Foam.
            new(nameof(WaterLook.FoamColor), l => l.FoamColor = new Color(0.2f, 0.3f, 0.4f, 0.5f), u => u.FoamColor.X),
            new(nameof(WaterLook.FoamStrength), l => l.FoamStrength = 0f, u => u.FoamParams.X),
            new(nameof(WaterLook.FoamCrestCoverage), l => l.FoamCrestCoverage = 0.05f, u => u.FoamParams.Y),
            new(nameof(WaterLook.FoamShoreWidth), l => l.FoamShoreWidth = 0.35f, u => u.FoamParams.Z),
            new(nameof(WaterLook.FoamPatternScale), l => l.FoamPatternScale = 0.7f, u => u.FoamParams.W),

            // Shore.
            new(nameof(WaterLook.ShoreFadeDistance), l => l.ShoreFadeDistance = 0.15f, u => u.ShoreGlint.X),
        };

        public static TheoryData<string> OverridableFields
        {
            get
            {
                var data = new TheoryData<string>();
                foreach (LookField f in Table) data.Add(f.Name);
                return data;
            }
        }

        [Theory]
        [MemberData(nameof(OverridableFields))]
        public void Setting_a_field_moves_its_slot_and_leaving_it_null_inherits_the_scene(string name)
        {
            LookField field = Table.Single(f => f.Name == name);
            var scene = new WaterSettings();
            WaterRenderer.WaterUbo scenePack = Pack(scene);

            // Set ONLY this field: its slot must move off the scene's value.
            var only = new WaterLook();
            field.Set(only);
            WaterRenderer.WaterUbo overridden = Pack(only.ResolveInto(new WaterSettings(), scene));
            Assert.True(MathF.Abs(field.Read(overridden) - field.Read(scenePack)) > 1e-6f,
                $"WaterLook.{name} was set but the packed slot did not move: the resolver drops it, so the knob " +
                "is silently not overridable.");

            // Set every OTHER field: this one must still read the scene's value. Catches a resolver that writes
            // the wrong target as well as one that writes an inherited field it should have left alone.
            var everythingElse = new WaterLook();
            foreach (LookField other in Table)
                if (other.Name != name) other.Set(everythingElse);
            WaterRenderer.WaterUbo inherited = Pack(everythingElse.ResolveInto(new WaterSettings(), scene));
            Assert.Equal(field.Read(scenePack), field.Read(inherited), 6);
        }

        [Fact]
        public void Every_overridable_field_is_covered_by_the_table()
        {
            HashSet<string> declared = LookFieldNames();
            Assert.True(declared.Count == 33,
                $"WaterLook declares {declared.Count} fields, not the 33 the design specifies. Either the cut " +
                "moved (update the design doc) or a field landed here by accident.");

            HashSet<string> covered = Table.Select(f => f.Name).ToHashSet();
            covered.Add(nameof(WaterLook.WaveSource));   // covered by the demand tests, not by a packed slot
            Assert.Equal(declared.OrderBy(n => n, StringComparer.Ordinal),
                covered.OrderBy(n => n, StringComparer.Ordinal));
        }

        [Fact]
        public void The_scene_wide_knobs_are_absent_from_the_look_entirely()
        {
            // Not "ignored if set" but ABSENT, which is the only version of this that cannot rot: each of these
            // backs a once-per-frame GPU resource or selects the pass's pipeline and index buffer, so a per-plane
            // value would be a promise the renderer cannot keep.
            HashSet<string> declared = LookFieldNames();
            foreach (string sceneWide in new[]
                     {
                         nameof(WaterSettings.SeaState), nameof(WaterSettings.Bathymetry),
                         nameof(WaterSettings.GridMode), nameof(WaterSettings.GridFocusBias),
                         nameof(WaterSettings.ClipmapCellSize), nameof(WaterSettings.ClipmapRingCells),
                         nameof(WaterSettings.ClipmapLevels), nameof(WaterSettings.ClipmapGeomorphBand),
                         nameof(WaterSettings.ClipmapBandLimitSamples), nameof(WaterSettings.FootprintSamples),
                         nameof(WaterSettings.HorizonColor), nameof(WaterSettings.SkyReflectionStrength),
                         nameof(WaterSettings.SkyReflectionSunStrength), nameof(WaterSettings.GlintStrength),
                         nameof(WaterSettings.GlintRoughness), nameof(WaterSettings.GlintDistantRoughness),
                         nameof(WaterSettings.GlintExponent), nameof(WaterSettings.SurfBreakerIndex),
                         nameof(WaterSettings.SurfBandWidth), nameof(WaterSettings.SurfCrestBias),
                         nameof(WaterSettings.SurfTrailWidth), nameof(WaterSettings.SurfAmplitudeCollapse),
                         nameof(WaterSettings.ShoalingDepthScale),
                     })
                Assert.DoesNotContain(sceneWide, declared);
        }

        // ---- Inheriting --------------------------------------------------------------------------------------

        [Fact]
        public void An_empty_look_packs_the_same_bytes_as_the_scene_settings()
        {
            // The no-look path in WaterRenderer.Draw hands PackUbo the caller's own settings object, so byte
            // identity there is structural. This pins the resolver to the same answer, which is what stops a
            // future refactor quietly forking the two paths.
            var scene = new WaterSettings();
            PerturbEveryField(scene);
            WaterRenderer.WaterUbo direct = Pack(scene);
            WaterRenderer.WaterUbo resolved = Pack(new WaterLook().ResolveInto(new WaterSettings(), scene));
            Assert.Equal(Bytes(direct), Bytes(resolved));
        }

        [Fact]
        public void CopyFrom_carries_every_field_of_WaterSettings()
        {
            // The scratch starts at its defaults and the scene is perturbed away from every default, so a field
            // CopyFrom forgets shows up as the default surviving. Reflection-driven on purpose: a hand-written
            // list would have to be kept in step with WaterSettings, which is the drift this guards.
            var scene = new WaterSettings();
            PerturbEveryField(scene);
            var scratch = new WaterSettings();
            WaterSettings resolved = new WaterLook().ResolveInto(scratch, scene);

            Assert.Same(scratch, resolved);
            foreach (FieldInfo f in SettingsFields())
                Assert.True(Equals(f.GetValue(scene), f.GetValue(resolved)),
                    $"WaterSettings.CopyFrom does not carry '{f.Name}', so a plane with a look silently reverts " +
                    "it to the default.");
        }

        [Fact]
        public void A_look_cannot_fork_the_sea_state_or_the_bathymetry()
        {
            var scene = new WaterSettings
            {
                SeaState = new WaterSeaState { WindSpeed = 17f },
                Bathymetry = new WaterBathymetry(8, 0f, 0f, 16f),
            };
            var look = new WaterLook { WaveSource = WaterWaveSource.Procedural, SwellAmplitude = 0.02f };

            WaterSettings effective = look.ResolveInto(new WaterSettings(), scene);

            // By REFERENCE, deliberately: one bake and one depth texture per scene, so the scratch has to point at
            // the very objects the producer and the map uploader were driven from.
            Assert.Same(scene.SeaState, effective.SeaState);
            Assert.Same(scene.Bathymetry, effective.Bathymetry);
        }

        // ---- Demand-driven ocean -----------------------------------------------------------------------------

        [Fact]
        public void One_plane_asking_for_the_ocean_is_enough_to_bake_it()
        {
            var scene = new WaterSettings { WaveSource = WaterWaveSource.Procedural };
            var planes = new[]
            {
                new WaterPlane(0f, 0f, 0f, 10f),
                new WaterPlane(60f, 0f, 0f, 10f, -1f, new WaterLook { WaveSource = WaterWaveSource.FftOcean }),
            };

            Assert.Equal(WaterWaveSource.Procedural, WaterRenderer.EffectiveWaveSource(planes[0], scene));
            Assert.Equal(WaterWaveSource.FftOcean, WaterRenderer.EffectiveWaveSource(planes[1], scene));
            Assert.True(WaterRenderer.AnyPlaneWantsOcean(planes, scene),
                "the producer would have been left inactive and the FftOcean plane would render procedurally, " +
                "silently: this is the behavioural fix the per-plane wave source forces.");
        }

        [Fact]
        public void Every_plane_overriding_away_from_the_ocean_leaves_it_unwanted()
        {
            var scene = new WaterSettings { WaveSource = WaterWaveSource.FftOcean };
            var calm = new WaterLook { WaveSource = WaterWaveSource.Procedural };
            var planes = new[]
            {
                new WaterPlane(0f, 0f, 0f, 10f, -1f, calm),
                new WaterPlane(60f, 0f, 0f, 10f, -1f, calm),
            };

            Assert.False(WaterRenderer.AnyPlaneWantsOcean(planes, scene));
            // A plane with no look still inherits the scene's source, so the same scene with one plain plane does
            // want it. Demand, not a new default.
            Assert.True(WaterRenderer.AnyPlaneWantsOcean(new[] { new WaterPlane(0f, 0f, 0f, 10f) }, scene));
        }

        [Fact]
        public void A_look_with_no_wave_source_leaves_the_planes_source_alone()
        {
            var scene = new WaterSettings { WaveSource = WaterWaveSource.FftOcean };
            var plane = new WaterPlane(0f, 0f, 0f, 10f, -1f, new WaterLook { FoamStrength = 0f });
            Assert.Equal(WaterWaveSource.FftOcean, WaterRenderer.EffectiveWaveSource(plane, scene));
            Assert.Equal(WaterWaveSource.FftOcean,
                new WaterLook { FoamStrength = 0f }.ResolveInto(new WaterSettings(), scene).WaveSource);
        }

        [Fact]
        public void A_plane_carries_its_look_and_defaults_to_none()
        {
            var look = new WaterLook { SurfStrength = 0f };
            Assert.Null(new WaterPlane(0f, 0f, 0f, 10f).Look);
            Assert.Null(new WaterPlane(0f, 0f, 0f, 10f, 20f).Look);
            Assert.Same(look, new WaterPlane(0f, 0f, 0f, 10f, 20f, look).Look);
        }

        // ---- helpers -----------------------------------------------------------------------------------------

        static WaterRenderer.WaterUbo Pack(WaterSettings settings)
            => WaterRenderer.PackUbo(Matrix4x4.Identity, Matrix4x4.Identity, -Vector3.UnitY, Color.White,
                new Vector3(3f, 12f, -7f), settings, new SkySettings(), timeSeconds: 1.25f);

        static byte[] Bytes(in WaterRenderer.WaterUbo u)
        {
            var bytes = new byte[(int)WaterRenderer.PayloadBytes];
            MemoryMarshal.Write(bytes, in u);
            return bytes;
        }

        static IEnumerable<FieldInfo> SettingsFields()
            => typeof(WaterSettings).GetFields(BindingFlags.Public | BindingFlags.Instance);

        static HashSet<string> LookFieldNames()
            => typeof(WaterLook).GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Select(f => f.Name).ToHashSet();

        /// <summary>Move every field of a <see cref="WaterSettings"/> off its default, so a copy that misses one
        /// is visible rather than accidentally right.</summary>
        static void PerturbEveryField(WaterSettings settings)
        {
            foreach (FieldInfo f in SettingsFields())
            {
                object? before = f.GetValue(settings);
                f.SetValue(settings, Perturbed(f.FieldType, before));
                Assert.False(Equals(before, f.GetValue(settings)),
                    $"the test's own perturbation of '{f.Name}' landed back on its original value, which would " +
                    "make the copy guard pass for free. Give this type a different perturbation.");
            }
        }

        static object Perturbed(Type type, object? value)
        {
            if (type.IsEnum)
            {
                foreach (object candidate in Enum.GetValues(type))
                    if (!Equals(candidate, value)) return candidate;
                throw new InvalidOperationException($"{type.Name} has only one value, so it cannot be perturbed.");
            }
            return value switch
            {
                float v => v + 1.5f,
                int v => v + 3,
                bool v => !v,
                Color c => new Color(c.R * 0.5f + 0.13f, c.G * 0.25f + 0.31f, c.B * 0.5f + 0.07f, c.A * 0.5f + 0.19f),
                WaterSeaState => new WaterSeaState(),
                WaterBathymetry b => new WaterBathymetry(b.Resolution + 2, 1f, 2f, 8f),
                null when type == typeof(WaterBathymetry) => new WaterBathymetry(8, 1f, 2f, 8f),
                _ => throw new InvalidOperationException(
                    $"WaterSettings gained a field of type {type.Name} that this test does not know how to " +
                    "perturb. Add a case, or the copy guard silently stops covering it."),
            };
        }
    }
}
