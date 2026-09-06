using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Telegraphs;
using Xunit;

namespace KhaozEngine.Tests.Telegraphs
{
    public class GroundTelegraphMappingTests
    {
        [Fact]
        public void Circle_maps_radius_progress_and_style()
        {
            var d = GroundTelegraphs.BuildCircle(new Vector3(2f, 0.5f, -3f), 4f, 0.5f, TelegraphStyle.Generic);
            Assert.Equal(DecalShape.Circle, d.Shape);
            Assert.Equal(new Vector3(2f, 0.5f, -3f), d.Center);
            Assert.Equal(4f, d.Size.X, 3);                 // radius
            var r = TelegraphResolve.Resolve(0.5f, TelegraphStyle.Generic);
            Assert.Equal(r.FillFraction, d.FillFraction, 4);
            Assert.Equal(r.Blend == TelegraphBlend.Additive ? DecalBlend.Additive : DecalBlend.Alpha, d.Blend);
            Assert.Equal((Vector4)r.FillColor, (Vector4)d.FillColor);
        }

        [Fact]
        public void Cone_packs_range_halfangle_and_rotation_from_direction()
        {
            // dir = +Z (xz) -> rotation atan2(z=1, x=0) = pi/2.
            var d = GroundTelegraphs.BuildCone(Vector3.Zero, new Vector2(0f, 1f), 0.6f, 5f, 1f, TelegraphStyle.Fire);
            Assert.Equal(DecalShape.Cone, d.Shape);
            Assert.Equal(5f, d.Size.X, 3);                 // range
            Assert.Equal(0.6f, d.Size.Y, 3);               // halfAngle
            Assert.Equal(MathF.PI / 2f, d.Rotation, 3);
        }

        [Fact]
        public void Modern_style_fields_map_to_world_space_decal_fields()
        {
            var d = GroundTelegraphs.BuildCircle(Vector3.Zero, 4f, 0.5f, TelegraphStyle.Frost);
            var r = TelegraphResolve.Resolve(0.5f, TelegraphStyle.Frost);

            // Feather: fraction of characteristic size (circle radius), so 4 * 0.18.
            Assert.Equal(4f * TelegraphStyle.Frost.FeatherWidth, d.FeatherWidth, 4);
            Assert.Equal(DecalFillPattern.RadialNoise, d.Pattern);
            Assert.Equal(TelegraphStyle.Frost.PatternSpeed, d.PatternSpeed, 4);
            // PatternScale: cells across the shape become cells per world unit.
            Assert.Equal(TelegraphStyle.Frost.PatternScale / 4f, d.PatternScale, 4);
            Assert.Equal(r.RimGlow, d.RimGlow, 4);
            Assert.Equal(r.SweepGlow, d.SweepGlow, 4);
            Assert.Equal(r.Sparkle, d.Sparkle, 4);
        }

        [Fact]
        public void Legacy_style_maps_to_zero_modern_decal_fields()
        {
            var red = new Color(1f, 0f, 0f, 1f);
            var legacy = new TelegraphStyle
            {
                FillColor = red, OutlineColor = Color.White, DangerColor = red,
                EdgeThickness = 2f, Opacity = 1f, FillMode = FillMode.OutlineAndFill,
                Animation = TelegraphAnim.FillSweep, Blend = TelegraphBlend.Alpha,
            };
            var d = GroundTelegraphs.BuildCircle(Vector3.Zero, 4f, 0.5f, legacy);
            Assert.Equal(0f, d.FeatherWidth);
            Assert.Equal(DecalFillPattern.Solid, d.Pattern);
            Assert.Equal(0f, d.PatternScale);
            Assert.Equal(0f, d.RimGlow);
            Assert.Equal(0f, d.SweepGlow);
            Assert.Equal(0f, d.Sparkle);
        }

        [Fact]
        public void Residue_fades_and_expands_with_age()
        {
            var young = GroundTelegraphs.BuildResidueCircle(Vector3.Zero, 3f, 0.1f, TelegraphStyle.Fire);
            var old = GroundTelegraphs.BuildResidueCircle(Vector3.Zero, 3f, 0.9f, TelegraphStyle.Fire);
            Assert.Equal(DecalShape.Circle, young.Shape);
            Assert.True(old.FillColor.A < young.FillColor.A);
            Assert.True(old.Size.X > young.Size.X);
            Assert.Equal(1f, young.FillFraction);
            Assert.Equal(0f, young.RimGlow);
            Assert.Equal(0f, young.SweepGlow);
            // Residue is quiet: no outline band.
            Assert.Equal(0f, young.OutlineColor.A);
        }

        [Fact]
        public void Residue_with_zero_radius_produces_finite_fields()
        {
            var d = GroundTelegraphs.BuildResidueCircle(Vector3.Zero, 0f, 0.5f, TelegraphStyle.Fire);
            Assert.True(float.IsFinite(d.PatternScale));
            Assert.Equal(0f, d.PatternScale);
            Assert.True(float.IsFinite(d.FeatherWidth));
        }

        [Fact]
        public void Residue_is_pure_and_clamps_age()
        {
            var a = GroundTelegraphs.BuildResidueCircle(new Vector3(1f, 0f, 1f), 2f, 0.5f, TelegraphStyle.Frost);
            var b = GroundTelegraphs.BuildResidueCircle(new Vector3(1f, 0f, 1f), 2f, 0.5f, TelegraphStyle.Frost);
            Assert.Equal((Vector4)a.FillColor, (Vector4)b.FillColor);
            var over = GroundTelegraphs.BuildResidueCircle(Vector3.Zero, 2f, 1.5f, TelegraphStyle.Frost);
            Assert.Equal(0f, over.FillColor.A, 3);
        }
    [Fact]
    public void Interior_dim_and_runner_map_through_to_the_decal()
    {
        var d = GroundTelegraphs.BuildCircle(Vector3.Zero, 4f, 0.5f, TelegraphStyle.Arcane);
        var r = TelegraphResolve.Resolve(0.5f, TelegraphStyle.Arcane);
        Assert.Equal(r.InteriorDim, d.InteriorDim, 4);
        Assert.True(d.InteriorDim > 0f);
        Assert.Equal(r.Runner, d.Runner, 4);
        Assert.True(d.Runner > 0f);
    }

    [Fact]
    public void Legacy_style_keeps_interior_dim_and_runner_zero()
    {
        var legacy = new TelegraphStyle
        {
            FillColor = new Color(1f, 0f, 0f, 1f), OutlineColor = Color.White, DangerColor = new Color(1f, 0f, 0f, 1f),
            EdgeThickness = 2f, Opacity = 1f, FillMode = FillMode.OutlineAndFill,
            Animation = TelegraphAnim.FillSweep, Blend = TelegraphBlend.Alpha,
        };
        var d = GroundTelegraphs.BuildCircle(Vector3.Zero, 4f, 0.5f, legacy);
        Assert.Equal(0f, d.InteriorDim);
        Assert.Equal(0f, d.Runner);
    }

    [Fact]
    public void Base_fill_maps_through_and_fill_mode_zeroes_the_outline_on_the_decal()
    {
        var s = TelegraphStyle.Frost;
        s.FillMode = FillMode.Fill;
        var d = GroundTelegraphs.BuildCircle(Vector3.Zero, 4f, 0.5f, s);
        Assert.Equal(TelegraphStyle.Frost.BaseFill, d.BaseFill, 4);
        Assert.Equal(0f, d.OutlineColor.A);
        Assert.Equal(0f, d.RimGlow);
        Assert.Equal(0f, d.Runner);
    }

    [Fact]
    public void World_edge_overrides_map_verbatim_to_the_decal()
    {
        var s = TelegraphStyle.Generic;
        s.EdgeWidthWorld = 0.04f;
        s.FeatherWidthWorld = 0.02f;
        // Radius 40: the derived edge would clamp to MaxEdgeWorld 0.3 and the feather fraction
        // would give 4.0, so the overrides are unmistakably in charge.
        var d = GroundTelegraphs.BuildCircle(Vector3.Zero, 40f, 0.5f, s);
        Assert.Equal(0.04f, d.EdgeThickness, 4);
        Assert.Equal(0.02f, d.FeatherWidth, 4);
    }

    [Fact]
    public void World_edge_overrides_apply_to_ring_shape()
    {
        var s = TelegraphStyle.Generic;
        s.EdgeWidthWorld = 0.04f;
        s.FeatherWidthWorld = 0.02f;
        var d = GroundTelegraphs.BuildRing(Vector3.Zero, 5.7f, 6f, 1f, s);
        Assert.Equal(0.04f, d.EdgeThickness, 4);
        Assert.Equal(0.02f, d.FeatherWidth, 4);
    }

    [Fact]
    public void Zero_world_overrides_keep_the_derived_edge_and_feather()
    {
        var d = GroundTelegraphs.BuildCircle(Vector3.Zero, 4f, 0.5f, TelegraphStyle.Generic);
        Assert.Equal(0.2f, d.EdgeThickness, 4);            // clamp(4 * 0.05, 0.03, 0.3)
        Assert.Equal(4f * TelegraphStyle.Generic.FeatherWidth, d.FeatherWidth, 4);
    }

    [Fact]
    public void Edge_and_feather_overrides_gate_independently()
    {
        var edgeOnly = TelegraphStyle.Generic;
        edgeOnly.EdgeWidthWorld = 0.04f;
        var d = GroundTelegraphs.BuildCircle(Vector3.Zero, 4f, 0.5f, edgeOnly);
        Assert.Equal(0.04f, d.EdgeThickness, 4);
        Assert.Equal(4f * TelegraphStyle.Generic.FeatherWidth, d.FeatherWidth, 4);

        var featherOnly = TelegraphStyle.Generic;
        featherOnly.FeatherWidthWorld = 0.02f;
        d = GroundTelegraphs.BuildCircle(Vector3.Zero, 4f, 0.5f, featherOnly);
        Assert.Equal(0.2f, d.EdgeThickness, 4);
        Assert.Equal(0.02f, d.FeatherWidth, 4);
    }

    [Fact]
    public void Void_fallback_maps_verbatim_to_the_decal()
    {
        var s = TelegraphStyle.Generic;
        s.VoidFallback = true;
        s.VoidDim = 0.15f;
        var d = GroundTelegraphs.BuildCircle(Vector3.Zero, 4f, 0.5f, s);
        Assert.True(d.VoidFallback);
        Assert.Equal(0.15f, d.VoidDim, 4);
    }

    [Fact]
    public void Void_fallback_maps_through_every_shape_builder()
    {
        // Hardpoint's ask is the range RING, but the flag lives on the shared Base() mapping, so every shape gets it.
        // If a builder ever stops routing through Base, this is what catches it.
        var s = TelegraphStyle.Generic;
        s.VoidFallback = true;
        s.VoidDim = 0.25f;
        var built = new[]
        {
            GroundTelegraphs.BuildCircle(Vector3.Zero, 4f, 0.5f, s),
            GroundTelegraphs.BuildRing(Vector3.Zero, 2f, 4f, 0.5f, s),
            GroundTelegraphs.BuildBeam(Vector3.Zero, new Vector2(1f, 0f), 6f, 1f, 0.5f, s),
            GroundTelegraphs.BuildCone(Vector3.Zero, new Vector2(1f, 0f), 0.5f, 5f, 0.5f, s),
            GroundTelegraphs.BuildArc(Vector3.Zero, 4f, 0.5f, 0f, 1.5f, 0.5f, s),
            GroundTelegraphs.BuildResidueCircle(Vector3.Zero, 4f, 0.5f, s),
        };
        foreach (var d in built)
        {
            Assert.True(d.VoidFallback);
            Assert.Equal(0.25f, d.VoidDim, 4);
        }
    }

    [Fact]
    public void Void_fallback_defaults_off_for_an_untouched_style()
    {
        var d = GroundTelegraphs.BuildCircle(Vector3.Zero, 4f, 0.5f, TelegraphStyle.Generic);
        Assert.False(d.VoidFallback);
        Assert.Equal(0f, d.VoidDim);
    }

    /// <summary>
    /// THE CAST AT THE HEART OF <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/229">#229</see>.
    /// <c>GroundTelegraphs.Base</c> maps the fill pattern with a direct <c>(DecalFillPattern)</c> cast, which is
    /// sound exactly as long as the two enums agree member for member and value for value. They did not: the decal
    /// side gained MoltenCracks in 13.4.0 and the telegraph side stopped at RadialNoise, so a telegraph could not
    /// author the molten look and a decal-side member added after this would map to whatever the cast happened to
    /// produce. This is the guard that keeps the cast honest.
    /// </summary>
    [Fact]
    public void Every_decal_fill_pattern_has_a_telegraph_twin_at_the_same_value()
    {
        string[] decalNames = Enum.GetNames<DecalFillPattern>();
        string[] telegraphNames = Enum.GetNames<TelegraphFillPattern>();
        Assert.Equal(decalNames, telegraphNames);

        foreach (TelegraphFillPattern p in Enum.GetValues<TelegraphFillPattern>())
        {
            Assert.True(Enum.IsDefined((DecalFillPattern)p),
                $"TelegraphFillPattern.{p} casts to an undefined DecalFillPattern");
            Assert.Equal(p.ToString(), ((DecalFillPattern)p).ToString());
        }
    }

    [Fact]
    public void Molten_cracks_and_its_knobs_map_through_to_the_decal()
    {
        var s = TelegraphStyle.Fire;
        s.Pattern = TelegraphFillPattern.MoltenCracks;
        s.AccentColor = new Color(1f, 0.55f, 0.15f, 0.8f);
        s.PatternParam = 0.3f;
        s.EdgeErosion = 0.45f;

        var d = GroundTelegraphs.BuildCircle(Vector3.Zero, 4f, 0.5f, s);

        Assert.Equal(DecalFillPattern.MoltenCracks, d.Pattern);
        Assert.Equal(1f, d.AccentColor.R, 4);
        Assert.Equal(0.55f, d.AccentColor.G, 4);
        Assert.Equal(0.15f, d.AccentColor.B, 4);
        Assert.Equal(0.8f, d.AccentColor.A, 4);
        Assert.Equal(0.3f, d.PatternParam, 4);
        Assert.Equal(0.45f, d.EdgeErosion, 4);
    }

    [Fact]
    public void Accent_alpha_follows_the_style_opacity_like_the_fill_does()
    {
        var s = TelegraphStyle.Fire;
        s.Pattern = TelegraphFillPattern.MoltenCracks;
        s.AccentColor = new Color(1f, 0.5f, 0.2f, 0.8f);
        s.Opacity = 0.5f;

        var d = GroundTelegraphs.BuildCircle(Vector3.Zero, 4f, 0.5f, s);

        Assert.Equal(0.4f, d.AccentColor.A, 4);
        Assert.Equal(1f, d.AccentColor.R, 4);      // rgb untouched, only the alpha is scaled
    }

    /// <summary>
    /// EdgeErosion is DIMENSIONLESS (0..1 of the shape's own half-thickness), unlike FeatherWidth, which the 3D
    /// path derives in world units from the shape's characteristic size or takes verbatim from the world-unit
    /// override. So the two do not interact at this layer: erosion passes straight through untouched whichever
    /// feather path a style is on, and the shader's documented order (erode first, then feather the surviving
    /// boundary) does the rest.
    /// </summary>
    [Fact]
    public void Edge_erosion_is_unaffected_by_which_feather_path_the_style_is_on()
    {
        var derived = TelegraphStyle.Generic;
        derived.EdgeErosion = 0.6f;
        derived.FeatherWidth = 0.1f;            // shape-relative fraction path

        var pinned = TelegraphStyle.Generic;
        pinned.EdgeErosion = 0.6f;
        pinned.FeatherWidthWorld = 0.25f;       // world-unit override path

        var a = GroundTelegraphs.BuildCircle(Vector3.Zero, 4f, 0.5f, derived);
        var b = GroundTelegraphs.BuildCircle(Vector3.Zero, 4f, 0.5f, pinned);

        Assert.Equal(0.6f, a.EdgeErosion, 4);
        Assert.Equal(0.6f, b.EdgeErosion, 4);
        Assert.NotEqual(a.FeatherWidth, b.FeatherWidth);   // the feather paths really are different
    }

    [Fact]
    public void Edge_erosion_clamps_to_the_unit_range()
    {
        var hot = TelegraphStyle.Generic;
        hot.EdgeErosion = 3f;
        Assert.Equal(1f, GroundTelegraphs.BuildCircle(Vector3.Zero, 4f, 0.5f, hot).EdgeErosion, 4);

        var cold = TelegraphStyle.Generic;
        cold.EdgeErosion = -2f;
        Assert.Equal(0f, GroundTelegraphs.BuildCircle(Vector3.Zero, 4f, 0.5f, cold).EdgeErosion, 4);
    }

    [Fact]
    public void Accent_pattern_param_and_erosion_map_through_every_shape_builder()
    {
        var s = TelegraphStyle.Generic;
        s.Pattern = TelegraphFillPattern.MoltenCracks;
        s.AccentColor = new Color(0.9f, 0.2f, 0.1f, 1f);
        s.PatternParam = 0.18f;
        s.EdgeErosion = 0.35f;
        var built = new[]
        {
            GroundTelegraphs.BuildCircle(Vector3.Zero, 4f, 0.5f, s),
            GroundTelegraphs.BuildRing(Vector3.Zero, 2f, 4f, 0.5f, s),
            GroundTelegraphs.BuildBeam(Vector3.Zero, new Vector2(1f, 0f), 6f, 1f, 0.5f, s),
            GroundTelegraphs.BuildCone(Vector3.Zero, new Vector2(1f, 0f), 0.5f, 5f, 0.5f, s),
            GroundTelegraphs.BuildArc(Vector3.Zero, 4f, 0.5f, 0f, 1.5f, 0.5f, s),
        };
        foreach (var d in built)
        {
            Assert.Equal(DecalFillPattern.MoltenCracks, d.Pattern);
            Assert.Equal(0.9f, d.AccentColor.R, 4);
            Assert.Equal(0.18f, d.PatternParam, 4);
            Assert.Equal(0.35f, d.EdgeErosion, 4);
        }
    }

    [Fact]
    public void An_untouched_style_keeps_accent_pattern_param_and_erosion_fully_zero()
    {
        // The zero-neutral contract: every authored preset predates these fields, so a style that never sets them
        // must still map to a byte-for-byte unchanged decal.
        foreach (var s in new[]
                 {
                     TelegraphStyle.Generic, TelegraphStyle.Fire, TelegraphStyle.Poison, TelegraphStyle.Steel,
                     TelegraphStyle.Frost, TelegraphStyle.Nature, TelegraphStyle.Arcane,
                 })
        {
            var d = GroundTelegraphs.BuildCircle(Vector3.Zero, 4f, 0.5f, s);
            Assert.Equal(0f, d.AccentColor.R);
            Assert.Equal(0f, d.AccentColor.G);
            Assert.Equal(0f, d.AccentColor.B);
            Assert.Equal(0f, d.AccentColor.A);
            Assert.Equal(0f, d.PatternParam);
            Assert.Equal(0f, d.EdgeErosion);
        }
    }

    }
}
