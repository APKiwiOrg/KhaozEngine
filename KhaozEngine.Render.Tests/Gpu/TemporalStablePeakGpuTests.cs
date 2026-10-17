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
    /// Isolated bright peaks one internal pixel across on a still wall, under a still camera and a still light: a
    /// bright texture dot, a grass glint or a lit pebble. Each peak ridges every frame and its converged history lies
    /// well past its own variance box, since one bright texel of nine moves the box's mean little, so a rule that
    /// releases a pixel's lock and accumulated weight on its own history past the box releases every peak every frame
    /// and shows the raw jittered reconstruction. Nothing changed there, so the resolve must keep what it held
    /// (TEMPORAL-RESOLVE-UPSCALING-DESIGN amendment 33).
    /// <para>
    /// Peaks stand on a grid every <see cref="Spacing"/> display pixels at sub-pixel offsets that vary from peak to
    /// peak, over the flat grey wall and over the textured one, at Quality and Native, under a dim ambient. Each run
    /// renders <see cref="FirstMeasured"/> frames and measures <see cref="Measured"/> more over the 3x3 display pixels
    /// around each peak: the mean frame-to-frame luma change, and the mean absolute luma error against the
    /// supersampled reference of the same frames. Both stay within <see cref="Tolerance"/> of the resolve's figures
    /// before the shading release (<see cref="Baseline"/>). A release on the history past the box alone raised the
    /// change by 100 and 39 percent at Quality, flat and textured, and by 52 percent at Native flat.
    /// </para>
    /// </summary>
    public sealed class TemporalStablePeakGpuTests(ITestOutputHelper output)
    {
        const int W = 320, H = 180, FirstMeasured = 80, Measured = 8, Spacing = 16, Margin = 8;
        const float OrthoSize = 1.5f;
        const double Tolerance = 0.1;

        // A dim ambient, so the wall reads dark and a peak bright.
        static readonly Color PeakColour = new(1f, 1f, 1f, 1f), Ambient = new(0.3f, 0.3f, 0.3f, 1f);

        readonly record struct Case(string Name, TemporalUpscale Preset, bool Textured);

        // Metal's frame-to-frame change and error at the peaks before the shading release.
        static readonly Dictionary<Case, (double Change, double Error)> Baseline = new()
        {
            [new("Quality flat", TemporalUpscale.Quality, false)] = (0.00203, 0.01504),
            [new("Quality textured", TemporalUpscale.Quality, true)] = (0.00237, 0.01541),
            [new("Native flat", TemporalUpscale.Native, false)] = (0.00134, 0.01149),
            [new("Native textured", TemporalUpscale.Native, true)] = (0.00148, 0.01284),
        };

        [GpuFact]
        public void Isolated_stable_peaks_keep_their_history_under_a_still_camera_and_light()
        {
            var failures = new List<string>();
            foreach (Case c in Baseline.Keys)
            {
                var stage = new FrontStage(W, H, OrthoSize);
                float size = stage.PixelWorld * (c.Preset == TemporalUpscale.Quality ? 1.5f : 1f);
                List<Vector3> peaks = Peaks(stage);
                void Draw(Scene3D s, int n)
                {
                    if (c.Textured) stage.TexturedWall(s);
                    else stage.Wall(s);
                    foreach (Vector3 p in peaks)
                        s.Draw(stage.Box, Matrix4x4.CreateScale(size, size, 0.02f) * Matrix4x4.CreateTranslation(p),
                            PeakColour);
                }

                void Setup(Scene3D s, TemporalUpscale preset)
                {
                    stage.Setup(s, AntiAliasing.Temporal, preset);
                    s.Post.AmbientColor = Ambient;
                }

                byte[][] reference = TemporalAcceptance.ReferenceSequence(W, H, s => Setup(s, TemporalUpscale.Native),
                    Draw, FirstMeasured, Measured);
                byte[][] frames = TemporalAcceptance.Sequence(W, H, s => Setup(s, c.Preset), Draw, FirstMeasured,
                    Measured);

                (double change, double referenceChange, double error, double contrast) =
                    Measure(stage, peaks, frames, reference);
                (double baseChange, double baseError) = Baseline[c];
                output.WriteLine($"{c.Name}: {peaks.Count} peaks, frame-to-frame change {change:0.00000} (reference "
                    + $"{referenceChange:0.00000}), error {error:0.00000}, reference peak contrast {contrast:0.000}");
                Assert.True(contrast > 0.1, $"{c.Name}: the reference shows no peaks, contrast {contrast:0.000}");
                if (change > baseChange * (1 + Tolerance))
                    failures.Add($"{c.Name}: change {change:0.00000} above {baseChange:0.00000} plus a tenth");
                if (error > baseError * (1 + Tolerance))
                    failures.Add($"{c.Name}: error {error:0.00000} above {baseError:0.00000} plus a tenth");
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }

        // Peak centres on the wall's front face, a grid at golden-ratio sub-pixel offsets.
        static List<Vector3> Peaks(FrontStage stage)
        {
            var peaks = new List<Vector3>();
            int columns = (W - 2 * Margin) / Spacing + 1, rows = (H - 2 * Margin) / Spacing + 1;
            for (int j = 0; j < rows; j++)
                for (int i = 0; i < columns; i++)
                {
                    int k = j * columns + i;
                    float fx = k * 0.618034f % 1f, fy = k * 0.754878f % 1f;
                    float px = Margin + i * Spacing + fx, py = Margin + j * Spacing + fy;
                    peaks.Add(new Vector3((px - W * 0.5f) * stage.PixelWorld, (H * 0.5f - py) * stage.PixelWorld,
                        -1.94f));
                }
            return peaks;
        }

        // The mean frame-to-frame luma change of the output and of the reference, and the mean absolute luma error,
        // over the 3x3 display pixels around every peak, and the reference's peak pixel less its 3x3's darkest.
        static (double Change, double ReferenceChange, double Error, double Contrast) Measure(FrontStage stage,
            List<Vector3> peaks, byte[][] frames, byte[][] reference)
        {
            IsoCamera3D camera = stage.Camera();
            double change = 0, referenceChange = 0, error = 0, contrast = 0;
            int changes = 0, errors = 0;
            foreach (Vector3 p in peaks)
            {
                camera.WorldToScreen(p, W, H, out Vector2 screen);
                int cx = (int)MathF.Floor(screen.X), cy = (int)MathF.Floor(screen.Y);
                float darkest = float.MaxValue;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int x = cx + dx, y = cy + dy;
                        darkest = MathF.Min(darkest, TemporalAcceptance.Luma(reference[0], W, x, y));
                        for (int t = 0; t < frames.Length; t++)
                        {
                            error += MathF.Abs(TemporalAcceptance.Luma(frames[t], W, x, y)
                                - TemporalAcceptance.Luma(reference[t], W, x, y));
                            errors++;
                            if (t == 0) continue;
                            change += MathF.Abs(TemporalAcceptance.Luma(frames[t], W, x, y)
                                - TemporalAcceptance.Luma(frames[t - 1], W, x, y));
                            referenceChange += MathF.Abs(TemporalAcceptance.Luma(reference[t], W, x, y)
                                - TemporalAcceptance.Luma(reference[t - 1], W, x, y));
                            changes++;
                        }
                    }
                contrast += TemporalAcceptance.Luma(reference[0], W, cx, cy) - darkest;
            }
            return (change / changes, referenceChange / changes, error / errors, contrast / peaks.Count);
        }
    }
}
