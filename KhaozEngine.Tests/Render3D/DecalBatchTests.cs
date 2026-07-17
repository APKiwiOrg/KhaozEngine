using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;
using DecalRun = KhaozEngine.Render3D.Rendering.GroundDecalRenderer.DecalRun;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Headless coverage for the batched, footprint-bounded ground-decal path (GroundDecalRenderer): per-instance
    /// attribute packing, the shape bounding radius, the projected screen-rect footprint math + camera-straddle
    /// fallback, and blend-run coalescing (submission-order preserving). The GPU proof that the batched render matches
    /// the pre-change output lives in KhaozEngine.Tests/Gpu/GroundDecalBatchGpuTests.
    /// </summary>
    public sealed class DecalBatchTests
    {
        static GroundDecal Circle(float cx, float cz, float radius, DecalBlend blend = DecalBlend.Alpha) => new()
        {
            Shape = DecalShape.Circle, Center = new Vector3(cx, 0f, cz), Rotation = 0f,
            Size = new Vector4(radius, 0, 0, 0),
            FillColor = new Color(1f, 0.2f, 0.1f, 0.6f), OutlineColor = new Color(1f, 0.9f, 0.2f, 0.9f),
            EdgeThickness = 0.08f, FillFraction = 1f, FlashAdd = 0f, Blend = blend,
            YTolerance = 0.3f, MaxStep = 0.4f,
        };

        [Fact]
        public void PackInstance_carries_shape_center_size_colors_and_gate()
        {
            var d = new GroundDecal
            {
                Shape = DecalShape.Cone, Center = new Vector3(2f, 0.5f, -3f), Rotation = 1.25f,
                Size = new Vector4(7f, 0.4f, 0f, 0f),
                FillColor = new Color(0.2f, 0.3f, 0.4f, 0.5f), OutlineColor = new Color(1f, 0.9f, 0.1f, 0.8f),
                EdgeThickness = 0.15f, FillFraction = 0.6f, FlashAdd = 0.25f, Blend = DecalBlend.Additive,
                YTolerance = 0.5f, MaxStep = 1.5f,
            };
            var rect = new Vector4(-0.5f, -0.25f, 0.5f, 0.25f);
            var inst = GroundDecalRenderer.PackInstance(d, rect);

            Assert.Equal(rect, inst.ScreenRect);
            Assert.Equal((float)(int)DecalShape.Cone, inst.Params.W, 3);   // shape index in Params.w
            Assert.Equal(d.Size, inst.Size);
            Assert.Equal(d.Center.X, inst.Center.X, 3);
            Assert.Equal(d.Rotation, inst.Center.W, 3);                    // rotation packed in Center.w
            Assert.Equal(d.FillColor.R, inst.Fill.X, 3);
            Assert.Equal(d.OutlineColor.A, inst.Outline.W, 3);
            Assert.Equal(d.EdgeThickness, inst.Params.X, 3);
            Assert.Equal(d.FillFraction, inst.Params.Y, 3);
            Assert.Equal(d.FlashAdd, inst.Params.Z, 3);
            Assert.Equal(d.Center.Y, inst.Gate.X, 3);                      // groundY
            Assert.Equal(d.YTolerance, inst.Gate.Y, 3);
            Assert.Equal(d.MaxStep, inst.Gate.Z, 3);
        }

        [Fact]
        public void PackInstance_carries_modern_fields()
        {
            var d = new GroundDecal
            {
                Shape = DecalShape.Circle,
                Center = new Vector3(1f, 0f, 2f),
                Size = new Vector4(3f, 0f, 0f, 0f),
                FeatherWidth = 0.25f,
                Pattern = DecalFillPattern.RadialNoise,
                PatternSpeed = 0.7f,
                PatternScale = 2.5f,
                RimGlow = 0.8f,
                SweepGlow = 0.6f,
                Sparkle = 0.4f,
            };
            var i = GroundDecalRenderer.PackInstance(in d, Vector4.Zero);
            Assert.Equal(0.25f, i.Gate.W);
            Assert.Equal(2f, i.PatternP.X);
            Assert.Equal(0.7f, i.PatternP.Y);
            Assert.Equal(2.5f, i.PatternP.Z);
            Assert.Equal(new Vector4(0.8f, 0.6f, 0.4f, 0f), i.Energy);
        }

        [Fact]
        public void PackInstance_legacy_decal_packs_all_zero_modern_lanes()
        {
            var d = new GroundDecal { Shape = DecalShape.Ring, Size = new Vector4(1f, 2f, 0f, 0f) };
            var i = GroundDecalRenderer.PackInstance(in d, Vector4.Zero);
            Assert.Equal(0f, i.Gate.W);
            Assert.Equal(Vector4.Zero, i.PatternP);
            Assert.Equal(Vector4.Zero, i.Energy);
        }

        [Fact]
        public void BoundingRadius_is_the_max_radial_extent_per_shape()
        {
            Assert.Equal(1.4f, GroundDecalRenderer.BoundingRadius(Circle(0, 0, 1.4f)), 3);
            var ring = Circle(0, 0, 0); ring.Shape = DecalShape.Ring; ring.Size = new Vector4(0.7f, 1.3f, 0, 0);
            Assert.Equal(1.3f, GroundDecalRenderer.BoundingRadius(ring), 3);          // outer radius
            var beam = Circle(0, 0, 0); beam.Shape = DecalShape.Beam; beam.Size = new Vector4(2f, 0.5f, 0, 0);
            Assert.Equal(4.5f, GroundDecalRenderer.BoundingRadius(beam), 3);          // 2*halfLength + halfWidth
            var cone = Circle(0, 0, 0); cone.Shape = DecalShape.Cone; cone.Size = new Vector4(3f, 0.5f, 0, 0);
            Assert.Equal(3f, GroundDecalRenderer.BoundingRadius(cone), 3);            // range
            var arc = Circle(0, 0, 0); arc.Shape = DecalShape.Arc; arc.Size = new Vector4(2f, 0.3f, 0, 0);
            Assert.Equal(2.3f, GroundDecalRenderer.BoundingRadius(arc), 3);           // radius + half band
        }

        [Fact]
        public void ScreenRect_bounds_the_footprint_and_stays_within_the_screen()
        {
            // A simple orthographic top-down view (w always 1) so the projection is affine and easy to reason about.
            var view = Matrix4x4.CreateLookAt(new Vector3(0, 10, 0.001f), Vector3.Zero, Vector3.UnitY);
            var proj = Matrix4x4.CreateOrthographic(20f, 20f, 0.1f, 100f);
            var vp = view * proj;

            var small = Circle(0, 0, 1f);     // a small centred decal: a tight sub-rect, well inside the screen
            Assert.True(GroundDecalRenderer.TryComputeScreenRect(small, vp, 0.02f, out Vector4 rSmall));
            Assert.True(rSmall.X >= -1f && rSmall.Y >= -1f && rSmall.Z <= 1f && rSmall.W <= 1f, "rect must be clamped to NDC");
            Assert.True(rSmall.Z > rSmall.X && rSmall.W > rSmall.Y, "rect must be non-degenerate");

            var big = Circle(0, 0, 50f);      // a decal far larger than the view: the rect saturates the whole screen
            Assert.True(GroundDecalRenderer.TryComputeScreenRect(big, vp, 0.02f, out Vector4 rBig));
            Assert.True(rBig.Z - rBig.X > rSmall.Z - rSmall.X, "a larger decal must span a wider screen rect");

            // The small decal's rect must be strictly smaller than fullscreen (proves fill is actually bounded).
            Assert.True((rSmall.Z - rSmall.X) < 1.5f, "a small decal must not need a near-fullscreen quad");
        }

        [Fact]
        public void ScreenRect_falls_back_to_fullscreen_when_a_corner_straddles_the_camera()
        {
            // Perspective camera very close to a large decal so the footprint AABB straddles the eye plane: at least
            // one corner projects to w <= 0, and the helper must bail to the fullscreen fallback (never clip a decal).
            var view = Matrix4x4.CreateLookAt(new Vector3(0, 0.5f, 0f), new Vector3(0, 0.5f, -1f), Vector3.UnitY);
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(1.2f, 1.3f, 0.1f, 100f);
            var vp = view * proj;
            var enclosing = Circle(0, 0, 8f);   // centre under the camera, radius reaches behind it
            Assert.False(GroundDecalRenderer.TryComputeScreenRect(enclosing, vp, 0.02f, out _),
                "a footprint straddling the camera must report the fullscreen fallback");
        }

        static List<DecalRun> Coalesce(params DecalBlend[] blends)
        {
            var decals = new GroundDecal[blends.Length];
            for (int i = 0; i < blends.Length; i++) decals[i] = Circle(i, 0, 1f, blends[i]);
            var runs = new List<DecalRun>();
            GroundDecalRenderer.CoalesceDecalRuns(decals, runs);
            return runs;
        }

        [Fact]
        public void Coalesce_merges_consecutive_same_blend_into_one_run()
        {
            var runs = Coalesce(DecalBlend.Alpha, DecalBlend.Alpha, DecalBlend.Alpha);
            Assert.Single(runs);
            Assert.Equal(DecalBlend.Alpha, runs[0].Blend);
            Assert.Equal(0, runs[0].Start);
            Assert.Equal(3, runs[0].Count);
        }

        [Fact]
        public void Coalesce_splits_at_blend_boundaries_preserving_submission_order()
        {
            // Alpha, Alpha, Additive, Alpha -> three runs in submission order (never globally grouped by blend), so an
            // additive decal queued between two alpha decals stays between them and overlapping decals composite right.
            var runs = Coalesce(DecalBlend.Alpha, DecalBlend.Alpha, DecalBlend.Additive, DecalBlend.Alpha);
            Assert.Equal(3, runs.Count);
            Assert.Equal((DecalBlend.Alpha, 0, 2), (runs[0].Blend, runs[0].Start, runs[0].Count));
            Assert.Equal((DecalBlend.Additive, 2, 1), (runs[1].Blend, runs[1].Start, runs[1].Count));
            Assert.Equal((DecalBlend.Alpha, 3, 1), (runs[2].Blend, runs[2].Start, runs[2].Count));
        }

        [Fact]
        public void Coalesce_of_empty_is_empty()
        {
            var runs = new List<DecalRun>();
            GroundDecalRenderer.CoalesceDecalRuns(System.ReadOnlySpan<GroundDecal>.Empty, runs);
            Assert.Empty(runs);
        }
    [Fact]
    public void PackInstance_carries_interior_dim_and_runner_lanes()
    {
        var d = new GroundDecal
        {
            Shape = DecalShape.Circle,
            Size = new Vector4(3f, 0f, 0f, 0f),
            Pattern = DecalFillPattern.ScrollingNoise,
            PatternSpeed = 1f,
            PatternScale = 2f,
            InteriorDim = 0.6f,
            RimGlow = 0.8f,
            Runner = 0.9f,
        };
        var i = GroundDecalRenderer.PackInstance(in d, Vector4.Zero);
        Assert.Equal(0.6f, i.PatternP.W);
        Assert.Equal(0.9f, i.Energy.W);
    }

    [Fact]
    public void PackInstance_carries_base_fill_in_the_extra_lane()
    {
        var d = new GroundDecal { Shape = DecalShape.Circle, Size = new Vector4(3f, 0f, 0f, 0f), BaseFill = 0.3f };
        var i = GroundDecalRenderer.PackInstance(in d, Vector4.Zero);
        Assert.Equal(new Vector4(0.3f, 0f, 0f, 0f), i.Extra);
    }

    // ---- Void fallback (GroundDecal.VoidFallback / VoidDim) ----
    // The feature's whole safety argument is that it is opt-in and additive: a decal that does not ask for it packs
    // and draws exactly what it did before. These tests are that contract.

    static GroundDecal VoidCircle(float cx, float cz, DecalBlend blend = DecalBlend.Alpha, float voidDim = 0f) => new()
    {
        Shape = DecalShape.Circle, Center = new Vector3(cx, 1f, cz), Size = new Vector4(2f, 0, 0, 0),
        FillColor = new Color(1f, 0.2f, 0.1f, 0.6f), OutlineColor = new Color(1f, 0.9f, 0.2f, 0.9f),
        EdgeThickness = 0.08f, FillFraction = 1f, Blend = blend, YTolerance = 0.3f, MaxStep = 0.4f,
        VoidFallback = true, VoidDim = voidDim,
    };

    [Fact]
    public void PackInstance_keeps_the_void_lanes_zero_for_an_unflagged_decal()
    {
        // The zero-neutral lock. An unflagged decal packs the pre-feature bytes even if VoidDim was authored, so a
        // stray VoidDim on a style that never opted in cannot move a single byte of the geometry pass.
        var d = VoidCircle(0f, 0f, voidDim: 0.4f);
        d.VoidFallback = false;
        d.BaseFill = 0.3f;
        Assert.Equal(new Vector4(0.3f, 0f, 0f, 0f), GroundDecalRenderer.PackInstance(in d, Vector4.Zero).Extra);
    }

    [Fact]
    public void PackInstance_asks_the_geometry_path_for_the_fallback_when_flagged()
    {
        // A flagged decal's BASE instance is not inert: out-of-band geometry (a cliff face below the decal's plane)
        // must depth-test the plane rather than discard, or an overhanging ring loses everything that hangs in
        // FRONT of that cliff. Extra.w is what asks for it. Extra.y stays 0 because this is still the geometry pass.
        var d = VoidCircle(0f, 0f, voidDim: 0.15f);
        d.BaseFill = 0.3f;
        Assert.Equal(new Vector4(0.3f, 0f, 0.15f, 1f), GroundDecalRenderer.PackInstance(in d, Vector4.Zero).Extra);
    }

    [Fact]
    public void PackVoidInstance_raises_the_background_marker_and_carries_the_dim()
    {
        var d = VoidCircle(0f, 0f, voidDim: 0.15f);
        d.BaseFill = 0.3f;
        var i = GroundDecalRenderer.PackVoidInstance(in d, Vector4.Zero);
        Assert.Equal(new Vector4(0.3f, 1f, 0.15f, 0f), i.Extra);
    }

    [Fact]
    public void PackVoidInstance_differs_from_the_base_instance_only_in_the_extra_lane()
    {
        var d = VoidCircle(2f, -3f, DecalBlend.Additive, voidDim: 0.2f);
        var rect = new Vector4(-0.5f, -0.25f, 0.5f, 0.25f);
        var b = GroundDecalRenderer.PackInstance(in d, rect);
        var v = GroundDecalRenderer.PackVoidInstance(in d, rect);
        Assert.Equal(b.ScreenRect, v.ScreenRect);
        Assert.Equal(b.Center, v.Center);
        Assert.Equal(b.Size, v.Size);
        Assert.Equal(b.Fill, v.Fill);
        Assert.Equal(b.Outline, v.Outline);
        Assert.Equal(b.Params, v.Params);
        Assert.Equal(b.Gate, v.Gate);
        Assert.Equal(b.PatternP, v.PatternP);
        Assert.Equal(b.Energy, v.Energy);
        Assert.NotEqual(b.Extra, v.Extra);
    }

    [Theory]
    [InlineData(-0.5f, 0f)]
    [InlineData(1.6f, 1f)]
    public void PackVoidInstance_clamps_the_dim(float authored, float expected)
    {
        var d = VoidCircle(0f, 0f, voidDim: authored);
        Assert.Equal(expected, GroundDecalRenderer.PackVoidInstance(in d, Vector4.Zero).Extra.Z);
    }

    [Fact]
    public void ScreenRectFlat_is_the_footprint_rect_flattened_onto_the_decal_plane()
    {
        // A decal with a TALL Y gate: the band rect must be strictly taller on screen than the flat one, because the
        // iso camera projects the AABB's Y extent into screen Y. The flat rect is the tighter, correct void bound.
        var d = VoidCircle(0f, 0f);
        d.YTolerance = 4f;
        d.MaxStep = 4f;
        Matrix4x4 vp = IsoVp();

        Assert.True(GroundDecalRenderer.TryComputeScreenRect(in d, vp, 0.02f, out Vector4 band));
        Assert.True(GroundDecalRenderer.TryComputeScreenRectFlat(in d, vp, 0.02f, out Vector4 flat));
        Assert.True(flat.W - flat.Y < band.W - band.Y, "the flat rect must be tighter in screen Y than the Y-gate band rect");
        Assert.Equal(band.X, flat.X, 3);   // X extent is unaffected: only the Y span of the AABB changed
        Assert.Equal(band.Z, flat.Z, 3);
    }

    [Fact]
    public void ScreenRectFlat_ignores_the_y_gate_entirely()
    {
        // Two decals identical but for their Y gate must yield the same FLAT rect: the plane projection never leaves
        // y = Center.Y, so the gate cannot legitimately influence its bound.
        var a = VoidCircle(0f, 0f);
        a.YTolerance = 0.3f; a.MaxStep = 0.4f;
        var b = a;
        b.YTolerance = 9f; b.MaxStep = 12f;
        Matrix4x4 vp = IsoVp();

        Assert.True(GroundDecalRenderer.TryComputeScreenRectFlat(in a, vp, 0.02f, out Vector4 ra));
        Assert.True(GroundDecalRenderer.TryComputeScreenRectFlat(in b, vp, 0.02f, out Vector4 rb));
        Assert.Equal(ra, rb);
    }

    static Matrix4x4 IsoVp()
    {
        var cam = new KhaozEngine.Render3D.IsoCamera3D();
        return cam.ViewProjection;
    }

    static List<DecalRun> VoidRuns(params GroundDecal[] decals)
    {
        var runs = new List<DecalRun>();
        GroundDecalRenderer.CoalesceVoidRuns(decals, decals.Length, runs);
        return runs;
    }

    [Fact]
    public void CoalesceVoidRuns_is_empty_when_nothing_is_flagged()
    {
        // THE zero-neutral assertion: no flagged decals means no void runs, which means no extra draws and the Equal
        // pipelines are never bound.
        Assert.Empty(VoidRuns(Circle(0, 0, 1f), Circle(2, 0, 1f, DecalBlend.Additive), Circle(4, 0, 1f)));
        var runs = new List<DecalRun>();
        GroundDecalRenderer.CoalesceVoidRuns(System.ReadOnlySpan<GroundDecal>.Empty, 0, runs);
        Assert.Empty(runs);
    }

    [Fact]
    public void CoalesceVoidRuns_addresses_instances_appended_after_the_base_ones()
    {
        // Three decals, the outer two flagged. Base instances occupy 0..2, so the void instances start at 3 and the
        // run must point there, NOT at the flagged decals' own indices.
        var decals = new[] { VoidCircle(0, 0), Circle(2, 0, 1f), VoidCircle(4, 0) };
        var runs = VoidRuns(decals);
        Assert.Single(runs);
        Assert.Equal((DecalBlend.Alpha, 3, 2), (runs[0].Blend, runs[0].Start, runs[0].Count));
    }

    [Fact]
    public void CoalesceVoidRuns_splits_at_blend_boundaries_preserving_submission_order()
    {
        var decals = new[]
        {
            VoidCircle(0, 0), VoidCircle(2, 0),
            VoidCircle(4, 0, DecalBlend.Additive),
            VoidCircle(6, 0),
        };
        var runs = VoidRuns(decals);
        Assert.Equal(3, runs.Count);
        Assert.Equal((DecalBlend.Alpha, 4, 2), (runs[0].Blend, runs[0].Start, runs[0].Count));
        Assert.Equal((DecalBlend.Additive, 6, 1), (runs[1].Blend, runs[1].Start, runs[1].Count));
        Assert.Equal((DecalBlend.Alpha, 7, 1), (runs[2].Blend, runs[2].Start, runs[2].Count));
    }

    [Fact]
    public void CoalesceVoidRuns_skips_unflagged_decals_when_coalescing()
    {
        // An UNFLAGGED additive decal between two flagged alpha ones must not split the void run: it contributes no
        // void instance, so the two flagged instances are still adjacent in the buffer.
        var decals = new[] { VoidCircle(0, 0), Circle(2, 0, 1f, DecalBlend.Additive), VoidCircle(4, 0) };
        var runs = VoidRuns(decals);
        Assert.Single(runs);
        Assert.Equal(2, runs[0].Count);
    }

    [Fact]
    public void CountVoidDecals_counts_only_the_flagged()
    {
        Assert.Equal(0, GroundDecalRenderer.CountVoidDecals(new[] { Circle(0, 0, 1f), Circle(2, 0, 1f) }));
        Assert.Equal(2, GroundDecalRenderer.CountVoidDecals(new[] { VoidCircle(0, 0), Circle(2, 0, 1f), VoidCircle(4, 0) }));
    }

    [Fact]
    public void CoalesceDecalRuns_is_unaffected_by_the_void_flag()
    {
        // The base pass must not learn about the flag: run structure stays a pure function of the blend sequence.
        var runs = new List<DecalRun>();
        GroundDecalRenderer.CoalesceDecalRuns(new[] { VoidCircle(0, 0), Circle(2, 0, 1f), VoidCircle(4, 0) }, runs);
        Assert.Single(runs);
        Assert.Equal((DecalBlend.Alpha, 0, 3), (runs[0].Blend, runs[0].Start, runs[0].Count));
    }

    }
}
