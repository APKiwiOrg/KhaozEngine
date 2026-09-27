using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A still line three eighths of an internal texel wide stands ahead of the flat wall while a keyed box 30 display
    /// pixels square slides down past it, nearer than the line, one internal texel away, at 2 display pixels a frame
    /// (<see cref="TemporalPassingBoxRuns"/>). Every pixel beside the line then has the box in its 3x3 and reprojects
    /// by its own motion (<c>TemporalResolveTuning.DilationReachInternalPixels</c>), so its history and its lock are
    /// its own. The line's energy on each frame, over its own supersampled coverage in the rows the box passes, is read
    /// as a share of the same frame of the run without the box. HDR is off, the sharpen is at its default, and the
    /// measured values in the comments are Metal on Apple silicon.
    /// </summary>
    public sealed class TemporalPassingBoxGpuTests(TemporalPassingBoxRuns runs, ITestOutputHelper output)
        : IClassFixture<TemporalPassingBoxRuns>
    {
        // The least share of the run without the box the line keeps on any frame.
        const double MinShareOfStill = 0.85;

        [GpuTheory]
        [InlineData(TemporalUpscale.Native, 1)]
        [InlineData(TemporalUpscale.Native, -1)]
        [InlineData(TemporalUpscale.Quality, 1)]
        [InlineData(TemporalUpscale.Quality, -1)]
        public void A_still_sub_texel_line_holds_while_a_keyed_box_slides_past_it(TemporalUpscale preset, int side)
        {
            // Measured: the worst frame keeps 0.987 at Native on both sides, and 0.993 and 0.994 at Quality with the
            // box right and left. While step 6 released the lock at every moving edge, the line fell to 0.010 at Native
            // and to 0.248 and 0.000 at Quality, and was still at 0.43 to 0.69 sixteen frames after the box passed. The
            // moved flag the pixels store plays no part: storing it only where a pixel followed the box changed
            // nothing.
            PassingBoxRun r = runs.Run(preset, side);
            string shares = string.Join(" ", r.Shares.Select((s, t) => $"{TemporalPassingBoxRuns.Warm + t}:{s:0.00}"));
            string message = $"{preset}, box {(side > 0 ? "right" : "left")}: worst {r.Worst:0.000} on frame "
                + $"{r.WorstFrame}, the line's reference sum {r.ReferenceSum:0.000} over {r.Pixels} pixels. {shares}";
            output.WriteLine(message);
            Assert.True(r.Pixels > 0, $"the line covered nothing. {message}");
            Assert.True(r.Worst >= MinShareOfStill, $"the line blinks out beside the box. {message}");
        }
    }

    /// <summary>One run: the line's energy on each frame as a share of the same frame without the box, the worst and
    /// its frame, and the line's coverage in the measured rows.</summary>
    internal sealed record PassingBoxRun(double[] Shares, double Worst, int WorstFrame, double ReferenceSum,
        int Pixels);

    /// <summary>
    /// The passing-box runs, rendered on first use and kept for the test class. The box's centre passes the middle of
    /// the measured rows on frame <see cref="PassFrame"/>, so it is beside them from about frame 27 to 47, and the
    /// window runs <see cref="Total"/> frames after <see cref="Warm"/>. The run without the box and the reference,
    /// which neither side changes, are rendered once a preset.
    /// </summary>
    public sealed class TemporalPassingBoxRuns
    {
        public const int W = 320, H = 180, Warm = 16, Total = 64, PassFrame = 36;
        const int RowsTop = 84, RowsBottom = 96, ColumnsLeft = 150, ColumnsRight = 170;
        const float OrthoSize = 4.5f, LineTexels = 0.375f, LineOffsetPixels = 0.3f, LineZ = -1f, BoxPixels = 30f;
        const float PixelsPerFrame = 2f, GapTexels = 1f;
        const ulong Key = 41;

        readonly Dictionary<(TemporalUpscale, int), PassingBoxRun> _runs = new();
        readonly Dictionary<TemporalUpscale, StillLine> _still = new();

        // The line's supersampled coverage in the measured rows, its wanted energy, and its energy on each frame of the
        // run without the box.
        sealed record StillLine(List<(int X, int Y)> Pixels, double Wanted, double[] Energies);

        internal PassingBoxRun Run(TemporalUpscale preset, int side)
        {
            if (_runs.TryGetValue((preset, side), out PassingBoxRun? cached)) return cached;
            var scene = new Passing(preset);
            StillLine still = Still(scene);
            byte[][] with = scene.Frames(side);
            var shares = new double[with.Length];
            for (int t = 0; t < with.Length; t++) shares[t] = Energy(with[t], still.Pixels) / still.Energies[t];
            int worst = Array.IndexOf(shares, shares.Min());
            return _runs[(preset, side)] = new PassingBoxRun(shares, shares[worst], Warm + worst, still.Wanted,
                still.Pixels.Count);
        }

        StillLine Still(Passing scene)
        {
            if (_still.TryGetValue(scene.Preset, out StillLine? cached)) return cached;
            byte[] reference = scene.Reference();
            float background = TemporalAcceptance.Luma(reference, W, 2, 2);
            var line = new List<(int X, int Y)>();
            double wanted = 0;
            for (int y = RowsTop; y < RowsBottom; y++)
                for (int x = ColumnsLeft; x < ColumnsRight; x++)
                {
                    float above = TemporalAcceptance.Luma(reference, W, x, y) - background;
                    if (MathF.Abs(above) <= TemporalAcceptance.CoverageLumaStep) continue;
                    line.Add((x, y));
                    wanted += above;
                }
            double[] energies = scene.Frames(0).Select(f => Energy(f, line)).ToArray();
            return _still[scene.Preset] = new StillLine(line, wanted, energies);
        }

        static double Energy(byte[] frame, List<(int X, int Y)> line)
        {
            float bg = TemporalAcceptance.Luma(frame, W, 2, 2);
            return line.Sum(p => TemporalAcceptance.Luma(frame, W, p.X, p.Y) - bg);
        }

        // The scene at one preset: the still line ahead of the flat wall, and the box beside it on one side or none.
        sealed class Passing
        {
            readonly FrontStage _stage = new(W, H, OrthoSize);
            readonly float _lineWidth, _lineLeft, _box, _gap;

            public Passing(TemporalUpscale preset)
            {
                Preset = preset;
                float factor = TemporalSettings.DisplayOverInternal(preset), pw = _stage.PixelWorld;
                _lineWidth = LineTexels * factor * pw;
                _lineLeft = LineOffsetPixels * pw;
                _box = BoxPixels * pw;
                _gap = GapTexels * factor * pw;
            }

            public TemporalUpscale Preset { get; }

            // Side 1 puts the box right of the line, -1 left, and 0 draws none.
            void Draw(Scene3D s, int n, int side)
            {
                _stage.Wall(s);
                s.Draw(_stage.Box, Matrix4x4.CreateScale(_lineWidth, 3.6f, 0.05f)
                    * Matrix4x4.CreateTranslation(_lineLeft + _lineWidth / 2f, 0f, LineZ), new Color(1f, 1f, 1f, 1f));
                if (side == 0) return;
                float boxX = side > 0 ? _lineLeft + _lineWidth + _gap + _box / 2f : _lineLeft - _gap - _box / 2f;
                Matrix4x4 world = Matrix4x4.CreateScale(_box, _box, 0.5f)
                    * Matrix4x4.CreateTranslation(boxX, (PassFrame - n) * PixelsPerFrame * _stage.PixelWorld, 0f);
                s.Draw(new RigidInstanceDraw(_stage.Box, world)
                {
                    Tint = new Color(1f, 0.25f, 0.2f, 1f), Motion = MotionKey.From(Key),
                });
            }

            Action<Scene3D> Setup(AntiAliasing aa) => s =>
            {
                _stage.Setup(s, aa, Preset);
                s.Post.Hdr.Enabled = false;
            };

            public byte[][] Frames(int side) => TemporalAcceptance.Sequence(W, H, Setup(AntiAliasing.Temporal),
                (s, n) => Draw(s, n, side), Warm, Total - Warm);

            public byte[] Reference() => TemporalAcceptance.Supersampled(W, H,
                TemporalAcceptance.SequenceReferenceFactor, Setup(AntiAliasing.Off), (s, n) => Draw(s, n, 0));
        }
    }
}
