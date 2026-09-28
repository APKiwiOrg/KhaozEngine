using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>How a followed walk ends: the avatar and the camera stop on the same frame, the avatar stops while a
    /// damped camera eases on after it, or the avatar slows, turns and walks back, its travel passing through
    /// zero.</summary>
    public enum FollowEnding { Stop, DampedStop, Reversal }

    /// <summary>One run: the avatar's own pixels on each frame from the one before the turn frame to
    /// <see cref="TemporalFollowStopRuns.After"/> frames after it, their mean luma error against the 4x reference, the
    /// temporal error (flicker) and added change over the frame steps of that window, and the avatar's pixels on the
    /// turn frame.</summary>
    internal sealed record StopRun(string Name, double[] Errors, double Flicker, double Added, int Pixels,
        double Seconds);

    /// <summary>
    /// A followed avatar that stops, or turns back, after a walk (TEMPORAL-RESOLVE-UPSCALING-DESIGN amendment 23). The
    /// perspective scene is <see cref="TemporalPerspectiveFollowRuns"/>'s: the keyed ridged box walks over the textured
    /// ground while <see cref="FollowCamera3D"/> follows it. The orthographic scene is
    /// <see cref="TemporalFollowCameraRuns"/>'s: the box walks right across the textured wall with the camera. Both
    /// stand still for <see cref="StillFrames"/> frames and walk until the turn frame <see cref="Turn"/>, the first
    /// frame whose travel is zero. A stop holds the box still from there. A damped stop moves the camera's target as
    /// <see cref="FollowCamera3D.EnableTargetDamping"/> does at its default rate and 60 frames a second, so the camera
    /// lags the walk and eases on after the box stops. A reversal slows the box over <see cref="RampFrames"/> frames,
    /// to zero travel on the turn frame, and back to the walk's speed the other way. The measure reads the pixels whose
    /// centre shows the box on each frame, by casting the pixel centre's ray, against the same frame of the
    /// supersampled reference (<see cref="TemporalAcceptance.ReferenceSequence"/>). HDR is off and the sharpen is at
    /// its default.
    /// </summary>
    public sealed class TemporalFollowStopRuns
    {
        public const int W = 320, H = 180, StillFrames = 16, Turn = StillFrames + 24, After = 15, RampFrames = 4;

        /// <summary>The per-frame weight of the damped camera's target: <see cref="FollowCamera3D"/>'s exponential
        /// smoothing at its default <see cref="FollowCamera3D.TargetDampingRate"/> over a sixtieth of a
        /// second.</summary>
        public static readonly float DampingWeight = 1f - MathF.Exp(-new FollowCamera3D().TargetDampingRate / 60f);

        const ulong Key = 79;
        const float OrthoSize = 4.5f;

        readonly Dictionary<string, StopRun> _runs = new();
        readonly Dictionary<string, byte[][]> _references = new();

        /// <summary>Wall time spent rendering and measuring so far, in seconds.</summary>
        internal double Seconds { get; private set; }

        /// <summary>The perspective walk at <paramref name="pixelsPerFrame"/> display pixels a frame of ground motion
        /// at the screen centre, at <paramref name="pitch"/> radians, heading <paramref name="heading"/>, ending as
        /// <paramref name="ending"/> says.</summary>
        internal StopRun Perspective(FollowEnding ending, TemporalUpscale preset, float pixelsPerFrame, float pitch,
            FollowHeading heading) =>
            Run(new PerspectiveStop(ending, pixelsPerFrame, pitch, heading), preset);

        /// <summary>The orthographic walk at <paramref name="pixelsPerFrame"/> display pixels a frame, ending as
        /// <paramref name="ending"/> says.</summary>
        internal StopRun Orthographic(FollowEnding ending, TemporalUpscale preset, float pixelsPerFrame) =>
            Run(new OrthographicStop(ending, pixelsPerFrame), preset);

        StopRun Run(StopScene scene, TemporalUpscale preset)
        {
            string name = $"{scene.Name}, {preset}";
            if (_runs.TryGetValue(name, out StopRun? cached)) return cached;
            long started = Stopwatch.GetTimestamp();
            const int first = Turn - 1, count = After + 2;
            if (!_references.TryGetValue(scene.Name, out byte[][]? references))
                _references[scene.Name] = references = TemporalAcceptance.ReferenceSequence(W, H,
                    s => scene.Setup(s, TemporalUpscale.Native), scene.Draw, first, count);
            byte[][] frames = TemporalAcceptance.Sequence(W, H, s => scene.Setup(s, preset), scene.Draw, first, count);
            var errors = new double[count];
            double flicker = 0, added = 0;
            long steps = 0;
            int pixels = 0;
            bool[]? before = null;
            for (int k = 0; k < count; k++)
            {
                bool[] mask = scene.Mask(first + k);
                double sum = 0;
                int shown = 0;
                for (int i = 0; i < mask.Length; i++)
                {
                    if (!mask[i]) continue;
                    int x = i % W, y = i / W;
                    float f = TemporalAcceptance.Luma(frames[k], W, x, y);
                    float r = TemporalAcceptance.Luma(references[k], W, x, y);
                    sum += MathF.Abs(f - r);
                    shown++;
                    if (before is null || !before[i]) continue;
                    float df = f - TemporalAcceptance.Luma(frames[k - 1], W, x, y);
                    float dr = r - TemporalAcceptance.Luma(references[k - 1], W, x, y);
                    flicker += MathF.Abs(df - dr);
                    added += MathF.Max(MathF.Abs(df) - MathF.Abs(dr), 0f);
                    steps++;
                }
                errors[k] = shown == 0 ? 0 : sum / shown;
                if (k == 1) pixels = shown;
                before = mask;
            }
            double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            Seconds += seconds;
            return _runs[name] = new StopRun(name, errors, steps == 0 ? 0 : flicker / steps,
                steps == 0 ? 0 : added / steps, pixels, seconds);
        }

        /// <summary>A followed walk and its ending: how far the box has walked and the camera's target has moved by
        /// each frame, the draw, and the pixels whose centre shows the box.</summary>
        abstract class StopScene
        {
            readonly float[] _walked = new float[Turn + After + 2], _target = new float[Turn + After + 2];

            protected StopScene(FollowEnding ending, float step)
            {
                Ending = ending;
                for (int n = 1; n < _walked.Length; n++)
                {
                    float travel = n <= StillFrames ? 0f
                        : ending == FollowEnding.Reversal ? step * Math.Clamp((Turn - n) / (float)RampFrames, -1f, 1f)
                        : n < Turn ? step : 0f;
                    _walked[n] = _walked[n - 1] + travel;
                    _target[n] = ending == FollowEnding.DampedStop
                        ? _target[n - 1] + DampingWeight * (_walked[n] - _target[n - 1])
                        : _walked[n];
                }
            }

            public FollowEnding Ending { get; }

            public abstract string Name { get; }

            /// <summary>How far the box has walked by frame <paramref name="n"/>.</summary>
            protected float Walked(int n) => _walked[Math.Clamp(n, 0, _walked.Length - 1)];

            /// <summary>How far the camera's target has moved by frame <paramref name="n"/>.</summary>
            protected float Target(int n) => _target[Math.Clamp(n, 0, _target.Length - 1)];

            public abstract void Setup(Scene3D s, TemporalUpscale preset);

            public abstract void Draw(Scene3D s, int n);

            public abstract bool[] Mask(int n);

            protected static MeshHandle RidgedBox(Scene3D s) => s.LoadMesh(MeshPrimitives.Box(1f),
                s.LoadTexture(TemporalNarrowCrossingRuns.RidgedTexels(), TemporalNarrowCrossingRuns.BoxTexture,
                    TemporalNarrowCrossingRuns.BoxTexture));

            protected static RigidInstanceDraw Keyed(MeshHandle box, Matrix4x4 world) =>
                new(box, world) { Tint = CrossingScene.Tint, Motion = MotionKey.From(Key) };
        }

        // The perspective walk of TemporalPerspectiveFollowRuns, with the ending's path.
        sealed class PerspectiveStop : StopScene
        {
            static readonly Vector3 Size = TemporalPerspectiveFollowRuns.AvatarSize;
            readonly GroundStage _stage;
            readonly Vector3 _direction;
            MeshHandle _box;

            public PerspectiveStop(FollowEnding ending, float speed, float pitch, FollowHeading heading)
                : this(ending, new PerspectiveWalk(W, H, pitch, heading, speed, StillFrames, Size),
                    $"perspective {heading} at pitch {pitch}, {speed} px a frame, {ending}")
            {
            }

            PerspectiveStop(FollowEnding ending, PerspectiveWalk walk, string name) : base(ending, walk.Step)
            {
                _stage = walk.Stage;
                _direction = walk.Direction;
                Name = name;
            }

            public override string Name { get; }

            Vector3 Foot(int n) => _direction * Walked(n);

            FollowCamera3D Camera(int n) => _stage.Camera(_direction * Target(n));

            public override void Setup(Scene3D s, TemporalUpscale preset)
            {
                _stage.Setup(s, AntiAliasing.Temporal, preset, Camera(0));
                _box = RidgedBox(s);
            }

            public override void Draw(Scene3D s, int n)
            {
                ((FollowCamera3D)s.CameraOverride!).Target = _stage.Offset + _direction * Target(n);
                _stage.DrawGround(s);
                s.Draw(Keyed(_box, _stage.Standing(Foot(n), Size)));
            }

            public override bool[] Mask(int n)
            {
                FollowCamera3D camera = Camera(n);
                Vector3 eye = camera.Eye, foot = Foot(n);
                var mask = new bool[W * H];
                for (int i = 0; i < mask.Length; i++)
                    mask[i] = GroundRays.GroundPoint(camera, W, H, i % W, i / W) is Vector3 g
                        && GroundRays.Hides(eye, g, foot, Size, GroundStage.Yaw);
                return mask;
            }
        }

        // The orthographic walk of TemporalFollowCameraRuns, with the ending's path.
        sealed class OrthographicStop : StopScene
        {
            const float Left = TemporalFollowCameraRuns.LeftPixels, Side = TemporalFollowCameraRuns.SizePixels;
            readonly FrontStage _stage = new(W, H, OrthoSize);
            MeshHandle _box;

            public OrthographicStop(FollowEnding ending, float speed) : base(ending, speed * OrthoSize / H) =>
                Name = $"orthographic, {speed} px a frame, {ending}";

            public override string Name { get; }

            Vector3 Size => new(Side * _stage.PixelWorld, Side * _stage.PixelWorld, 0.5f);

            Vector3 Centre(int n) => new((Left + Side / 2f - W / 2f) * _stage.PixelWorld + Walked(n), 0f, 0f);

            public override void Setup(Scene3D s, TemporalUpscale preset)
            {
                _stage.Setup(s, AntiAliasing.Temporal, preset);
                s.Post.Hdr.Enabled = false;
                _box = RidgedBox(s);
            }

            public override void Draw(Scene3D s, int n)
            {
                s.Camera.Target = new Vector3(Target(n), 0f, 0f);
                _stage.TexturedWall(s);
                s.Draw(Keyed(_box, Matrix4x4.CreateScale(Size) * Matrix4x4.CreateTranslation(Centre(n))));
            }

            // The box's front face, a rectangle facing the camera, holds the pixels whose centre lies inside it.
            public override bool[] Mask(int n)
            {
                IsoCamera3D camera = _stage.Camera(Target(n));
                Vector3 c = Centre(n), half = new(Size.X * 0.5f, Size.Y * 0.5f, 0f);
                c.Z += Size.Z * 0.5f;
                camera.WorldToScreen(c - half, W, H, out Vector2 a);
                camera.WorldToScreen(c + half, W, H, out Vector2 b);
                float x0 = MathF.Min(a.X, b.X), x1 = MathF.Max(a.X, b.X);
                float y0 = MathF.Min(a.Y, b.Y), y1 = MathF.Max(a.Y, b.Y);
                var mask = new bool[W * H];
                for (int i = 0; i < mask.Length; i++)
                {
                    float x = i % W + 0.5f, y = i / W + 0.5f;
                    mask[i] = x > x0 && x < x1 && y > y0 && y < y1;
                }
                return mask;
            }
        }
    }
}
