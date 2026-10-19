using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Leading and trailing silhouettes of the M and MS shadow stage. Unlike its infinitely tall strips, finite
    /// strips expose real edges along their motion. The one to six display pixels inside each edge include pixels
    /// whose current 3x3 is object-only but whose reprojected history ring reaches the background. The full rim's
    /// error and temporal error are bounded against the frozen bilinear-input control on each device class. Raw
    /// frame-to-frame change is reported alongside the reference change.
    /// The PCF filter changed the scene input. These rows measure the unchanged 21.4.0 resolve on bilinear 3x3 PCF.
    /// The signal is judged against this frozen input-matched control.
    /// </summary>
    public sealed class TemporalMovingShadeRimGpuTests(TemporalMovingShadeRimScene scene, ITestOutputHelper output)
        : IClassFixture<TemporalMovingShadeRimScene>
    {
        [GpuFact]
        public void Moving_shadow_receivers_keep_their_leading_and_trailing_rims()
        {
            var failures = new List<string>();
            foreach (TemporalUpscale preset in new[] { TemporalUpscale.Quality, TemporalUpscale.Native })
                foreach (float shadow in new[] { 0f, 1f })
                    foreach (float motion in shadow == 0f ? new[] { 0.5f, 1f, 2f, 4f, 8f } : new[] { 1f, 2f })
                    {
                        string name = $"{preset} {(shadow == 0f ? "M" : "MS")} {motion:0.0}";
                        byte[][] frames = scene.Capture(preset, shadow, motion, reference: false);
                        byte[][] reference = scene.Capture(preset, shadow, motion, reference: true);
                        foreach ((string rim, TemporalReviewFigures figures) in Measure(frames, reference, motion))
                        {
                            output.WriteLine($"RIM {TemporalDeviceClass.Name}|{name} {rim}|{figures}");
                            output.WriteLine($"RIM-EXACT|{TemporalDeviceClass.Name}|{name} {rim}|{figures.Error:R}"
                                + $"|{figures.Change:R}|{figures.Temporal:R}|{figures.Reference:R}");
                            TemporalReviewBaseline.Check("rim", $"{name} {rim}", figures, failures);
                        }
                    }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }

        [GpuFact]
        public void A_reused_rim_scene_matches_a_fresh_scene_after_other_configurations()
        {
            scene.Capture(TemporalUpscale.Native, 0f, 8f, reference: false);
            scene.Capture(TemporalUpscale.Quality, 1f, 2f, reference: false);
            scene.Capture(TemporalUpscale.Native, 1f, 1f, reference: true);
            byte[][] reused = scene.Capture(TemporalUpscale.Quality, 0f, 1f, reference: false);
            using var fresh = new TemporalMovingShadeRimScene();
            byte[][] control = fresh.Capture(TemporalUpscale.Quality, 0f, 1f, reference: false);
            for (int i = 0; i < reused.Length; i++) Assert.Equal(control[i], reused[i]);
        }

        static IEnumerable<(string, TemporalReviewFigures)> Measure(byte[][] frames, byte[][] reference, float motion)
        {
            var regions = new[] { new ShadowEdgeRegion(), new ShadowEdgeRegion(), new ShadowEdgeRegion(),
                new ShadowEdgeRegion() };
            const int W = TemporalMovingShadeRimScene.W, H = TemporalMovingShadeRimScene.H;
            for (int t = 0; t < frames.Length; t++)
            {
                float centre = H * 0.5f - (TemporalMovingShadeRimScene.Warm + t
                    - TemporalMovingShadeRimScene.Anchor) * motion;
                float top = centre - TemporalMovingShadeRimScene.HalfHeight;
                float bottom = centre + TemporalMovingShadeRimScene.HalfHeight;
                for (int y = 2; y < H - 2; y++)
                    for (int x = 2; x < W - 2; x++)
                    {
                        float side = x + 0.5f - W * 0.5f;
                        if (MathF.Abs(side) <= MovingShadeStage.SeamMargin
                            || MathF.Abs(side) >= MovingShadeStage.StripPixels - MovingShadeStage.SideMargin) continue;
                        float leading = y + 0.5f - top, trailing = bottom - y - 0.5f;
                        int rim = leading >= 1f && leading <= 6f ? 0 : trailing >= 1f && trailing <= 6f ? 2 : -1;
                        if (rim < 0) continue;
                        ShadowEdgeRegion region = regions[rim + (side > 0f ? 1 : 0)];
                        float value = TemporalAcceptance.Luma(frames[t], W, x, y);
                        float r = TemporalAcceptance.Luma(reference[t], W, x, y);
                        region.Add(value - r, 0f, 0f, 0f);
                        if (t > 0)
                            region.AddChange(value - TemporalAcceptance.Luma(frames[t - 1], W, x, y),
                                r - TemporalAcceptance.Luma(reference[t - 1], W, x, y));
                    }
            }
            string[] names = { "leading textured", "leading flat", "trailing textured", "trailing flat" };
            for (int i = 0; i < regions.Length; i++)
            {
                ShadowEdgeRegion r = regions[i];
                Assert.True(r.Count >= 4000, $"{names[i]} rim has only {r.Count} samples");
                yield return (names[i], new(r.Mean, r.Change, r.ChangeError, r.ChangeRef));
            }
        }
    }

    /// <summary>Lazy shared temporal and supersampled scenes. Each capture starts at the same jitter phase, cuts
    /// history and warms its own path. The parity fact compares an aged scene against a fresh one.</summary>
    public sealed class TemporalMovingShadeRimScene : IDisposable
    {
        internal const int W = 320, H = 180, Warm = 80, Count = 8, Anchor = Warm + Count / 2;
        internal const float HalfHeight = 36f;
        const int Factor = TemporalAcceptance.SequenceReferenceFactor;
        TemporalFixture? _temporal, _reference;
        int _issued;

        internal byte[][] Capture(TemporalUpscale preset, float shadow, float motion, bool reference)
        {
            TemporalFixture fx;
            if (reference) fx = _reference ??= new TemporalFixture(W * Factor, H * Factor);
            else
            {
                fx = _temporal ??= new TemporalFixture(W, H);
                int pad = (144 - _issued % 144) % 144;
                fx.SkipFrames(pad);
                _issued += pad;
            }
            var stage = new MovingShadeStage(W, H, MovingShadeKind.Object, shadow, motion, Anchor, HalfHeight);
            stage.Setup(fx.Scene, reference ? AntiAliasing.Off : AntiAliasing.Temporal, preset,
                holdTexels: reference ? 0f : 1f);
            fx.Scene.CameraCut();
            int n = reference ? Warm : 0;
            void Draw(Scene3D s, int _) => stage.Draw(s, n++);
            if (!reference) fx.Frames(Warm, Draw);
            var frames = new byte[Count][];
            for (int t = 0; t < Count; t++)
            {
                byte[] image = fx.Frame(Draw);
                frames[t] = reference ? Rgba8Stats.BoxDownsample(image, W * Factor, H * Factor, Factor) : image;
            }
            if (!reference) _issued += Warm + Count;
            return frames;
        }

        public void Dispose()
        {
            _temporal?.Dispose();
            _reference?.Dispose();
        }
    }
}
