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

    }
}
