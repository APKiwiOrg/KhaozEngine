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
    /// TEMPORAL-RESOLVE-UPSCALING-DESIGN amendment 23 from the side where it can cost: surfaces moving more than
    /// <c>TemporalResolveTuning.DilationReachInternalPixels</c> apart, 2 internal pixels a frame here. A keyed line one
    /// internal pixel wide and a keyed box, both tilted so their edges are anti-aliased, cross the flat wall, and a
    /// still wide box stands in front of the textured wall and against the sky while a perspective camera steps
    /// sideways. Each run is read against the same path's per-frame 4x supersampled reference sequence over the band
    /// its object crosses (<see cref="TemporalAcceptance.Flicker"/>), by the mean luma error over the pixels within 2
    /// of the object's rectangle, and by the trail two frames after the object passed
    /// (<see cref="TemporalAcceptance.Trail"/>).
    /// <para>
    /// The bounds guard what the resolve gives now, each beside its measured value. The keyed line's fast flips and
    /// trail and the parallax runs' trail and sky flicker are where the resolve before amendment 23 did worse, or where
    /// reprojecting by the centre texel's own motion beside static surfaces did. HDR is off and the sharpen is at its
    /// default, as in the other temporal acceptance tests, and the table test prints every run.
    /// </para>
    /// </summary>
    public sealed class TemporalFastEdgeGpuTests(TemporalFastEdgeRuns runs, ITestOutputHelper output)
        : IClassFixture<TemporalFastEdgeRuns>
    {
        // At most this share of the reference's raw flips may be fast flips of the tested sequence.
        const double MaxFastFlipShare = 0.5;

        string Report(FastEdgeScene scene, TemporalUpscale preset)
        {
            FastEdgeRun r = runs.Run(scene, preset);
            string line = $"{scene}, {preset}: {r.Flicker}, edge error {r.EdgeError:0.00000}, trail {r.TrailOver} of "
                + $"{r.TrailChecked} (worst {r.TrailWorst:0.000})";
            output.WriteLine(line);
            return line;
        }

        [GpuTheory]
        [InlineData(TemporalUpscale.Native)]
        [InlineData(TemporalUpscale.Quality)]
        public void A_keyed_line_crossing_fast_leaves_no_trail_and_does_not_shimmer(TemporalUpscale preset)
        {
            // Measured: fast flips 0.0015 and 0.0047 against reference flips of 0.0175 and 0.0194, trail 1 and 0,
            // energy 0.69 and 0.43. Before amendment 23 the line smeared: 8 trail pixels at Native, fast flips 0.0124
            // and 0.0213, energy 2.09 and 1.28. It is dimmer now, and the energy floor keeps it from fading further.
            string message = Report(FastEdgeScene.KeyedLine, preset);
            FastEdgeRun r = runs.Run(FastEdgeScene.KeyedLine, preset);
            Assert.True(r.TrailOver <= 1, $"the line leaves a trail. {message}");
            Assert.True(r.Flicker.FastFlips <= MaxFastFlipShare * r.Flicker.ReferenceFlips,
                $"the line shimmers. {message}");
            Assert.True(r.Flicker.Energy >= 0.3, $"the line fades. {message}");
        }

        [GpuTheory]
        [InlineData(TemporalUpscale.Native)]
        [InlineData(TemporalUpscale.Quality)]
        public void A_keyed_box_crossing_fast_keeps_its_edges_close_to_the_reference(TemporalUpscale preset)
        {
            // Measured: edge error 0.00163 and 0.00204, fast flips 0.00053 and 0.00164 against reference flips of
            // 0.00176 and 0.00546, no trail. Before amendment 23 the edge error was 0.00083 and 0.00140: an edge pixel
            // whose centre texel misses the box now reads its own history, not the box's edge carried along.
            string message = Report(FastEdgeScene.KeyedBoxEdge, preset);
            FastEdgeRun r = runs.Run(FastEdgeScene.KeyedBoxEdge, preset);
            Assert.True(r.EdgeError <= 0.003, $"the box's edges stray from the reference. {message}");
            Assert.True(r.Flicker.FastFlips <= MaxFastFlipShare * r.Flicker.ReferenceFlips,
                $"the edges shimmer. {message}");
            Assert.True(r.TrailOver == 0, $"the box leaves a trail. {message}");
        }

        [GpuTheory]
        [InlineData(TemporalUpscale.Native, 0.045)]
        [InlineData(TemporalUpscale.Quality, 0.064)]
        public void A_still_box_over_the_textured_wall_under_a_sideways_camera_keeps_its_edges(TemporalUpscale preset,
            double maxEdgeError)
        {
            // Measured: edge error 0.0373 and 0.0530, trail 6 and 10. Reprojecting by the centre texel's own motion
            // wherever the two static surfaces moved apart left 18 and 19 trail pixels, and the resolve before
            // amendment 23 left 7 and 22.
            string message = Report(FastEdgeScene.ParallaxOverWall, preset);
            FastEdgeRun r = runs.Run(FastEdgeScene.ParallaxOverWall, preset);
            Assert.True(r.TrailOver <= 12, $"the box leaves a trail. {message}");
            Assert.True(r.EdgeError <= maxEdgeError, $"the box's edges stray from the reference. {message}");
        }

        [GpuTheory]
        [InlineData(TemporalUpscale.Native, 0.02)]
        [InlineData(TemporalUpscale.Quality, 0.032)]
        public void A_still_box_against_the_sky_under_a_sideways_camera_keeps_steady_edges(TemporalUpscale preset,
            double maxEdgeError)
        {
            // Measured: fast flips 0 and 0.00004, edge error 0.0165 and 0.0270, as before amendment 23. Reprojecting
            // the sky beside the box by its own motion gave fast flips of 0.0035 and 0.0063 and edge errors of 0.0278
            // and 0.0362.
            string message = Report(FastEdgeScene.ParallaxOverSky, preset);
            FastEdgeRun r = runs.Run(FastEdgeScene.ParallaxOverSky, preset);
            Assert.True(r.Flicker.FastFlips <= 0.001, $"the box's edges flicker against the sky. {message}");
            Assert.True(r.EdgeError <= maxEdgeError, $"the box's edges stray from the reference. {message}");
            Assert.True(r.TrailOver <= 3, $"the box leaves a trail. {message}");
        }

        [GpuFact]
        public void The_fast_edge_table_prints_every_run()
        {
            output.WriteLine("| Scene | Preset | Error | Frozen | Added | Removed | Sharp | Flips | Fast flips "
                + "| Reference flips | Edge error | Trail | Trail worst | Energy |");
            output.WriteLine("|" + string.Concat(System.Linq.Enumerable.Repeat(" --- |", 14)));
            foreach (FastEdgeScene scene in Enum.GetValues<FastEdgeScene>())
                foreach (TemporalUpscale preset in new[] { TemporalUpscale.Native, TemporalUpscale.Quality })
                {
                    FastEdgeRun r = runs.Run(scene, preset);
                    FlickerStats f = r.Flicker;
                    output.WriteLine($"| {scene} | {preset} | {f.TemporalError:0.00000} | {f.ReferenceChange:0.00000} "
                        + $"| {f.AddedChange:0.00000} | {f.RemovedChange:0.00000} | {f.Sharpness:0.000} "
                        + $"| {f.Flips:0.00000} | {f.FastFlips:0.00000} | {f.ReferenceFlips:0.00000} "
                        + $"| {r.EdgeError:0.00000} | {r.TrailOver} of {r.TrailChecked} | {r.TrailWorst:0.000} "
                        + $"| {f.Energy:0.000} |");
                    Assert.True(r.TrailChecked > 0, $"{scene}, {preset}: the trail region measured nothing");
                }
        }
    }

    /// <summary>The scenes of <see cref="TemporalFastEdgeRuns"/>.</summary>
    internal enum FastEdgeScene
    {
        /// <summary>A keyed line one internal pixel wide, tilted, crossing the flat wall.</summary>
        KeyedLine,

        /// <summary>A keyed box, tilted, crossing the flat wall.</summary>
        KeyedBoxEdge,

        /// <summary>A still wide box in front of the textured wall, the perspective camera stepping sideways.</summary>
        ParallaxOverWall,

        /// <summary>The same box against the sky.</summary>
        ParallaxOverSky,
    }

    /// <summary>One run: the flicker over the band the object crosses, the mean luma error from the reference over
    /// the pixels within 2 of the object's rectangle on each frame, and the trail on the last frame, pixels the object
    /// covered two or more frames before that differ from the path without it by more than 0.05 in any
    /// channel.</summary>
    internal sealed record FastEdgeRun(FlickerStats Flicker, double EdgeError, int TrailOver, int TrailChecked,
        float TrailWorst);

    /// <summary>
    /// The fast-edge runs, rendered on first use and kept for the test class. Every surface pair moves
    /// <see cref="InternalPixelsPerFrame"/> apart: the keyed objects cross the still wall at that speed, and the
    /// perspective camera steps sideways so the box moves that much against the wall behind it, or against the sky,
    /// which only rotation moves. The window is <see cref="Measured"/> frames after <see cref="Warm"/>.
    /// </summary>
    public sealed class TemporalFastEdgeRuns
    {
        public const int W = 320, H = 180, Warm = 8, Measured = 16;

        /// <summary>The relative motion, in internal pixels a frame, between the moving surface and what lies behind
        /// it, above <c>TemporalResolveTuning.DilationReachInternalPixels</c>.</summary>
        public const float InternalPixelsPerFrame = 2f;

        const float OrthoSize = 4.5f, CameraZ = 4f, BoxZ = 0f, WallZ = -1.95f, LineTilt = 0.3f, BoxTilt = 0.2f;
        const float LineHeight = 3.6f, LineDepth = 0.2f;
        const ulong Key = 41;
        static readonly Vector3 BoxSize = new(1f, 1.5f, 0.5f), ParallaxBox = new(2f, 1.5f, 0.5f);
        static readonly Color Tint = new(1f, 0.25f, 0.2f, 1f);

        readonly Dictionary<(FastEdgeScene, TemporalUpscale), FastEdgeRun> _runs = new();

        internal FastEdgeRun Run(FastEdgeScene scene, TemporalUpscale preset)
        {
            if (_runs.TryGetValue((scene, preset), out FastEdgeRun? cached)) return cached;
            var stage = new FrontStage(W, H, OrthoSize);
            float factor = TemporalSettings.DisplayOverInternal(preset);
            bool parallax = scene is FastEdgeScene.ParallaxOverWall or FastEdgeScene.ParallaxOverSky;
            float displayPerFrame = InternalPixelsPerFrame * factor;
            // Display pixels one world unit of sideways camera step moves a point this far ahead, for the 60 degree
            // vertical field of view.
            float PixelsPerUnit(float depth) => H / 2f / (depth * MathF.Tan(MathF.PI / 6f));
            float step = scene == FastEdgeScene.ParallaxOverWall
                ? displayPerFrame / (PixelsPerUnit(CameraZ - BoxZ) - PixelsPerUnit(CameraZ - WallZ))
                : displayPerFrame / PixelsPerUnit(CameraZ - BoxZ);
            int total = Warm + Measured;
            FlyCamera3D Camera(int n) => new()
            {
                Position = new Vector3((n - total / 2f) * step, 0f, CameraZ), Yaw = MathF.PI,
                FieldOfView = MathF.PI / 3f, AspectRatio = (float)W / H, NearPlane = 0.1f, FarPlane = 100f,
            };
            Vector3 ObjectAt(int n) => new(-2f + n * displayPerFrame * stage.PixelWorld, 0f, 0f);
            Vector3 size = scene switch
            {
                FastEdgeScene.KeyedLine => new Vector3(factor * stage.PixelWorld, LineHeight, LineDepth),
                FastEdgeScene.KeyedBoxEdge => BoxSize,
                _ => ParallaxBox,
            };
            float tilt = scene == FastEdgeScene.KeyedLine ? LineTilt
                : scene == FastEdgeScene.KeyedBoxEdge ? BoxTilt : 0f;

            void Background(Scene3D s, int n)
            {
                if (!parallax)
                {
                    stage.Wall(s);
                    return;
                }
                s.CameraOverride = Camera(n);
                if (scene == FastEdgeScene.ParallaxOverWall) stage.TexturedWall(s);
            }
            void Draw(Scene3D s, int n)
            {
                Background(s, n);
                if (parallax)
                {
                    s.Draw(stage.Box, Matrix4x4.CreateScale(size) * Matrix4x4.CreateTranslation(0f, 0f, BoxZ), Tint);
                    return;
                }
                Matrix4x4 world = Matrix4x4.CreateScale(size) * Matrix4x4.CreateRotationZ(tilt)
                    * Matrix4x4.CreateTranslation(ObjectAt(n));
                s.Draw(new RigidInstanceDraw(stage.Box, world) { Tint = Tint, Motion = MotionKey.From(Key) });
            }
            // The object's screen rectangle on frame n, the bounding box of the tilted ones.
            PixelRect Footprint(int n)
            {
                if (parallax)
                    return TemporalAcceptance.Footprint(Camera(n),
                        new Vector3(-size.X / 2f, -size.Y / 2f, BoxZ - size.Z / 2f),
                        new Vector3(size.X / 2f, size.Y / 2f, BoxZ + size.Z / 2f), W, H);
                Vector3 c = ObjectAt(n);
                float hx = MathF.Cos(tilt) * size.X / 2f + MathF.Sin(tilt) * size.Y / 2f;
                float hy = MathF.Sin(tilt) * size.X / 2f + MathF.Cos(tilt) * size.Y / 2f;
                return TemporalAcceptance.Footprint(stage.Camera(), c - new Vector3(hx, hy, size.Z / 2f),
                    c + new Vector3(hx, hy, size.Z / 2f), W, H);
            }

            Action<Scene3D> Setup(AntiAliasing aa) => s =>
            {
                stage.Setup(s, aa, preset);
                s.Post.Hdr.Enabled = false;
            };
            byte[][] frames = TemporalAcceptance.Sequence(W, H, Setup(AntiAliasing.Temporal), Draw, Warm, Measured);
            byte[][] references = TemporalAcceptance.ReferenceSequence(W, H, Setup(AntiAliasing.Off), Draw, Warm,
                Measured);
            byte[] background;
            using (var fx = new TemporalFixture(W, H, Setup(AntiAliasing.Temporal)))
            {
                fx.Frames(total - 1, Background);
                background = fx.Frame(Background);
            }

            PixelRect band = Footprint(Warm);
            for (int n = Warm + 1; n < total; n++)
            {
                PixelRect r = Footprint(n);
                band = new PixelRect(Math.Min(band.X0, r.X0), Math.Min(band.Y0, r.Y0), Math.Max(band.X1, r.X1),
                    Math.Max(band.Y1, r.Y1));
            }
            FlickerStats flicker = TemporalAcceptance.Flicker(frames, references, W, H, band.Inflate(3).Clip(W, H));

            double edgeSum = 0;
            long edgeCount = 0;
            for (int t = 0; t < Measured; t++)
            {
                PixelRect box = Footprint(Warm + t), outer = box.Inflate(2).Clip(W, H), inner = box.Inflate(-2);
                for (int y = outer.Y0; y < outer.Y1; y++)
                    for (int x = outer.X0; x < outer.X1; x++)
                    {
                        if (inner.Contains(x, y)) continue;
                        edgeSum += TemporalAcceptance.Difference(frames[t], references[t], W, x, y,
                            PixelDifference.Luma);
                        edgeCount++;
                    }
            }

            var footprints = new PixelRect[9];
            for (int k = 0; k < footprints.Length; k++) footprints[k] = Footprint(total - 1 - k);
            var (over, check, worst) = TemporalAcceptance.Trail(frames[^1], background, W, H, footprints, 0.05f,
                PixelDifference.MaxChannel);
            var run = new FastEdgeRun(flicker, edgeCount == 0 ? 0 : edgeSum / edgeCount, over, check, worst);
            return _runs[(scene, preset)] = run;
        }
    }
}
