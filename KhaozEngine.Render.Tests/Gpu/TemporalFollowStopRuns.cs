using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>How a followed walk ends: the avatar and the camera stop on the same frame, the avatar stops while a
    /// damped camera eases on after it, or the avatar slows, turns and walks back, its travel passing through
    /// zero.</summary>
    public enum FollowEnding { Stop, DampedStop, Reversal }

    /// <summary>What stands behind the followed avatar: the textured ground or wall, nothing (the clear colour beside
    /// and behind its whole outline), or the ground or wall with a second keyed box walking past behind the avatar as
    /// it stops, showing beside its outline.</summary>
    public enum StopSurround { Ground, ClearColour, Passer }

    /// <summary>A set of the avatar's pixels in one run: their mean luma error against the 4x reference on each frame
    /// from the one before the turn frame to <see cref="TemporalFollowStopRuns.After"/> frames after it, the temporal
    /// error (flicker) and added change over the frame steps of that window, and the pixels on the turn frame.
    /// <see cref="Cycle"/> holds the error on each of the <see cref="TemporalFollowStopRuns.Lead"/> frames before the
    /// turn frame, the last of them the first of <see cref="Errors"/>.</summary>
    internal sealed record StopMeasure(double[] Errors, double Flicker, double Added, int Pixels, double[] Cycle);

    /// <summary>One run: every pixel whose centre shows the avatar (<see cref="Whole"/>), those more than the
    /// reconstruction's reach inside its outline (<see cref="Inner"/>), whose own history no neighbouring surface's
    /// rule touches, and those within that reach outside it (<see cref="Ring"/>), whose centre shows the ground, the
    /// wall or the clear colour beside the avatar. <see cref="Phases"/> is the length of the preset's jitter
    /// sequence.</summary>
    internal sealed record StopRun(string Name, StopMeasure Whole, StopMeasure Inner, StopMeasure Ring,
        double Seconds, int Phases, FollowEnding Ending, StopSurround Surround);

    /// <summary>
    /// A followed avatar that stops, or turns back, after a walk (TEMPORAL-RESOLVE-UPSCALING-DESIGN amendment 23). The
    /// perspective scene is <see cref="TemporalPerspectiveFollowRuns"/>'s: the keyed ridged box walks over the textured
    /// ground while <see cref="FollowCamera3D"/> follows it. The orthographic scene is
    /// <see cref="TemporalFollowCameraRuns"/>'s: the box walks right across the textured wall with the camera. Both
    /// stand still for <see cref="StillFrames"/> frames and walk until the turn frame <see cref="Turn"/>, the first
    /// frame whose travel is zero. A stop holds the box still from there. A damped stop moves the camera's target as
    /// <see cref="FollowCamera3D.EnableTargetDamping"/> does at its default rate and 60 frames a second, so the camera
    /// lags the walk and eases on after the box stops. A reversal slows the box over <see cref="RampFrames"/> frames,
    /// to zero travel on the turn frame, and back to the walk's speed the other way. Either scene can leave out the
    /// ground or wall, so the clear colour lies beside and behind the box's whole outline, or add a second keyed box
    /// walking past behind the box as it stops (<see cref="StopSurround"/>). The measure reads the pixels whose
    /// centre shows the box on each frame, by casting the pixel centre's ray, against the same frame of the
    /// supersampled reference (<see cref="TemporalAcceptance.ReferenceSequence"/>). HDR is off and the sharpen is at
    /// its default.
    /// </summary>
    public sealed class TemporalFollowStopRuns
    {
        public const int W = 320, H = 180, StillFrames = 16, Turn = StillFrames + 24, After = 15, RampFrames = 4;

        /// <summary>The frames measured before the turn frame: a whole jitter sequence at Quality, the longest of the
        /// presets the stop facts run, and two at Native.</summary>
        public const int Lead = 18;

        /// <summary>The per-frame weight of the damped camera's target: <see cref="FollowCamera3D"/>'s exponential
        /// smoothing at its default <see cref="FollowCamera3D.TargetDampingRate"/> over a sixtieth of a
        /// second.</summary>
        public static readonly float DampingWeight = 1f - MathF.Exp(-new FollowCamera3D().TargetDampingRate / 60f);

        const ulong Key = 79;
        const float OrthoSize = 4.5f;

        readonly Dictionary<string, StopRun> _runs = new();
        readonly Dictionary<string, byte[][]> _references = new();
        readonly Dictionary<string, (Func<StopScene> Still, TemporalUpscale Preset, int Phase)> _controls = new();

        /// <summary>Wall time spent rendering and measuring so far, in seconds.</summary>
        internal double Seconds { get; private set; }

        /// <summary>The perspective walk at <paramref name="pixelsPerFrame"/> display pixels a frame of ground motion
        /// at the screen centre, at <paramref name="pitch"/> radians, heading <paramref name="heading"/>, ending as
        /// <paramref name="ending"/> says, its jitter sequence started <paramref name="phase"/> phases on
        /// (<see cref="TemporalFixture.SkipPhases"/>).</summary>
        internal StopRun Perspective(FollowEnding ending, TemporalUpscale preset, float pixelsPerFrame, float pitch,
            FollowHeading heading, StopSurround surround = StopSurround.Ground, int phase = 0) =>
            Run(new PerspectiveStop(ending, pixelsPerFrame, pitch, heading, surround, false), preset,
                () => new PerspectiveStop(ending, pixelsPerFrame, pitch, heading, surround, true), phase);

        /// <summary>The orthographic walk at <paramref name="pixelsPerFrame"/> display pixels a frame, ending as
        /// <paramref name="ending"/> says, its jitter sequence started <paramref name="phase"/> phases on.</summary>
        internal StopRun Orthographic(FollowEnding ending, TemporalUpscale preset, float pixelsPerFrame,
            StopSurround surround = StopSurround.Ground, int phase = 0) =>
            Run(new OrthographicStop(ending, pixelsPerFrame, surround, false), preset,
                () => new OrthographicStop(ending, pixelsPerFrame, surround, true), phase);

        /// <summary>The control of <paramref name="walk"/>: the same scene and ending with the avatar standing still
        /// in the world while the camera and the passer keep their paths relative to it. The avatar shows as it does
        /// in the walk on every frame, and its own history is kept by construction, since it never travels.</summary>
        internal StopRun Control(StopRun walk)
        {
            var (still, preset, phase) = _controls[walk.Name];
            return Run(still(), preset, still, phase);
        }

        /// <summary>The passer's speed across the screen once the camera stops, in display pixels a frame.</summary>
        public const float PasserPixels = 1f;

        const ulong PasserKey = 83;

        static readonly Color PasserTint = new(0.95f, 0.85f, 0.55f, 1f);

        /// <summary>What a run's name adds for the passer (<see cref="StopSurround.Passer"/>).</summary>
        internal static string PasserSuffix => Suffix(StopSurround.Passer);

        static string Suffix(StopSurround surround) => surround switch
        {
            StopSurround.ClearColour => ", over the clear colour",
            StopSurround.Passer => $", a passer at {PasserPixels} px a frame behind",
            _ => "",
        };

        StopRun Run(StopScene scene, TemporalUpscale preset, Func<StopScene> still, int phase)
        {
            string name = $"{scene.Name}, {preset}" + (phase == 0 ? "" : $", from jitter phase {phase}");
            _controls[name] = (still, preset, phase);
            if (_runs.TryGetValue(name, out StopRun? cached)) return cached;
            long started = Stopwatch.GetTimestamp();
            const int first = Turn - Lead, count = Lead + After + 1;
            if (!_references.TryGetValue(scene.Name, out byte[][]? references))
                _references[scene.Name] = references = TemporalAcceptance.ReferenceSequence(W, H,
                    s => scene.Setup(s, TemporalUpscale.Native), scene.Draw, first, count);
            byte[][] frames = TemporalAcceptance.Sequence(W, H, s => scene.Setup(s, preset), scene.Draw, first, count,
                phase);
            var masks = new bool[count][];
            for (int k = 0; k < count; k++) masks[k] = scene.Mask(first + k);
            int reach = TemporalFollowCameraRuns.SpillPixels(preset);
            var inner = new bool[count][];
            var ring = new bool[count][];
            for (int k = 0; k < count; k++)
            {
                inner[k] = Erode(masks[k], reach);
                ring[k] = Ring(masks[k], reach);
            }
            double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            Seconds += seconds;
            return _runs[name] = new StopRun(name, Measure(frames, references, masks),
                Measure(frames, references, inner), Measure(frames, references, ring), seconds,
                TemporalJitter.PhaseCount(TemporalSettings.DisplayOverInternal(preset)), scene.Ending, scene.Surround);
        }

        // Each frame's mean luma error over its mask, and over each frame step from the frame before the turn on the
        // change against the reference's on the pixels in both frames' masks.
        static StopMeasure Measure(byte[][] frames, byte[][] references, bool[][] masks)
        {
            var errors = new double[frames.Length];
            double flicker = 0, added = 0;
            long steps = 0;
            int pixels = 0;
            for (int k = 0; k < frames.Length; k++)
            {
                double sum = 0;
                int shown = 0;
                for (int i = 0; i < masks[k].Length; i++)
                {
                    if (!masks[k][i]) continue;
                    int x = i % W, y = i / W;
                    float f = TemporalAcceptance.Luma(frames[k], W, x, y);
                    float r = TemporalAcceptance.Luma(references[k], W, x, y);
                    sum += MathF.Abs(f - r);
                    shown++;
                    if (k < Lead || !masks[k - 1][i]) continue;
                    float df = f - TemporalAcceptance.Luma(frames[k - 1], W, x, y);
                    float dr = r - TemporalAcceptance.Luma(references[k - 1], W, x, y);
                    flicker += MathF.Abs(df - dr);
                    added += MathF.Max(MathF.Abs(df) - MathF.Abs(dr), 0f);
                    steps++;
                }
                errors[k] = shown == 0 ? 0 : sum / shown;
                if (k == Lead) pixels = shown;
            }
            return new StopMeasure(errors[(Lead - 1)..], steps == 0 ? 0 : flicker / steps,
                steps == 0 ? 0 : added / steps, pixels, errors[..Lead]);
        }

        // The pixels of the mask whose every neighbour within reach, rows and columns, is in it.
        static bool[] Erode(bool[] mask, int reach)
        {
            var inner = new bool[mask.Length];
            for (int i = 0; i < mask.Length; i++)
            {
                int x = i % W, y = i / W;
                bool all = mask[i];
                for (int dy = -reach; dy <= reach && all; dy++)
                    for (int dx = -reach; dx <= reach && all; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        all = xx >= 0 && xx < W && yy >= 0 && yy < H && mask[yy * W + xx];
                    }
                inner[i] = all;
            }
            return inner;
        }

        // The pixels outside the mask with a neighbour within reach, rows and columns, in it.
        static bool[] Ring(bool[] mask, int reach)
        {
            var ring = new bool[mask.Length];
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i]) continue;
                int x = i % W, y = i / W;
                bool any = false;
                for (int dy = -reach; dy <= reach && !any; dy++)
                    for (int dx = -reach; dx <= reach && !any; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        any = xx >= 0 && xx < W && yy >= 0 && yy < H && mask[yy * W + xx];
                    }
                ring[i] = any;
            }
            return ring;
        }

        /// <summary>A followed walk and its ending: how far the box has walked and the camera's target has moved by
        /// each frame, the draw, and the pixels whose centre shows the box.</summary>
        abstract class StopScene
        {
            readonly float[] _walked = new float[Turn + After + 2], _target = new float[Turn + After + 2];

            protected StopScene(FollowEnding ending, float step, StopSurround surround, bool still)
            {
                Ending = ending;
                Surround = surround;
                Still = still;
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

            public StopSurround Surround { get; }

            /// <summary>Whether the avatar stands still in the world while the camera and the passer keep their paths
            /// relative to it, so the avatar shows as it does in the walk on every frame and no followed mark is
            /// ever stored: the walk's control.</summary>
            public bool Still { get; }

            public abstract string Name { get; }

            /// <summary>How far the box has walked by frame <paramref name="n"/>.</summary>
            protected float Walked(int n) => Still ? 0f : Walk(n);

            /// <summary>How far the camera's target has moved by frame <paramref name="n"/>.</summary>
            protected float Target(int n) => _target[Math.Clamp(n, 0, _target.Length - 1)] - (Still ? Walk(n) : 0f);

            /// <summary>Where the passer's path is laid on frame <paramref name="n"/>: at the avatar's place on the
            /// turn frame, and for an avatar standing still, as far from it as the walking avatar is then.</summary>
            protected float Along(int n) => Walk(Turn) - (Still ? Walk(n) : 0f);

            float Walk(int n) => _walked[Math.Clamp(n, 0, _walked.Length - 1)];

            // The control's name: the avatar stands still and the camera keeps its path relative to the avatar.
            protected string Named(string name) => Still ? name + ", standing still" : name;

            public abstract void Setup(Scene3D s, TemporalUpscale preset);

            public abstract void Draw(Scene3D s, int n);

            public abstract bool[] Mask(int n);

            protected static MeshHandle RidgedBox(Scene3D s) => s.LoadMesh(MeshPrimitives.Box(1f),
                s.LoadTexture(TemporalNarrowCrossingRuns.RidgedTexels(), TemporalNarrowCrossingRuns.BoxTexture,
                    TemporalNarrowCrossingRuns.BoxTexture));

            protected static RigidInstanceDraw Keyed(MeshHandle box, Matrix4x4 world) =>
                new(box, world) { Tint = CrossingScene.Tint, Motion = MotionKey.From(Key) };

            /// <summary>The passer, the same ridged box keyed and tinted apart, at <paramref name="world"/>, drawn
            /// only where the surround has one.</summary>
            protected void DrawPasser(Scene3D s, MeshHandle box, Matrix4x4 world)
            {
                if (Surround == StopSurround.Passer)
                    s.Draw(new RigidInstanceDraw(box, world) { Tint = PasserTint, Motion = MotionKey.From(PasserKey) });
            }
        }

        // The perspective walk of TemporalPerspectiveFollowRuns, with the ending's path.
        sealed class PerspectiveStop : StopScene
        {
            static readonly Vector3 Size = TemporalPerspectiveFollowRuns.AvatarSize;
            readonly GroundStage _stage;
            readonly Vector3 _direction;
            MeshHandle _box;

            // The passer walks across the camera's view this far behind the avatar, its centre this far to the right
            // of the avatar's on the turn frame, so it shows beside the avatar's right edge and over its top.
            const float PasserBehind = 1f, PasserRight = 0.45f;
            readonly float _passerStep;

            public PerspectiveStop(FollowEnding ending, float speed, float pitch, FollowHeading heading,
                StopSurround surround, bool still)
                : this(ending, new PerspectiveWalk(W, H, pitch, heading, speed, StillFrames, Size), surround,
                    $"perspective {heading} at pitch {pitch}, {speed} px a frame, {ending}{Suffix(surround)}", still)
            {
            }

            PerspectiveStop(FollowEnding ending, PerspectiveWalk walk, StopSurround surround, string name,
                bool still)
                : base(ending, walk.Step, surround, still)
            {
                _stage = walk.Stage;
                _direction = walk.Direction;
                Name = Named(name);
                _passerStep = PasserStep();
            }

            public override string Name { get; }

            Vector3 Foot(int n) => _direction * Walked(n);

            FollowCamera3D Camera(int n) => _stage.Camera(_direction * Target(n));

            Vector3 PasserFoot(int n) => _direction * Along(n) + GroundStage.Forward * PasserBehind
                + GroundStage.Right * (PasserRight - _passerStep * (n - Turn));

            // The metres a frame that move the passer's middle PasserPixels across the screen on the turn frame.
            float PasserStep()
            {
                FollowCamera3D camera = Camera(Turn);
                Vector3 middle = PasserFoot(Turn) + new Vector3(0f, Size.Y / 2f, 0f);
                camera.WorldToScreen(middle, W, H, out Vector2 at);
                float lo = 0f, hi = 2f;
                for (int i = 0; i < 48; i++)
                {
                    float mid = 0.5f * (lo + hi);
                    camera.WorldToScreen(middle - GroundStage.Right * mid, W, H, out Vector2 moved);
                    if (Vector2.Distance(moved, at) < PasserPixels) lo = mid; else hi = mid;
                }
                return 0.5f * (lo + hi);
            }

            public override void Setup(Scene3D s, TemporalUpscale preset)
            {
                _stage.Setup(s, AntiAliasing.Temporal, preset, Camera(0));
                _box = RidgedBox(s);
            }

            public override void Draw(Scene3D s, int n)
            {
                ((FollowCamera3D)s.CameraOverride!).Target = _stage.Offset + _direction * Target(n);
                if (Surround != StopSurround.ClearColour) _stage.DrawGround(s);
                DrawPasser(s, _box, _stage.Standing(PasserFoot(n), Size));
                s.Draw(Keyed(_box, _stage.Standing(Foot(n), Size)));
            }

            public override bool[] Mask(int n)
            {
                FollowCamera3D camera = Camera(n);
                Vector3 eye = camera.Eye, foot = Foot(n);
                var mask = new bool[W * H];
                for (int i = 0; i < mask.Length; i++)
                {
                    // Where the ray meets no ground, its point a kilometre out stands in for the ground behind.
                    int x = i % W, y = i / W;
                    Vector3 behind = GroundRays.GroundPoint(camera, W, H, x, y)
                        ?? eye + camera.ScreenToRay(new Vector2(x + 0.5f, y + 0.5f), W, H).Direction * 1000f;
                    mask[i] = GroundRays.Hides(eye, behind, foot, Size, GroundStage.Yaw);
                }
                return mask;
            }
        }

        // The orthographic walk of TemporalFollowCameraRuns, with the ending's path.
        sealed class OrthographicStop : StopScene
        {
            const float Left = TemporalFollowCameraRuns.LeftPixels, Side = TemporalFollowCameraRuns.SizePixels;
            readonly FrontStage _stage = new(W, H, OrthoSize);
            MeshHandle _box;

            public OrthographicStop(FollowEnding ending, float speed, StopSurround surround, bool still)
                : base(ending, speed * OrthoSize / H, surround, still) =>
                Name = Named($"orthographic, {speed} px a frame, {ending}{Suffix(surround)}");

            public override string Name { get; }

            Vector3 Size => new(Side * _stage.PixelWorld, Side * _stage.PixelWorld, 0.5f);

            Vector3 Centre(int n) => new((Left + Side / 2f - W / 2f) * _stage.PixelWorld + Walked(n), 0f, 0f);

            // Halfway between the box and the wall, walking left, its centre half the box's side to the right of the
            // box's and a third of it above on the turn frame, so it shows beside the box's right edge and top.
            Vector3 PasserCentre(int n) => new((Left + Side / 2f - W / 2f) * _stage.PixelWorld + Along(n)
                + Size.X * (0.5f - PasserPixels * (n - Turn) / Side), Size.Y * 0.3f, -1f);

            public override void Setup(Scene3D s, TemporalUpscale preset)
            {
                _stage.Setup(s, AntiAliasing.Temporal, preset);
                s.Post.Hdr.Enabled = false;
                _box = RidgedBox(s);
            }

            public override void Draw(Scene3D s, int n)
            {
                s.Camera.Target = new Vector3(Target(n), 0f, 0f);
                if (Surround != StopSurround.ClearColour) _stage.TexturedWall(s);
                DrawPasser(s, _box, Matrix4x4.CreateScale(Size) * Matrix4x4.CreateTranslation(PasserCentre(n)));
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
