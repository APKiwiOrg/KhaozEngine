using System;
using System.Numerics;
using System.Text.RegularExpressions;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>The relief half of the tile-ground params block, headless: where its entries land, what selects the
    /// relief program, the layer checks it packs behind, and the GLSL declarations the packing has to agree with.</summary>
    public class TileGroundReliefTests
    {
        static TileGroundLayerImage Plain() => new() { AlbedoRgba = new byte[4] };

        [Fact]
        public void The_relief_block_is_the_plain_block_then_surface_blend_and_relief()
        {
            Assert.Equal(TileGroundMaterialConfig.SteepBandIndex + TileGroundMaterialConfig.MaxMaterials,
                TileGroundMaterialConfig.SurfaceIndex);
            Assert.Equal((int)TileGroundMaterialConfig.ParamsBytes / 16, TileGroundMaterialConfig.SurfaceIndex);
            Assert.Equal(TileGroundMaterialConfig.SurfaceIndex + TileGroundMaterialConfig.MaxMaterials,
                TileGroundMaterialConfig.BlendIndex);
            Assert.Equal(TileGroundMaterialConfig.BlendIndex + TileGroundMaterialConfig.MaxMaterials,
                TileGroundMaterialConfig.ReliefIndex);
            Assert.Equal((uint)(TileGroundMaterialConfig.ReliefIndex + 1) * 16, TileGroundMaterialConfig.ReliefParamsBytes);

            var layers = new[]
            {
                new TileGroundLayerImage { AlbedoRgba = new byte[4], TilesPerMetre = 0.3f, HeightMapped = true },
                new TileGroundLayerImage
                {
                    AlbedoRgba = new byte[4],
                    Steep = new TileGroundSteepLayer { CompanionSlot = 0, FromDegrees = 40f, ToDegrees = 55f },
                },
            };
            Vector4[] plain = TileGroundMaterialConfig.BuildParams(layers, 0.2f);
            Vector4[] relief = TileGroundMaterialConfig.BuildReliefParams(layers, 0.2f);

            Assert.Equal((int)TileGroundMaterialConfig.ReliefParamsBytes / 16, relief.Length);
            for (int i = 0; i < plain.Length; i++) Assert.Equal(plain[i], relief[i]);
            Assert.Equal(TileGroundRelief.ReliefParams, relief[TileGroundMaterialConfig.ReliefIndex]);
        }

        [Fact]
        public void Surface_layers_are_numbered_over_the_layers_that_carry_texels_in_slot_order()
        {
            var layers = new[]
            {
                new TileGroundLayerImage { AlbedoRgba = new byte[4], SurfaceRgba = new byte[4], NormalMapped = true },
                new TileGroundLayerImage { AlbedoRgba = new byte[4], AntiTiling = TileGroundAntiTiling.Rotate },
                new TileGroundLayerImage
                {
                    AlbedoRgba = new byte[4], SurfaceRgba = new byte[4], RoughnessMapped = true,
                    AntiTiling = TileGroundAntiTiling.Offset,
                },
                Plain(),
            };

            Vector4[] tail = TileGroundMaterialConfig.BuildReliefParams(layers, 0.15f);

            Assert.Equal(new Vector4(1f, 1f, 0f, 0f), tail[TileGroundMaterialConfig.SurfaceIndex]);
            Assert.Equal(new Vector4(0f, 0f, 0f, (float)TileGroundAntiTiling.Rotate),
                tail[TileGroundMaterialConfig.SurfaceIndex + 1]);
            Assert.Equal(new Vector4(2f, 0f, 1f, (float)TileGroundAntiTiling.Offset),
                tail[TileGroundMaterialConfig.SurfaceIndex + 2]);
            for (int i = 3; i < TileGroundMaterialConfig.MaxMaterials; i++)
                Assert.Equal(Vector4.Zero, tail[TileGroundMaterialConfig.SurfaceIndex + i]);
            Assert.Equal(2, TileGroundRelief.SurfaceLayerCount(layers));
        }

        [Fact]
        public void Blend_entries_carry_height_sharpness_and_breakup_and_a_flat_layer_packs_no_sharpness()
        {
            var layers = new[]
            {
                new TileGroundLayerImage
                {
                    AlbedoRgba = new byte[4], HeightMapped = true, HeightBlendSharpness = 12f, FeatherBreakup = 0.4f,
                },
                // A sharpness the shader never reads, because there is no height map to resolve, packs as zero.
                new TileGroundLayerImage { AlbedoRgba = new byte[4], HeightBlendSharpness = 12f, FeatherBreakup = 0.7f },
                Plain(),
            };

            Vector4[] tail = TileGroundMaterialConfig.BuildReliefParams(layers, 0.15f);

            Assert.Equal(new Vector4(1f, 12f, 0.4f, 0f), tail[TileGroundMaterialConfig.BlendIndex]);
            Assert.Equal(new Vector4(0f, 0f, 0.7f, 0f), tail[TileGroundMaterialConfig.BlendIndex + 1]);
            for (int i = 2; i < TileGroundMaterialConfig.MaxMaterials; i++)
                Assert.Equal(Vector4.Zero, tail[TileGroundMaterialConfig.BlendIndex + i]);
        }

        [Fact]
        public void The_height_bound_keeps_a_skipped_weight_below_one_percent_at_the_softest_sharpness()
        {
            // The shader skips a role at weight 0.001 or less. The most that role could have won is its weight times
            // exp(influence) for a full height difference, raised to the sharpness, and that has to stay invisible.
            float largestPop = MathF.Pow(0.001f * MathF.Exp(TileGroundRelief.HeightInfluence),
                TileGroundRelief.MinimumHeightBlendSharpness);
            Assert.True(largestPop <= 0.0101f, $"a skipped role could have won {largestPop}");
            Assert.InRange(TileGroundRelief.DefaultHeightBlendSharpness,
                TileGroundRelief.MinimumHeightBlendSharpness, TileGroundRelief.MaximumHeightBlendSharpness);
        }

        static TileGroundLayerImage WithMember(string member) => member switch
        {
            "surface" => new TileGroundLayerImage { AlbedoRgba = new byte[4], SurfaceRgba = new byte[4], NormalMapped = true },
            "height" => new TileGroundLayerImage { AlbedoRgba = new byte[4], HeightMapped = true },
            "breakup" => new TileGroundLayerImage { AlbedoRgba = new byte[4], FeatherBreakup = 0.25f },
            "antiTiling" => new TileGroundLayerImage { AlbedoRgba = new byte[4], AntiTiling = TileGroundAntiTiling.Offset },
            _ => throw new ArgumentOutOfRangeException(nameof(member)),
        };

        [Theory]
        [InlineData("surface")]
        [InlineData("height")]
        [InlineData("breakup")]
        [InlineData("antiTiling")]
        public void Any_relief_member_on_any_layer_selects_the_relief_program(string member)
        {
            Assert.True(TileGroundRelief.UsesRelief(new[] { Plain(), WithMember(member) }));
        }

        [Fact]
        public void A_plain_set_and_a_sharpness_alone_keep_the_plain_program()
        {
            Assert.False(TileGroundRelief.UsesRelief(new[] { Plain(), Plain() }));
            Assert.False(TileGroundRelief.UsesRelief(new[]
            {
                new TileGroundLayerImage { AlbedoRgba = new byte[4], HeightBlendSharpness = 30f },
            }));
        }

        static TileGroundLayerImage Refused(string rule) => rule switch
        {
            "normalFlag" => new TileGroundLayerImage { NormalMapped = true },
            "roughnessFlag" => new TileGroundLayerImage { RoughnessMapped = true },
            "unclaimedSurface" => new TileGroundLayerImage { SurfaceRgba = new byte[4] },
            "sharpnessLow" => new TileGroundLayerImage { HeightMapped = true, HeightBlendSharpness = 0.5f },
            "sharpnessHigh" => new TileGroundLayerImage { HeightMapped = true, HeightBlendSharpness = 65f },
            "sharpnessNaN" => new TileGroundLayerImage { HeightMapped = true, HeightBlendSharpness = float.NaN },
            "breakupLow" => new TileGroundLayerImage { FeatherBreakup = -0.1f },
            "breakupHigh" => new TileGroundLayerImage { FeatherBreakup = 1.1f },
            "breakupNaN" => new TileGroundLayerImage { FeatherBreakup = float.NaN },
            "antiTiling" => new TileGroundLayerImage { AntiTiling = (TileGroundAntiTiling)7 },
            _ => throw new ArgumentOutOfRangeException(nameof(rule)),
        };

        [Theory]
        [InlineData("normalFlag", TileGroundReliefRefusal.MapFlagWithoutSurface, null)]
        [InlineData("roughnessFlag", TileGroundReliefRefusal.MapFlagWithoutSurface, null)]
        [InlineData("unclaimedSurface", TileGroundReliefRefusal.SurfaceWithoutMapFlag, null)]
        [InlineData("sharpnessLow", TileGroundReliefRefusal.HeightBlendSharpnessOutsideRange, 0.5f)]
        [InlineData("sharpnessHigh", TileGroundReliefRefusal.HeightBlendSharpnessOutsideRange, 65f)]
        [InlineData("sharpnessNaN", TileGroundReliefRefusal.HeightBlendSharpnessOutsideRange, float.NaN)]
        [InlineData("breakupLow", TileGroundReliefRefusal.FeatherBreakupOutsideRange, -0.1f)]
        [InlineData("breakupHigh", TileGroundReliefRefusal.FeatherBreakupOutsideRange, 1.1f)]
        [InlineData("breakupNaN", TileGroundReliefRefusal.FeatherBreakupOutsideRange, float.NaN)]
        [InlineData("antiTiling", TileGroundReliefRefusal.AntiTilingUndefined, 7f)]
        public void A_layer_whose_relief_members_disagree_is_refused_by_name(string rule, TileGroundReliefRefusal reason,
            float? value)
        {
            TileGroundLayerImage layer = Refused(rule);
            layer.AlbedoRgba = new byte[4];
            var ex = Assert.Throws<TileGroundReliefArgumentException>(() =>
                TileGroundMaterialConfig.BuildReliefParams(new[] { Plain(), layer }, 0.15f));

            Assert.IsAssignableFrom<ArgumentException>(ex);
            Assert.Equal("layers", ex.ParamName);
            Assert.Equal(reason, ex.Reason);
            Assert.Equal(1, ex.LayerIndex);
            Assert.Equal(value, ex.Value);
        }

        [Fact]
        public void A_sound_layer_with_every_member_packs()
        {
            // The passing control for every refusal above: each member set within its rule.
            TileGroundMaterialConfig.BuildReliefParams(new[]
            {
                new TileGroundLayerImage
                {
                    AlbedoRgba = new byte[4], SurfaceRgba = new byte[4], NormalMapped = true, RoughnessMapped = true,
                    HeightMapped = true, HeightBlendSharpness = TileGroundRelief.MaximumHeightBlendSharpness,
                    FeatherBreakup = 1f, AntiTiling = TileGroundAntiTiling.Rotate,
                },
                new TileGroundLayerImage
                {
                    AlbedoRgba = new byte[4], HeightMapped = true,
                    HeightBlendSharpness = TileGroundRelief.MinimumHeightBlendSharpness, FeatherBreakup = 0f,
                },
                // Out of range, and not read: this layer has no height map.
                new TileGroundLayerImage { AlbedoRgba = new byte[4], HeightBlendSharpness = 0f },
            }, 0.15f);
        }

        [Fact]
        public void The_relief_glsl_block_extends_the_plain_block_in_packing_order()
        {
            string frag = ShaderSources.TileGroundReliefFrag;
            int Index(string declaration) => frag.IndexOf(declaration, StringComparison.Ordinal);
            int n = TileGroundMaterialConfig.MaxMaterials;
            int[] order =
            {
                Index("vec4 TintTiling[" + n + "];"), Index("vec4 Misc;"), Index("vec4 Triplanar;"),
                Index("vec4 SteepBand[" + n + "];"), Index("vec4 Surface[" + n + "];"), Index("vec4 Blend[" + n + "];"),
                Index("vec4 Relief;"),
            };
            Assert.True(order[0] >= 0, "TileGroundReliefFrag lost its params block.");
            for (int i = 1; i < order.Length; i++)
                Assert.True(order[i] > order[i - 1],
                    "TileGroundParams must declare TintTiling, Misc, Triplanar, SteepBand, Surface, Blend, Relief in " +
                    "that order, the order TileGroundMaterialConfig.BuildReliefParams packs them.");
            Assert.Contains("layout(set=1, binding=2) uniform texture2DArray SurfaceArray;", frag);
        }

        [Fact]
        public void Every_relief_tap_samples_with_explicit_gradients()
        {
            // The relief taps run under data-dependent branches (the weight skip, the triplanar, companion, surface
            // and hex branches), where an implicit texture() takes undefined derivatives.
            string frag = ShaderSources.TileGroundReliefFrag;
            Assert.DoesNotMatch(new Regex(@"\btexture\("), frag);
            // The two hoisted derivatives in main's uniform flow are the only ones the fragment takes.
            Assert.Contains("vec3 dWx = dFdx(vWorldPos) * materialGradScale();", frag);
            Assert.Contains("vec3 dWy = dFdy(vWorldPos) * materialGradScale();", frag);
            Assert.Single(Regex.Matches(frag, @"\bdFdx\("));
            Assert.Single(Regex.Matches(frag, @"\bdFdy\("));
        }
    }
}
