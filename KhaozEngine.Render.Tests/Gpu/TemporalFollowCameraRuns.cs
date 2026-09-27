using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A keyed box the camera follows across the textured wall, as a third-person camera follows a walking avatar. The
    /// box is textured as the ridged box of <see cref="TemporalNarrowCrossingRuns"/>, so its pixels take ridges and
    /// locks. It stands still for <see cref="StillFrames"/> frames, then walks right while the camera keeps it at the
    /// same place on screen, so the box is still on screen and the wall pans left under it. Frame <see cref="Last"/> is
    /// measured as the crossings measure their trails: each wall pixel the box uncovered at its trailing edge, by its
    /// largest channel difference from the bare wall on the same camera path, in excess of a floor that starts that
    /// bare wall with no history on the frame the box uncovered the pixel. A pixel's age is the frames since the box
    /// last covered the wall there. HDR is off and the sharpen is at its default.
    /// </summary>
    public sealed class TemporalFollowCameraRuns
    {
        public const int W = 320, H = 180, StillFrames = 16, Last = StillFrames + 23;

        /// <summary>The box's left edge on screen and its size, in display pixels.</summary>
        public const float LeftPixels = 150f, SizePixels = 30f;

        const ulong Key = 61;

        readonly Dictionary<(TemporalUpscale, float), CrossingTrail> _runs = new();
        readonly Dictionary<(TemporalUpscale, float), (byte[] Wall, byte[][] Floors)> _walls = new();

        /// <summary>Wall time spent rendering and measuring so far, in seconds.</summary>
        internal double Seconds { get; private set; }

        /// <summary>The walk at <paramref name="pixelsPerFrame"/> display pixels a frame at
        /// <paramref name="preset"/>.</summary>
        internal CrossingTrail Run(TemporalUpscale preset, float pixelsPerFrame)
        {
            if (_runs.TryGetValue((preset, pixelsPerFrame), out CrossingTrail? cached)) return cached;
            long started = Stopwatch.GetTimestamp();
            var walk = new Walk(pixelsPerFrame);
            var (wall, floors) = Walls(walk, preset);
            byte[] frame;
            using (var fx = new TemporalFixture(W, H, s => walk.Setup(s, preset)))
            {
                fx.Frames(Last, walk.Draw);
                frame = fx.Frame(walk.Draw);
            }
            var footprints = new PixelRect[TemporalGhostingRuns.TrailFrames];
            footprints[0] = walk.Covered(Last);
            for (int k = 1; k < footprints.Length; k++) footprints[k] = walk.Covered(Last - k);
            CrossingTrail trail = TemporalNarrowCrossingRuns.Measure(
                $"follow camera at {pixelsPerFrame} px a frame, {preset}", frame, wall, floors, footprints,
                Stopwatch.GetElapsedTime(started).TotalSeconds);
            Seconds += trail.Seconds;
            return _runs[(preset, pixelsPerFrame)] = trail;
        }

        // The converged bare wall at the measured frame on the same camera path, and each age's floor: the bare wall
        // from the frame a pixel of that age was uncovered, with no history before it. Floors[k] is age k's.
        (byte[] Wall, byte[][] Floors) Walls(Walk walk, TemporalUpscale preset)
        {
            if (_walls.TryGetValue((preset, walk.Speed), out var cached)) return cached;
            Action<Scene3D> setup = s => walk.Setup(s, preset);
            byte[] wall;
            using (var fx = new TemporalFixture(W, H, setup))
            {
                fx.Frames(Last, walk.Background);
                wall = fx.Frame(walk.Background);
            }
            var floors = new byte[TemporalGhostingRuns.TrailFrames][];
            for (int age = TemporalGhostingRuns.FirstAge; age < floors.Length; age++)
            {
                using var fx = new TemporalFixture(W, H, setup);
                fx.SkipFrames(Last - age + 1);
                fx.Frames(age - 1, walk.Background);
                floors[age] = fx.Frame(walk.Background);
            }
            return _walls[(preset, walk.Speed)] = (wall, floors);
        }

        // The walk: the box and the camera move right together from frame StillFrames on. Every setup loads the
        // stage's meshes, then the textured box.
        sealed class Walk
        {
            readonly FrontStage _stage = new(W, H, 4.5f);
            MeshHandle _box;

            public Walk(float speed) => Speed = speed;

            public float Speed { get; }

            float Walked(int n) => Math.Max(0, n - StillFrames) * Speed * _stage.PixelWorld;

            Vector3 Size => new(SizePixels * _stage.PixelWorld, SizePixels * _stage.PixelWorld, 0.5f);

            Vector3 Centre(int n) =>
                new((LeftPixels + SizePixels / 2f - W / 2f) * _stage.PixelWorld + Walked(n), 0f, 0f);

            public void Setup(Scene3D s, TemporalUpscale preset)
            {
                _stage.Setup(s, AntiAliasing.Temporal, preset);
                s.Post.Hdr.Enabled = false;
                _box = s.LoadMesh(MeshPrimitives.Box(1f), s.LoadTexture(TemporalNarrowCrossingRuns.RidgedTexels(),
                    TemporalNarrowCrossingRuns.BoxTexture, TemporalNarrowCrossingRuns.BoxTexture));
            }

            public void Background(Scene3D s, int n)
            {
                s.Camera.Target = new Vector3(Walked(n), 0f, 0f);
                _stage.TexturedWall(s);
            }

            public void Draw(Scene3D s, int n)
            {
                Background(s, n);
                Matrix4x4 world = Matrix4x4.CreateScale(Size) * Matrix4x4.CreateTranslation(Centre(n));
                s.Draw(new RigidInstanceDraw(_box, world) { Tint = CrossingScene.Tint, Motion = MotionKey.From(Key) });
            }

            // The wall the box covered on frame n, on the measured frame's screen.
            public PixelRect Covered(int n)
            {
                Vector3 c = Centre(n), half = Size * 0.5f;
                return TemporalAcceptance.Footprint(_stage.Camera(Walked(Last)), c - half, c + half, W, H);
            }
        }
    }
}
