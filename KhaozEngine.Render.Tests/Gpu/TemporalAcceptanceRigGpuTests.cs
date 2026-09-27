using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The temporal acceptance rig against answers known in advance, so a metric that later fails measures the resolve
    /// and not the rig. A supersampled reference frames the display view and box-filters a quarter-pixel edge to the
    /// exact quarter mix, with the render cap raised for it. The flip counter counts every reversal of a patch toggled
    /// each frame and nothing on a still wall. Every scene renders one frame the same way twice. The rectangles the
    /// metrics read cover exactly what the scenes draw.
    /// </summary>
    public sealed class TemporalAcceptanceRigGpuTests(ITestOutputHelper output)
    {
        const int W = 160, H = 90, SceneW = 320, SceneH = 180;

        static readonly Color Bright = new(0.95f, 0.85f, 0.6f, 1f);

        // A 30 by 20 pixel box whose left edge is a quarter of the way into column 60 and whose top edge is a quarter
        // of the way into row 20, at 0.05 world units a pixel. Its shadow falls below the view.
        static Matrix4x4 QuarterEdgeBox(FrontStage stage)
        {
            float px = stage.PixelWorld, left = -W * 0.5f * px + 60.25f * px, top = H * 0.5f * px - 20.25f * px;
            return Matrix4x4.CreateScale(30f * px, 20f * px, 0.5f)
                * Matrix4x4.CreateTranslation(left + 15f * px, top - 10f * px, 0f);
        }

        static int Channel(byte[] rgba, int x, int y, int c) => rgba[(y * W + x) * 4 + c];

        [GpuTheory]
        [InlineData(4, false)]
        [InlineData(8, false)]
        [InlineData(8, true)]
        public void A_supersampled_reference_frames_the_display_view_and_box_filters_a_quarter_pixel_edge(int factor,
            bool lowCap)
        {
            var stage = new FrontStage(W, H, 4.5f);
            void Draw(Scene3D s, int _) { stage.Wall(s); s.Draw(stage.Box, QuarterEdgeBox(stage), Bright); }

            // The setup asks for temporal anti-aliasing, which a reference must override. lowCap also caps the render
            // at the display size, which only a reference that raises the cap renders past.
            byte[] reference = TemporalAcceptance.Supersampled(W, H, factor, s =>
            {
                stage.Setup(s, AntiAliasing.Temporal);
                if (lowCap) { s.Post.MaxRenderWidth = W; s.Post.MaxRenderHeight = H; }
            }, Draw);
            byte[] aliased = TemporalAcceptance.Snapshot(W, H, s => stage.Setup(s, AntiAliasing.Off), s => Draw(s, 0));

            // Row 30 crosses the left edge at column 60 and column 75 crosses the top edge at row 20. Each walk runs
            // from two pixels on the wall side, through the edge pixel, to two pixels into the box.
            foreach (var (name, ex, ey, dx, dy) in new[] { ("left", 60, 30, 1, 0), ("top", 75, 20, 0, 1) })
                for (int c = 0; c < 3; c++)
                {
                    int At(byte[] image, int step) => Channel(image, ex + step * dx, ey + step * dy, c);
                    int wall = At(reference, -2), before = At(reference, -1), edge = At(reference, 0);
                    int after = At(reference, 1), box = At(reference, 2);
                    int want = (wall + 3 * box + 2) / 4;
                    string ctx = $"{name} edge, channel {c}, factor {factor}, low cap {lowCap}: wall {wall}, "
                        + $"before {before}, edge {edge} against {want}, after {after}, box {box}, "
                        + $"aliased {At(aliased, -1)} {At(aliased, 0)}";
                    output.WriteLine(ctx);
                    Assert.True(Math.Abs(box - wall) >= 16, "the box and the wall must differ for the edge to measure "
                        + ctx);
                    Assert.True(Math.Abs(edge - want) <= 1, "the edge pixel is not the quarter mix " + ctx);
                    Assert.True(Math.Abs(before - wall) <= 1 && Math.Abs(after - box) <= 1,
                        "the reference edge is not one pixel sharp " + ctx);
                    Assert.True(Math.Abs(At(aliased, -2) - wall) <= 1 && Math.Abs(At(aliased, 2) - box) <= 1,
                        "the reference and the display frame disagree away from the edge " + ctx);
                    Assert.True(Math.Abs(At(aliased, 0) - box) <= 1 && Math.Abs(At(aliased, -1) - wall) <= 1,
                        "the aliased edge is not where the scene put it " + ctx);
                }
        }

        [GpuFact]
        public void The_flip_counter_counts_a_patch_toggled_every_frame_and_nothing_on_a_still_wall()
        {
            var stage = new FrontStage(W, H, 4.5f);
            var patch = new Vector3(1f, 1f, 0.5f);   // 20 by 20 pixels at the centre
            const int Frames = 10;
            var dark = new Color(0.1f, 0.1f, 0.1f, 1f);
            var light = new Color(0.9f, 0.9f, 0.9f, 1f);
            using var fx = new TemporalFixture(W, H, s => stage.Setup(s, AntiAliasing.Off));
            PixelRect inner = stage.Footprint(fx.Scene, Vector3.Zero, patch).Inflate(-1);
            var inside = new FlipCounter(W, H, inner);
            var still = new FlipCounter(W, H, new PixelRect(4, 4, 40, 40));
            for (int i = 0; i < Frames; i++)
            {
                byte[] frame = fx.Frame((s, n) =>
                {
                    stage.Wall(s);
                    s.Draw(stage.Box, Matrix4x4.CreateScale(patch), n % 2 == 0 ? dark : light);
                });
                inside.Add(frame);
                still.Add(frame);
            }
            output.WriteLine($"patch {inner}: {inside.Flips} flips, still wall: {still.Flips} flips");
            Assert.Equal(324, inner.Area);
            Assert.Equal((long)inner.Area * (Frames - 2), inside.Flips);
            Assert.Equal(0, still.Flips);
        }

        // Each scene at the size the acceptance tests use, under temporal anti-aliasing, with the frame to compare.
        static IEnumerable<(string Name, Action<Scene3D> Setup, Action<Scene3D, int> Draw, int Frame)> Scenes()
        {
            var fence = new FenceScene(SceneW, SceneH);
            yield return ("fence pan", s => fence.Stage.Setup(s, AntiAliasing.Temporal),
                (s, n) => fence.Draw(s, n * 0.2f), 8);
            var yard = new IsoYard(SceneW, SceneH);
            yield return ("isometric zoom", s => yard.Setup(s, AntiAliasing.Temporal), yard.Draw,
                IsoYard.HoldFrames + 4);
            var crossing = new CrossingScene(SceneW, SceneH, keyed: true, textured: true);
            yield return ("keyed crossing, textured", s => crossing.Stage.Setup(s, AntiAliasing.Temporal),
                crossing.Draw, CrossingScene.StillFrames + 4);
            var reveal = new RevealScene(SceneW, SceneH, textured: true);
            yield return ("reveal, textured", s => reveal.Stage.Setup(s, AntiAliasing.Temporal), reveal.Draw,
                RevealScene.RevealFrame);
            var prop = new FadeProp(SceneW, SceneH);
            yield return ("crossfade pan", s => prop.Stage.Setup(s, AntiAliasing.Temporal),
                (s, n) => prop.Draw(s, n * FadeProp.PanPixelsPerFrame, 0.5f, crossfade: true), 8);
        }

        static byte[] RunTo(Action<Scene3D> setup, Action<Scene3D, int> draw, int frame)
        {
            using var fx = new TemporalFixture(SceneW, SceneH, setup);
            fx.Frames(frame, draw);
            return fx.Frame(draw);
        }

        [GpuFact]
        public void Every_scene_renders_the_same_frame_the_same_way_twice()
        {
            foreach (var (name, setup, draw, frame) in Scenes())
            {
                byte[] a = RunTo(setup, draw, frame), b = RunTo(setup, draw, frame);
                int differing = 0;
                for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) differing++;
                output.WriteLine($"{name}, frame {frame}: {differing} bytes differ");
                Assert.True(differing == 0, $"{name}: two renders of frame {frame} differ in {differing} bytes");
            }
        }

        // With anti-aliasing off nothing carries between frames, so a frame shows exactly what that frame drew.
        static byte[] Still(FrontStage stage, Action<Scene3D, int> draw, int frame)
        {
            using var fx = new TemporalFixture(SceneW, SceneH, s => stage.Setup(s, AntiAliasing.Off));
            fx.SkipFrames(frame);
            return fx.Frame(draw);
        }

        // Pixels inside the rectangle that differ from the background, and pixels outside it that differ.
        static (int Inside, int Outside) Differing(byte[] frame, byte[] background, PixelRect rect)
        {
            int inside = 0, outside = 0;
            for (int y = 0; y < SceneH; y++)
                for (int x = 0; x < SceneW; x++)
                {
                    int i = (y * SceneW + x) * 4;
                    bool differs = frame[i] != background[i] || frame[i + 1] != background[i + 1]
                        || frame[i + 2] != background[i + 2];
                    if (!differs) continue;
                    if (rect.Contains(x, y)) inside++;
                    else outside++;
                }
            return (inside, outside);
        }

        [GpuTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void The_crossing_footprints_cover_exactly_what_the_crossing_draws(bool textured)
        {
            var crossing = new CrossingScene(SceneW, SceneH, keyed: true, textured);
            const int Frame = CrossingScene.StillFrames + 14;
            byte[] frame = Still(crossing.Stage, crossing.Draw, Frame);
            byte[] background = Still(crossing.Stage, crossing.Background, Frame);
            IReadOnlyList<PixelRect> footprints = crossing.Footprints(Frame);

            var (inside, outside) = Differing(frame, background, footprints[0]);
            PixelRect inner = footprints[0].Inflate(-1);
            var (innerDiffering, _) = Differing(frame, background, inner);
            var (over, check, worst) = TemporalAcceptance.Trail(frame, background, SceneW, SceneH, footprints, 0.05f,
                PixelDifference.MaxChannel);
            output.WriteLine($"textured {textured}: footprint {footprints[0]}, {inside} differing inside, "
                + $"{outside} outside, {innerDiffering} of {inner.Area} inner pixels differ, trail {over} of {check}, "
                + $"worst {worst:0.000}");

            Assert.Equal(0, outside);
            Assert.Equal(inner.Area, innerDiffering);
            Assert.True(check > 100, $"the trail region holds {check} pixels");
            Assert.Equal(0, over);
            Assert.Equal(0f, worst);
        }

        [GpuTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void The_revealed_rectangle_shows_only_the_background_and_the_occluder_lands_where_it_says(bool textured)
        {
            var reveal = new RevealScene(SceneW, SceneH, textured);
            byte[] covered = Still(reveal.Stage, reveal.Draw, RevealScene.RevealFrame - 1);
            byte[] revealed = Still(reveal.Stage, reveal.Draw, RevealScene.RevealFrame);
            byte[] background = Still(reveal.Stage, reveal.Background, RevealScene.RevealFrame);

            PixelRect landedInner = reveal.Landed.Inflate(-1);
            var (coveredInside, coveredOutside) = Differing(covered, background, reveal.Covered);
            var (landedInside, landedOutside) = Differing(revealed, background, reveal.Landed);
            var (revealedDiffering, _) = Differing(revealed, background, reveal.Revealed);
            var (innerCovered, _) = Differing(covered, background, reveal.Revealed);
            var (innerLanded, _) = Differing(revealed, background, landedInner);
            output.WriteLine($"textured {textured}: covered {reveal.Covered} {coveredInside} in {coveredOutside} out, "
                + $"landed {reveal.Landed} {landedInside} in {landedOutside} out, "
                + $"revealed {reveal.Revealed} {revealedDiffering}");

            Assert.Equal(0, coveredOutside);
            Assert.Equal(0, landedOutside);
            Assert.Equal(reveal.Revealed.Area, innerCovered);
            Assert.Equal(landedInner.Area, innerLanded);
            Assert.Equal(0, revealedDiffering);
        }

        [GpuFact]
        public void The_fade_prop_region_stays_on_the_prop_through_the_pan()
        {
            var prop = new FadeProp(SceneW, SceneH);
            const float Pan = 16f;
            PixelRect region = prop.Region(Pan);
            foreach (float pan in new[] { 0f, Pan })
            {
                byte[] solid = Still(prop.Stage, (s, _) => prop.Draw(s, pan, 0f, crossfade: false), 0);
                byte[] wall = Still(prop.Stage, (s, _) => prop.Background(s, pan), 0);
                var (inside, _) = Differing(solid, wall, region);
                output.WriteLine($"pan {pan}: {inside} of {region.Area} region pixels on the prop");
                Assert.True(region.Area >= 400, $"the region {region} is too small to measure");
                Assert.Equal(region.Area, inside);
            }
        }
    }
}
