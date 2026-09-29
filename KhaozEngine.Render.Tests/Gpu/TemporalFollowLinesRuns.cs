using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>A follow walk with still thin lines on its ground, for <see cref="TemporalFollowLinesRuns"/>.</summary>
    internal interface IFollowLinesWalk
    {
        int W { get; }
        int H { get; }

        /// <summary>Display pixels two internal texels span, the band's reach beside the followed box.</summary>
        int BandPixels { get; }

        void Setup(Scene3D s, TemporalUpscale preset);

        /// <summary>Frame <paramref name="n"/>: the ground, the lines when <paramref name="lines"/>, the keyed box
        /// when <paramref name="box"/>.</summary>
        void Draw(Scene3D s, int n, bool box, bool lines);

        /// <summary>The pixels of frame <paramref name="last"/> whose ground point the box hid on frame
        /// <paramref name="n"/>.</summary>
        bool[] Hidden(int n, int last);

        /// <summary>Whether the box hid the ground point pixel (<paramref name="x"/>, <paramref name="y"/>) of frame
        /// <paramref name="last"/> shows on frame <paramref name="n"/>, or that of a pixel beside it.</summary>
        bool HidNear(int n, int last, int x, int y);

        /// <summary>Which line a pixel of frame <paramref name="n"/> lies near: 0 for none, 1 across the box's path,
        /// 2 along its edge.</summary>
        int LineAt(int n, int x, int y);
    }

    /// <summary>One line's measure: its energy share of the walk without the box on each measured frame and the worst,
    /// and its pixels' trail two or more frames after the box's band left them, those the box never hid within the
    /// trail's frames (<see cref="Trail"/>) and those it hid and revealed (<see cref="Revealed"/>).</summary>
    internal sealed record LineMeasure(double[] Shares, double Worst, int WorstFrame, TrailTally Trail,
        TrailTally Revealed);

    /// <summary>
    /// Still lines narrower than a texel on the ground a followed box walks over: one across its trailing path, which
    /// the box covers and uncovers, and one along its edge, which stays beside it (TEMPORAL-RESOLVE-UPSCALING-DESIGN
    /// amendment 23, the band). Three runs of the same camera path: with the box and the lines, the lines alone, and
    /// the ground alone. A line's pixels on a frame are those the lines alone lift over the ground alone by more than
    /// <see cref="CoverageStep"/> within <see cref="NearPixels"/> of the box and past the band's reach from it, two
    /// internal texels and a pixel, where the box's own edge no longer reaches through the reconstruction, and that the
    /// box did not hide within the trail's frames, where the line restarts from nothing as any revealed feature does.
    /// Its energy is their luma over the ground alone, and its share that energy with the box over that without it, as
    /// <see cref="TemporalPassingBoxGpuTests"/> reads a still line. Its trail is read on the last frame over its pixels
    /// that the box, grown by the band's reach, left two to eight frames before, by the excess over a floor that
    /// restarts the lines alone on the frame it left them, as the follow walks read the ground.
    /// </summary>
    public sealed class TemporalFollowLinesRuns
    {
        public const int StillFrames = 16, Last = StillFrames + 23, NearPixels = 24;
        const float CoverageStep = 0.02f;

        readonly Dictionary<string, (LineMeasure Across, LineMeasure Along)> _runs = new();
        readonly Dictionary<string, byte[][]> _ground = new();

        internal double Seconds { get; private set; }

        internal (LineMeasure Across, LineMeasure Along) Run(string key, IFollowLinesWalk walk, TemporalUpscale preset,
            string groundKey)
        {
            if (_runs.TryGetValue(key, out var cached)) return cached;
            long started = Stopwatch.GetTimestamp();
            int w = walk.W, h = walk.H, count = Last - StillFrames + 1;
            byte[][] with = Sequence(walk, preset, true, true), lines = Sequence(walk, preset, false, true);
            if (!_ground.TryGetValue(groundKey, out byte[][]? ground))
                _ground[groundKey] = ground = Sequence(walk, preset, false, false);
            var floors = new byte[TemporalGhostingRuns.TrailFrames][];
            for (int age = TemporalGhostingRuns.FirstAge; age < floors.Length; age++)
            {
                using var fx = new TemporalFixture(w, h, s => walk.Setup(s, preset));
                fx.SkipFrames(Last - age + 1);
                fx.Frames(age - 1, (s, n) => walk.Draw(s, n, false, true));
                floors[age] = fx.Frame((s, n) => walk.Draw(s, n, false, true));
            }
            var hidden = new bool[count][];
            for (int t = 0; t < count; t++) hidden[t] = walk.Hidden(StillFrames + t, StillFrames + t);
            var result = (Measure(1), Measure(2));
            Seconds += Stopwatch.GetElapsedTime(started).TotalSeconds;
            return _runs[key] = result;

            LineMeasure Measure(int line)
            {
                var shares = new double[count];
                for (int t = 0; t < count; t++)
                {
                    int n = StillFrames + t;
                    bool[] near = PerspectiveWalk.Grow(hidden[t], w, h, NearPixels);
                    bool[] band = PerspectiveWalk.Grow(hidden[t], w, h, walk.BandPixels + 1);
                    double energy = 0, still = 0;
                    for (int i = 0; i < w * h; i++)
                    {
                        if (!near[i] || band[i] || walk.LineAt(n, i % w, i / w) != line) continue;
                        float lifted = Luma(lines[t], i) - Luma(ground[t], i);
                        if (lifted <= CoverageStep || Revealed(n, i % w, i / w)) continue;
                        still += lifted;
                        energy += Luma(with[t], i) - Luma(ground[t], i);
                    }
                    shares[t] = still > 0 ? energy / still : double.NaN;
                }
                int worst = -1;
                for (int t = 0; t < count; t++)
                    if (!double.IsNaN(shares[t]) && (worst < 0 || shares[t] < shares[worst])) worst = t;
                return new LineMeasure(shares, worst < 0 ? double.NaN : shares[worst], StillFrames + worst,
                    Trail(line, false), Trail(line, true));
            }

            TrailTally Trail(int line, bool revealed)
            {
                // Now and one frame ago as the follow walks exclude them, then the band's reach around the box.
                var left = new bool[TemporalGhostingRuns.TrailFrames][];
                for (int k = 0; k < left.Length; k++)
                    left[k] = PerspectiveWalk.Grow(walk.Hidden(Last - k, Last), w, h,
                        k == 0 ? 2 : k == 1 ? 1 : walk.BandPixels + 1);
                var tally = new TrailTally();
                for (int i = 0; i < w * h; i++)
                {
                    if (left[0][i] || left[1][i] || walk.LineAt(Last, i % w, i / w) != line) continue;
                    if (Luma(lines[count - 1], i) - Luma(ground[count - 1], i) <= CoverageStep) continue;
                    int age = 0;
                    for (int k = TemporalGhostingRuns.FirstAge; k < left.Length && age == 0; k++)
                        if (left[k][i]) age = k;
                    if (age == 0 || Revealed(Last, i % w, i / w) != revealed) continue;
                    int x = i % w, y = i / w;
                    tally.Add(TemporalAcceptance.Difference(with[count - 1], lines[count - 1], w, x, y,
                            PixelDifference.MaxChannel),
                        TemporalAcceptance.Difference(with[count - 1], lines[count - 1], w, x, y, PixelDifference.Luma),
                        TemporalAcceptance.Difference(floors[age], lines[count - 1], w, x, y,
                            PixelDifference.MaxChannel),
                        TemporalGhostingRuns.Tolerance);
                }
                return tally;
            }

            float Luma(byte[] frame, int i) => TemporalAcceptance.Luma(frame, w, i % w, i / w);

            // Whether the box hid the line there within the trail's frames, so the line restarted from nothing when
            // the box uncovered it, as any disoccluded feature does. Their trail is read apart.
            bool Revealed(int n, int x, int y)
            {
                for (int k = 0; k < TemporalGhostingRuns.TrailFrames; k++)
                    if (walk.HidNear(n - k, n, x, y)) return true;
                return false;
            }
        }

        static byte[][] Sequence(IFollowLinesWalk walk, TemporalUpscale preset, bool box, bool lines) =>
            TemporalAcceptance.Sequence(walk.W, walk.H, s => walk.Setup(s, preset),
                (s, n) => walk.Draw(s, n, box, lines), StillFrames, Last - StillFrames + 1);
    }

    /// <summary>
    /// The orthographic follow walk of <see cref="TemporalFollowCameraRuns"/> with two still white lines on the
    /// textured wall, halfway between the wall and the box: one upright, which the box's trailing edge uncovers
    /// <see cref="Uncovered"/> frames before the last, and one level, an internal texel above the box's top edge.
    /// </summary>
    internal sealed class OrthoFollowLines : IFollowLinesWalk
    {
        /// <summary>Frames before the last on which the box's trailing edge passes the line across its path.</summary>
        public const int Uncovered = 6;

        const float LeftPixels = 150f, SizePixels = 30f, LineZ = -1f;
        readonly FrontStage _stage;
        readonly float _speed, _factor, _width, _acrossX, _alongY;
        MeshHandle _box;

        public OrthoFollowLines(int w, int h, float speed, TemporalUpscale preset, float lineTexels)
        {
            _stage = new FrontStage(w, h, 4.5f);
            _speed = speed;
            _factor = TemporalSettings.DisplayOverInternal(preset);
            float pw = _stage.PixelWorld;
            _width = lineTexels * _factor * pw;
            _acrossX = (LeftPixels - 0.5f - w / 2f) * pw + Walked(TemporalFollowLinesRuns.Last - Uncovered);
            _alongY = (SizePixels / 2f + _factor) * pw + _width / 2f;
        }

        public int W => _stage.W;
        public int H => _stage.H;
        public int BandPixels => (int)MathF.Ceiling(2f * _factor);

        float Walked(int n) => Math.Max(0, n - TemporalFollowLinesRuns.StillFrames) * _speed * _stage.PixelWorld;

        Vector3 Centre(int n) =>
            new((LeftPixels + SizePixels / 2f - W / 2f) * _stage.PixelWorld + Walked(n), 0f, 0f);

        public void Setup(Scene3D s, TemporalUpscale preset)
        {
            _stage.Setup(s, AntiAliasing.Temporal, preset);
            s.Post.Hdr.Enabled = false;
            _box = s.LoadMesh(MeshPrimitives.Box(1f), s.LoadTexture(TemporalNarrowCrossingRuns.RidgedTexels(),
                TemporalNarrowCrossingRuns.BoxTexture, TemporalNarrowCrossingRuns.BoxTexture));
        }

        public void Draw(Scene3D s, int n, bool box, bool lines)
        {
            s.Camera.Target = new Vector3(Walked(n), 0f, 0f);
            _stage.TexturedWall(s);
            if (lines)
            {
                var white = new Color(1f, 1f, 1f, 1f);
                s.Draw(_stage.Box, Matrix4x4.CreateScale(_width, 3.6f, 0.05f)
                    * Matrix4x4.CreateTranslation(_acrossX, 0f, LineZ), white);
                s.Draw(_stage.Box, Matrix4x4.CreateScale(40f, _width, 0.05f)
                    * Matrix4x4.CreateTranslation(0f, _alongY, LineZ), white);
            }
            if (!box) return;
            float size = SizePixels * _stage.PixelWorld;
            s.Draw(new RigidInstanceDraw(_box, Matrix4x4.CreateScale(size, size, 0.5f)
                * Matrix4x4.CreateTranslation(Centre(n)))
            { Tint = CrossingScene.Tint, Motion = MotionKey.From(61) });
        }

        public bool[] Hidden(int n, int last)
        {
            float size = SizePixels * _stage.PixelWorld;
            Vector3 c = Centre(n), half = new(size / 2f, size / 2f, 0.25f);
            PixelRect r = TemporalAcceptance.Footprint(_stage.Camera(Walked(last)), c - half, c + half, W, H);
            var mask = new bool[W * H];
            for (int y = r.Y0; y < r.Y1; y++)
                for (int x = r.X0; x < r.X1; x++) mask[y * W + x] = true;
            return mask;
        }

        public bool HidNear(int n, int last, int x, int y)
        {
            float size = SizePixels * _stage.PixelWorld;
            Vector3 c = Centre(n), half = new(size / 2f, size / 2f, 0.25f);
            return TemporalAcceptance.Footprint(_stage.Camera(Walked(last)), c - half, c + half, W, H).Inflate(1)
                .Contains(x, y);
        }

        public int LineAt(int n, int x, int y)
        {
            float pw = _stage.PixelWorld;
            float acrossPixel = (_acrossX - Walked(n)) / pw + W / 2f, alongPixel = H / 2f - _alongY / pw;
            if (MathF.Abs(x + 0.5f - acrossPixel) < 2f) return 1;
            return MathF.Abs(y + 0.5f - alongPixel) < 2f ? 2 : 0;
        }
    }

    /// <summary>
    /// The perspective follow walk of <see cref="TemporalPerspectiveFollowRuns"/>, away from the camera at the boot
    /// pitch, with still white blades standing on the ground, <see cref="BladeHeight"/> metres tall and as wide and
    /// deep as the line's share of an internal texel where the box stands: a row across its path where it stands
    /// <see cref="OrthoFollowLines.Uncovered"/> frames before the last, which it walks through, and a row along its
    /// right edge an internal texel out, which it walks past.
    /// </summary>
    internal sealed class PerspectiveFollowLines : IFollowLinesWalk
    {
        public const float BladeHeight = 0.6f;
        const float AcrossSpacing = 0.25f, AlongSpacing = 0.3f;
        readonly PerspectiveWalk _walk;
        readonly float _factor, _width;
        readonly List<(Vector3 Foot, int Line)> _blades = new();
        readonly Dictionary<int, int[]> _labels = new();

        public PerspectiveFollowLines(int w, int h, float speed, TemporalUpscale preset, float lineTexels)
        {
            _walk = new PerspectiveWalk(w, h, TemporalPerspectiveFollowGpuTests.BootPitch, FollowHeading.Away, speed,
                TemporalFollowLinesRuns.StillFrames, TemporalPerspectiveFollowRuns.AvatarSize);
            _factor = TemporalSettings.DisplayOverInternal(preset);
            float metres = (_walk.Camera(0).Eye - _walk.Foot(0)).Length() * MathF.Tan(GroundStage.FieldOfView / 2f)
                * 2f * _factor / h;
            TexelMetres = metres;
            _width = lineTexels * metres;
            Vector3 across = _walk.Foot(TemporalFollowLinesRuns.Last - OrthoFollowLines.Uncovered);
            for (int i = -5; i <= 5; i++) _blades.Add((across + GroundStage.Right * (i * AcrossSpacing), 1));
            float side = TemporalPerspectiveFollowRuns.AvatarSize.X / 2f + metres + _width / 2f;
            float end = (_walk.Foot(TemporalFollowLinesRuns.Last) - _walk.Foot(0)).Length() + 2f;
            for (float d = -1f; d <= end; d += AlongSpacing)
                _blades.Add((GroundStage.Forward * d + GroundStage.Right * side, 2));
        }

        public int W => _walk.Stage.W;
        public int H => _walk.Stage.H;
        public int BandPixels => (int)MathF.Ceiling(2f * _factor);

        /// <summary>The walk the box and the camera follow.</summary>
        public PerspectiveWalk Walk => _walk;

        /// <summary>The metres an internal texel spans where the box stands.</summary>
        public float TexelMetres { get; }

        public void Setup(Scene3D s, TemporalUpscale preset) => _walk.Setup(s, preset);

        public void Draw(Scene3D s, int n, bool box, bool lines)
        {
            _walk.Background(s, n);
            if (lines)
                foreach (var (foot, _) in _blades)
                    s.Draw(_walk.Stage.Box, _walk.Stage.Standing(foot, new Vector3(_width, BladeHeight, _width)),
                        new Color(1f, 1f, 1f, 1f));
            if (box) _walk.DrawBox(s, n, 67);
        }

        public bool[] Hidden(int n, int last) => _walk.Hidden(n, last);

        public bool HidNear(int n, int last, int x, int y)
        {
            for (int yy = Math.Max(0, y - 1); yy <= Math.Min(H - 1, y + 1); yy++)
                for (int xx = Math.Max(0, x - 1); xx <= Math.Min(W - 1, x + 1); xx++)
                    if (_walk.Hides(n, last, xx, yy)) return true;
            return false;
        }

        public int LineAt(int n, int x, int y)
        {
            if (!_labels.TryGetValue(n, out int[]? labels)) _labels[n] = labels = Label(n);
            return labels[y * W + x];
        }

        // Each blade's line near the segment from its foot to its top on frame n's screen, within two pixels.
        int[] Label(int n)
        {
            FollowCamera3D camera = _walk.Camera(n);
            var labels = new int[W * H];
            foreach (var (foot, line) in _blades)
            {
                if (!camera.WorldToScreen(foot, W, H, out Vector2 a)
                    || !camera.WorldToScreen(foot + new Vector3(0f, BladeHeight, 0f), W, H, out Vector2 b)) continue;
                int steps = (int)MathF.Ceiling((b - a).Length()) + 1;
                for (int k = 0; k <= steps; k++)
                {
                    Vector2 p = Vector2.Lerp(a, b, (float)k / steps);
                    for (int yy = (int)MathF.Floor(p.Y - 2f); yy <= (int)(p.Y + 2f); yy++)
                        for (int xx = (int)MathF.Floor(p.X - 2f); xx <= (int)(p.X + 2f); xx++)
                            if (xx >= 0 && xx < W && yy >= 0 && yy < H) labels[yy * W + xx] = line;
                }
            }
            return labels;
        }
    }
}
