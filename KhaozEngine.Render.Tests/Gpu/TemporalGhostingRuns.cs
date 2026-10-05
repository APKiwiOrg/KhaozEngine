using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A tally over trail pixels. Each pixel's difference from the background frame and the floor's difference from
    /// it are taken under <see cref="PixelDifference.MaxChannel"/>, which also sees a tint that keeps the luma. A
    /// pixel is over when its difference passes the tolerance, and in excess when its difference passes the floor's
    /// by more than the tolerance.
    /// </summary>
    internal sealed class TrailTally
    {
        public int Checked, Over, LumaOver, FloorOver, Excess;
        public float Worst, FloorWorst, WorstExcess;

        public void Add(float difference, float luma, float floor, float tolerance)
        {
            Checked++;
            Worst = MathF.Max(Worst, difference);
            FloorWorst = MathF.Max(FloorWorst, floor);
            WorstExcess = MathF.Max(WorstExcess, difference - floor);
            if (difference > tolerance) Over++;
            if (luma > tolerance) LumaOver++;
            if (floor > tolerance) FloorOver++;
            if (difference - floor > tolerance) Excess++;
        }

        public override string ToString() =>
            $"{Over} of {Checked} over (luma {LumaOver}, worst {Worst:0.000}), floor {FloorOver} (worst "
            + $"{FloorWorst:0.000}), excess {Excess} (worst {WorstExcess:0.000})";
    }

    /// <summary>
    /// One crossing's trail two or more frames after the box passed. <see cref="Total"/> is the whole trail region of
    /// <see cref="TemporalAcceptance.Trail"/>. <see cref="Ages"/> splits it by the frames since the box last covered a
    /// pixel, from 2 up. <see cref="Column"/> is the column right behind the box's trailing edge one frame ago, which
    /// read the box's motion then and is two frames past it now. <see cref="Reach"/> is how far behind the trailing
    /// edge the farthest pixel in excess lies, in display pixels, and <see cref="OldestAge"/> its age in frames, both 0
    /// when none is.
    /// </summary>
    internal sealed record CrossingTrail(string Name, TrailTally Total, IReadOnlyList<TrailTally> Ages,
        TrailTally Column, int Reach, int OldestAge, double Seconds)
    {
        public TrailTally Age(int age) => Ages[age - TemporalGhostingRuns.FirstAge];
    }

    /// <summary>The teleport: whether its jump frame kept history, the motion read at the landed box's centre and
    /// the analytic jump in UV, the internal size it was read at, and the trail two frames after it left.</summary>
    internal sealed record TeleportRun(bool HistoryValid, Vector2 Motion, float Want, int InternalWidth,
        int InternalHeight, TrailTally Trail, double Seconds);

    /// <summary>The zoom's ghost pixels at each checkpoint frame against a supersampled reference.</summary>
    internal sealed record ZoomRun(IReadOnlyList<(int Frame, float Zoom, int Ghosts)> Checkpoints, double Seconds);

    /// <summary>The burst region two frames after the last particle, against the background run with no particles,
    /// and the reactive estimate on a frame the particles draw: pixels at or above one half inside and outside the
    /// burst region, and the largest value inside.</summary>
    internal sealed record BurstRun(PixelRect Region, TrailTally Trail, int ReactiveInside, int ReactiveOutside,
        float ReactiveMax, double Seconds);

    /// <summary>
    /// The runs the ghosting acceptance reads, rendered on first use and kept for the whole test class, so the table
    /// test and each assertion read the same runs. HDR is off in every run, as in the other temporal acceptance
    /// tests, and the sharpen stays at its default. Every comparison is between runs of one session.
    /// <para>
    /// A crossing's trail pixels were uncovered only a few frames ago. There the resolve rejects the box's history as
    /// disoccluded and starts again from one sample, so the pixel is not yet what the long-converged background run
    /// shows. On the flat wall that costs nothing, and on the textured wall a young accumulation of a jittered texture
    /// differs from a converged one. That floor is measured per pixel: a pixel the box last covered <c>k</c> frames
    /// ago is compared with a run of the bare wall whose first rendered frame, with no history, is the frame the box
    /// uncovered it. A fresh fixture's first frame drops history exactly as a disocclusion does, and it starts at the
    /// same frame index and jitter phase. The ghost is the excess over that floor.
    /// </para>
    /// </summary>
    public sealed class TemporalGhostingRuns
    {
        public const int W = 320, H = 180;

        /// <summary>The difference, 0 to 1, a trail pixel may keep.</summary>
        public const float Tolerance = 0.05f;

        /// <summary>The crossing's measured frame: 23 frames after the box appears, 92 display pixels along.</summary>
        public const int CrossingLast = CrossingScene.StillFrames + 23;

        /// <summary>The rectangles the trail reads, now and the 8 frames before.</summary>
        public const int TrailFrames = 9;

        /// <summary>The youngest trail pixel: two frames after the box passed.</summary>
        public const int FirstAge = 2;

        /// <summary>The teleport's box jumps on this frame and is measured on the one after.</summary>
        public const int JumpFrame = 24;

        /// <summary>The teleport's stage and box: 13 cm a pixel, so a 10 m jump stays on screen.</summary>
        public const float TeleportOrthoSize = 24f;
        public static readonly Vector3 TeleportSize = new(1.5f, 1.5f, 1f);
        public static readonly Vector3 TeleportFrom = new(-6f, 0f, 0f), TeleportTo = new(4f, 0f, 0f);
        public const ulong TeleportKey = 12;

        /// <summary>The zoom's checkpoints, and the per-axis factor of their supersampled references.</summary>
        internal static readonly int[] ZoomCheckpoints = { 28, 34, 40, 46 };
        public const int ZoomReferenceFactor = 4;

        /// <summary>Particles draw on frames 20 to 23, the reactive estimate is read on 21, and the trail on 25, two
        /// frames after the last.</summary>
        public const int BurstStart = 20, BurstEnd = 24, BurstCheck = 25, ReactiveFrame = 21;

        /// <summary>The background under the burst pans this many display pixels a frame.</summary>
        public const float BurstPanPixels = 2f;

        internal static readonly TemporalUpscale[] Presets = Enum.GetValues<TemporalUpscale>();

        readonly Dictionary<(bool Keyed, bool Textured, TemporalUpscale Preset), CrossingTrail> _crossings = new();
        readonly Dictionary<(bool Textured, TemporalUpscale Preset), (byte[] Wall, byte[][] Floors)> _walls = new();
        readonly Dictionary<TemporalUpscale, TeleportRun> _teleports = new();
        readonly Dictionary<TemporalUpscale, ZoomRun> _zooms = new();
        readonly Dictionary<TemporalUpscale, BurstRun> _bursts = new();
        byte[][]? _zoomReferences;

        /// <summary>Wall time spent rendering and measuring so far, in seconds.</summary>
        internal double Seconds { get; private set; }

        static Action<Scene3D> Setup(FrontStage stage, TemporalUpscale preset) => s =>
        {
            stage.Setup(s, AntiAliasing.Temporal, preset);
            s.Post.Hdr.Enabled = false;
        };

        static byte[] RunTo(Action<Scene3D> setup, int last, Action<Scene3D, int> draw)
        {
            using var fx = new TemporalFixture(W, H, setup);
            fx.Frames(last, draw);
            return fx.Frame(draw);
        }

        /// <summary>The box crossing the flat or textured wall, keyed or not, at <paramref name="preset"/>.</summary>
        internal CrossingTrail Crossing(bool keyed, bool textured, TemporalUpscale preset)
        {
            if (_crossings.TryGetValue((keyed, textured, preset), out CrossingTrail? cached)) return cached;
            long started = Stopwatch.GetTimestamp();
            var scene = new CrossingScene(W, H, keyed, textured);
            var (wall, floors) = Wall(scene, preset);
            byte[] frame = RunTo(Setup(scene.Stage, preset), CrossingLast, scene.Draw);
            string name = $"{(keyed ? "keyed" : "unkeyed")} crossing, {(textured ? "textured" : "flat")} wall, "
                + $"{preset}";
            CrossingTrail trail = MeasureCrossing(name, frame, wall, floors,
                scene.Footprints(CrossingLast, TrailFrames), Stopwatch.GetElapsedTime(started).TotalSeconds);
            Seconds += trail.Seconds;
            return _crossings[(keyed, textured, preset)] = trail;
        }

        // The converged wall at the measured frame, and each age's floor: the bare wall from the frame a pixel of that
        // age was uncovered, with no history before it. Floors[k] is age k's.
        (byte[] Wall, byte[][] Floors) Wall(CrossingScene scene, TemporalUpscale preset)
        {
            if (_walls.TryGetValue((scene.Textured, preset), out var cached)) return cached;
            Action<Scene3D> setup = Setup(scene.Stage, preset);
            byte[] wall = RunTo(setup, CrossingLast, scene.Background);
            var floors = new byte[TrailFrames][];
            for (int age = FirstAge; age < TrailFrames; age++)
            {
                using var fx = new TemporalFixture(W, H, setup);
                fx.SkipFrames(CrossingLast - age + 1);
                fx.Frames(age - 1, scene.Background);
                floors[age] = fx.Frame(scene.Background);
            }
            return _walls[(scene.Textured, preset)] = (wall, floors);
        }

        // Trail pixels by age, the smallest k from FirstAge whose rectangle holds the pixel, under the region rule of
        // TemporalAcceptance.Trail: never within one pixel of the box now or inside where it was one frame ago.
        static CrossingTrail MeasureCrossing(string name, byte[] frame, byte[] wall, byte[][] floors,
            IReadOnlyList<PixelRect> footprints, double seconds)
        {
            var total = new TrailTally();
            var ages = new TrailTally[TrailFrames - FirstAge];
            for (int i = 0; i < ages.Length; i++) ages[i] = new TrailTally();
            var column = new TrailTally();
            int columnX = footprints[1].X0 - 1, reach = 0, oldest = 0;
            PixelRect now = footprints[0].Inflate(1);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    if (now.Contains(x, y) || footprints[1].Contains(x, y)) continue;
                    int age = 0;
                    for (int k = FirstAge; k < footprints.Count && age == 0; k++)
                        if (footprints[k].Contains(x, y)) age = k;
                    if (age == 0) continue;
                    float d = TemporalAcceptance.Difference(frame, wall, W, x, y, PixelDifference.MaxChannel);
                    float luma = TemporalAcceptance.Difference(frame, wall, W, x, y, PixelDifference.Luma);
                    float floor = TemporalAcceptance.Difference(floors[age], wall, W, x, y, PixelDifference.MaxChannel);
                    total.Add(d, luma, floor, Tolerance);
                    ages[age - FirstAge].Add(d, luma, floor, Tolerance);
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

        /// <summary>The keyed box jumping 10 m on a still camera over the flat wall, at <paramref name="preset"/>.
        /// </summary>
        internal TeleportRun Teleport(TemporalUpscale preset)
        {
            if (_teleports.TryGetValue(preset, out TeleportRun? cached)) return cached;
            long started = Stopwatch.GetTimestamp();
            var stage = new FrontStage(W, H, TeleportOrthoSize);
            void Draw(Scene3D s, int n)
            {
                stage.Wall(s);
                Matrix4x4 world = Matrix4x4.CreateScale(TeleportSize)
                    * Matrix4x4.CreateTranslation(n < JumpFrame ? TeleportFrom : TeleportTo);
                s.Draw(new RigidInstanceDraw(stage.Box, world)
                {
                    Tint = CrossingScene.Tint,
                    Motion = MotionKey.From(TeleportKey),
                });
            }

            Action<Scene3D> setup = Setup(stage, preset);
            byte[] after;
            bool valid;
            Vector2 motion;
            int mw, mh;
            using (var fx = new TemporalFixture(W, H, setup))
            {
                fx.Frames(JumpFrame, Draw);
                fx.Frames(1, Draw);   // the jump frame
                valid = fx.Scene.LastTemporalDiagnostics.HistoryValid;
                (Vector2[] field, mw, mh) = fx.Scene.ReadMotionTargetForTests();
                PixelRect landed = stage.Footprint(fx.Scene, TeleportTo, TeleportSize);
                int cx = (landed.X0 + landed.X1) / 2 * mw / W, cy = (landed.Y0 + landed.Y1) / 2 * mh / H;
                motion = field[cy * mw + cx];
                after = fx.Frame(Draw);   // two frames after the box left
            }
            byte[] wall = RunTo(setup, JumpFrame + 1, (s, _) => stage.Wall(s));
            IsoCamera3D camera = stage.Camera();
            PixelRect Rect(Vector3 c) => TemporalAcceptance.Footprint(camera, c - TeleportSize * 0.5f,
                c + TeleportSize * 0.5f, W, H);
            PixelRect now = Rect(TeleportTo).Inflate(1), from = Rect(TeleportFrom);
            var trail = new TrailTally();
            for (int y = from.Y0; y < from.Y1; y++)
                for (int x = from.X0; x < from.X1; x++)
                {
                    if (now.Contains(x, y)) continue;
                    float d = TemporalAcceptance.Difference(after, wall, W, x, y, PixelDifference.MaxChannel);
                    trail.Add(d, TemporalAcceptance.Difference(after, wall, W, x, y, PixelDifference.Luma), 0f,
                        Tolerance);
                }
            float want = (TeleportTo.X - TeleportFrom.X) / (TeleportOrthoSize * W / H);
            var run = new TeleportRun(valid, motion, want, mw, mh, trail,
                Stopwatch.GetElapsedTime(started).TotalSeconds);
            Seconds += run.Seconds;
            return _teleports[preset] = run;
        }

        /// <summary>The engine's default isometric camera zooming over the yard, at <paramref name="preset"/>, with
        /// the ghost pixels of each checkpoint frame against its supersampled reference.</summary>
        internal ZoomRun Zoom(TemporalUpscale preset, float ghostTolerance, float edgeStep)
        {
            if (_zooms.TryGetValue(preset, out ZoomRun? cached)) return cached;
            long started = Stopwatch.GetTimestamp();
            var yard = new IsoYard(W, H);
            Action<Scene3D> Setup(AntiAliasing aa) => s =>
            {
                yard.Setup(s, aa);
                s.Post.Temporal.Upscale = preset;
                s.Post.Hdr.Enabled = false;
            };
            _zoomReferences ??= Array.ConvertAll(ZoomCheckpoints, n => TemporalAcceptance.Supersampled(W, H,
                ZoomReferenceFactor, Setup(AntiAliasing.Off), yard.Draw, n));
            var checkpoints = new List<(int, float, int)>();
            using (var fx = new TemporalFixture(W, H, Setup(AntiAliasing.Temporal)))
            {
                int last = ZoomCheckpoints[^1];
                for (int n = 0; n <= last; n++)
                {
                    int i = Array.IndexOf(ZoomCheckpoints, n);
                    if (i < 0) { fx.Frames(1, yard.Draw); continue; }
                    byte[] frame = fx.Frame(yard.Draw);
                    checkpoints.Add((n, yard.ZoomAt(n), TemporalAcceptance.GhostPixels(frame, _zoomReferences[i], W, H,
                        ghostTolerance, edgeStep)));
                }
            }
            var run = new ZoomRun(checkpoints, Stopwatch.GetElapsedTime(started).TotalSeconds);
            Seconds += run.Seconds;
            return _zooms[preset] = run;
        }

        /// <summary>A burst of additive particles on frames 20 to 23 over the striped wall panning 2 display pixels a
        /// frame, at <paramref name="preset"/>.</summary>
        internal BurstRun Burst(TemporalUpscale preset)
        {
            if (_bursts.TryGetValue(preset, out BurstRun? cached)) return cached;
            long started = Stopwatch.GetTimestamp();
            var burst = new ParticleBurst(new FrontStage(W, H, 4.5f));
            Action<Scene3D> setup = Setup(burst.Stage, preset);
            byte[] frame, wall;
            PixelRect region;
            using (var fx = new TemporalFixture(W, H, setup))
            {
                fx.Frames(BurstCheck, burst.Draw);
                frame = fx.Frame(burst.Draw);
                region = burst.Region(fx.Scene.Camera);
            }
            wall = RunTo(setup, BurstCheck, burst.Background);

            // The floor: the bare wall with no history before the first frame without particles.
            byte[] floor;
            using (var fx = new TemporalFixture(W, H, setup))
            {
                fx.SkipFrames(BurstEnd);
                fx.Frames(BurstCheck - BurstEnd, burst.Background);
                floor = fx.Frame(burst.Background);
            }
            var trail = new TrailTally();
            for (int y = region.Y0; y < region.Y1; y++)
                for (int x = region.X0; x < region.X1; x++)
                    trail.Add(TemporalAcceptance.Difference(frame, wall, W, x, y, PixelDifference.MaxChannel),
                        TemporalAcceptance.Difference(frame, wall, W, x, y, PixelDifference.Luma),
                        TemporalAcceptance.Difference(floor, wall, W, x, y, PixelDifference.MaxChannel), Tolerance);

            // The Reactive view on a frame the particles draw: the dimmed grey scene mixed towards (1, 0.85, 0.1) by
            // the reactive estimate, so red minus blue is 0.9 times the estimate.
            int inside = 0, outside = 0;
            float max = 0f;
            using (var fx = new TemporalFixture(W, H, s => { setup(s); s.DebugView = SceneDebugView.Reactive; }))
            {
                fx.Frames(ReactiveFrame, burst.Draw);
                byte[] view = fx.Frame(burst.Draw);
                PixelRect drawn = burst.Region(fx.Scene.Camera);
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        int i = (y * W + x) * 4;
                        float reactive = (view[i] - view[i + 2]) / (0.9f * 255f);
                        if (drawn.Contains(x, y)) max = MathF.Max(max, reactive);
                        if (reactive < 0.5f) continue;
                        if (drawn.Contains(x, y)) inside++;
                        else outside++;
                    }
            }
            var run = new BurstRun(region, trail, inside, outside, max, Stopwatch.GetElapsedTime(started).TotalSeconds);
            Seconds += run.Seconds;
            return _bursts[preset] = run;
        }
    }

    /// <summary>
    /// Forty additive soft glows around a point in front of the striped wall on frames
    /// <see cref="TemporalGhostingRuns.BurstStart"/> to <see cref="TemporalGhostingRuns.BurstEnd"/> minus 1, while the
    /// camera pans 2 display pixels a frame. The particles stay put in the world. <see cref="Background"/> is the same
    /// path without them.
    /// </summary>
    internal sealed class ParticleBurst
    {
        public static readonly Vector3 Centre = new(0.5f, 0.2f, 0f);
        public readonly FrontStage Stage;

        public ParticleBurst(FrontStage stage) => Stage = stage;

        public void Background(Scene3D s, int n)
        {
            s.Camera.Target = new Vector3(n * TemporalGhostingRuns.BurstPanPixels * Stage.PixelWorld, 0f, 0f);
            Stage.StripedWall(s);
        }

        public void Draw(Scene3D s, int n)
        {
            Background(s, n);
            if (n < TemporalGhostingRuns.BurstStart || n >= TemporalGhostingRuns.BurstEnd) return;
            for (int i = 0; i < 40; i++)
            {
                float r = 0.5f * (i % 5) / 5f;
                s.DrawParticle(new ParticleSprite
                {
                    Position = Centre + new Vector3(r * MathF.Cos(i * 2.4f), r * MathF.Sin(i * 2.4f), 0.5f),
                    Size = 0.25f,
                    Color = new Color(1f, 0.8f, 0.4f, 1f),
                    Shape = ParticleShape.SoftGlow,
                    Blend = BillboardBlend.Additive,
                    LifeNorm = 0.3f,
                    Seed = i,
                });
            }
        }

        /// <summary>Every pixel a particle can reach under <paramref name="camera"/>, with a margin.</summary>
        public PixelRect Region(IIsoCamera3D camera) => TemporalAcceptance.Footprint(camera,
            Centre - new Vector3(0.7f, 0.7f, 0f), Centre + new Vector3(0.7f, 0.7f, 0.6f), Stage.W, Stage.H);
    }
}
