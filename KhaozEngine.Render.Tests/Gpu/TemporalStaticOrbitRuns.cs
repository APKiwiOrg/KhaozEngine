using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>How the camera moves over the still field: orbiting its target a few degrees a frame or slowly, as a
    /// middle-drag orbits it, strafing sideways at a run, or creeping sideways by less than a millimetre a frame, as a
    /// damped camera settles, which far from the origin moves the eye in steps of its float spacing.</summary>
    public enum StaticPath { FastOrbit, SlowOrbit, Strafe, Creep }

    /// <summary>What one still-field run stored: per measured frame, the display pixels whose state holds the band
    /// mark and the moved mark (<c>temporalStoreLock</c>), and when asked the static travel of every internal texel.
    /// The eye's least distance to a tower's surface over the measured frames, in metres. WarmBandMarks counts the
    /// band marks over the warm frames, whose last one the first measured frame reads back.</summary>
    internal sealed record StaticRun(string Name, int[] BandMarks, int[] MovedMarks, StaticTravel? Travel,
        float NearestTower, bool[] OriginSteps, double Seconds, int WarmBandMarks);

    /// <summary>
    /// The static travel of every internal texel over the measured frames: how far its sample's motion carries it
    /// from where a static point there stood last frame, in internal pixels, as the resolve measures it
    /// (<c>temporalReproject</c>). On still content it is the motion target's and the reprojection's own error. The
    /// excess is the travel less <see cref="TemporalResolveTuning.MovingSurfaceMotionFraction"/> of the texel's screen
    /// motion, the part the band's world-motion test compares with <c>WorldMotionMetres</c> at the texel's depth.
    /// </summary>
    internal sealed class StaticTravel
    {
        public long Texels;
        public float Max, MaxExcess, NearMaxExcess, MaxExcessDepth, MaxExcessScreen, MaxWorldExcess;

        /// <summary>The depth of the texel with the largest excess in metres, and the deepest texel read.</summary>
        public float MaxWorldExcessDepth, MaxDepth;
        public readonly long[] OverExcess = new long[Bounds.Length];
        public readonly float[] FrameMaxExcess = new float[TemporalStaticOrbitRuns.Measured];

        /// <summary>The excess bounds the counts are taken over, in internal pixels.</summary>
        public static readonly float[] Bounds = { 0.005f, 0.01f, 0.02f, 0.05f };

        /// <summary>Depth under which a texel counts as near the camera, in metres.</summary>
        public const float NearDepth = 2f;

        /// <summary>One texel on measured frame <paramref name="frame"/>. <paramref name="metresPerPixel"/> is the
        /// width in metres one internal pixel spans at the texel's depth.</summary>
        public void Add(int frame, float travel, float screen, float depth, float metresPerPixel)
        {
            Texels++;
            Max = MathF.Max(Max, travel);
            float excess = travel - screen * TemporalResolveTuning.MovingSurfaceMotionFraction;
            FrameMaxExcess[frame] = MathF.Max(FrameMaxExcess[frame], excess);
            MaxDepth = MathF.Max(MaxDepth, depth);
            if (excess * metresPerPixel > MaxWorldExcess)
            {
                MaxWorldExcess = excess * metresPerPixel;
                MaxWorldExcessDepth = depth;
            }
            if (excess > MaxExcess) { MaxExcess = excess; MaxExcessDepth = depth; MaxExcessScreen = screen; }
            if (depth < NearDepth) NearMaxExcess = MathF.Max(NearMaxExcess, excess);
            for (int i = 0; i < Bounds.Length; i++) if (excess > Bounds[i]) OverExcess[i]++;
        }

        public override string ToString() =>
            $"{Texels} texels, travel max {Max:0.00000}, excess max {MaxExcess:0.00000} "
            + $"(depth {MaxExcessDepth:0.00} m, "
            + $"screen {MaxExcessScreen:0.00} px), in metres {MaxWorldExcess:0.0000000} at {MaxWorldExcessDepth:0.0} m "
            + $"of {MaxDepth:0.0} m deepest, near {NearDepth} m "
            + $"{NearMaxExcess:0.00000}, over "
            + string.Join(", ", Array.ConvertAll(Bounds, b => b.ToString("0.000")))
            + $": {string.Join(", ", OverExcess)}";
    }

    /// <summary>
    /// The follow camera with no mover over a still field on <see cref="GroundStage"/>, from the boot pitch or the
    /// grazing one, where the ground reaches the camera's far plane: 81 textured crates a metre a side every 3 metres
    /// around the target, and towers 12 metres tall placed so the eye passes 0.6 metres from one, as a camera orbiting
    /// or strafing passes a pillar. The camera orbits its target a few degrees a frame or slowly, or strafes sideways
    /// at a run, near the world origin or far from it, where it crosses a render-origin step (<c>WorldFrame.Grid</c>).
    /// Every surface is still, so no pixel may store the band mark (<c>TemporalResolveTuning.WorldMotionMetres</c>),
    /// and no pixel can drop a band history without one.
    /// </summary>
    public sealed class TemporalStaticOrbitRuns
    {
        public const int Warm = 4, Measured = 20;
        const float FastDegrees = 3f, SlowDegrees = 0.3f, StrafeMetres = 0.15f, CreepMetres = 0.0004f;
        const float TowerInside = 0.9f;

        /// <summary>The follow camera's pitch the field is seen from by default, and the grazing one, in
        /// radians.</summary>
        public const float BootPitch = 0.75f, GrazingPitch = 0.26f;

        readonly Dictionary<(StaticPath, bool, TemporalUpscale, int, bool, float), StaticRun> _runs = new();

        internal double Seconds { get; private set; }

        /// <summary>One run at <paramref name="w"/> by <paramref name="h"/> display, far from the world origin when
        /// <paramref name="far"/>, reading every texel's static travel when <paramref name="travel"/>, seen from
        /// <paramref name="pitch"/> radians.</summary>
        internal StaticRun Run(StaticPath path, bool far, TemporalUpscale preset, int w, int h, bool travel,
            float pitch = BootPitch)
        {
            var key = (path, far, preset, w, travel, pitch);
            if (_runs.TryGetValue(key, out StaticRun? cached)) return cached;
            long started = Stopwatch.GetTimestamp();
            var field = new StillField(w, h, path, far, pitch);
            var band = new int[Measured];
            var moved = new int[Measured];
            int warmBand = 0;
            StaticTravel? stats = travel ? new StaticTravel() : null;
            float nearest = float.MaxValue;
            var steps = new bool[Measured];
            using (var fx = new TemporalFixture(w, h, s => field.Setup(s, preset)))
            {
                Vector3 origin = default;
                for (int n = 0; n < Warm + Measured; n++)
                {
                    fx.Frames(1, field.Draw);
                    bool stepped = n > 0 && fx.Scene.RenderOrigin != origin;
                    origin = fx.Scene.RenderOrigin ?? default;
                    TemporalHistory history = fx.Scene.TemporalHistory;
                    float[] state = TemporalTextureIo.Read(fx.Device, history.Confidence(history.WriteIndex));
                    if (n < Warm)
                    {
                        for (int i = 1; i < state.Length; i += 2)
                            if (state[i] < -2.5f) warmBand++;
                        continue;
                    }
                    steps[n - Warm] = stepped;
                    for (int i = 1; i < state.Length; i += 2)
                    {
                        if (state[i] < -2.5f) band[n - Warm]++;
                        if (state[i] < -0.5f) moved[n - Warm]++;
                    }
                    nearest = MathF.Min(nearest, field.NearestTower(n));
                    if (stats is not null) AddTravel(fx, stats, n - Warm);
                }
            }
            string name = $"{path}, {(far ? "far" : "near")} origin, {preset}, {w}x{h}"
                + (pitch == BootPitch ? "" : $", pitch {pitch}");
            var run = new StaticRun(name, band, moved, stats, nearest, steps,
                Stopwatch.GetElapsedTime(started).TotalSeconds, warmBand);
            Seconds += run.Seconds;
            return _runs[key] = run;
        }

        // Every internal texel's static travel this frame, from the motion target, the stored linear depth and the
        // resolve's uniforms, as temporalReproject computes it (TemporalResolveMath.StaticPreviousUv mirrors it).
        static void AddTravel(TemporalFixture fx, StaticTravel stats, int frame)
        {
            TemporalHistory history = fx.Scene.TemporalHistory;
            TemporalResolveUniforms u = fx.Scene.TemporalResolveRendererForTests!.LastUniforms;
            MotionTargetReadback motion = fx.Scene.ReadMotionTargetForTests();
            float[] depth = TemporalTextureIo.Read(fx.Device, history.PreviousDepth(history.WriteIndex));
            var size = new Vector2(u.Sizes.X, u.Sizes.Y);
            var jitter = new Vector2(u.Jitter.X, u.Jitter.Y);
            for (int y = 0; y < motion.Height; y++)
                for (int x = 0; x < motion.Width; x++)
                {
                    int i = y * motion.Width + x;
                    Vector2 m = motion.Motion[i];
                    if (MathF.Abs(m.X) > TemporalResolveTuning.MotionSentinel) continue;
                    Vector2 sampleUv = (new Vector2(x + 0.5f, y + 0.5f) - jitter) / size;
                    var ndc = new Vector2(sampleUv.X * 2f - 1f, 1f - sampleUv.Y * 2f);
                    if (TemporalResolveMath.StaticPreviousUv(u, ndc, depth[i]) is not Vector2 staticUv) continue;
                    float travel = ((staticUv - (sampleUv - m)) * size).Length();
                    float clipW = u.CurrentDepth.X > 0.5f ? depth[i] : 1f;
                    stats.Add(frame, travel, (m * size).Length(), depth[i],
                        2f * clipW / (u.PreviousProjection.M11 * size.X));
                }
        }

        // The field, its camera path and its towers. Every setup loads the stage's meshes, then the textured box.
        sealed class StillField
        {
            readonly GroundStage _stage;
            readonly StaticPath _path;
            readonly List<Vector3> _towers = new();
            readonly Vector3 _start;
            MeshHandle _crate;

            public StillField(int w, int h, StaticPath path, bool far, float pitch)
            {
                _path = path;
                // Far from the origin the eye crosses a render-origin step on x mid-run, and the orbits cross one on z
                // too, where the local coordinates are at their largest.
                _stage = new GroundStage(w, h, pitch, far ? new Vector3(10000f, 0f, 10000f) : Vector3.Zero);
                // Half way between the anchors 78 and 79 grid cells out, where the nearest one changes, and a creep
                // crosses it within its few millimetres.
                float step = 78.5f * WorldFrame.Grid - 10000f + (path == StaticPath.Creep ? 0.0005f : 0.013f);
                _start = far ? new Vector3(step - Eye(Warm + Measured / 2).X, 0f, step - Eye(Warm + 5).Z)
                    : Vector3.Zero;
                float radius = GroundStage.Distance * MathF.Cos(_stage.Pitch) - TowerInside;
                int[] frames = path switch
                {
                    StaticPath.FastOrbit => new[] { Warm + 2, Warm + 7, Warm + 12, Warm + 17 },
                    StaticPath.SlowOrbit or StaticPath.Creep => new[] { Warm + Measured / 2 },
                    _ => new[] { Warm + 2, Warm + 12 },
                };
                foreach (int frame in frames)
                {
                    if (path is StaticPath.Strafe or StaticPath.Creep)
                        _towers.Add(Target(frame) + Horizontal(GroundStage.Yaw) * radius);
                    else
                        _towers.Add(_start + Horizontal(Yaw(frame)) * radius);
                }
            }

            static Vector3 Horizontal(float yaw) => new(MathF.Sin(yaw), 0f, MathF.Cos(yaw));

            // The eye on frame n relative to the stage's offset.
            Vector3 Eye(int n) => Camera(n).Eye - _stage.Offset;

            float Yaw(int n) => GroundStage.Yaw + YawOffset(n);

            float YawOffset(int n) => _path switch
            {
                StaticPath.FastOrbit => n * FastDegrees * MathF.PI / 180f,
                StaticPath.SlowOrbit => n * SlowDegrees * MathF.PI / 180f,
                _ => 0f,
            };

            Vector3 Target(int n) => _path switch
            {
                StaticPath.Strafe => _start + GroundStage.Right * (StrafeMetres * n),
                StaticPath.Creep => _start + GroundStage.Right * (CreepMetres * n),
                _ => _start,
            };

            FollowCamera3D Camera(int n) => _stage.Camera(Target(n), YawOffset(n));

            public void Setup(Scene3D s, TemporalUpscale preset)
            {
                _stage.Setup(s, AntiAliasing.Temporal, preset, Camera(0));
                s.RenderOrigin = null;
                _crate = s.LoadMesh(MeshPrimitives.Box(1f), s.LoadTexture(TemporalNarrowCrossingRuns.RidgedTexels(),
                    TemporalNarrowCrossingRuns.BoxTexture, TemporalNarrowCrossingRuns.BoxTexture));
            }

            public void Draw(Scene3D s, int n)
            {
                var camera = (FollowCamera3D)s.CameraOverride!;
                camera.Target = _stage.Offset + Target(n);
                camera.Yaw = Yaw(n);
                _stage.DrawGround(s);
                var tint = new Color(0.55f, 0.8f, 0.45f, 1f);
                for (int i = -4; i <= 4; i++)
                    for (int j = -4; j <= 4; j++)
                        s.Draw(_crate,
                            _stage.Standing(_start + new Vector3(i * 3f + 0.4f, 0f, j * 3f + 0.7f), Vector3.One), tint);
                foreach (Vector3 tower in _towers)
                    s.Draw(_crate, _stage.Standing(tower, new Vector3(0.6f, 12f, 0.6f)), tint);
            }

            /// <summary>The eye's distance to the nearest tower's side on frame <paramref name="n"/>, in
            /// metres.</summary>
            public float NearestTower(int n)
            {
                Vector3 eye = Eye(n);
                float nearest = float.MaxValue;
                foreach (Vector3 tower in _towers)
                    nearest = MathF.Min(nearest, new Vector2(eye.X - tower.X, eye.Z - tower.Z).Length() - 0.3f);
                return nearest;
            }
        }
    }
}
