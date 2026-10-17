using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The shadow edge stage (<see cref="ShadowEdgeStage"/>) with the pixel under the edge moving. Its key light, its
    /// caster and its light hold are the stage's own. <see cref="MovingShadeKind.Shadow"/> draws the stage as it is.
    /// <see cref="MovingShadeKind.Pan"/> draws a taller half textured, half flat wall and pans the camera up the screen
    /// at <see cref="Motion"/> display pixels a frame over the shadow the still light casts on it, so the ground moves
    /// on screen while the shadow stays fixed on it, as under a follow camera. <see cref="MovingShadeKind.Object"/>
    /// draws the stage's wall and, <see cref="ObjectDepth"/> in front of it, two keyed strips side by side, the left
    /// textured as the wall's panel and the right flat grey, that translate up the screen at <see cref="Motion"/>
    /// display pixels a frame into the shadow under a still camera centred on the edge where it falls on them. They
    /// stand in for a character crossing a shadow edge. They are long enough to cover the view from top to bottom on
    /// every frame, so every measured pixel reprojects to a place on the strips, and only their sides show an edge.
    /// <para>
    /// Every motion is anchored at <see cref="ShadowEdgeStage.AnchorFrame"/>, where the camera, the strips and the
    /// light stand where the still stage has them. Nothing reads the clock or a random source.
    /// </para>
    /// </summary>
    internal sealed class MovingShadeStage
    {
        /// <summary>How far the strips' front faces stand in front of the wall, in world units, 36 display pixels at
        /// 320 by 180, a relative depth step past the resolve's disocclusion tolerance.</summary>
        public const float ObjectDepth = 0.3f;

        /// <summary>Display pixels from the seam between the strips to each strip's outer side.</summary>
        public const float StripPixels = 140f;

        /// <summary>Display pixels left out beside each strip's outer side and beside the seam between the
        /// halves.</summary>
        public const float SideMargin = 6f, SeamMargin = 4f;

        public const ulong TexturedKey = 31, FlatKey = 32;

        /// <summary>Half the height of the pan's wall in world units, which keeps it in view at 8 display pixels a
        /// frame over every frame to <see cref="ShadowEdgeStage.AnchorFrame"/> and 16 after it.</summary>
        public const float PanWallHalfHeight = 7f;

        public readonly ShadowEdgeStage Edge;
        public readonly MovingShadeKind Kind;

        /// <summary>Display pixels a frame the camera pans or the strips move, up the screen.</summary>
        public readonly float Motion;

        MeshHandle _panWall, _strip;

        public MovingShadeStage(int w, int h, MovingShadeKind kind, float shadowPixelsPerFrame, float motion,
            int anchorFrame)
        {
            Edge = new ShadowEdgeStage(w, h, shadowPixelsPerFrame, anchorFrame);
            Kind = kind;
            Motion = motion;
        }

        FrontStage Stage => Edge.Stage;

        float Travel(int n) => (n - Edge.AnchorFrame) * Motion * Stage.PixelWorld;

        /// <summary>The camera's target on frame <paramref name="n"/>.</summary>
        public Vector3 CameraTarget(int n) => Kind switch
        {
            MovingShadeKind.Pan => new Vector3(0f, Travel(n), 0f),
            MovingShadeKind.Object => new Vector3(0f, ObjectDepth * ShadowEdgeStage.AnchorTan, 0f),
            _ => Vector3.Zero,
        };

        /// <summary>The plane the measured receiver's front face lies in.</summary>
        public float ReceiverZ =>
            Kind == MovingShadeKind.Object ? ShadowEdgeStage.WallZ + ObjectDepth : ShadowEdgeStage.WallZ;

        public void Setup(Scene3D s, AntiAliasing aa, TemporalUpscale preset, float holdTexels)
        {
            Edge.Setup(s, aa, preset, holdTexels);
            s.Camera.Target = CameraTarget(0);
            Scene3D.TextureHandle noise = s.LoadTexture(FrontStage.NoiseTexture(), FrontStage.TextureSize,
                FrontStage.TextureSize);
            float half = ShadowEdgeStage.OrthoSize * 3f;
            _panWall = s.LoadMesh(TexturedQuad(-half, 0f, -PanWallHalfHeight, PanWallHalfHeight,
                ShadowEdgeStage.WallZ), noise);
            float strip = StripPixels * Stage.PixelWorld;
            float reach = StripReach();
            _strip = s.LoadMesh(TexturedQuad(-strip, 0f, -reach, reach, 0f), noise);
        }

        // Half the strips' length: half the view, the travel over every frame to the anchor and 16 after, and a margin.
        float StripReach() => (Stage.H * 0.5f + (Edge.AnchorFrame + 16) * MathF.Abs(Motion) + 32f) * Stage.PixelWorld;

        public void Draw(Scene3D s, int n)
        {
            s.Camera.Target = CameraTarget(n);
            if (Kind != MovingShadeKind.Pan)
            {
                Edge.Draw(s, n);
                if (Kind == MovingShadeKind.Object) DrawStrips(s, n);
                return;
            }
            s.Post.LightDirection = Edge.LightAt(n);
            float half = ShadowEdgeStage.OrthoSize * 3f;
            s.Draw(_panWall, Matrix4x4.Identity);
            s.Draw(Stage.Box, Matrix4x4.CreateScale(half * 2f, PanWallHalfHeight * 2f, 0.1f)
                * Matrix4x4.CreateTranslation(half, 0f, ShadowEdgeStage.WallZ - 0.05f), ShadowEdgeStage.FlatGrey);
            Edge.DrawCaster(s);
        }

        void DrawStrips(Scene3D s, int n)
        {
            float z = ShadowEdgeStage.WallZ + ObjectDepth, y = ObjectDepth * ShadowEdgeStage.AnchorTan + Travel(n);
            float strip = StripPixels * Stage.PixelWorld;
            s.Draw(new RigidInstanceDraw(_strip, Matrix4x4.CreateTranslation(0f, y, z))
            {
                Motion = MotionKey.From(TexturedKey),
            });
            s.Draw(new RigidInstanceDraw(Stage.Box, Matrix4x4.CreateScale(strip, 2f * StripReach(), 0.1f)
                * Matrix4x4.CreateTranslation(strip * 0.5f, y, z - 0.05f))
            {
                Tint = ShadowEdgeStage.FlatGrey,
                Motion = MotionKey.From(FlatKey),
            });
        }

        /// <summary>The measured receiver: the wall, or the strips less <see cref="SideMargin"/> beside their outer
        /// sides, less <see cref="SeamMargin"/> beside the seam between the halves either way.</summary>
        public ShadowEdgeSample Receiver()
        {
            int frame = int.MinValue;
            Vector2 point = default, normal = default;
            float seam = Stage.W * 0.5f;
            return (int n, int x, int y, out bool flat, out float edgeDistance) =>
            {
                float fromSeam = x + 0.5f - seam;
                flat = fromSeam > 0f;
                edgeDistance = 0f;
                bool inside = MathF.Abs(fromSeam) > SeamMargin
                    && (Kind != MovingShadeKind.Object || MathF.Abs(fromSeam) < StripPixels - SideMargin);
                if (!inside) return false;
                if (n != frame)
                {
                    (point, normal) = Edge.EdgeLine(n, CameraTarget(n), ReceiverZ);
                    frame = n;
                }
                edgeDistance = Vector2.Dot(new Vector2(x + 0.5f, y + 0.5f) - point, normal);
                return true;
            };
        }

        // A quad facing the camera at z over x0 to x1 and y0 to y1, both windings, with the wall panel's texel size.
        GltfMesh TexturedQuad(float x0, float x1, float y0, float y1, float z)
        {
            float period = FrontStage.TexelPixels * Stage.PixelWorld * FrontStage.TextureSize;
            var normal = Vector3.UnitZ;
            var colour = Vector4.One;
            ModelVertex V(float px, float py) => new(new Vector3(px, py, z), normal, colour,
                new Vector2(px / period, -py / period));
            var v = new[] { V(x0, y0), V(x1, y0), V(x1, y1), V(x0, y1) };
            return new GltfMesh(v, new ushort[] { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 });
        }
    }

    /// <summary>What moves under <see cref="MovingShadeStage"/>'s shadow edge.</summary>
    internal enum MovingShadeKind
    {
        /// <summary>Only the light, over the still wall under a still camera.</summary>
        Shadow,

        /// <summary>The camera, panning over the shadow a still light casts on a still wall.</summary>
        Pan,

        /// <summary>Keyed strips in front of the wall under a still camera, with the light still or turning.</summary>
        Object,
    }

    /// <summary>
    /// How far a temporal resolve lags a shadow edge where the pixel under it moves, or the edge travels several
    /// display pixels a frame, at Quality and Native, over a textured and a flat receiver. The release of a still
    /// pixel whose shading changed (TEMPORAL-RESOLVE-UPSCALING-DESIGN amendment 33) reads a pixel as still below
    /// <c>LockMotionStartPixels</c>, and <c>TemporalShadowEdgeLagGpuTests</c> measures it at 0.25 and 1 display
    /// pixel a frame of edge travel. This measures the rest, on <see cref="MovingShadeStage"/>:
    /// <list type="bullet">
    /// <item><b>S</b>: still camera, the edge travelling 2, 4 and 8 display pixels a frame, advancing and
    /// retreating.</item>
    /// <item><b>C</b>: the camera panning 1, 2, 4 and 8 display pixels a frame across the shadow a still light casts,
    /// which reprojection should explain. It is the control.</item>
    /// <item><b>M</b>: keyed strips moving 0.5, 1, 2, 4 and 8 display pixels a frame into a still shadow under a still
    /// camera.</item>
    /// <item><b>MS</b>: the strips moving 1 and 2 display pixels a frame into the shadow while the light turns so its
    /// edge advances about 1 display pixel a frame over them, the two adding.</item>
    /// </list>
    /// Each run renders <see cref="FirstMeasured"/> frames, then measures <see cref="Measured"/> more against the
    /// supersampled reference of the same frames (<see cref="TemporalAcceptance.ReferenceSequence"/>), rendered with
    /// the light hold off. The regions, band, tail and control, textured and flat, are
    /// <see cref="ShadowEdgeMeasure"/>'s, with the travel taken over the receiver: for C the edge advances on screen,
    /// and for M and MS it advances over the strips. It asserts only that each scene shows its shadow edge and fills
    /// every region. A fix states its bounds against the printed figures.
    /// </summary>
    public sealed class TemporalMovingShadeLagGpuTests(ITestOutputHelper output)
    {
        const int W = 320, H = 180;
        const int FirstMeasured = 80, Measured = 8;

        readonly record struct Case(string Name, TemporalUpscale Preset, MovingShadeKind Kind, float Shadow,
            float Motion)
        {
            /// <summary>Display pixels a frame the edge moves over the receiver, about.</summary>
            public float RelativeTravel => Math.Abs(Shadow) + (Kind == MovingShadeKind.Shadow ? 0f : Motion);

            public float Sign => Shadow < 0f ? -1f : 1f;
        }

        static IEnumerable<Case> Cases()
        {
            foreach (TemporalUpscale preset in new[] { TemporalUpscale.Quality, TemporalUpscale.Native })
            {
                string p = preset == TemporalUpscale.Quality ? "Quality" : "Native";
                foreach (float v in new[] { 2f, 4f, 8f })
                {
                    yield return new($"{p} S {v:0.0} px advancing", preset, MovingShadeKind.Shadow, v, 0f);
                    yield return new($"{p} S {v:0.0} px retreating", preset, MovingShadeKind.Shadow, -v, 0f);
                }
                foreach (float v in new[] { 1f, 2f, 4f, 8f })
                    yield return new($"{p} C pan {v:0.0} px", preset, MovingShadeKind.Pan, 0f, v);
                foreach (float v in new[] { 0.5f, 1f, 2f, 4f, 8f })
                    yield return new($"{p} M object {v:0.0} px", preset, MovingShadeKind.Object, 0f, v);
                foreach (float v in new[] { 1f, 2f })
                    yield return new($"{p} MS object {v:0.0} px, shadow 1.0 px", preset, MovingShadeKind.Object, 1f, v);
            }
        }

        [GpuFact]
        public void Shadow_edge_lag_metrics_on_moving_pixels_and_fast_shadows()
        {
            output.WriteLine($"{W}x{H}, device class {TemporalDeviceClass.Name}. Band {ShadowEdgeMeasure.BandPixels} px, "
                + $"tail to {ShadowEdgeMeasure.TailPixels} px behind, control {ShadowEdgeMeasure.FarPixels} px ahead and "
                + $"beyond, frames {FirstMeasured} to {FirstMeasured + Measured - 1}.");
            var references = new Dictionary<(MovingShadeKind, float, float), byte[][]>();
            var summary = new List<string>();
            foreach (Case c in Cases())
            {
                var stage = new MovingShadeStage(W, H, c.Kind, c.Shadow, c.Motion, FirstMeasured);
                if (!references.TryGetValue((c.Kind, c.Shadow, c.Motion), out byte[][]? reference))
                {
                    reference = TemporalAcceptance.ReferenceSequence(W, H,
                        s => stage.Setup(s, AntiAliasing.Temporal, TemporalUpscale.Native, holdTexels: 0f), stage.Draw,
                        FirstMeasured, Measured);
                    references[(c.Kind, c.Shadow, c.Motion)] = reference;
                }

                byte[][] frames = TemporalAcceptance.Sequence(W, H,
                    s => stage.Setup(s, AntiAliasing.Temporal, c.Preset, holdTexels: 1f), stage.Draw, FirstMeasured,
                    Measured);
                var m = new ShadowEdgeMeasure(W, H, stage.Receiver(), c.Sign, frames, reference, FirstMeasured);

                output.WriteLine($"{c.Name}: edge travel over the receiver about {c.RelativeTravel:0.0} px a frame, "
                    + $"reference edge contrast {m.ReferenceContrast:0.000}, drawn edge {m.EdgeOffset:0.00} px off the "
                    + "analytic line");
                output.WriteLine("  region          pixels   mean     p95      bias     grain out  grain ref  err grain  "
                    + "change   chg ref  chg err");
                var r = new Dictionary<string, ShadowEdgeRegion>();
                foreach ((string name, ShadowEdgeRegion region) in m.Regions)
                {
                    output.WriteLine($"  {name,-15} {region.Count,6}   {region.Mean:0.0000}   {region.P95:0.0000}   "
                        + $"{region.Bias,7:0.0000}  {region.GrainOut:0.0000}     {region.GrainRef:0.0000}     "
                        + $"{region.ErrorGrain:0.0000}     {region.Change:0.0000}   {region.ChangeRef:0.0000}   "
                        + $"{region.ChangeError:0.0000}");
                    Assert.True(region.Count > 0, $"{c.Name}: the {name} region holds no pixels");
                    r[name] = region;
                }
                int span = ShadowEdgeMeasure.ProfileBins * ShadowEdgeMeasure.ProfileBin;
                string bins = $"{ShadowEdgeMeasure.ProfileBin} px bins of travel from "
                    + $"{ShadowEdgeMeasure.ProfileAhead - span} (behind) to {ShadowEdgeMeasure.ProfileAhead} (ahead)";
                output.WriteLine($"  textured signed error x1000, {bins}: {m.Profile(flat: false)}");
                output.WriteLine($"  flat signed error x1000, {bins}: {m.Profile(flat: true)}");
                Assert.True(m.ReferenceContrast > 0.05,
                    $"{c.Name}: the reference shows no shadow edge, contrast {m.ReferenceContrast:0.000}");
                summary.Add($"{c.Name,-38} {r["band textured"].Mean:0.0000} {r["tail textured"].Mean:0.0000} "
                    + $"{r["far textured"].Mean:0.0000}  {r["band flat"].Mean:0.0000} {r["tail flat"].Mean:0.0000} "
                    + $"{r["far flat"].Mean:0.0000}  {r["band textured"].GrainOut:0.0000} "
                    + $"{r["band textured"].GrainRef:0.0000} {r["band textured"].ErrorGrain:0.0000}  "
                    + $"{r["band flat"].ErrorGrain:0.0000}  {r["band textured"].Change:0.0000} "
                    + $"{r["band textured"].ChangeRef:0.0000} {r["band textured"].ChangeError:0.0000}  "
                    + $"{r["band flat"].Change:0.0000} {r["band flat"].ChangeRef:0.0000} "
                    + $"{r["band flat"].ChangeError:0.0000}");
            }
            output.WriteLine("");
            output.WriteLine("summary, mean absolute luma error, then the textured band's grain out, ref and error, "
                + "the flat band's error grain, and each band's frame-to-frame change out, ref and error");
            output.WriteLine($"{"case",-38} bandT  tailT  farT    bandF  tailF  farF    grOutT grRefT grErrT  grErrF  "
                + "chgT   chRefT chErrT  chgF   chRefF chErrF");
            foreach (string line in summary) output.WriteLine(line);
        }
    }
}
