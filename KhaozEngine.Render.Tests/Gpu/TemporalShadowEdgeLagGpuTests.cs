using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A key light shadow edge sliding over a still wall under a still camera, the way a turning sun moves a shadow
    /// across a standing character or the ground. The front stage's orthographic camera looks at a wall whose left
    /// half is the textured panel and whose right half is flat grey. A wide slab off the top of the view casts the
    /// shadow: its bottom front edge, tilted <see cref="TiltDegrees"/> on screen, stands <see cref="CasterReach"/> in
    /// front of the wall, so the hold's tall caster model and this caster agree on how far a turn moves the shadow.
    /// The light turns in the vertical plane through the view axis, so that edge's shadow travels across the wall at
    /// a fixed number of display pixels a frame and crosses the view centre at <see cref="AnchorFrame"/>.
    /// <para>
    /// The camera stands <see cref="CameraDistance"/> from its target, so the wall lies in the middle of cascade 0's
    /// view depth slice, and the ortho size makes a cascade 0 texel about one display pixel at 320 by 180, near what
    /// an outdoor framing gives at a large display. Nothing reads the clock or a random source.
    /// </para>
    /// </summary>
    internal sealed class ShadowEdgeStage
    {
        public const float OrthoSize = 1.5f;
        public const float CameraDistance = 6f;
        public const float CasterReach = 12f;
        public const float TiltDegrees = 15f;
        public const float WallZ = -1.95f;

        /// <summary>The tangent of the light's angle below the view axis on the anchor frame, 45 degrees.</summary>
        public const float AnchorTan = 1f;

        public static readonly Color FlatGrey = new(0.45f, 0.5f, 0.55f, 1f);
        public static readonly Color CasterColour = new(0.3f, 0.3f, 0.3f, 1f);

        public readonly FrontStage Stage;

        /// <summary>Display pixels a frame the shadow edge travels, positive where the shadow advances over the lit
        /// wall and negative where it retreats.</summary>
        public readonly float PixelsPerFrame;

        public readonly int AnchorFrame;

        public ShadowEdgeStage(int w, int h, float pixelsPerFrame, int anchorFrame)
        {
            Stage = new FrontStage(w, h, OrthoSize);
            PixelsPerFrame = pixelsPerFrame;
            AnchorFrame = anchorFrame;
        }

        static float Tilt => TiltDegrees * MathF.PI / 180f;

        /// <summary>The tangent of the light's angle below the view axis on frame <paramref name="n"/>. The edge's
        /// shadow lies <c>CasterReach * tan</c> below the caster edge, so a linear tangent moves it linearly.</summary>
        public float TanAt(int n) =>
            AnchorTan + (n - AnchorFrame) * PixelsPerFrame * Stage.PixelWorld / (CasterReach * MathF.Cos(Tilt));

        /// <summary>The direction the key light travels on frame <paramref name="n"/>: down and into the wall.</summary>
        public Vector3 LightAt(int n) => Vector3.Normalize(new Vector3(0f, -TanAt(n), -1f));

        public void Setup(Scene3D s, AntiAliasing aa, TemporalUpscale preset, float holdTexels)
        {
            Stage.Setup(s, aa, preset);
            s.Camera.Distance = CameraDistance;
            s.Post.AmbientColor = new Color(0.3f, 0.3f, 0.3f, 1f);
            s.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
            s.Post.Quality.Shadows.ShadowLightHoldTexels = holdTexels;
            s.Post.LightDirection = LightAt(0);
        }

        public void Draw(Scene3D s, int n)
        {
            s.Post.LightDirection = LightAt(n);
            float half = OrthoSize * 3f;
            s.Draw(Stage.Panel, Matrix4x4.CreateTranslation(-half, 0f, 0f));
            s.Draw(Stage.Box, Matrix4x4.CreateScale(half * 2f, OrthoSize * 3f, 0.1f)
                * Matrix4x4.CreateTranslation(half, 0f, WallZ - 0.05f), FlatGrey);
            s.Draw(Stage.Box, Matrix4x4.CreateScale(40f, 4f, 2f) * Matrix4x4.CreateTranslation(0f, 2f, -1f)
                * Matrix4x4.CreateRotationZ(Tilt)
                * Matrix4x4.CreateTranslation(0f, CasterReach * AnchorTan, WallZ + CasterReach), CasterColour);
        }

        /// <summary>The display column of the seam between the textured and the flat halves.</summary>
        public float SeamX => Stage.W * 0.5f;

        /// <summary>The signed distance in display pixels from the centre of pixel (<paramref name="x"/>,
        /// <paramref name="y"/>) to the shadow edge on frame <paramref name="n"/>, positive on the shadow side.</summary>
        public float EdgeDistance(int n, int x, int y)
        {
            IsoCamera3D camera = Stage.Camera();
            float lineY = CasterReach * (AnchorTan - TanAt(n));
            var u = new Vector3(MathF.Cos(Tilt), MathF.Sin(Tilt), 0f);
            camera.WorldToScreen(new Vector3(0f, lineY, WallZ), Stage.W, Stage.H, out Vector2 p0);
            camera.WorldToScreen(new Vector3(0f, lineY, WallZ) + u, Stage.W, Stage.H, out Vector2 p1);
            Vector2 along = Vector2.Normalize(p1 - p0);
            var normal = new Vector2(along.Y, -along.X);
            if (normal.Y > 0f) normal = -normal;   // up the screen, towards the caster
            return Vector2.Dot(new Vector2(x + 0.5f, y + 0.5f) - p0, normal);
        }

        /// <summary>The stage's camera as the scene builds it, for the cascade fit computed without a scene.</summary>
        public IsoCamera3D Camera()
        {
            IsoCamera3D camera = Stage.Camera();
            camera.Distance = CameraDistance;
            return camera;
        }
    }

    /// <summary>
    /// How far a still temporal resolve lags a shadow edge that a turning key light slides over still surfaces, at
    /// each per-frame travel. The edge travels 0.25 display pixels a frame, about a 60 Hz sun, and 1, about the 15 Hz
    /// unfocused throttle, both ways, at Quality and Native, beside a still light as the control, and at 0.25 and 1 at
    /// Quality with the shadow fit's light hold off. Each run renders <see cref="FirstMeasured"/> frames with the light
    /// turning, then measures <see cref="Measured"/> more against the supersampled reference of the same frames
    /// (<see cref="TemporalAcceptance.ReferenceSequence"/>), rendered with the hold off so it shows the live light.
    /// <para>
    /// The band holds the pixels within <see cref="BandPixels"/> display pixels of the reference edge, the tail those
    /// beyond it on the side the edge came from, out to <see cref="TailPixels"/>, and the control those
    /// <see cref="FarPixels"/> or more ahead of it, which no edge has crossed, each split into the textured and the
    /// flat halves. Per region it prints the mean and 95th percentile absolute luma error, the mean signed error
    /// (bias), the grain of the output and of the reference, a pixel's luma less its 3x3 median, and the grain of the
    /// error image itself, and per half the signed error profile along the travel.
    /// </para>
    /// <para>
    /// It asserts the resolve's release of a still pixel whose shading changed (TEMPORAL-RESOLVE-UPSCALING-DESIGN
    /// amendment 33). The textured tail at 1 display pixel a frame is at most <see cref="TravelTailRatio"/> times the
    /// tail at 0.25, each way and at each preset, so the lag no longer grows with travel. At Quality the textured band
    /// at 1 is at most <see cref="QualityBandLimit"/>. With the light still, the textured band and control stay within
    /// a tenth of the resolve's figures before the release (<see cref="StillBand"/>, <see cref="StillFar"/>), and
    /// their output grain stays at least <see cref="StillGrainShare"/> of the reference's, so texture detail is kept.
    /// </para>
    /// </summary>
    public sealed class TemporalShadowEdgeLagGpuTests(ITestOutputHelper output)
    {
        const int W = 320, H = 180;
        const int FirstMeasured = 80, Measured = 8;
        const float BandPixels = 12f, TailPixels = 48f, FarPixels = 32f, SeamMargin = 4f;
        const int Border = 2, ProfileBins = 16, ProfileBin = 4, ProfileAhead = 16;

        // Before the release, Metal measured the Quality textured tail at 1 display pixel a frame 10 and 8 times the
        // tail at 0.25, and the band at 1 at 0.111 and 0.106, advancing and retreating. Native's tail was 9 and 7 times.
        // The release measures 0.74 and 0.87 times at Quality, 1.26 and 1.55 at Native, and the Quality band at 1 0.027
        // and 0.033. The tail bound keeps the lag's growth with travel within 1.75 times, against 7 to 10 before, with
        // room for another backend over Native's 1.55 retreating. Forcing every hold to zero, the most the locks could
        // give back, measured the Quality band at 0.031, and the band bound is that plus about a tenth.
        const double TravelTailRatio = 1.75, QualityBandLimit = 0.035, StillGrainShare = 0.9;

        // The still light's textured band and control mean errors before the release, plus a tenth: Quality 0.0058
        // and 0.0052, Native 0.0044 and 0.0039. Forcing every hold to zero raised Quality's to 0.0072 and 0.0064.
        static readonly Dictionary<TemporalUpscale, double> StillBand = new()
        {
            [TemporalUpscale.Quality] = 0.0058 * 1.1, [TemporalUpscale.Native] = 0.0044 * 1.1,
        };

        static readonly Dictionary<TemporalUpscale, double> StillFar = new()
        {
            [TemporalUpscale.Quality] = 0.0052 * 1.1, [TemporalUpscale.Native] = 0.0039 * 1.1,
        };

        readonly record struct Case(string Name, TemporalUpscale Preset, float PixelsPerFrame, float Hold);

        static IEnumerable<Case> Cases()
        {
            foreach (TemporalUpscale preset in new[] { TemporalUpscale.Quality, TemporalUpscale.Native })
            {
                string p = preset == TemporalUpscale.Quality ? "Quality" : "Native";
                yield return new($"{p} still", preset, 0f, 1f);
                foreach (float v in new[] { 0.25f, 1f })
                {
                    yield return new($"{p} {v:0.00} px advancing", preset, v, 1f);
                    yield return new($"{p} {v:0.00} px retreating", preset, -v, 1f);
                }
            }
            foreach (float v in new[] { 0.25f, 1f })
            {
                yield return new($"Quality {v:0.00} px advancing, hold 0", TemporalUpscale.Quality, v, 0f);
                yield return new($"Quality {v:0.00} px retreating, hold 0", TemporalUpscale.Quality, -v, 0f);
            }
        }

        [GpuFact]
        public void Shadow_edge_lag_metrics_under_a_turning_key_light_at_each_travel_rate()
        {
            PrintGeometry();
            var references = new Dictionary<float, byte[][]>();
            var measured = new Dictionary<Case, Dictionary<string, RegionStats>>();
            foreach (Case c in Cases())
            {
                var stage = new ShadowEdgeStage(W, H, c.PixelsPerFrame, FirstMeasured);
                if (!references.TryGetValue(c.PixelsPerFrame, out byte[][]? reference))
                {
                    reference = TemporalAcceptance.ReferenceSequence(W, H,
                        s => stage.Setup(s, AntiAliasing.Temporal, TemporalUpscale.Native, holdTexels: 0f), stage.Draw,
                        FirstMeasured, Measured);
                    references[c.PixelsPerFrame] = reference;
                }

                var frames = new byte[Measured][];
                int refits = 0;
                using (var fx = new TemporalFixture(W, H,
                    s => stage.Setup(s, AntiAliasing.Temporal, c.Preset, c.Hold)))
                {
                    fx.Frames(FirstMeasured, stage.Draw);
                    for (int i = 0; i < Measured; i++)
                    {
                        frames[i] = fx.Frame(stage.Draw);
                        if (fx.Scene.LastShadowPassDiagnostics.Rendered) refits++;
                    }
                }

                var m = new EdgeMeasure(stage, frames, reference, FirstMeasured);

                output.WriteLine($"{c.Name}: shadow re-records in the {Measured} measured frames {refits}, reference "
                    + $"edge contrast {m.ReferenceContrast:0.000} (flat lit minus shadow luma), drawn edge "
                    + $"{m.EdgeOffset:0.00} px off the analytic line");
                output.WriteLine("  region          pixels   mean     p95      bias     grain out  grain ref  "
                    + "err grain");
                foreach ((string name, RegionStats r) in m.Regions)
                {
                    output.WriteLine($"  {name,-15} {r.Count,6}   {r.Mean:0.0000}   {r.P95:0.0000}   {r.Bias,7:0.0000}  "
                        + $"{r.GrainOut:0.0000}     {r.GrainRef:0.0000}     {r.ErrorGrain:0.0000}");
                    Assert.True(r.Count > 0, $"{c.Name}: the {name} region holds no pixels");
                }
                string bins = $"{ProfileBin} px bins of travel from {ProfileAhead - ProfileBins * ProfileBin} (behind) to "
                    + $"{ProfileAhead} (ahead)";
                output.WriteLine($"  textured signed error x1000, {bins}: {m.Profile(flat: false)}");
                output.WriteLine($"  flat signed error x1000, {bins}: {m.Profile(flat: true)}");
                Assert.True(m.ReferenceContrast > 0.05,
                    $"{c.Name}: the reference shows no shadow edge, contrast {m.ReferenceContrast:0.000}");
                measured[c] = new Dictionary<string, RegionStats>();
                foreach ((string name, RegionStats r) in m.Regions) measured[c][name] = r;
            }
            AssertReleased(measured);
        }

        // The acceptance bounds, checked after every case has printed, so a failure still shows the whole table.
        static void AssertReleased(Dictionary<Case, Dictionary<string, RegionStats>> measured)
        {
            var failures = new List<string>();
            foreach ((Case c, Dictionary<string, RegionStats> r) in measured)
            {
                if (c.Hold != 1f) continue;
                if (c.PixelsPerFrame == 0f)
                {
                    Check(r["band textured"].Mean <= StillBand[c.Preset],
                        $"{c.Name}: textured band {r["band textured"].Mean:0.0000} above {StillBand[c.Preset]:0.0000}");
                    Check(r["far textured"].Mean <= StillFar[c.Preset],
                        $"{c.Name}: textured control {r["far textured"].Mean:0.0000} above {StillFar[c.Preset]:0.0000}");
                    foreach (string region in new[] { "band textured", "far textured" })
                        Check(r[region].GrainOut >= StillGrainShare * r[region].GrainRef,
                            $"{c.Name}: {region} grain {r[region].GrainOut:0.0000} below {StillGrainShare} of the "
                            + $"reference's {r[region].GrainRef:0.0000}");
                    continue;
                }
                if (MathF.Abs(c.PixelsPerFrame) != 1f) continue;
                Case slow = c with { Name = c.Name.Replace("1.00", "0.25"), PixelsPerFrame = c.PixelsPerFrame / 4f };
                double tail = r["tail textured"].Mean, slowTail = measured[slow]["tail textured"].Mean;
                Check(tail <= TravelTailRatio * slowTail,
                    $"{c.Name}: textured tail {tail:0.0000} above {TravelTailRatio} times the 0.25 px tail {slowTail:0.0000}");
                if (c.Preset == TemporalUpscale.Quality)
                    Check(r["band textured"].Mean <= QualityBandLimit,
                        $"{c.Name}: textured band {r["band textured"].Mean:0.0000} above {QualityBandLimit}");
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

            void Check(bool ok, string failure)
            {
                if (!ok) failures.Add(failure);
            }
        }

        // The cascade the wall samples, its texel in display pixels and the hold's step as display pixels of edge travel.
        void PrintGeometry()
        {
            var stage = new ShadowEdgeStage(W, H, 1f, FirstMeasured);
            IsoCamera3D camera = stage.Camera();
            Vector3 light = stage.LightAt(FirstMeasured);
            var defaults = new ShadowSettings();
            int res = defaults.ShadowMapResolution;
            Span<Vector3> corners = stackalloc Vector3[8];
            Assert.True(ShadowMapMath.FrustumCornersWorld(camera.ViewProjection, corners));
            Vector3 eye = camera.Eye, fwd = camera.Forward;
            Vector3 nearC = (corners[0] + corners[1] + corners[2] + corners[3]) * 0.25f;
            Vector3 farC = (corners[4] + corners[5] + corners[6] + corners[7]) * 0.25f;
            float camNear = Vector3.Dot(nearC - eye, fwd), camFar = Vector3.Dot(farC - eye, fwd);
            float range = MathF.Max(camFar - camNear, 1e-3f);
            int count = defaults.ResolvedCascadeCount;
            Span<float> splits = stackalloc float[ShadowSettings.MaxCascades];
            ShadowMapMath.FillCascadeSplits(splits, count, defaults.ShadowNearDistance, defaults.ResolvedMaxDistance);
            var mats = new Matrix4x4[count];
            var radii = new float[count];
            float prev = camNear;
            for (int i = 0; i < count; i++)
            {
                float d = Math.Clamp(splits[i], camNear, camFar);
                ShadowMapMath.SliceBoundingSphere(corners, (prev - camNear) / range, (d - camNear) / range,
                    out Vector3 center, out radii[i]);
                mats[i] = ShadowMapMath.BuildLightViewProj(light, center, radii[i], res);
                prev = MathF.Max(d, prev);
            }
            int sel = ShadowMapMath.SelectCascade(mats, count, new Vector3(0f, 0f, ShadowEdgeStage.WallZ));
            Assert.True(sel >= 0, "the wall lies outside every cascade");
            float pixel = stage.Stage.PixelWorld;
            float texel = ShadowMapMath.TexelWorldSize(radii[sel], res);
            float minRadius = float.MaxValue;
            foreach (float r in radii) minRadius = MathF.Min(minRadius, r);
            float threshold = ShadowLightHold.ThresholdRadians(minRadius, res, ShadowLightHold.SinElevation(light),
                defaults.ShadowLightHoldCasterHeight, defaults.ShadowLightHoldTexels);
            float tan = stage.TanAt(FirstMeasured);
            float holdTravel = threshold * (1f + tan * tan) * ShadowEdgeStage.CasterReach
                * MathF.Cos(ShadowEdgeStage.TiltDegrees * MathF.PI / 180f) / pixel;
            output.WriteLine($"{W}x{H}, wall in cascade {sel} of {count}, radius {radii[sel]:0.00}, shadow texel "
                + $"{texel / pixel:0.00} display px. Hold step {holdTravel:0.00} display px of edge travel. Band "
                + $"{BandPixels} px, tail to {TailPixels} px behind, control {FarPixels} px ahead and beyond, frames {FirstMeasured} to "
                + $"{FirstMeasured + Measured - 1}.");
        }

        /// <summary>One region's pooled values over every measured frame.</summary>
        sealed class RegionStats
        {
            readonly List<float> _abs = new();
            double _signed, _grainOut, _grainRef, _errorGrain;

            public void Add(float error, float grainOut, float grainRef, float errorGrain)
            {
                _abs.Add(MathF.Abs(error));
                _signed += error;
                _grainOut += grainOut;
                _grainRef += grainRef;
                _errorGrain += errorGrain;
            }

            public int Count => _abs.Count;
            double Per(double sum) => Count == 0 ? 0 : sum / Count;
            public double Mean { get { double s = 0; foreach (float a in _abs) s += a; return Per(s); } }
            public double Bias => Per(_signed);
            public double GrainOut => Per(_grainOut);
            public double GrainRef => Per(_grainRef);
            public double ErrorGrain => Per(_errorGrain);

            public double P95
            {
                get
                {
                    if (Count == 0) return 0;
                    var sorted = _abs.ToArray();
                    Array.Sort(sorted);
                    return sorted[Math.Min(sorted.Length - 1, (int)(0.95 * sorted.Length))];
                }
            }
        }

        /// <summary>
        /// The band, tail and control metrics of a run's measured frames against their references. The edge is where
        /// the reference's flat half crosses midway between its lit and its shadow luma, found from the analytic line
        /// first, since normal offset and filtering move the drawn edge off it. Distances are then taken along the
        /// travel, <c>t</c>, positive on the side the edge moves towards, which it has not crossed, and negative on
        /// the side it came from. The band is <c>|t|</c> up to <see cref="BandPixels"/>, the tail <c>t</c> from
        /// <see cref="TailPixels"/> to minus <see cref="BandPixels"/>, and the control <c>t</c> from
        /// <see cref="FarPixels"/> on, which no edge has crossed. A still light counts as advancing.
        /// </summary>
        sealed class EdgeMeasure
        {
            readonly RegionStats _bandTextured = new(), _bandFlat = new(), _tailTextured = new(), _tailFlat = new(),
                _farTextured = new(), _farFlat = new();
            readonly double[] _profileSum = new double[2 * ProfileBins];
            readonly int[] _profileCount = new int[2 * ProfileBins];
            readonly ShadowEdgeStage _stage;
            readonly float _sign;

            public EdgeMeasure(ShadowEdgeStage stage, IReadOnlyList<byte[]> frames, IReadOnlyList<byte[]> references,
                int firstFrame)
            {
                _stage = stage;
                _sign = stage.PixelsPerFrame < 0f ? -1f : 1f;
                FindEdge(references, firstFrame);
                var window = new float[9];
                for (int i = 0; i < frames.Count; i++) Add(firstFrame + i, frames[i], references[i], window);
            }

            /// <summary>The drawn edge's offset from the analytic line in display pixels, towards the shadow.</summary>
            public float EdgeOffset { get; private set; }

            /// <summary>The reference's flat lit luma 14 to 30 pixels from the edge less its shadow luma there.</summary>
            public double ReferenceContrast { get; private set; }

            public IEnumerable<(string, RegionStats)> Regions => new[]
            {
                ("band textured", _bandTextured), ("band flat", _bandFlat), ("tail textured", _tailTextured),
                ("tail flat", _tailFlat), ("far textured", _farTextured), ("far flat", _farFlat),
            };

            public string Profile(bool flat)
            {
                var parts = new string[ProfileBins];
                for (int i = 0; i < ProfileBins; i++)
                {
                    int k = (flat ? ProfileBins : 0) + i;
                    parts[i] = _profileCount[k] == 0 ? "-" : $"{1000 * _profileSum[k] / _profileCount[k]:0}";
                }
                return string.Join(" ", parts);
            }

            static float[] LumaOf(byte[] rgba)
            {
                var l = new float[W * H];
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++) l[y * W + x] = TemporalAcceptance.Luma(rgba, W, x, y);
                return l;
            }

            bool Flat(int x, out bool inside)
            {
                float fromSeam = x + 0.5f - _stage.SeamX;
                inside = MathF.Abs(fromSeam) > SeamMargin;
                return fromSeam > 0f;
            }

            void FindEdge(IReadOnlyList<byte[]> references, int firstFrame)
            {
                const float Reach = 8f, Step = 0.25f;
                int bins = (int)(2 * Reach / Step);
                var sum = new double[bins];
                var count = new int[bins];
                double lit = 0, shadow = 0;
                int litCount = 0, shadowCount = 0;
                for (int i = 0; i < references.Count; i++)
                {
                    float[] lr = LumaOf(references[i]);
                    for (int y = Border; y < H - Border; y++)
                        for (int x = Border; x < W - Border; x++)
                        {
                            if (!Flat(x, out bool inside) || !inside) continue;
                            float d = _stage.EdgeDistance(firstFrame + i, x, y);
                            float ad = MathF.Abs(d);
                            if (ad >= 14f && ad <= 30f)
                            {
                                if (d < 0f) { lit += lr[y * W + x]; litCount++; }
                                else { shadow += lr[y * W + x]; shadowCount++; }
                            }
                            if (d >= -Reach && d < Reach)
                            {
                                int b = (int)((d + Reach) / Step);
                                sum[b] += lr[y * W + x];
                                count[b]++;
                            }
                        }
                }
                double litMean = litCount == 0 ? 0 : lit / litCount;
                double shadowMean = shadowCount == 0 ? 0 : shadow / shadowCount;
                ReferenceContrast = litMean - shadowMean;
                double mid = 0.5 * (litMean + shadowMean);
                EdgeOffset = 0f;
                for (int b = 0; b < bins; b++)
                    if (count[b] > 0 && sum[b] / count[b] < mid)
                    {
                        EdgeOffset = -Reach + (b + 0.5f) * Step;
                        break;
                    }
            }

            void Add(int n, byte[] frame, byte[] reference, float[] window)
            {
                float[] lo = LumaOf(frame), lr = LumaOf(reference);
                var e = new float[W * H];
                for (int p = 0; p < e.Length; p++) e[p] = lo[p] - lr[p];
                for (int y = Border; y < H - Border; y++)
                    for (int x = Border; x < W - Border; x++)
                    {
                        bool flat = Flat(x, out bool inside);
                        if (!inside) continue;
                        float t = -_sign * (_stage.EdgeDistance(n, x, y) - EdgeOffset);
                        int p = y * W + x;
                        if (t >= -ProfileBins * ProfileBin + ProfileAhead && t < ProfileAhead)
                        {
                            int bin = (int)MathF.Floor((t + ProfileBins * ProfileBin - ProfileAhead) / ProfileBin);
                            _profileSum[(flat ? ProfileBins : 0) + bin] += e[p];
                            _profileCount[(flat ? ProfileBins : 0) + bin]++;
                        }
                        RegionStats? region = MathF.Abs(t) <= BandPixels ? (flat ? _bandFlat : _bandTextured)
                            : t < 0f && t >= -TailPixels ? (flat ? _tailFlat : _tailTextured)
                            : t >= FarPixels ? (flat ? _farFlat : _farTextured) : null;
                        if (region == null) continue;
                        region.Add(e[p], MathF.Abs(lo[p] - Median(lo, x, y, window)),
                            MathF.Abs(lr[p] - Median(lr, x, y, window)), MathF.Abs(e[p] - Median(e, x, y, window)));
                    }
            }

            static float Median(float[] image, int x, int y, float[] window)
            {
                int k = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++) window[k++] = image[(y + dy) * W + x + dx];
                Array.Sort(window);
                return window[4];
            }
        }
    }
}
