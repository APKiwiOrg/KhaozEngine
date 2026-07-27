using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Headless coverage for the ocean presets (swell/ripple/foam/glint bundles applied to
    /// <see cref="WaterSettings"/>). Pure settings mutation, no GPU.
    /// </summary>
    public class OceanPresetsTests
    {
        [Theory]
        [InlineData(OceanPresetKind.Calm)]
        [InlineData(OceanPresetKind.Moderate)]
        [InlineData(OceanPresetKind.Rough)]
        public void Every_preset_leaves_grid_clipmap_bathymetry_and_surf_fields_untouched(OceanPresetKind kind)
        {
            var water = new WaterSettings();
            var gridMode = water.GridMode;
            var clipmapCellSize = water.ClipmapCellSize;
            var clipmapRingCells = water.ClipmapRingCells;
            var clipmapLevels = water.ClipmapLevels;
            var bathymetry = water.Bathymetry;
            var surfStrength = water.SurfStrength;

            OceanPresets.Apply(kind, water);

            Assert.Equal(gridMode, water.GridMode);
            Assert.Equal(clipmapCellSize, water.ClipmapCellSize);
            Assert.Equal(clipmapRingCells, water.ClipmapRingCells);
            Assert.Equal(clipmapLevels, water.ClipmapLevels);
            Assert.Equal(bathymetry, water.Bathymetry);
            Assert.Equal(surfStrength, water.SurfStrength);
        }

        [Fact]
        public void Swell_amplitude_is_ordered_calm_moderate_rough()
        {
            var calm = new WaterSettings();
            OceanPresets.Apply(OceanPresetKind.Calm, calm);
            var moderate = new WaterSettings();
            OceanPresets.Apply(OceanPresetKind.Moderate, moderate);
            var rough = new WaterSettings();
            OceanPresets.Apply(OceanPresetKind.Rough, rough);

            Assert.True(calm.SwellAmplitude < moderate.SwellAmplitude);
            Assert.True(moderate.SwellAmplitude < rough.SwellAmplitude);
        }

        [Fact]
        public void Calm_has_sparse_foam_and_rough_has_dense_whitecaps()
        {
            var calm = new WaterSettings();
            OceanPresets.Apply(OceanPresetKind.Calm, calm);
            var rough = new WaterSettings();
            OceanPresets.Apply(OceanPresetKind.Rough, rough);

            Assert.True(calm.FoamCrestCoverage < rough.FoamCrestCoverage);
            Assert.True(calm.FoamStrength < rough.FoamStrength);
        }

        [Fact]
        public void Rough_has_stronger_ripple_and_glint_than_calm()
        {
            var calm = new WaterSettings();
            OceanPresets.Apply(OceanPresetKind.Calm, calm);
            var rough = new WaterSettings();
            OceanPresets.Apply(OceanPresetKind.Rough, rough);

            Assert.True(calm.NormalStrength < rough.NormalStrength);
            Assert.True(calm.GlintStrength < rough.GlintStrength);
        }

        [Fact]
        public void All_set_values_are_finite_and_in_range()
        {
            foreach (var kind in new[] { OceanPresetKind.Calm, OceanPresetKind.Moderate, OceanPresetKind.Rough })
            {
                var water = new WaterSettings();
                OceanPresets.Apply(kind, water);

                Assert.True(float.IsFinite(water.SwellAmplitude) && water.SwellAmplitude >= 0f);
                Assert.True(float.IsFinite(water.SwellWavelength) && water.SwellWavelength > 0f);
                Assert.True(float.IsFinite(water.SwellSteepness) && water.SwellSteepness >= 0f && water.SwellSteepness <= 1f);
                Assert.True(float.IsFinite(water.SwellSpeed) && water.SwellSpeed >= 0f);
                Assert.True(float.IsFinite(water.NormalStrength) && water.NormalStrength >= 0f);
                Assert.True(float.IsFinite(water.FoamStrength) && water.FoamStrength >= 0f);
                Assert.True(float.IsFinite(water.FoamCrestCoverage) && water.FoamCrestCoverage >= 0f && water.FoamCrestCoverage <= 1f);
                Assert.True(float.IsFinite(water.GlintStrength) && water.GlintStrength >= 0f);
                Assert.True(float.IsFinite(water.GlintRoughness) && water.GlintRoughness > 0f);
            }
        }
    }
}
