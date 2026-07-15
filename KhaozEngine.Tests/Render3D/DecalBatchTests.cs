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
    }
}
