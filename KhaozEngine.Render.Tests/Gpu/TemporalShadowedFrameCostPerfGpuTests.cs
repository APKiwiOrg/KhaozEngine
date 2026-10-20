using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// What the whole lit frame costs with the key light's shadow map on, at a 2560x1440 display, as a measurement on
    /// any real GPU. <see cref="TemporalResolveCostPerfGpuTests"/> and <see cref="TemporalOutlineCostPerfGpuTests"/>
    /// leave shadows off, so the shadow filter never runs there. Here it runs on the ground that fills the view, and
    /// where the renderer carries a shadow signal from the lit pass through its history into the resolve, that
    /// signal holds real shadow edges.
    /// <para>
    /// Every round renders one block of frames in each mode on one scene: anti-aliasing and shadows off at the
    /// internal size as the drift control, FXAA with shadows at the internal size, and the fused and the split
    /// resolve each with shadows and without, in an order that rotates from round to round so drift lands on every
    /// mode alike. The FXAA mode carries the shadow filter without the resolve, the shadowless temporal modes the
    /// resolve without a shadow signal. It prints the backend, the device, every block, the pairs those modes make,
    /// how often the key light's depth pass rendered, the resolve's counts and the share of display pixels the
    /// shadow darkens and that lie near a shadow edge. It asserts only that each block rendered the mode it claims.
    /// No budget is asserted. Virtual devices skip (<c>RequiresRealGpu</c>) and software rasterisers return before
    /// measuring.
    /// </para>
    /// <para>
    /// The scene is a yard: the textured ground under a still follow camera at a game's boot pitch, a grid of crates
    /// and thin posts standing on it, walkers crossing it with their own motion, and a low key light swaying about
    /// a fixed afternoon direction. The camera holds still so the resolve keeps locks on the still ground. The sway
    /// slides the still casters' shadow edges over that ground, and the walkers' shadows move and disocclude it.
    /// Nothing reads the clock or a random source.
    /// </para>
    /// </summary>
    [Collection("GpuTimingSensitive")]
    public sealed class TemporalShadowedFrameCostPerfGpuTests(ITestOutputHelper output)
    {
        // Three warm frames, as the outline fixture keeps: a mode that turns the shadow on or off changes the shadow
        // signal on every shadowed pixel, and the resolve releases and rebuilds on the frames after it.
        const int Rounds = 15, Warm = 3, Block = 12;

        // The frame the path mix is read on, and the luma drop in 8-bit levels that counts a pixel as darkened.
        const int MixFrame = 90;
        const int DarkenedLevels = 4, EdgePixels = 2;

        [GpuFact(RequiresRealGpu = true)]
        public void TheShadowedYardAtQualityIsMeasuredAt1440p() => Run(2560, 1440, TemporalUpscale.Quality);

        [GpuFact(RequiresRealGpu = true)]
        public void TheShadowedYardAtNativeIsMeasuredAt1440p() => Run(2560, 1440, TemporalUpscale.Native);

        // The modes of one round. Each names its anti-aliasing, whether the key shadow is on and, for the temporal
        // modes, the resolve entry it selects.
        enum Mode { Off, FxaaShadowed, FusedShadowed, SplitShadowed, FusedShadowless, SplitShadowless }

        static readonly Mode[] Modes =
            [Mode.Off, Mode.FxaaShadowed, Mode.FusedShadowed, Mode.SplitShadowed, Mode.FusedShadowless,
                Mode.SplitShadowless];

        static bool IsTemporal(Mode mode) => mode is not (Mode.Off or Mode.FxaaShadowed);

        static bool IsShadowed(Mode mode) => mode is Mode.FxaaShadowed or Mode.FusedShadowed or Mode.SplitShadowed;

        // The entry point a mode records. Off and FXAA record none and leave the scene on the fused one, which holds
        // no split targets.
        static TemporalResolveEntry EntryOf(Mode mode) =>
            mode is Mode.SplitShadowed or Mode.SplitShadowless
                ? TemporalResolveEntry.Split
                : TemporalResolveEntry.Fused;

        static string Label(Mode mode) => mode switch
        {
            Mode.FxaaShadowed => "FXAA with shadows",
            Mode.FusedShadowed => "fused with shadows",
            Mode.SplitShadowed => "split with shadows",
            Mode.FusedShadowless => "fused without shadows",
            Mode.SplitShadowless => "split without shadows",
            _ => "off without shadows",
        };

        static double Median(IEnumerable<double> xs)
        {
            double[] s = xs.OrderBy(x => x).ToArray();
            return s.Length % 2 == 1 ? s[s.Length / 2] : 0.5 * (s[s.Length / 2 - 1] + s[s.Length / 2]);
        }

        static string Join(IEnumerable<double> xs) =>
            string.Join(" ", xs.Select(x => x.ToString("0.000", CultureInfo.InvariantCulture)));

        static string F(double x) => x.ToString("0.000", CultureInfo.InvariantCulture);

        static string Percent(long part, long whole) =>
            (100.0 * part / whole).ToString("0.00", CultureInfo.InvariantCulture) + "%";

        void Run(int w, int h, TemporalUpscale preset)
        {
            var yard = new Yard(w, h);
            using var fx = new TemporalFixture(w, h, s => yard.Setup(s, preset));
            string device = fx.Device.Capabilities.DeviceName;
            output.WriteLine($"{fx.Device.Backend} on {device}");
            if (TemporalResolveCostPerfGpuTests.IsSoftwareDevice(device))
            {
                output.WriteLine("  not measured: a software rasteriser says nothing about a GPU's cost");
                return;
            }
            Scene3D scene = fx.Scene;
            Action<Scene3D, int> draw = yard.Draw;
            ShadowResolution shadows = scene.Post.Quality.Shadows.ResolveFor(fx.Device.Capabilities);
            Assert.Equal(ShadowMode.ShadowMap, shadows.Effective);

            // Settle on each entry point with shadows on, which builds the split's objects and the shadow atlas, then
            // read the internal size the resolve reads from and sample its counts once.
            scene.TemporalResolveEntryForTests = TemporalResolveEntry.Split;
            fx.Frames(4, draw);
            scene.TemporalResolveEntryForTests = TemporalResolveEntry.Fused;
            fx.Frames(4, draw);
            scene.RequestTemporalCounts();
            fx.Frames(2, draw);
            TemporalDiagnostics counts = scene.LastTemporalDiagnostics;
            Assert.True(counts.CountsFrameIndex >= 0, "the resolve's counts were never sampled");
            Assert.True(scene.LastShadowPassDiagnostics.Active, "the key light's shadow map never ran");
            int iw = counts.InternalWidth, ih = counts.InternalHeight;
            var renderer = scene.TemporalResolveRendererForTests
                ?? throw new InvalidOperationException("the resolve never ran");
            Assert.NotNull(renderer.SplitResolveForTests);

            var wall = Modes.ToDictionary(m => m, _ => new List<double>());
            var submit = Modes.ToDictionary(m => m, _ => new List<double>());
            int shadowedFrames = 0, depthRenders = 0;
            for (int r = 0; r < Rounds; r++)
                for (int k = 0; k < Modes.Length; k++)
                {
                    Mode mode = Modes[(r + k) % Modes.Length];
                    bool temporal = IsTemporal(mode), shadowed = IsShadowed(mode);
                    Apply(scene, mode, iw, ih);
                    fx.Frames(Warm, draw);
                    double submitted = 0;
                    long t0 = Stopwatch.GetTimestamp();
                    for (int i = 0; i < Block; i++)
                    {
                        fx.Frames(1, draw);
                        submitted += fx.LastSubmitMilliseconds;
                        if (shadowed && scene.LastShadowPassDiagnostics.Rendered) depthRenders++;
                    }
                    wall[mode].Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds / Block);
                    submit[mode].Add(submitted / Block);
                    if (shadowed) shadowedFrames += Block;
                    // Each block rendered the mode it claims, at the one internal size.
                    Assert.Equal(temporal, scene.ResolvedLastRenderForTests);
                    Assert.Equal(shadowed, scene.LastShadowPassDiagnostics.Active);
                    Assert.Equal((iw, ih), (scene.CurrentFrameView.Width, scene.CurrentFrameView.Height));
                    if (temporal) Assert.Equal(EntryOf(mode), renderer.LastEntry);
                }
            scene.TemporalResolveEntryForTests = null;

            double off = Median(wall[Mode.Off]);
            output.WriteLine($"  {w}x{h} {preset}, shadowed yard, internal {iw}x{ih}, shadows {shadows}: "
                + $"{Rounds} rounds of {Block} frames after {Warm}, one block a mode a round, the order rotating");
            output.WriteLine($"  {Label(Mode.Off)} at {iw}x{ih}: {F(off)} ms a frame, blocks {Join(wall[Mode.Off])}");
            foreach (Mode mode in Modes.Skip(1))
            {
                var paired = wall[mode].Zip(wall[Mode.Off], (t, o) => t - o).ToList();
                double submitted = Median(submit[mode]) - Median(submit[Mode.Off]);
                output.WriteLine($"  {Label(mode)}: {F(Median(wall[mode]))} ms a frame, over off "
                    + $"{F(Median(wall[mode]) - off)} ms, paired round differences median {F(Median(paired))} min "
                    + $"{F(paired.Min())} max {F(paired.Max())}, submit and drain difference {F(submitted)}");
                output.WriteLine($"    blocks {Join(wall[mode])}");
            }
            PrintPair("shadows under the fused resolve", wall[Mode.FusedShadowed], wall[Mode.FusedShadowless]);
            PrintPair("shadows under the split resolve", wall[Mode.SplitShadowed], wall[Mode.SplitShadowless]);
            PrintPair("the fused resolve over FXAA with shadows", wall[Mode.FusedShadowed], wall[Mode.FxaaShadowed]);
            PrintPair("the split resolve over FXAA with shadows", wall[Mode.SplitShadowed], wall[Mode.FxaaShadowed]);
            PrintPair("split against fused with shadows", wall[Mode.SplitShadowed], wall[Mode.FusedShadowed]);
            output.WriteLine($"  the key light's depth pass rendered on {depthRenders} of {shadowedFrames} timed "
                + "shadowed frames");
            output.WriteLine($"  the policy picks {TemporalResolvePolicy.Measured} for "
                + $"{fx.Device.Backend} at {preset}, internal {iw}x{ih} "
                + $"{(iw < w || ih < h ? "below" : "at")} the display"
                + (TemporalResolvePolicy.Forced is { } forced ? $", forced to {forced} in this process" : ""));
            output.WriteLine($"  resolve counts with shadows on frame {counts.CountsFrameIndex}: "
                + $"{counts.DisoccludedPixels} disoccluded, {counts.ClippedPixels} clipped, of {w * h} display pixels");
            PrintPathMix(fx, yard, iw, ih);
        }

        static void Apply(Scene3D scene, Mode mode, int iw, int ih)
        {
            bool temporal = IsTemporal(mode);
            scene.Post.Quality.AntiAliasing = temporal ? AntiAliasing.Temporal
                : mode == Mode.FxaaShadowed ? AntiAliasing.Fxaa : AntiAliasing.Off;
            scene.Post.Quality.Shadows.Mode = IsShadowed(mode) ? ShadowMode.ShadowMap : ShadowMode.Off;
            scene.Post.RenderScale = temporal ? RenderScale.MatchViewport : RenderScale.FixedInternal;
            scene.Post.RenderWidth = iw;
            scene.Post.RenderHeight = ih;
            scene.TemporalResolveEntryForTests = EntryOf(mode);
        }

        // A difference of two modes, from their medians and round by round.
        void PrintPair(string what, List<double> a, List<double> b)
        {
            var paired = a.Zip(b, (x, y) => x - y).ToList();
            output.WriteLine($"  {what}: {F(Median(a) - Median(b))} ms from the medians, paired round differences "
                + $"median {F(Median(paired))} min {F(paired.Min())} max {F(paired.Max())}");
        }

        // How much of the display the shadow reaches, from one frame rendered twice with anti-aliasing off at the
        // internal size, shadows on and off. A display pixel is darkened where its luma drops by more than
        // DarkenedLevels with shadows on, and near an edge where a pixel within EdgePixels on either axis is darkened
        // and it is not, or the other way round. Read after the timed rounds, so it cannot disturb them.
        void PrintPathMix(TemporalFixture fx, Yard yard, int iw, int ih)
        {
            Scene3D scene = fx.Scene;
            Action<Scene3D, int> frozen = (s, _) => yard.Draw(s, MixFrame);
            Apply(scene, Mode.FxaaShadowed, iw, ih);
            scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
            fx.Frames(3, frozen);
            byte[] lit = fx.Frame(frozen);
            scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
            fx.Frames(3, frozen);
            byte[] bare = fx.Frame(frozen);
            scene.TemporalResolveEntryForTests = null;

            int w = fx.DisplayWidth, h = fx.DisplayHeight;
            var darkened = new bool[w * h];
            long dark = 0;
            for (int i = 0; i < w * h; i++)
                if (Luma(bare, i) - Luma(lit, i) > DarkenedLevels) { darkened[i] = true; dark++; }
            long near = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (NearEdge(darkened, w, h, x, y)) near++;
            output.WriteLine($"  path mix on frame {MixFrame}, anti-aliasing off at {iw}x{ih}: {yard.Casters} casters, "
                + $"{Percent(dark, (long)w * h)} of display pixels darkened by the key shadow, "
                + $"{Percent(near, (long)w * h)} within {EdgePixels} display pixels of a shadow edge");
        }

        static double Luma(byte[] rgba, int i) =>
            0.2126 * rgba[4 * i] + 0.7152 * rgba[4 * i + 1] + 0.0722 * rgba[4 * i + 2];

        static bool NearEdge(bool[] darkened, int w, int h, int x, int y)
        {
            bool centre = darkened[y * w + x];
            for (int dy = -EdgePixels; dy <= EdgePixels; dy++)
            {
                int yy = y + dy;
                if (yy < 0 || yy >= h) continue;
                for (int dx = -EdgePixels; dx <= EdgePixels; dx++)
                {
                    int xx = x + dx;
                    if (xx >= 0 && xx < w && darkened[yy * w + xx] != centre) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The shadowed yard on the ground stage at a game's boot pitch, the camera held still at the origin. A grid
        /// of <see cref="Columns"/> by <see cref="Rows"/> casters stands on the textured ground, crates of varied
        /// height alternating with thin posts, and <see cref="Walkers"/> walkers cross it side to side at walking
        /// pace, each on its own phase and with its own motion. The ground receives shadows and casts none. The key
        /// light comes down at <see cref="Elevation"/> and sways <see cref="Sway"/> radians either side of
        /// <see cref="Azimuth"/> over <see cref="SwayFrames"/> frames.
        /// </summary>
        sealed class Yard
        {
            const float Pitch = 0.75f;
            const int Columns = 7, Rows = 7, Walkers = 8;
            const float ColumnSpacing = 3f, RowSpacing = 4f, FirstRow = -3f;
            const float WalkReach = 8f, WalkRate = 0.006f;
            const float Elevation = 0.6f, Azimuth = 0.9f, Sway = 0.1f;
            const int SwayFrames = 360;

            static readonly Color GroundTint = new(0.55f, 0.55f, 0.55f, 1f);
            static readonly Color CrateTint = new(0.75f, 0.6f, 0.45f, 1f), PostTint = new(0.4f, 0.35f, 0.3f, 1f);
            static readonly Color WalkerTint = new(0.3f, 0.45f, 0.75f, 1f);

            readonly GroundStage _stage;

            public Yard(int w, int h) => _stage = new GroundStage(w, h, Pitch);

            public int Casters => Columns * Rows + Walkers;

            public void Setup(Scene3D s, TemporalUpscale preset)
            {
                _stage.Setup(s, AntiAliasing.Temporal, preset, _stage.Camera(Vector3.Zero));
                s.Post.AmbientColor = new Color(0.35f, 0.35f, 0.35f, 1f);
                s.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
                s.Post.LightDirection = LightAt(0);
            }

            /// <summary>The direction the key light travels on frame <paramref name="n"/>: down, to the camera's
            /// right and away from it.</summary>
            static Vector3 LightAt(int n)
            {
                float azimuth = Azimuth + Sway * MathF.Sin(2f * MathF.PI * n / SwayFrames);
                Vector3 across = GroundStage.Right * MathF.Cos(azimuth) + GroundStage.Forward * MathF.Sin(azimuth);
                return Vector3.Normalize(across * MathF.Cos(Elevation) - Vector3.UnitY * MathF.Sin(Elevation));
            }

            public void Draw(Scene3D s, int n)
            {
                s.Post.LightDirection = LightAt(n);
                s.Draw(new RigidInstanceDraw(_stage.Ground, Matrix4x4.CreateTranslation(_stage.Offset))
                { Tint = GroundTint, CastsShadows = false });
                for (int i = 0; i < Columns; i++)
                    for (int j = 0; j < Rows; j++)
                    {
                        Vector3 foot = GroundStage.Right * ((i - Columns / 2) * ColumnSpacing)
                            + GroundStage.Forward * (FirstRow + j * RowSpacing);
                        bool post = ((i + j) & 1) == 1;
                        float height = post ? 2.6f : 0.8f + 0.35f * ((3 * i + 5 * j) % 6);
                        var size = post ? new Vector3(0.18f, height, 0.18f) : new Vector3(1.1f, height, 0.9f);
                        s.Draw(new RigidInstanceDraw(_stage.Box, _stage.Standing(foot, size))
                        { Tint = post ? PostTint : CrateTint });
                    }
                for (int k = 0; k < Walkers; k++)
                {
                    float across = WalkReach * MathF.Sin(WalkRate * n + 0.8f * k);
                    Vector3 foot = GroundStage.Right * across
                        + GroundStage.Forward * (FirstRow + 0.5f * RowSpacing + k * 0.75f * RowSpacing);
                    s.Draw(new RigidInstanceDraw(_stage.Box, _stage.Standing(foot, new Vector3(0.5f, 1.7f, 0.4f)))
                    { Tint = WalkerTint, Motion = MotionKey.From((ulong)(300 + k)) });
                }
            }
        }
    }
}
