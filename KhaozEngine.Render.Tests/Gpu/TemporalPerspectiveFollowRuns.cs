using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>Which way the followed avatar walks, seen from the camera.</summary>
    public enum FollowHeading { Away, Sideways, Towards }

    /// <summary>
    /// A keyed avatar-sized box the perspective follow camera keeps at the centre of the screen while it walks over the
    /// textured ground (<see cref="GroundStage"/>), as a third-person camera follows an avatar. The box has the ridged
    /// box's texture (<see cref="TemporalNarrowCrossingRuns.RidgedTexels"/>), so its pixels take ridges and locks. It
    /// stands still for <see cref="StillFrames"/> frames, or the hold a run is given, then walks away from the camera,
    /// to its right or towards it for <see cref="WalkFrames"/> frames while the camera follows, so the box is still on
    /// screen and the ground pans under it. The walk is given as the ground's motion at the screen centre in display
    /// pixels a frame. The last frame (<see cref="Last"/> at the default hold) is measured as the orthographic follow
    /// walk is (<see cref="TemporalFollowCameraRuns"/>): each ground pixel the box uncovered, by
    /// its largest channel difference from the bare ground on the same camera path, in excess of a floor that starts
    /// that bare ground with no history on the frame the box uncovered the pixel. Which frames the box hid a pixel's
    /// ground point is found by casting rays: from the measured frame's camera through the pixel centre to the ground,
    /// then from each earlier frame's eye to that point, grown by a pixel to take the partly covered pixels in. HDR is
    /// off and the sharpen is at its default.
    /// </summary>
    public sealed class TemporalPerspectiveFollowRuns
    {
        public const int W = 320, H = 180, StillFrames = 16, WalkFrames = 23, Last = StillFrames + WalkFrames;

        /// <summary>The avatar's width, height and depth in metres.</summary>
        public static readonly Vector3 AvatarSize = new(0.8f, 1.8f, 0.5f);

        const ulong Key = 67;

        readonly Dictionary<(TemporalUpscale, float, float, FollowHeading, int, int), CrossingTrail> _runs = new();
        readonly Dictionary<(TemporalUpscale, float, float, FollowHeading, int, int), int> _bandMarks = new();
        readonly Dictionary<(TemporalUpscale, float, float, FollowHeading, int, int), CrossingTrail> _beyond = new();
        readonly Dictionary<(TemporalUpscale, float, float, FollowHeading, int), (byte[] Ground, byte[][] Floors)>
            _bare = new();

        /// <summary>Wall time spent rendering and measuring so far, in seconds.</summary>
        internal double Seconds { get; private set; }

        /// <summary>The walk at <paramref name="pixelsPerFrame"/> display pixels a frame of ground motion at the
        /// screen centre, at <paramref name="pitch"/> radians, heading <paramref name="heading"/>, after
        /// <paramref name="hold"/> still frames, its jitter sequence started <paramref name="phase"/> phases on
        /// (<see cref="TemporalFixture.SkipPhases"/>).</summary>
        internal CrossingTrail Run(TemporalUpscale preset, float pixelsPerFrame, float pitch, FollowHeading heading,
            int hold = StillFrames, int phase = 0)
        {
            var key = (preset, pixelsPerFrame, pitch, heading, hold, phase);
            if (_runs.TryGetValue(key, out CrossingTrail? cached)) return cached;
            long started = Stopwatch.GetTimestamp();
            var walk = new PerspectiveWalk(W, H, pitch, heading, pixelsPerFrame, hold, AvatarSize);
            int last = hold + WalkFrames;
            var (ground, floors) = Bare(walk, preset, hold, phase);
            byte[] frame;
            using (var fx = new TemporalFixture(W, H, s => walk.Setup(s, preset)))
            {
                fx.SkipPhases(phase);
                fx.Frames(last, (s, n) => walk.Draw(s, n, Key));
                frame = fx.Frame((s, n) => walk.Draw(s, n, Key));
                _bandMarks[key] = BandMarks(fx);
            }
            int[] ages = walk.Ages(last, TemporalGhostingRuns.TrailFrames, out PixelRect now);
            string held = (hold == StillFrames ? "" : $", after {hold} still frames")
                + (phase == 0 ? "" : $", from jitter phase {phase}");
            string name =
                $"perspective follow at {pixelsPerFrame} px a frame, pitch {pitch}, {heading}, {preset}{held}";
            CrossingTrail trail = Measure(name, frame, ground, floors, ages, now,
                Stopwatch.GetElapsedTime(started).TotalSeconds);
            int spill = TemporalFollowCameraRuns.SpillPixels(preset);
            int[] beyond = walk.Ages(last, TemporalGhostingRuns.TrailFrames, out PixelRect grown, spill);
            _beyond[key] = Measure($"{name}, past {spill} px", frame, ground, floors, beyond, grown, 0);
            Seconds += trail.Seconds;
            return _runs[key] = trail;
        }

        /// <summary>The walk's trail past the reconstruction's reach from the box: only the ground pixels more than
        /// <see cref="TemporalFollowCameraRuns.SpillPixels"/> from the pixels showing it now, where the reconstruction
        /// cannot spread its texel and only a history kept from it can leave its colour. The reach is counted from
        /// there.</summary>
        internal CrossingTrail BeyondSpill(TemporalUpscale preset, float pixelsPerFrame, float pitch,
            FollowHeading heading, int hold = StillFrames, int phase = 0)
        {
            Run(preset, pixelsPerFrame, pitch, heading, hold, phase);
            return _beyond[(preset, pixelsPerFrame, pitch, heading, hold, phase)];
        }

        /// <summary>The display pixels whose stored state holds the band mark on the measured frame of the walk, which
        /// <see cref="Run"/> rendered.</summary>
        internal int BandMarks(TemporalUpscale preset, float pixelsPerFrame, float pitch, FollowHeading heading,
            int hold = StillFrames, int phase = 0) => _bandMarks[(preset, pixelsPerFrame, pitch, heading, hold, phase)];

        // Stored states below minus 2.5 are band marks, beside the edge or on the followed surface (the resolve's
        // temporalStoreLock).
        static int BandMarks(TemporalFixture fx)
        {
            var history = fx.Scene.TemporalHistory;
            float[] state = TemporalTextureIo.Read(fx.Device, history.Confidence(history.WriteIndex));
            int marks = 0;
            for (int i = 1; i < state.Length; i += 2) if (state[i] < -2.5f) marks++;
            return marks;
        }

        // The converged bare ground at the measured frame on the same camera path, and each age's floor. Only the first
        // phase's are kept: another phase's serve one walk.
        (byte[] Ground, byte[][] Floors) Bare(PerspectiveWalk walk, TemporalUpscale preset, int hold, int phase)
        {
            var key = (preset, walk.Speed, walk.Pitch, walk.Heading, hold);
            if (phase == 0 && _bare.TryGetValue(key, out var cached)) return cached;
            Action<Scene3D> setup = s => walk.Setup(s, preset);
            int last = hold + WalkFrames;
            byte[] ground;
            using (var fx = new TemporalFixture(W, H, setup))
            {
                fx.SkipPhases(phase);
                fx.Frames(last, walk.Background);
                ground = fx.Frame(walk.Background);
            }
            var floors = new byte[TemporalGhostingRuns.TrailFrames][];
            for (int age = TemporalGhostingRuns.FirstAge; age < floors.Length; age++)
            {
                using var fx = new TemporalFixture(W, H, setup);
                fx.SkipPhases(phase);
                fx.SkipFrames(last - age + 1);
                fx.Frames(age - 1, walk.Background);
                floors[age] = fx.Frame(walk.Background);
            }
            return phase == 0 ? _bare[key] = (ground, floors) : (ground, floors);
        }

        /// <summary>The trail by age as <see cref="TemporalNarrowCrossingRuns.Measure"/> reads it, from an age per
        /// pixel (0 where the pixel is not in the trail) rather than from rectangles. The reach is how far the farthest
        /// pixel in excess lies outside the box's rectangle now, in display pixels.</summary>
        internal static CrossingTrail Measure(string name, byte[] frame, byte[] ground, byte[][] floors, int[] ages,
            PixelRect now, double seconds)
        {
            const float Tolerance = TemporalGhostingRuns.Tolerance;
            var total = new TrailTally();
            var byAge = new TrailTally[TemporalGhostingRuns.TrailFrames - TemporalGhostingRuns.FirstAge];
            for (int i = 0; i < byAge.Length; i++) byAge[i] = new TrailTally();
            int reach = 0, oldest = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int age = ages[y * W + x];
                    if (age == 0) continue;
                    float d = TemporalAcceptance.Difference(frame, ground, W, x, y, PixelDifference.MaxChannel);
                    float luma = TemporalAcceptance.Difference(frame, ground, W, x, y, PixelDifference.Luma);
                    float floor = TemporalAcceptance.Difference(floors[age], ground, W, x, y,
                        PixelDifference.MaxChannel);
                    total.Add(d, luma, floor, Tolerance);
                    byAge[age - TemporalGhostingRuns.FirstAge].Add(d, luma, floor, Tolerance);
                    if (d - floor > Tolerance)
                    {
                        int dx = Math.Max(0, Math.Max(now.X0 - x, x - now.X1 + 1));
                        int dy = Math.Max(0, Math.Max(now.Y0 - y, y - now.Y1 + 1));
                        reach = Math.Max(reach, Math.Max(dx, dy));
                        oldest = Math.Max(oldest, age);
                    }
                }
            return new CrossingTrail(name, total, byAge, new TrailTally(), reach, oldest, seconds);
        }
    }

    /// <summary>
    /// One perspective walk: the camera follows a box standing on the ground from frame <c>still</c> on, at a step a
    /// frame that moves the ground at the screen centre by the walk's display pixels. <see cref="Draw"/> draws the
    /// ground and the keyed box, <see cref="Background"/> the ground alone on the same camera path.
    /// </summary>
    internal sealed class PerspectiveWalk
    {
        readonly GroundStage _stage;
        readonly int _still;
        readonly Vector3 _direction, _size;
        readonly float _step;
        MeshHandle _box;

        public PerspectiveWalk(int w, int h, float pitch, FollowHeading heading, float speed, int still, Vector3 size)
        {
            _stage = new GroundStage(w, h, pitch);
            _still = still;
            _size = size;
            Pitch = pitch;
            Heading = heading;
            Speed = speed;
            _direction = heading switch
            {
                FollowHeading.Away => GroundStage.Forward,
                FollowHeading.Sideways => GroundStage.Right,
                _ => -GroundStage.Forward,
            };
            _step = StepFor(speed);
        }

        public float Pitch { get; }
        public FollowHeading Heading { get; }
        public float Speed { get; }
        public GroundStage Stage => _stage;

        /// <summary>The metres a frame the box walks.</summary>
        public float Step => _step;

        /// <summary>The unit direction along the ground the box walks.</summary>
        public Vector3 Direction => _direction;

        /// <summary>Where the box stands on frame <paramref name="n"/>, the camera's target.</summary>
        public Vector3 Foot(int n) => _direction * (_step * Math.Max(0, n - _still));

        public FollowCamera3D Camera(int n) => _stage.Camera(Foot(n));

        public void Setup(Scene3D s, TemporalUpscale preset)
        {
            _stage.Setup(s, AntiAliasing.Temporal, preset, _stage.Camera(Foot(0)));
            _box = s.LoadMesh(MeshPrimitives.Box(1f), s.LoadTexture(TemporalNarrowCrossingRuns.RidgedTexels(),
                TemporalNarrowCrossingRuns.BoxTexture, TemporalNarrowCrossingRuns.BoxTexture));
        }

        public void Background(Scene3D s, int n)
        {
            ((FollowCamera3D)s.CameraOverride!).Target = Foot(n);
            _stage.DrawGround(s);
        }

        public void Draw(Scene3D s, int n, ulong key)
        {
            Background(s, n);
            DrawBox(s, n, key);
        }

        /// <summary>The keyed box alone on frame <paramref name="n"/>.</summary>
        public void DrawBox(Scene3D s, int n, ulong key) =>
            s.Draw(new RigidInstanceDraw(_box, _stage.Standing(Foot(n), _size))
            {
                Tint = CrossingScene.Tint,
                Motion = MotionKey.From(key),
            });

        /// <summary>Whether the box hid the ground point pixel (<paramref name="x"/>, <paramref name="y"/>) of frame
        /// <paramref name="last"/> shows on frame <paramref name="n"/>.</summary>
        public bool Hides(int n, int last, int x, int y) =>
            GroundRays.GroundPoint(Camera(last), _stage.W, _stage.H, x, y) is Vector3 g
            && GroundRays.Hides(Camera(n).Eye, g, Foot(n), _size, GroundStage.Yaw);

        /// <summary>The pixels of frame <paramref name="last"/> whose ground point the box hid on frame
        /// <paramref name="n"/>, by the ray through each pixel centre.</summary>
        public bool[] Hidden(int n, int last)
        {
            int w = _stage.W, h = _stage.H;
            FollowCamera3D measured = Camera(last);
            Vector3 eye = Camera(n).Eye, foot = Foot(n);
            var mask = new bool[w * h];
            for (int i = 0; i < w * h; i++)
                mask[i] = GroundRays.GroundPoint(measured, w, h, i % w, i / w) is Vector3 g
                    && GroundRays.Hides(eye, g, foot, _size, GroundStage.Yaw);
            return mask;
        }

        /// <summary>For each pixel of frame <paramref name="last"/>, the frames since the box last hid the ground
        /// point it shows, from 2 up to <paramref name="frames"/> less one, and 0 where the pixel shows the box now or
        /// lies within <paramref name="reach"/> pixels of one that does, the box hid it one frame ago, it hid it on
        /// none of those frames, or the pixel shows no ground. <paramref name="now"/> is the rectangle around the
        /// pixels showing the box now, grown by <paramref name="reach"/>.</summary>
        public int[] Ages(int last, int frames, out PixelRect now, int reach = 2)
        {
            int w = _stage.W, h = _stage.H;
            FollowCamera3D measured = Camera(last);
            var hidden = new bool[frames][];
            var ground = new Vector3?[w * h];
            for (int i = 0; i < w * h; i++) ground[i] = GroundRays.GroundPoint(measured, w, h, i % w, i / w);
            for (int k = 0; k < frames; k++)
            {
                Vector3 eye = Camera(last - k).Eye, foot = Foot(last - k);
                var mask = new bool[w * h];
                for (int i = 0; i < w * h; i++)
                    mask[i] = ground[i] is Vector3 g && GroundRays.Hides(eye, g, foot, _size, GroundStage.Yaw);
                hidden[k] = Grow(mask, w, h, k == 0 ? reach : 1);
            }
            int x0 = w, y0 = h, x1 = 0, y1 = 0;
            var ages = new int[w * h];
            for (int i = 0; i < w * h; i++)
            {
                if (hidden[0][i])
                {
                    x0 = Math.Min(x0, i % w); y0 = Math.Min(y0, i / w);
                    x1 = Math.Max(x1, i % w + 1); y1 = Math.Max(y1, i / w + 1);
                }
                if (ground[i] is null || hidden[0][i] || hidden[1][i]) continue;
                for (int k = TemporalGhostingRuns.FirstAge; k < frames && ages[i] == 0; k++)
                    if (hidden[k][i]) ages[i] = k;
            }
            now = x0 < x1 ? new PixelRect(x0, y0, x1, y1) : default;
            return ages;
        }

        internal static bool[] Grow(bool[] mask, int w, int h, int by)
        {
            var grown = new bool[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (!mask[y * w + x]) continue;
                    for (int yy = Math.Max(0, y - by); yy <= Math.Min(h - 1, y + by); yy++)
                        for (int xx = Math.Max(0, x - by); xx <= Math.Min(w - 1, x + by); xx++)
                            grown[yy * w + xx] = true;
                }
            return grown;
        }

        // The step whose ground motion at the screen centre is the walk's display pixels a frame: the camera moved by
        // the step shows the ground point at the centre where the unmoved camera shows it moved back by the step.
        float StepFor(float speed)
        {
            FollowCamera3D camera = _stage.Camera(Vector3.Zero);
            camera.WorldToScreen(Vector3.Zero, _stage.W, _stage.H, out Vector2 centre);
            float lo = 0f, hi = 2f;
            for (int i = 0; i < 48; i++)
            {
                float mid = 0.5f * (lo + hi);
                camera.WorldToScreen(-_direction * mid, _stage.W, _stage.H, out Vector2 moved);
                if (Vector2.Distance(moved, centre) < speed) lo = mid; else hi = mid;
            }
            return 0.5f * (lo + hi);
        }
    }
}
