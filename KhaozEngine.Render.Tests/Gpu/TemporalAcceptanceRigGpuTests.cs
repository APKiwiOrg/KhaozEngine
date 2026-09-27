using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// metrics read cover exactly what the scenes draw. A reference sequence equals an anti-aliasing off sequence at
    /// the larger size box-filtered by hand, byte for byte, and its lag control is the same sequence one frame
    /// earlier. On the fence pan, raw flips cannot tell MSAA 4x from no anti-aliasing while the temporal error against
    /// a reference sequence can.
    /// </summary>
    public sealed class TemporalAcceptanceRigGpuTests(ITestOutputHelper output)
    {
        const int W = 160, H = 90, SceneW = 320, SceneH = 180;

        // Lit, it stays under 1 in every channel with HDR off and differs from the wall by more than 16 steps in each.
        static readonly Color BoxColour = new(0.75f, 0.15f, 0.1f, 1f);

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
        [InlineData(4, false, true)]
        [InlineData(8, false, true)]
        [InlineData(8, true, true)]
        [InlineData(8, false, false)]
        public void A_supersampled_reference_frames_the_display_view_and_box_filters_a_quarter_pixel_edge(int factor,
            bool lowCap, bool hdr)
        {
            var stage = new FrontStage(W, H, 4.5f);
            void Draw(Scene3D s, int _) { stage.Wall(s); s.Draw(stage.Box, QuarterEdgeBox(stage), BoxColour); }

            // The setup asks for temporal anti-aliasing, which a reference must override. lowCap also caps the render
            // at the display size, which only a reference that raises the cap renders past. hdr false is the legacy
            // chain with no tonemap, which the convergence acceptance runs.
            byte[] reference = TemporalAcceptance.Supersampled(W, H, factor, s =>
            {
                stage.Setup(s, AntiAliasing.Temporal);
                s.Post.Hdr.Enabled = hdr;
                if (lowCap) { s.Post.MaxRenderWidth = W; s.Post.MaxRenderHeight = H; }
            }, Draw);
            byte[] aliased = TemporalAcceptance.Snapshot(W, H, s =>
            {
                stage.Setup(s, AntiAliasing.Off);
                s.Post.Hdr.Enabled = hdr;
            }, s => Draw(s, 0));

            // Row 30 crosses the left edge at column 60 and column 75 crosses the top edge at row 20. Each walk runs
            // from two pixels on the wall side, through the edge pixel, to two pixels into the box.
            foreach (var (name, ex, ey, dx, dy) in new[] { ("left", 60, 30, 1, 0), ("top", 75, 20, 0, 1) })
                for (int c = 0; c < 3; c++)
                {
                    int At(byte[] image, int step) => Channel(image, ex + step * dx, ey + step * dy, c);
                    int wall = At(reference, -2), before = At(reference, -1), edge = At(reference, 0);
                    int after = At(reference, 1), box = At(reference, 2);
                    int want = (wall + 3 * box + 2) / 4;
                    string ctx = $"{name} edge, channel {c}, factor {factor}, low cap {lowCap}, HDR {hdr}: "
                        + $"wall {wall}, before {before}, edge {edge} against {want}, after {after}, box {box}, "
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
            yield return ("fence over the wall, pan", s => fence.Stage.Setup(s, AntiAliasing.Temporal),
                (s, n) => fence.DrawOverWall(s, n * 0.2f), 8);
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

            // The box's edges lie on pixel boundaries, so its footprint is exactly the pixels it covers.
            var (inside, outside) = Differing(frame, background, footprints[0]);
            var (over, check, worst) = TemporalAcceptance.Trail(frame, background, SceneW, SceneH, footprints, 0.05f,
                PixelDifference.MaxChannel);
            output.WriteLine($"textured {textured}: footprint {footprints[0]}, {inside} differing inside, "
                + $"{outside} outside, trail {over} of {check}, worst {worst:0.000}");

            Assert.Equal(900, footprints[0].Area);
            Assert.Equal(footprints[0].Area, inside);
            Assert.Equal(0, outside);
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

            // The box's edges lie on pixel boundaries, so both rectangles are exactly the pixels it covers.
            var (coveredInside, coveredOutside) = Differing(covered, background, reveal.Covered);
            var (landedInside, landedOutside) = Differing(revealed, background, reveal.Landed);
            var (revealedDiffering, _) = Differing(revealed, background, reveal.Revealed);
            output.WriteLine($"textured {textured}: covered {reveal.Covered} {coveredInside} in {coveredOutside} out, "
                + $"landed {reveal.Landed} {landedInside} in {landedOutside} out, "
                + $"revealed {reveal.Revealed} {revealedDiffering}");

            Assert.Equal(3600, reveal.Covered.Area);
            Assert.Equal((reveal.Covered.Area, 0), (coveredInside, coveredOutside));
            Assert.Equal((reveal.Landed.Area, 0), (landedInside, landedOutside));
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

        // 0 the SSAA mode, 1 a raw supersample, 2 temporal anti-aliasing, 3 forced temporal rendering, 4 the motion
        // vectors view.
        [GpuTheory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void A_snapshot_refuses_the_supersampling_paths_and_a_jittered_frame(int kind)
        {
            var stage = new FrontStage(W, H, 4.5f);
            void Setup(Scene3D s)
            {
                stage.Setup(s, kind switch
                {
                    0 => AntiAliasing.Ssaa(4f),
                    2 => AntiAliasing.Temporal,
                    _ => AntiAliasing.Off,
                });
                if (kind == 1) s.Post.Supersample = 2f;
                if (kind == 3) s.ForceTemporalForTests = true;
                if (kind == 4) s.DebugView = SceneDebugView.MotionVectors;
            }
            var refused = Assert.Throws<ArgumentException>(() => TemporalAcceptance.Snapshot(W, H, Setup, stage.Wall));
            output.WriteLine(refused.Message);
        }

        [GpuFact]
        public void A_reference_sequence_equals_a_larger_sequence_box_filtered_and_its_lag_is_one_frame_earlier()
        {
            // The fence over the wall panning 0.2 pixels a frame, HDR off, frames 3 to 5. The larger sequence renders
            // its first frames where the reference sequence skips them.
            const int First = 3, Count = 3, Factor = TemporalAcceptance.SequenceReferenceFactor;
            var fence = new FenceScene(SceneW, SceneH);
            void Draw(Scene3D s, int n) => fence.DrawOverWall(s, n * 0.2f);
            void Setup(Scene3D s)
            {
                fence.Stage.Setup(s, AntiAliasing.Off);
                s.Post.Hdr.Enabled = false;
            }

            byte[][] reference = TemporalAcceptance.ReferenceSequence(SceneW, SceneH, Setup, Draw, First, Count);
            byte[][] large = TemporalAcceptance.Sequence(SceneW * Factor, SceneH * Factor, Setup, Draw, First, Count);
            byte[][] early = TemporalAcceptance.ReferenceSequence(SceneW, SceneH, Setup, Draw, First - 1, Count);
            byte[] before = TemporalAcceptance.Supersampled(SceneW, SceneH, Factor, Setup, Draw, First - 1);
            byte[][] lagged = TemporalAcceptance.LaggedReference(reference, before);

            for (int t = 0; t < Count; t++)
            {
                byte[] filtered = Rgba8Stats.BoxDownsample(large[t], SceneW * Factor, SceneH * Factor, Factor);
                Assert.True(filtered.AsSpan().SequenceEqual(reference[t]),
                    $"frame {First + t} differs from the reference");
                Assert.True(lagged[t].AsSpan().SequenceEqual(early[t]),
                    $"lagged frame {t} is not frame {First + t - 1}");
            }
            Assert.False(reference[0].AsSpan().SequenceEqual(reference[1]), "the pan must move the image");
        }

        [GpuFact(RequiresFourSampleMsaa = true)]
        public void On_the_fence_pan_raw_flips_cannot_tell_msaa_from_aliasing_and_the_temporal_error_can()
        {
            // The pan and window a stability acceptance uses: 0.2 display pixels a frame, 16 warm frames, 64 measured.
            // HDR is off, the legacy chain with no tonemap, so the reference's box filter after the tonemap and the
            // MSAA resolve before it average the same values.
            const int Warm = 16, Measured = 64;
            var fence = new FenceScene(SceneW, SceneH);
            void Draw(Scene3D s, int n) => fence.Draw(s, n * 0.2f);
            Action<Scene3D> Setup(AntiAliasing aa) => s =>
            {
                fence.Stage.Setup(s, aa);
                s.Post.Hdr.Enabled = false;
            };
            PixelRect region = fence.Region;

            long started = Stopwatch.GetTimestamp();
            byte[][] reference = TemporalAcceptance.ReferenceSequence(SceneW, SceneH, Setup(AntiAliasing.Off), Draw,
                Warm, Measured);
            double referenceMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            // The nearly ideal candidate: the same path at 8x per axis, 64 samples a pixel.
            byte[][] ideal = TemporalAcceptance.ReferenceSequence(SceneW, SceneH, Setup(AntiAliasing.Off), Draw,
                Warm, Measured, factor: 8);
            byte[][] off = TemporalAcceptance.Sequence(SceneW, SceneH, Setup(AntiAliasing.Off), Draw, Warm, Measured);
            byte[][] msaa = TemporalAcceptance.Sequence(SceneW, SceneH, Setup(AntiAliasing.Msaa(4)), Draw, Warm,
                Measured);

            FlickerStats o = TemporalAcceptance.Flicker(off, reference, SceneW, SceneH, region);
            FlickerStats m = TemporalAcceptance.Flicker(msaa, reference, SceneW, SceneH, region);
            FlickerStats i = TemporalAcceptance.Flicker(ideal, reference, SceneW, SceneH, region);
            output.WriteLine($"{Measured} 4x references at {SceneW} by {SceneH}: {referenceMs:0} ms");
            output.WriteLine($"off: {o}");
            output.WriteLine($"MSAA 4x: {m}");
            output.WriteLine($"8x reference: {i}");

            Assert.Equal(0.0, TemporalAcceptance.TemporalError(reference, reference, SceneW, SceneH, region));
            Assert.True(o.Flips > 0.005, $"the fence pan must flip without anti-aliasing. off {o.Flips:0.00000}");
            Assert.InRange(m.Flips / o.Flips, 0.75, 1.25);
            Assert.True(m.TemporalError < 0.8 * o.TemporalError,
                $"MSAA 4x must sit closer to the reference than no anti-aliasing: {m.TemporalError:0.00000} against "
                + $"{o.TemporalError:0.00000}");
            Assert.True(i.TemporalError < 0.5 * o.TemporalError,
                "the 8x sequence must sit far closer to the reference than no anti-aliasing: "
                + $"{i.TemporalError:0.00000} against {o.TemporalError:0.00000}");
            // A frozen image scores the reference's own change, so a sequence that tracks the motion must sit well
            // under it, or the error could not tell tracking from suppression.
            Assert.True(i.TemporalError < 0.5 * i.ReferenceChange,
                $"the 8x sequence must track the motion: {i.TemporalError:0.00000} against a frozen image's "
                + $"{i.ReferenceChange:0.00000}");
        }
    }
}
