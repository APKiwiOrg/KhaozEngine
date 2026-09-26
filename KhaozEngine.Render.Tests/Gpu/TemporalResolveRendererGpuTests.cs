using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The temporal resolve against synthetic inputs, one fact per step of TEMPORAL-RESOLVE-UPSCALING-DESIGN section 3,
    /// on an 8 by 8 target at native resolution unless a fact says otherwise. Grey inputs keep YCoCg chroma at zero, so
    /// the expected output follows from the mirrors in <see cref="TemporalResolveMath"/>. The confidence target stores
    /// accumulated weight over <see cref="TemporalResolveTuning.MaxAccumulation"/>. Step 6, the thin feature lock, has its
    /// facts in <see cref="TemporalResolveLockGpuTests"/>.
    /// <para>The uniforms come from <see cref="TemporalResolveMath.BuildUniforms"/> over a real perspective camera, and
    /// the scene depth is the NDC depth that camera gives a wall <see cref="SceneMetres"/> ahead. A surface whose motion
    /// disagrees with the camera's own reprojection of it reads as moving and skips the depth test, so under a still
    /// camera any nonzero motion skips it. Every fact that needs the depth test uses zero motion under a still camera
    /// or the camera-consistent motion of a moving one.</para>
    /// </summary>
    public sealed class TemporalResolveRendererGpuTests
    {
        const int N = 8;
        const float Max = TemporalResolveTuning.MaxAccumulation;
        const float SceneMetres = 2f;
        static readonly Vector3 Eye = new(0f, 1.7f, 10f);
        static readonly Matrix4x4 Projection = Perspective(1f);
        static readonly TemporalViewInput Still = View(Eye, Projection);
        static readonly Vector4 Depth = TemporalResolveMath.DepthParams(Projection);
        static readonly float SceneNdc = NdcDepth(Projection, SceneMetres);
        static float SceneLinear => TemporalResolveMath.LinearDepth(SceneNdc, Depth);

        static float Q(float v) => (float)(Half)v;                    // what a half float texel holds
        static float Ramp(int x) => Q(0.1f + 0.05f * x);
        static float Weighted(float grey) => TemporalResolveMath.ToWeighted(new Vector3(grey)).X;
        static float Unweighted(float y) => y / (1f - y);             // FromWeighted for grey
        static int At(int x, int y) => y * N + x;

        [GpuFact]
        public void A_reset_frame_outputs_the_current_frame_and_stores_linear_depth_with_background_marked()
        {
            using var rig = new Rig();
            rig.BeginFrame();
            rig.Fill(Grey((x, _) => Ramp(x)), Grey((x, _) => Ramp(x)),
                Motion((x, _) => x == 0 ? new Vector2(65504f) : Vector2.Zero));
            rig.Resolve(Uniforms(historyValid: false));

            float[] color = rig.ReadColor(), state = rig.ReadState(), depth = rig.ReadPreviousDepth();
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    Assert.Equal(Ramp(x), color[At(x, y) * 4], 1e-3);
                    Assert.Equal(1f, color[At(x, y) * 4 + 3], 1e-3);
                    Assert.Equal(1f / Max, state[At(x, y) * 2], 1e-3);
                    Assert.Equal(0f, state[At(x, y) * 2 + 1], 1e-3);
                    if (x == 0) Assert.Equal(TemporalResolveTuning.BackgroundLinearDepth, depth[At(x, y)]);
                    else Assert.Equal(SceneLinear, depth[At(x, y)], 4);
                }
        }

        [GpuFact]
        public void A_still_pixel_blends_its_history_by_its_accumulated_weight()
        {
            using var rig = new Rig();
            rig.BeginFrame();
            rig.Fill(Grey((x, _) => Ramp(x)), Grey((x, _) => Ramp(x)), Motion((_, _) => Vector2.Zero));
            rig.FillHistory(Grey((x, _) => Q(Ramp(x) + 0.01f)), State((_, _) => new Vector2(Q(3f / Max), 0f)), SceneLinear);
            rig.Resolve(Uniforms(historyValid: true));
            AssertInteriorBlend(rig, x => Q(Ramp(x) + 0.01f), accumulated: Q(3f / Max) * Max);
        }

        [GpuFact]
        public void A_disoccluded_pixel_drops_its_history()
        {
            using var rig = new Rig();
            rig.BeginFrame();
            rig.Fill(Grey((x, _) => Ramp(x)), Grey((x, _) => Ramp(x)), Motion((_, _) => Vector2.Zero));
            rig.FillHistory(Grey((_, _) => Q(0.9f)), State((_, _) => new Vector2(Q(3f / Max), 0f)), SceneLinear * 0.5f);
            rig.Resolve(Uniforms(historyValid: true));
            AssertInteriorCurrent(rig);
        }

        [GpuFact]
        public void A_reprojection_off_screen_drops_its_history()
        {
            using var rig = new Rig();
            rig.BeginFrame();
            rig.Fill(Grey((x, _) => Ramp(x)), Grey((x, _) => Ramp(x)), Motion((_, _) => new Vector2(-2f, 0f)));
            rig.FillHistory(Grey((_, _) => Q(0.9f)), State((_, _) => new Vector2(Q(3f / Max), 0f)), SceneLinear);
            rig.Resolve(Uniforms(historyValid: true));
            AssertInteriorCurrent(rig);
        }

        [GpuFact]
        public void Background_reprojects_by_rotation_and_keeps_its_history()
        {
            using var rig = new Rig();
            rig.BeginFrame();
            rig.Fill(Grey((x, _) => Ramp(x)), Grey((x, _) => Ramp(x)), Motion((_, _) => new Vector2(65504f)));
            rig.FillHistory(Grey((x, _) => Q(Ramp(x) + 0.01f)), State((_, _) => new Vector2(Q(3f / Max), 0f)),
                TemporalResolveTuning.BackgroundLinearDepth);
            rig.Resolve(Uniforms(historyValid: true));
            AssertInteriorBlend(rig, x => Q(Ramp(x) + 0.01f), accumulated: Q(3f / Max) * Max);
        }

        [GpuTheory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        public void A_history_that_is_not_finite_never_reaches_the_output(float poison)
        {
            using var rig = new Rig();
            rig.BeginFrame();
            rig.Fill(Grey((x, _) => Ramp(x)), Grey((x, _) => Ramp(x)), Motion((_, _) => Vector2.Zero));
            rig.FillHistory(Grey((_, _) => poison), State((_, _) => new Vector2(Q(3f / Max), 0f)), SceneLinear);
            rig.Resolve(Uniforms(historyValid: true));
            AssertInteriorCurrent(rig);
        }

        [GpuFact]
        public void Reactive_content_gives_up_confidence_in_proportion()
        {
            using var rig = new Rig();
            rig.BeginFrame();
            rig.Fill(Grey((x, _) => Ramp(x)), Grey((_, _) => 0f), Motion((_, _) => Vector2.Zero));
            rig.FillHistory(Grey((x, _) => Q(Ramp(x) + 0.01f)), State((_, _) => new Vector2(Q(3f / Max), 0f)), SceneLinear);
            rig.Resolve(Uniforms(historyValid: true));

            float[] state = rig.ReadState();
            for (int x = 2; x < N - 2; x++)
            {
                // The largest difference in the 3x3 is the brightest column, x + 1.
                float reactive = Math.Clamp(Weighted(Ramp(x + 1)) * TemporalResolveTuning.ReactiveGain, 0f, 1f);
                float accumulated = Q(3f / Max) * Max * (1f - reactive * TemporalResolveTuning.ReactiveStrength);
                Assert.Equal((accumulated + 1f) / Max, state[At(x, 4) * 2], 1e-3);
            }
        }

        [GpuFact]
        public void The_reconstruction_weights_each_sample_by_lanczos_on_its_jittered_offset()
        {
            using var rig = new Rig();
            var jitter = new Vector2(0.3f, -0.2f);
            Func<int, int, float> spot = (x, y) => x == 4 && y == 4 ? Q(1f) : Q(0.1f);
            rig.BeginFrame();
            rig.Fill(Grey(spot), Grey(spot), Motion((_, _) => Vector2.Zero));
            rig.Resolve(Uniforms(historyValid: false, jitter));

            float[] color = rig.ReadColor(), state = rig.ReadState();
            for (int y = 3; y <= 5; y++)
                for (int x = 3; x <= 5; x++)
                {
                    var (expected, sampleWeight) = Reconstruct(spot, x, y, jitter);
                    Assert.Equal(expected, color[At(x, y) * 4], 1e-3);
                    Assert.Equal(sampleWeight / Max, state[At(x, y) * 2], 1e-3);
                }
        }

        [GpuFact]
        public void The_nearest_depth_in_the_neighbourhood_carries_the_motion()
        {
            // Background everywhere except one surface texel at (4, 4) moving one pixel right. Display pixel (3, 4) is
            // background itself, but its 3x3 holds the surface, so it takes the surface's motion and reads history one
            // pixel left, at column 2, the one column whose confidence differs. Under a still camera that motion is the
            // surface's own, so the pixel skips the depth test. Pixel (1, 4) has only background around it, reprojects
            // in place, and finds a stored surface nearer than background there, which is a disocclusion.
            using var rig = new Rig();
            rig.BeginFrame();
            rig.Fill(Grey((_, _) => Q(0.1f)), Grey((_, _) => Q(0.1f)),
                Motion((x, y) => x == 4 && y == 4 ? new Vector2(1f / N, 0f) : new Vector2(65504f)));
            rig.FillHistory(Grey((_, _) => Q(0.1f)), State((x, _) => new Vector2(Q((x == 2 ? 5f : 3f) / Max), 0f)),
                (x, _) => x == 2 ? SceneLinear : SceneLinear * 0.5f);
            rig.Resolve(Uniforms(historyValid: true));

            float[] state = rig.ReadState();
            Assert.Equal((Q(5f / Max) * Max + 1f) / Max, state[At(3, 4) * 2], 1e-3);
            Assert.Equal(1f / Max, state[At(1, 4) * 2], 1e-3);
        }

        [GpuFact]
        public void A_fast_sideways_step_keeps_the_depth_test_and_drops_only_the_history_that_was_covered()
        {
            // The camera steps 1.5 m right past a wall 2 m ahead, so the wall moves about 11 pixels left. The motion is
            // what the motion target writes for a static wall, so the resolve reads the wall as static and runs the
            // depth test. Last frame something 1 m away covered the right of the wall, from stored column CoverFrom on.
            // Pixels that reproject there drop their history, and pixels that reproject onto the wall keep it.
            // A y flip between the shader's static reprojection and StaticPreviousUv would put the static UV
            // |1 - 2v| * 16 pixels from where the motion says, at least one pixel on every row here. Every pixel would
            // then read as moving and skip the depth test, and the covered region would keep its history.
            const int W = 48, H = 16, CoverFrom = 32;
            var size = new Vector2(W, H);
            Matrix4x4 projection = Perspective((float)W / H);
            TemporalViewInput now = View(Eye, projection), then = View(Eye - new Vector3(1.5f, 0f, 0f), projection);
            TemporalResolveUniforms u = TemporalResolveMath.BuildUniforms(now, then, Vector2.Zero, W, H, W, H,
                historyValid: true, TemporalJitter.NativePhaseCount);
            float ndc = NdcDepth(projection, SceneMetres);
            float wall = TemporalResolveMath.LinearDepth(ndc, u.CurrentDepth);
            Vector2[] motion = CameraMotion(u, now, then, W, H, wall);
            Assert.True(MathF.Abs(motion[0].X * W) > 8f, $"the step moves the wall {motion[0].X * W} pixels");

            using var rig = new Rig(W, H, W, H, ndc);
            rig.BeginFrame();
            rig.Fill(Grey(W, H, (_, _) => Q(0.3f)), Grey(W, H, (_, _) => Q(0.3f)), Pairs(W, H, (x, y) => motion[y * W + x]));
            rig.FillHistory(Grey(W, H, (_, _) => Q(0.3f)), Pairs(W, H, (_, _) => new Vector2(Q(3f / Max), 0f)),
                (x, _) => x >= CoverFrom ? 1f : wall);
            rig.Resolve(u);

            float[] state = rig.ReadState();
            int covered = 0, kept = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    // The dilated texel of a flat wall is the first of the 3x3, and its previous footprint starts at
                    // floor(previous - 0.5) in stored pixels. Pixels within a quarter pixel of a class edge are skipped.
                    Vector2 m = motion[Math.Max(y - 1, 0) * W + Math.Max(x - 1, 0)];
                    Vector2 previous = (new Vector2(x + 0.5f, y + 0.5f) / size - m) * size;
                    if (previous.X < 0.25f || previous.X > W - 0.25f) continue;
                    float motionPixels = (m * size).Length();
                    float cap = Max - (Max - TemporalResolveTuning.MovingAccumulation)
                        * Math.Clamp(motionPixels / TemporalResolveTuning.MotionAccumulationPixels, 0f, 1f);
                    float confidence = state[(y * W + x) * 2];
                    if (previous.X - 0.5f >= CoverFrom + 0.25f)
                    {
                        Assert.True(MathF.Abs(1f / Max - confidence) < 1e-3f, $"covered ({x}, {y}) kept {confidence * Max}");
                        covered++;
                    }
                    else if (previous.X + 0.5f <= CoverFrom - 0.25f)
                    {
                        float expected = MathF.Min(MathF.Min(Q(3f / Max) * Max, cap) + 1f, cap) / Max;
                        Assert.True(MathF.Abs(expected - confidence) < 1e-3f, $"wall ({x}, {y}) dropped to {confidence * Max}");
                        kept++;
                    }
                }
            Assert.True(covered >= 10 * H && kept >= 10 * H, $"{covered} covered and {kept} kept pixels");
        }

        [GpuFact]
        public void A_glint_in_the_history_never_rings_the_fetch_darker_than_the_surface_beside_it()
        {
            // A 50.0 history texel in a 0.1 surface, and a slow sideways pan that moves the surface a third of a pixel
            // left, so each pixel reads history a third of a pixel right of its centre. Catmull-Rom then weights the
            // texel left of the pixel by about -0.07, so the pixel right of the glint would fetch about -3.6. Scene
            // colour is 0.1 around the glint, which sits in the current frame too, so the variance box of that pixel
            // reaches below zero and would accept the black the ring is floored to. The fetch is clamped to its taps,
            // which keeps it at 0.1.
            // The motion is camera-consistent and the stored depth matches, so the history passes the depth test.
            const int G = 4;
            float bg = Q(0.1f), glint = Q(50f);
            float step = 2f * (1f / (3f * N)) * SceneMetres / Projection.M11;
            TemporalViewInput then = View(Eye - new Vector3(step, 0f, 0f), Projection);
            TemporalResolveUniforms u = TemporalResolveMath.BuildUniforms(Still, then, Vector2.Zero, N, N, N, N,
                historyValid: true, TemporalJitter.NativePhaseCount);
            Vector2[] motion = CameraMotion(u, Still, then, N, N, SceneLinear);
            Assert.InRange(-motion[At(G, G)].X * N, 0.32f, 0.35f);

            using var rig = new Rig();
            Func<int, int, float> surface = (x, y) => x == G && y == G ? glint : bg;
            rig.BeginFrame();
            rig.Fill(Grey(surface), Grey(surface), Motion((x, y) => motion[At(x, y)]));
            rig.FillHistory(Grey(surface), State((_, _) => new Vector2(Q(3f / Max), 0f)), SceneLinear);
            rig.Resolve(u);

            float[] color = rig.ReadColor(), state = rig.ReadState();
            for (int i = 0; i < color.Length; i++)
                Assert.True(color[i] >= 0f && float.IsFinite(color[i]), $"channel {i % 4} of pixel {i / 4} is {color[i]}");
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    if (x == G && y == G) continue;
                    Assert.True(color[At(x, y) * 4] >= bg - 1e-3f, $"({x}, {y}) came out {color[At(x, y) * 4]} beside the glint");
                }
            // The pixel right of the glint read its history, or the fact would prove nothing.
            float cap = Max - (Max - TemporalResolveTuning.MovingAccumulation) * Math.Clamp(
                (motion[At(G, G)] * N).Length() / TemporalResolveTuning.MotionAccumulationPixels, 0f, 1f);
            Assert.Equal(MathF.Min(Q(3f / Max) * Max + 1f, cap) / Max, state[At(G + 1, G) * 2], 1e-3);
        }

        [GpuFact]
        public void An_infinite_scene_colour_leaves_every_output_finite()
        {
            // One texel at +infinity in the scene and in the opaque copy, a reset frame and then a frame that reads it
            // back as history. Held at HalfMax before the luma weighting it stays finite. Unheld it would weight to
            // infinity over infinity, a NaN that the variance box spreads over its 3x3 on a device whose min and max
            // keep NaN. On one whose min and max drop NaN, Metal among them, the output stays finite but the texel
            // vanishes into the surface, so the fact also holds it far brighter than the surface. Held, the weighted
            // round trip caps it near 1e4.
            using var rig = new Rig();
            Func<int, int, float> firefly = (x, y) => x == 4 && y == 4 ? float.PositiveInfinity : Q(0.1f);
            for (int frame = 0; frame < 2; frame++)
            {
                rig.BeginFrame();
                rig.Fill(Grey(firefly), Grey(firefly), Motion((_, _) => Vector2.Zero));
                rig.Resolve(Uniforms(historyValid: frame > 0));
                float[] color = rig.ReadColor(), state = rig.ReadState();
                for (int i = 0; i < color.Length; i++)
                    Assert.True(float.IsFinite(color[i]), $"frame {frame}: colour channel {i % 4} of pixel {i / 4} is {color[i]}");
                for (int i = 0; i < state.Length; i++)
                    Assert.True(float.IsFinite(state[i]), $"frame {frame}: state channel {i % 2} of pixel {i / 2} is {state[i]}");
                Assert.True(color[At(4, 4) * 4] > 100f, $"frame {frame}: the infinite texel came out {color[At(4, 4) * 4]}");
            }
        }

        static void AssertInteriorBlend(Rig rig, Func<int, float> history, float accumulated)
        {
            float[] color = rig.ReadColor(), state = rig.ReadState();
            float weight = 1f / (accumulated + 1f);
            for (int y = 2; y < N - 2; y++)
                for (int x = 2; x < N - 2; x++)
                {
                    float h = Weighted(history(x)), c = Weighted(Ramp(x));
                    Assert.Equal(Unweighted(h + (c - h) * weight), color[At(x, y) * 4], 1e-3);
                    Assert.Equal((accumulated + 1f) / Max, state[At(x, y) * 2], 1e-3);
                }
        }

        static void AssertInteriorCurrent(Rig rig)
        {
            float[] color = rig.ReadColor(), state = rig.ReadState();
            for (int y = 2; y < N - 2; y++)
                for (int x = 2; x < N - 2; x++)
                {
                    Assert.Equal(Ramp(x), color[At(x, y) * 4], 1e-3);
                    Assert.Equal(1f / Max, state[At(x, y) * 2], 1e-3);
                }
        }

        // The shader's step 4 for a grey image: the Lanczos-weighted 3x3 around the texel whose jittered sample lands
        // nearest, clamped to the neighbourhood, and the nearest sample's weight.
        static (float Colour, float SampleWeight) Reconstruct(Func<int, int, float> grey, int px, int py, Vector2 jitter)
        {
            var centre = new Vector2(px + 0.5f, py + 0.5f);
            int cx = Math.Clamp((int)MathF.Floor(centre.X + jitter.X), 0, N - 1);
            int cy = Math.Clamp((int)MathF.Floor(centre.Y + jitter.Y), 0, N - 1);
            float sum = 0f, weight = 0f, min = float.MaxValue, max = float.MinValue, best = 0f;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int tx = Math.Clamp(cx + dx, 0, N - 1), ty = Math.Clamp(cy + dy, 0, N - 1);
                    float w = Weighted(grey(tx, ty));
                    Vector2 d = TemporalResolveMath.UnjitteredSamplePosition(new Vector2(tx, ty), jitter) - centre;
                    float k = TemporalResolveMath.Lanczos2(d.X) * TemporalResolveMath.Lanczos2(d.Y);
                    sum += w * k;
                    weight += k;
                    min = MathF.Min(min, w);
                    max = MathF.Max(max, w);
                    best = MathF.Max(best, Math.Clamp(k, 0f, 1f));
                }
            return (Unweighted(Math.Clamp(sum / MathF.Max(weight, 1e-4f), min, max)), best);
        }

        // The still camera's uniforms: Params.x one over the native jitter cycle, jitter in internal pixels as
        // TemporalJitter.Apply receives it, and a reprojection that returns every static point to itself.
        static TemporalResolveUniforms Uniforms(bool historyValid, Vector2 jitter = default) => TemporalResolveMath.BuildUniforms(
            Still, Still, jitter, N, N, N, N, historyValid, TemporalJitter.NativePhaseCount);

        static Matrix4x4 Perspective(float aspect) => Matrix4x4.CreatePerspectiveFieldOfView(1f, aspect, 0.1f, 100f);

        static TemporalViewInput View(Vector3 eye, Matrix4x4 projection)
        {
            Matrix4x4 view = Matrix4x4.CreateLookAt(eye, eye - Vector3.UnitZ, Vector3.UnitY);
            return new TemporalViewInput(view, projection, view * projection);
        }

        // What the depth target holds for a point this far ahead: the projection's NDC depth.
        static float NdcDepth(in Matrix4x4 projection, float metres)
        {
            Vector4 clip = Vector4.Transform(new Vector4(0f, 0f, -metres, 1f), projection);
            return clip.Z / clip.W;
        }

        static Vector2 Ndc(Vector2 uv) => new(uv.X * 2f - 1f, 1f - uv.Y * 2f);

        // The camera-consistent motion of a static wall at one linear depth, zero jitter: each texel's sample UV minus
        // the resolve's static previous UV of that sample. Each is held to what MotionMath writes for the world point,
        // within 0.05 pixels plus 1/1024 of the motion, so a flip in StaticPreviousUv against the motion target fails.
        static Vector2[] CameraMotion(in TemporalResolveUniforms u, TemporalViewInput now, TemporalViewInput then,
            int width, int height, float linearDepth)
        {
            var size = new Vector2(width, height);
            Assert.True(Matrix4x4.Invert(now.View, out Matrix4x4 inverseView));
            var motion = new Vector2[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    Vector2 uv = TemporalResolveMath.UnjitteredSamplePosition(new Vector2(x, y), Vector2.Zero) / size;
                    Vector2? previous = TemporalResolveMath.StaticPreviousUv(u, Ndc(uv), linearDepth);
                    Assert.True(previous.HasValue, $"texel ({x}, {y}) has no previous position");
                    motion[y * width + x] = uv - previous.Value;

                    Vector2 ndc = Ndc(uv);
                    var viewPoint = new Vector4(ndc.X * linearDepth / now.Projection.M11,
                        ndc.Y * linearDepth / now.Projection.M22, -linearDepth, 1f);
                    Vector4 world = Vector4.Transform(viewPoint, inverseView);
                    Vector2 written = MotionMath.UvMotion(Vector4.Transform(world, now.ViewProjection),
                        Vector4.Transform(world, then.ViewProjection));
                    float apart = ((written - motion[y * width + x]) * size).Length();
                    float tolerance = 0.05f + (written * size).Length() / 1024f;
                    Assert.True(apart <= tolerance, $"texel ({x}, {y}): {apart} px from the written motion");
                }
            return motion;
        }

        static float[] Grey(Func<int, int, float> value) => Grey(N, N, value);

        static float[] Grey(int width, int height, Func<int, int, float> value)
        {
            var v = new float[width * height * 4];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    float g = value(x, y);
                    int i = (y * width + x) * 4;
                    v[i] = g; v[i + 1] = g; v[i + 2] = g; v[i + 3] = 1f;
                }
            return v;
        }

        static float[] Motion(Func<int, int, Vector2> value) => Pairs(N, N, value);
        static float[] State(Func<int, int, Vector2> value) => Pairs(N, N, value);

        static float[] Pairs(int width, int height, Func<int, int, Vector2> value)
        {
            var v = new float[width * height * 2];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    Vector2 p = value(x, y);
                    v[(y * width + x) * 2] = p.X;
                    v[(y * width + x) * 2 + 1] = p.Y;
                }
            return v;
        }

        /// <summary>The shared rig on the 8 by 8 native target, the wall <see cref="SceneMetres"/> ahead in every texel,
        /// unless a fact gives other sizes.</summary>
        sealed class Rig : TemporalResolveRig
        {
            public Rig() : base(N, N, N, N, SceneNdc) { }

            public Rig(int internalWidth, int internalHeight, int displayWidth, int displayHeight, float sceneNdc)
                : base(internalWidth, internalHeight, displayWidth, displayHeight, sceneNdc) { }
        }
    }
}
