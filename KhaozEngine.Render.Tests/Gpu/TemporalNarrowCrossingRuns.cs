using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>The crossings of <see cref="TemporalNarrowCrossingRuns"/>.</summary>
    internal enum NarrowCrossing
    {
        /// <summary>A keyed line one internal texel wide at 2 internal pixels a frame.</summary>
        LineOneTexel,

        /// <summary>A keyed line two internal texels wide at 2 internal pixels a frame.</summary>
        LineTwoTexels,

        /// <summary>A keyed box 30 display pixels square, textured with texels about a display pixel across, so its
        /// pixels take ridges and locks, at 2 display pixels a frame.</summary>
        RidgedBox,
    }

    /// <summary>
    /// Keyed objects crossing the textured wall, rendered on first use and kept for the test class. Only the wall draws
    /// for the first <see cref="StillFrames"/> frames, then the object moves right from display x
    /// <see cref="StartPixels"/>, and frame <see cref="Last"/> is measured as <see cref="TemporalGhostingRuns"/>
    /// measures the crossing: each trail pixel by its largest channel difference from the bare wall, in excess of a
    /// floor that starts the bare wall with no history on the frame the object uncovered it. A thin object jumps pixels
    /// it never covers, so each frame's rectangle reaches forward to the next frame's and a pixel's age is the frame
    /// the object passed it. HDR is off and the sharpen is at its default.
    /// </summary>
    public sealed class TemporalNarrowCrossingRuns
    {
        public const int W = 320, H = 180, StillFrames = 16, Last = StillFrames + 23;
        public const float StartPixels = 60f, HeightPixels = 30f;
        const int BoxTexture = 32;
        const uint BoxTextureSeed = 0x1234567u;
        const ulong Key = 51;

        readonly Dictionary<(NarrowCrossing, TemporalUpscale), CrossingTrail> _runs = new();
        readonly Dictionary<TemporalUpscale, (byte[] Wall, byte[][] Floors)> _walls = new();

        /// <summary>Wall time spent rendering and measuring so far, in seconds.</summary>
        internal double Seconds { get; private set; }

        internal CrossingTrail Run(NarrowCrossing crossing, TemporalUpscale preset)
        {
            if (_runs.TryGetValue((crossing, preset), out CrossingTrail? cached)) return cached;
            long started = Stopwatch.GetTimestamp();
            var scene = new Crossing(crossing, preset);
            var (wall, floors) = Walls(scene, preset);
            byte[] frame;
            using (var fx = new TemporalFixture(W, H, s => scene.Setup(s, preset)))
            {
                fx.Frames(Last, scene.Draw);
                frame = fx.Frame(scene.Draw);
            }
            var footprints = new PixelRect[TemporalGhostingRuns.TrailFrames];
            footprints[0] = scene.Rect(Last);
            for (int k = 1; k < footprints.Length; k++) footprints[k] = scene.Swept(Last - k);
            CrossingTrail trail = Measure($"{crossing}, {preset}", frame, wall, floors, footprints,
                Stopwatch.GetElapsedTime(started).TotalSeconds);
            Seconds += trail.Seconds;
            return _runs[(crossing, preset)] = trail;
        }

        // The converged wall at the measured frame, and each age's floor: the bare wall from the frame a pixel of that
        // age was uncovered, with no history before it. Floors[k] is age k's.
        (byte[] Wall, byte[][] Floors) Walls(Crossing scene, TemporalUpscale preset)
        {
            if (_walls.TryGetValue(preset, out var cached)) return cached;
            Action<Scene3D> setup = s => scene.Setup(s, preset);
            byte[] wall;
            using (var fx = new TemporalFixture(W, H, setup))
            {
                fx.Frames(Last, scene.Background);
                wall = fx.Frame(scene.Background);
            }
            var floors = new byte[TemporalGhostingRuns.TrailFrames][];
            for (int age = TemporalGhostingRuns.FirstAge; age < floors.Length; age++)
            {
                using var fx = new TemporalFixture(W, H, setup);
                fx.SkipFrames(Last - age + 1);
                fx.Frames(age - 1, scene.Background);
                floors[age] = fx.Frame(scene.Background);
            }
            return _walls[preset] = (wall, floors);
        }

        // Trail pixels by age, the smallest k from FirstAge whose rectangle holds the pixel, never within one pixel of
        // the object now or inside its rectangle one frame ago, as TemporalAcceptance.Trail reads them.
        static CrossingTrail Measure(string name, byte[] frame, byte[] wall, byte[][] floors,
            IReadOnlyList<PixelRect> footprints, double seconds)
        {
            const float Tolerance = TemporalGhostingRuns.Tolerance;
            var total = new TrailTally();
            var ages = new TrailTally[footprints.Count - TemporalGhostingRuns.FirstAge];
            for (int i = 0; i < ages.Length; i++) ages[i] = new TrailTally();
            var column = new TrailTally();
            int columnX = footprints[1].X0 - 1, reach = 0, oldest = 0;
            PixelRect now = footprints[0].Inflate(1);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    if (now.Contains(x, y) || footprints[1].Contains(x, y)) continue;
                    int age = 0;
                    for (int k = TemporalGhostingRuns.FirstAge; k < footprints.Count && age == 0; k++)
                        if (footprints[k].Contains(x, y)) age = k;
                    if (age == 0) continue;
                    float d = TemporalAcceptance.Difference(frame, wall, W, x, y, PixelDifference.MaxChannel);
                    float luma = TemporalAcceptance.Difference(frame, wall, W, x, y, PixelDifference.Luma);
                    float floor = TemporalAcceptance.Difference(floors[age], wall, W, x, y, PixelDifference.MaxChannel);
                    total.Add(d, luma, floor, Tolerance);
                    ages[age - TemporalGhostingRuns.FirstAge].Add(d, luma, floor, Tolerance);
                    if (x == columnX && y >= footprints[1].Y0 && y < footprints[1].Y1)
                        column.Add(d, luma, floor, Tolerance);
                    if (d - floor > Tolerance)
                    {
                        reach = Math.Max(reach, footprints[0].X0 - x);
                        oldest = Math.Max(oldest, age);
                    }
                }
            return new CrossingTrail(name, total, ages, column, reach, oldest, seconds);
        }

        // One crossing on FrontStage's textured wall. Every setup loads the stage's meshes, then the textured box.
        sealed class Crossing
        {
            readonly FrontStage _stage = new(W, H, 4.5f);
            readonly bool _textured;
            readonly float _width, _speed;
            MeshHandle _texturedBox;

            public Crossing(NarrowCrossing crossing, TemporalUpscale preset)
            {
                float factor = TemporalSettings.DisplayOverInternal(preset);
                (_width, _speed, _textured) = crossing switch
                {
                    NarrowCrossing.LineOneTexel => (factor, 2f * factor, false),
                    NarrowCrossing.LineTwoTexels => (2f * factor, 2f * factor, false),
                    _ => (HeightPixels, 2f, true),
                };
            }

            Vector3 Size => new(_width * _stage.PixelWorld, HeightPixels * _stage.PixelWorld, 0.5f);

            Vector3 Centre(int n) => new((StartPixels + Math.Max(0, n - StillFrames) * _speed + _width / 2f - W / 2f)
                * _stage.PixelWorld, 0f, 0f);

            public void Setup(Scene3D s, TemporalUpscale preset)
            {
                _stage.Setup(s, AntiAliasing.Temporal, preset);
                s.Post.Hdr.Enabled = false;
                _texturedBox = s.LoadMesh(MeshPrimitives.Box(1f), s.LoadTexture(BoxTexels(), BoxTexture, BoxTexture));
            }

            public void Background(Scene3D s, int n) => _stage.TexturedWall(s);

            public void Draw(Scene3D s, int n)
            {
                Background(s, n);
                if (n < StillFrames) return;
                Matrix4x4 world = Matrix4x4.CreateScale(Size) * Matrix4x4.CreateTranslation(Centre(n));
                s.Draw(new RigidInstanceDraw(_textured ? _texturedBox : _stage.Box, world)
                {
                    Tint = CrossingScene.Tint, Motion = MotionKey.From(Key),
                });
            }

            public PixelRect Rect(int n)
            {
                if (n < StillFrames) return default;
                Vector3 c = Centre(n);
                return TemporalAcceptance.Footprint(_stage.Camera(), c - Size * 0.5f, c + Size * 0.5f, W, H);
            }

            // Frame n's rectangle reaching forward to frame n + 1's, so the pixels a thin object jumps are counted.
            public PixelRect Swept(int n)
            {
                PixelRect r = Rect(n);
                return r.Area == 0 ? r : new PixelRect(r.X0, r.Y0, Math.Max(r.X1, Rect(n + 1).X0), r.Y1);
            }

            // Independent greys from 64 to 255, one a texel, so neighbouring texels differ and pixels take ridges.
            static byte[] BoxTexels()
            {
                var rgba = new byte[BoxTexture * BoxTexture * 4];
                for (uint y = 0; y < BoxTexture; y++)
                    for (uint x = 0; x < BoxTexture; x++)
                    {
                        uint h = x * 0x8DA6B343u ^ y * 0xD8163841u ^ BoxTextureSeed;
                        h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
                        int i = (int)(y * BoxTexture + x) * 4;
                        rgba[i] = rgba[i + 1] = rgba[i + 2] = (byte)(64 + h % 192);
                        rgba[i + 3] = 255;
                    }
                return rgba;
            }
        }
    }
}
