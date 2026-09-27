using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>The on-request counts: a known reveal is counted, a still frame counts almost nothing, a sharp change
    /// of colour is counted as clipped, and a scene that never asks builds no probe. The disoccluded count is the
    /// resolve's own decision, sample for sample, and a request is read back once, at the next PrepareFrame.</summary>
    public sealed class TemporalCountProbeGpuTests(ITestOutputHelper output)
    {
        const int W = 320, H = 180;

        sealed class Jump
        {
            public MeshHandle Box;
            public int JumpFrame = 24;
            // From this frame on the wall is green instead of grey.
            public int RecolourFrame = int.MaxValue;
            // 60 by 60 display pixels, jumping 80 pixels right: the old footprint, 3600 pixels, is revealed.
            public void Draw(Scene3D s, int n)
            {
                s.Draw(Box, Matrix4x4.CreateScale(12f, 7f, 0.1f) * Matrix4x4.CreateTranslation(0f, 0f, -2f),
                    n < RecolourFrame ? new Color(0.45f, 0.5f, 0.55f, 1f) : new Color(0.1f, 0.8f, 0.2f, 1f));
                float x = n < JumpFrame ? -1f : 1f;
                s.Draw(new RigidInstanceDraw(Box,
                    Matrix4x4.CreateScale(1.5f, 1.5f, 0.5f) * Matrix4x4.CreateTranslation(x, 0f, 0f))
                { Tint = new Color(1f, 0.1f, 0.1f, 1f), Motion = MotionKey.From(41) });
            }
        }

        static TemporalFixture Fixture(Jump jump, SceneDebugView view = SceneDebugView.None) => new(W, H, s =>
        {
            s.Post.UseSmoothPreset();
            s.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            s.Camera.Azimuth = 0f; s.Camera.Elevation = 0f; s.Camera.AspectRatio = (float)W / H;
            s.Camera.OrthoSize = 4.5f; s.Camera.Target = Vector3.Zero;
            s.DebugView = view;
            jump.Box = s.LoadMesh(MeshPrimitives.Box(1f));
        });

        [GpuFact]
        public void ARevealedBackgroundIsCountedAsDisoccluded()
        {
            var jump = new Jump();
            using TemporalFixture fx = Fixture(jump);
            for (int n = 0; n < jump.JumpFrame; n++) fx.Frame(jump.Draw);
            fx.Scene.RequestTemporalCounts();
            fx.Frame(jump.Draw);
            fx.Scene.CollectTemporalCountsForTests();
            TemporalDiagnostics d = fx.Scene.LastTemporalDiagnostics;
            output.WriteLine($"reveal: disoccluded {d.DisoccludedPixels}, reactive {d.ReactivePixels}, "
                + $"clipped {d.ClippedPixels}, frame {d.CountsFrameIndex}");
            Assert.Equal(d.FrameIndex, d.CountsFrameIndex);
            Assert.InRange(d.DisoccludedPixels, 0.6 * 3600, 1.5 * 3600);
        }

        [GpuFact]
        public void AStillFrameCountsAlmostNothing()
        {
            var jump = new Jump { JumpFrame = int.MaxValue };
            using TemporalFixture fx = Fixture(jump);
            for (int n = 0; n < 24; n++) fx.Frame(jump.Draw);
            fx.Scene.RequestTemporalCounts();
            fx.Frame(jump.Draw);
            fx.Scene.CollectTemporalCountsForTests();
            TemporalDiagnostics d = fx.Scene.LastTemporalDiagnostics;
            output.WriteLine($"still: disoccluded {d.DisoccludedPixels}, reactive {d.ReactivePixels}, "
                + $"clipped {d.ClippedPixels}");
            Assert.True(d.DisoccludedPixels <= W * H / 100,
                $"a still frame reported {d.DisoccludedPixels} disoccluded pixels");
            Assert.True(d.ClippedPixels <= W * H / 20, $"a still frame reported {d.ClippedPixels} clipped pixels");
        }

        /// <summary>
        /// The clip flag still marks a clip that matters. The same still scene, whose wall turns from grey to green on
        /// the requested frame, leaves the wall's history far outside its new neighbourhood, and the clip that pulls it
        /// in is flagged across the wall, which is all of the frame but the box's 3600 pixels.
        /// </summary>
        [GpuFact]
        public void ASharpChangeOfColourIsCountedAsClipped()
        {
            var jump = new Jump { JumpFrame = int.MaxValue, RecolourFrame = 24 };
            using TemporalFixture fx = Fixture(jump);
            for (int n = 0; n < jump.RecolourFrame; n++) fx.Frames(1, jump.Draw);
            fx.Scene.RequestTemporalCounts();
            fx.Frame(jump.Draw);
            fx.Scene.CollectTemporalCountsForTests();
            TemporalDiagnostics d = fx.Scene.LastTemporalDiagnostics;
            output.WriteLine($"recolour: disoccluded {d.DisoccludedPixels}, reactive {d.ReactivePixels}, "
                + $"clipped {d.ClippedPixels} of {W * H - 3600} wall pixels");
            Assert.True(d.ClippedPixels >= 0.75 * W * H,
                $"a sharp change of colour flagged only {d.ClippedPixels} clipped pixels");
            Assert.True(d.DisoccludedPixels <= W * H / 100,
                $"a still recoloured frame reported {d.DisoccludedPixels} disoccluded pixels");
        }

        [GpuFact]
        public void WithoutARequestNoProbeIsBuilt()
        {
            var jump = new Jump();
            using TemporalFixture fx = Fixture(jump);
            for (int n = 0; n < 30; n++) fx.Frame(jump.Draw);
            Assert.False(fx.Scene.TemporalCountProbeBuiltForTests);
            Assert.Equal(-1, fx.Scene.LastTemporalDiagnostics.CountsFrameIndex);
        }

        /// <summary>
        /// The counts are the resolve's decisions, not a second derivation. On the reveal frame the Disocclusion view
        /// re-evaluates the resolve's function at every display pixel over the same set, so at the probe's 9216 sample
        /// pixels its red marks must be exactly the samples the probe counted. The sample set is symmetric under a
        /// flip of either axis at this size, so the comparison does not depend on the readback's row order.
        /// </summary>
        [GpuFact]
        public void TheDisoccludedCountIsTheResolvesOwnDecisionAtEverySample()
        {
            var jump = new Jump();
            using TemporalFixture fx = Fixture(jump, SceneDebugView.Disocclusion);
            for (int n = 0; n < jump.JumpFrame; n++) fx.Frames(1, jump.Draw);
            fx.Scene.RequestTemporalCounts();
            byte[] view = fx.Frame(jump.Draw);
            fx.Scene.CollectTemporalCountsForTests();

            int red = 0;
            foreach ((int x, int y) in SamplePixels(W, H))
            {
                int i = (y * W + x) * 4;
                if (view[i] > 200 && view[i + 1] < 60 && view[i + 2] < 60) red++;
            }
            TemporalDiagnostics d = fx.Scene.LastTemporalDiagnostics;
            int expected = (int)Math.Round(red * (double)W * H / TemporalCountProbe.TotalSamples);
            output.WriteLine($"red samples in the Disocclusion view {red} of {TemporalCountProbe.TotalSamples}, "
                + $"so {expected} pixels, probe {d.DisoccludedPixels}");
            Assert.True(red > 0, "the reveal frame's Disocclusion view marks no sample");
            Assert.Equal(expected, d.DisoccludedPixels);
        }

        /// <summary>The frame a request arms records the grid, and the next PrepareFrame reads it back, once. Later
        /// frames read nothing and leave the counts alone.</summary>
        [GpuFact]
        public void ARequestIsReadBackOnceAtTheNextPrepareFrame()
        {
            var jump = new Jump();
            using TemporalFixture fx = Fixture(jump);
            fx.Frames(jump.JumpFrame, jump.Draw);
            fx.Scene.RequestTemporalCounts();
            fx.Frames(1, jump.Draw);
            long requested = fx.Scene.LastTemporalDiagnostics.FrameIndex;
            Assert.True(fx.Scene.TemporalCountProbeBuiltForTests);
            Assert.Equal(0, fx.Scene.TemporalCountReadbacksForTests);
            Assert.Equal(-1, fx.Scene.LastTemporalDiagnostics.CountsFrameIndex);

            fx.Frames(1, jump.Draw);   // its PrepareFrame harvests the requested frame
            TemporalDiagnostics harvested = fx.Scene.LastTemporalDiagnostics;
            Assert.Equal(1, fx.Scene.TemporalCountReadbacksForTests);
            Assert.Equal(requested + 1, harvested.FrameIndex);
            Assert.Equal(requested, harvested.CountsFrameIndex);
            Assert.InRange(harvested.DisoccludedPixels, 0.6 * 3600, 1.5 * 3600);

            fx.Frames(8, jump.Draw);
            TemporalDiagnostics later = fx.Scene.LastTemporalDiagnostics;
            Assert.Equal(1, fx.Scene.TemporalCountReadbacksForTests);
            Assert.Equal((harvested.CountsFrameIndex, harvested.DisoccludedPixels, harvested.ReactivePixels,
                harvested.ClippedPixels), (later.CountsFrameIndex, later.DisoccludedPixels, later.ReactivePixels,
                later.ClippedPixels));
        }

        /// <summary>The display pixels the probe program samples: 4 by 4 per cell of the 32 by 18 grid, each at
        /// <c>(cell + (s + 0.5) / 4) / grid</c> of the display, truncated and clamped as the shader does.</summary>
        static IEnumerable<(int X, int Y)> SamplePixels(int width, int height)
        {
            for (int cy = 0; cy < TemporalCountProbe.GridHeight; cy++)
                for (int cx = 0; cx < TemporalCountProbe.GridWidth; cx++)
                    for (int sy = 0; sy < 4; sy++)
                        for (int sx = 0; sx < 4; sx++)
                        {
                            float ax = (cx + (sx + 0.5f) / 4f) / TemporalCountProbe.GridWidth;
                            float ay = (cy + (sy + 0.5f) / 4f) / TemporalCountProbe.GridHeight;
                            yield return (Math.Clamp((int)(ax * width), 0, width - 1),
                                Math.Clamp((int)(ay * height), 0, height - 1));
                        }
        }
    }
}
