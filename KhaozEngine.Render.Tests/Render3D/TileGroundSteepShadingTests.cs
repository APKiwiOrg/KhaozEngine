using System;
using System.Numerics;
using System.Text.RegularExpressions;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>The slope-aware half of the tile-ground params block: where the Triplanar vector and the SteepBand
    /// entries land, what they hold, the checks a companion must pass to be packed, and the GLSL declarations the
    /// packing has to agree with. Headless.</summary>
    public class TileGroundSteepShadingTests
    {
        static TileGroundLayerImage Layer(TileGroundSteepLayer? steep = null) =>
            new() { AlbedoRgba = new byte[4], Tint = Color.White, TilesPerMetre = 0.5f, Steep = steep };

        static float Cos(float degrees) => MathF.Cos(degrees * MathF.PI / 180f);

        [Fact]
        public void The_block_is_two_material_arrays_and_two_globals()
        {
            Assert.Equal((2 * TileGroundMaterialConfig.MaxMaterials + 2) * 16, (int)TileGroundMaterialConfig.ParamsBytes);
            Assert.Equal(2080u, TileGroundMaterialConfig.ParamsBytes);
            Assert.Equal(64, TileGroundMaterialConfig.MiscIndex);
            Assert.Equal(65, TileGroundMaterialConfig.TriplanarIndex);
            Assert.Equal(66, TileGroundMaterialConfig.SteepBandIndex);
            Assert.Equal(
                (int)TileGroundMaterialConfig.ParamsBytes / 16,
                TileGroundMaterialConfig.SteepBandIndex + TileGroundMaterialConfig.MaxMaterials);
        }

        [Fact]
        public void Triplanar_packs_the_engine_thresholds_as_cosines()
        {
            Vector4[] tail = TileGroundMaterialConfig.BuildParams(new[] { Layer() }, 0.15f);

            Vector4 t = tail[TileGroundMaterialConfig.TriplanarIndex];
            Assert.Equal(TileGroundSteepShading.TriplanarParams, t);
            Assert.Equal(Cos(45f), t.X, 6);
            Assert.Equal(Cos(60f), t.Y, 6);
            Assert.Equal(4f, t.Z);
            Assert.Equal(0f, t.W);
            Assert.True(t.Y < t.X, "smoothstep's edges have to rise, so cos(full) must sit below cos(start).");
            Assert.True(TileGroundSteepShading.TriplanarStartDegrees < TileGroundSteepShading.TriplanarFullDegrees);
        }

        [Fact]
        public void A_companion_packs_its_slot_plus_one_and_its_band_low_edge_first()
        {
            var layers = new[]
            {
                Layer(new TileGroundSteepLayer { CompanionSlot = 2, FromDegrees = 40f, ToDegrees = 55f }),
                Layer(),
                Layer(),
            };

            Vector4[] tail = TileGroundMaterialConfig.BuildParams(layers, 0.15f);

            Vector4 band = tail[TileGroundMaterialConfig.SteepBandIndex];
            Assert.Equal(3f, band.X);
            Assert.Equal(Cos(55f), band.Y, 6);
            Assert.Equal(Cos(40f), band.Z, 6);
            Assert.Equal(0f, band.W);
            Assert.True(band.Y < band.Z);
        }

        [Fact]
        public void A_layer_with_no_companion_and_every_unfilled_slot_pack_zero()
        {
            var layers = new[]
            {
                Layer(new TileGroundSteepLayer { CompanionSlot = 1, FromDegrees = 30f, ToDegrees = 70f }),
                Layer(),
            };

            Vector4[] tail = TileGroundMaterialConfig.BuildParams(layers, 0.15f);

            Assert.NotEqual(Vector4.Zero, tail[TileGroundMaterialConfig.SteepBandIndex]);
            for (int i = 1; i < TileGroundMaterialConfig.MaxMaterials; i++)
                Assert.Equal(Vector4.Zero, tail[TileGroundMaterialConfig.SteepBandIndex + i]);
            Assert.Equal(Vector4.Zero, TileGroundSteepShading.SteepBand(null));
        }

        [Fact]
        public void Adding_a_companion_leaves_the_tint_and_misc_entries_where_they_were()
        {
            var plain = new[] { Layer(), Layer() };
            var steep = new[] { Layer(new TileGroundSteepLayer { CompanionSlot = 1, FromDegrees = 40f, ToDegrees = 55f }), Layer() };

            Vector4[] a = TileGroundMaterialConfig.BuildParams(plain, 0.3f);
            Vector4[] b = TileGroundMaterialConfig.BuildParams(steep, 0.3f);

            for (int i = 0; i <= TileGroundMaterialConfig.TriplanarIndex; i++) Assert.Equal(a[i], b[i]);
        }

        [Fact]
        public void A_companion_naming_its_own_layer_is_refused()
        {
            var layers = new[] { Layer(new TileGroundSteepLayer { CompanionSlot = 0, FromDegrees = 40f, ToDegrees = 55f }), Layer() };
            var ex = Assert.Throws<ArgumentException>(() => TileGroundMaterialConfig.BuildParams(layers, 0.15f));
            Assert.Contains("names itself", ex.Message);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(2)]
        public void A_companion_outside_the_set_is_refused(int slot)
        {
            var layers = new[] { Layer(new TileGroundSteepLayer { CompanionSlot = slot, FromDegrees = 40f, ToDegrees = 55f }), Layer() };
            var ex = Assert.Throws<ArgumentException>(() => TileGroundMaterialConfig.BuildParams(layers, 0.15f));
            Assert.Contains("outside the set's 2 layers", ex.Message);
        }

        [Theory]
        [InlineData(0f, 55f)]
        [InlineData(40f, 90f)]
        [InlineData(55f, 40f)]
        [InlineData(50f, 50f)]
        [InlineData(float.NaN, 50f)]
        public void A_band_that_does_not_rise_inside_a_right_angle_is_refused(float from, float to)
        {
            var layers = new[] { Layer(new TileGroundSteepLayer { CompanionSlot = 1, FromDegrees = from, ToDegrees = to }), Layer() };
            var ex = Assert.Throws<ArgumentException>(() => TileGroundMaterialConfig.BuildParams(layers, 0.15f));
            Assert.Contains("must rise strictly inside (0, 90)", ex.Message);
        }

        [Fact]
        public void The_glsl_block_declares_the_arrays_in_packing_order()
        {
            string frag = ShaderSources.TileGroundFrag;
            int tint = frag.IndexOf("vec4 TintTiling[" + TileGroundMaterialConfig.MaxMaterials + "];", StringComparison.Ordinal);
            int misc = frag.IndexOf("vec4 Misc;", StringComparison.Ordinal);
            int triplanar = frag.IndexOf("vec4 Triplanar;", StringComparison.Ordinal);
            int steep = frag.IndexOf("vec4 SteepBand[" + TileGroundMaterialConfig.MaxMaterials + "];", StringComparison.Ordinal);

            Assert.True(tint >= 0 && misc > tint && triplanar > misc && steep > triplanar,
                "TileGroundParams must declare TintTiling, Misc, Triplanar, SteepBand in that order, the order " +
                "TileGroundMaterialConfig.BuildParams packs them.");
            Assert.DoesNotContain("SteepBand", ShaderSources.TileGroundVert);
        }

        [Fact]
        public void The_material_tap_samples_with_explicit_gradients_only()
        {
            // The taps run under data-dependent branches (the weight skip, the triplanar and companion branches),
            // where an implicit texture() takes undefined derivatives. Every side tap is a swizzle of the hoisted
            // dWx/dWy instead.
            Match body = Regex.Match(ShaderSources.TileGroundFrag,
                @"vec3 sampleMaterial\(.*?\n\}", RegexOptions.Singleline);
            Assert.True(body.Success, "TileGroundFrag lost sampleMaterial.");
            Assert.DoesNotContain("texture(", body.Value);
            Assert.DoesNotContain("dFdx", body.Value);
            Assert.Contains("sampleSlot(", body.Value);
            Assert.Contains("vec2(wpAbs.z, -wpAbs.y)", body.Value);
            Assert.Contains("vec2(wpAbs.x, -wpAbs.y)", body.Value);
            Assert.Contains("vec3 dWx = dFdx(vWorldPos)", ShaderSources.TileGroundFrag);
        }

        [Fact]
        public void The_companion_is_one_hop_and_clamped_to_the_real_layers()
        {
            string frag = ShaderSources.TileGroundFrag;
            Assert.Contains("vec4 sb = SteepBand[sl];", frag);
            Assert.Contains("sampleMaterial(clamp(int(sb.x - 0.5), 0, maxSlot)", frag);
            Assert.Contains("float s = 1.0 - smoothstep(sb.y, sb.z, ny);", frag);
            Assert.Contains("float triFade = 1.0 - smoothstep(Triplanar.y, Triplanar.x, ny);", frag);
        }
    }
}
