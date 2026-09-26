using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The temporal resolve (TEMPORAL-RESOLVE-UPSCALING-DESIGN section 3) where it is easiest to get wrong: a fast camera
    /// step that must keep the disocclusion test, a glint the history fetch must not ring around, an infinite scene
    /// colour, and a moving pixel whose clip must narrow. Same rig and camera as
    /// <see cref="TemporalResolveRendererGpuTests"/>, through <see cref="TemporalResolveGpuFacts"/>.
    /// </summary>
    public sealed class TemporalResolveEdgeCaseGpuTests : TemporalResolveGpuFacts
    {
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
            // The stored weight of 12 is above the cap step 8 applies at 11 pixels of motion, about 10.2, so the pixels
            // that keep their history come out at the cap.
            const int W = 48, H = 16, CoverFrom = 32;
            const float Stored = 12f;
            var size = new Vector2(W, H);
            Matrix4x4 projection = Perspective((float)W / H);
            TemporalViewInput now = View(Eye, projection), then = View(Eye - new Vector3(1.5f, 0f, 0f), projection);
            TemporalResolveUniforms u = TemporalResolveMath.BuildUniforms(now, then, Vector2.Zero, W, H, W, H,
                historyValid: true);
            float ndc = NdcDepth(projection, SceneMetres);
            float wall = TemporalResolveMath.LinearDepth(ndc, u.CurrentDepth);
            Vector2[] motion = CameraMotion(u, now, then, W, H, wall);
            Assert.True(MathF.Abs(motion[0].X * W) > 8f, $"the step moves the wall {motion[0].X * W} pixels");

            using var rig = new Rig(W, H, W, H, ndc);
            rig.BeginFrame();
            rig.Fill(Grey(W, H, (_, _) => Q(0.3f)), Grey(W, H, (_, _) => Q(0.3f)), Pairs(W, H, (x, y) => motion[y * W + x]));
            rig.FillHistory(Grey(W, H, (_, _) => Q(0.3f)), Pairs(W, H, (_, _) => new Vector2(Q(Stored / Max), 0f)),
                (x, _) => x >= CoverFrom ? 1f : wall);
            rig.Resolve(u);

            float[] state = rig.ReadState();
            int covered = 0, kept = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    // The dilated texel of a flat wall is the first of the 3x3, and its previous footprint starts at
                    // floor(previous - 0.5) in stored pixels. Pixels within a quarter pixel of a class edge are skipped.
                    // The motion is read back as the half float the motion target holds.
                    Vector2 m = motion[Math.Max(y - 1, 0) * W + Math.Max(x - 1, 0)];
                    m = new Vector2(Q(m.X), Q(m.Y));
                    Vector2 previous = (new Vector2(x + 0.5f, y + 0.5f) / size - m) * size;
                    if (previous.X < 0.25f || previous.X > W - 0.25f) continue;
                    float cap = MotionCap((m * size).Length());
                    float confidence = state[(y * W + x) * 2];
                    if (previous.X - 0.5f >= CoverFrom + 0.25f)
                    {
                        Assert.True(MathF.Abs(1f / Max - confidence) < 1e-3f, $"covered ({x}, {y}) kept {confidence * Max}");
                        covered++;
                    }
                    else if (previous.X + 0.5f <= CoverFrom - 0.25f)
                    {
                        Assert.True(Q(Stored / Max) * Max > cap + 1f, $"the cap {cap} does not bind");
                        Assert.True(MathF.Abs(cap / Max - confidence) < 1e-3f,
                            $"wall ({x}, {y}) came out at {confidence * Max}, the cap is {cap}");
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
            // reaches below zero and would accept the black the ring is floored to. The fetch is clamped to the history
            // texels of its bilinear footprint, both 0.1 there, which keeps it at 0.1.
            // The motion is camera-consistent and the stored depth matches, so the history passes the depth test.
            const int G = 4;
            float bg = Q(0.1f), glint = Q(50f);
            float step = 2f * (1f / (3f * N)) * SceneMetres / Projection.M11;
            TemporalViewInput then = View(Eye - new Vector3(step, 0f, 0f), Projection);
            TemporalResolveUniforms u = TemporalResolveMath.BuildUniforms(Still, then, Vector2.Zero, N, N, N, N,
                historyValid: true);
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
            float cap = MotionCap((motion[At(G, G)] * N).Length());
            Assert.Equal(MathF.Min(Q(3f / Max) * Max + 1f, cap) / Max, state[At(G + 1, G) * 2], 1e-3);
        }

        [GpuFact]
        public void An_infinite_scene_colour_leaves_every_output_finite()
        {
            // One texel at +infinity in the scene and in the opaque copy. On a reset frame the current weight is 1, so
            // the output is this frame's reconstruction alone, whatever the lock and the clip would do with a history.
            // Held at HalfMax before the luma weighting, the texel stays finite and the weighted round trip caps it near
            // 1e4. Unheld it would weight to infinity over infinity, a NaN that the variance box spreads over its 3x3 on
            // a device whose min and max keep NaN. On one whose min and max drop NaN, Metal among them, the output stays
            // finite but the texel vanishes into the surface, so the fact also holds it far brighter than the surface.
            // The next frame reads that texel back as history and must stay finite too.
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
                if (frame == 0)
                    Assert.True(color[At(4, 4) * 4] > 100f, $"the reset frame shows the infinite texel at {color[At(4, 4) * 4]}");
            }
        }

        [GpuFact]
        public void A_moving_pixel_clips_its_history_to_the_narrower_moving_box()
        {
            // Step 5's gamma narrows from GammaStill to GammaMoving over GammaMotionPixels of display motion. Every
            // surface here moves exactly that far right, a whole number of pixels, so each pixel reads one history texel
            // exactly. That texel sits 1.2 standard deviations of the pixel's current 3x3 above its mean, inside the
            // still box and outside the moving one, so the history is clipped to the moving box's edge. Under a still
            // camera the motion skips the depth test, which this fact does not need, and the current ramp holds no
            // ridge, so no lock takes part.
            const int Shift = (int)TemporalResolveTuning.GammaMotionPixels;
            static float Ramp(int x) => Q(0.1f + 0.05f * x);
            (float Mean, float Deviation) Box(int x)
            {
                float a = Weighted(Ramp(x - 1)), b = Weighted(Ramp(x)), c = Weighted(Ramp(x + 1));
                float mean = (a + b + c) / 3f;
                return (mean, MathF.Sqrt(MathF.Max((a * a + b * b + c * c) / 3f - mean * mean, 0f)));
            }
            float HistoryAt(int column)
            {
                (float mean, float deviation) = Box(column + Shift);
                return Q(Unweighted(mean + 1.2f * deviation));
            }

            using var rig = new Rig();
            rig.BeginFrame();
            rig.Fill(Grey((x, _) => Ramp(x)), Grey((x, _) => Ramp(x)), Motion((_, _) => new Vector2((float)Shift / N, 0f)));
            rig.FillHistory(Grey((x, _) => HistoryAt(x)), State((_, _) => new Vector2(Q(3f / Max), 0f)), SceneLinear);
            rig.Resolve(Uniforms(historyValid: true));

            float[] color = rig.ReadColor(), state = rig.ReadState();
            float accumulated = MathF.Min(Q(3f / Max) * Max, MotionCap(Shift));
            for (int y = 2; y < N - 2; y++)
                for (int x = Shift; x < N - 1; x++)
                {
                    (float mean, float deviation) = Box(x);
                    float h = Weighted(HistoryAt(x - Shift));
                    Assert.InRange(h - mean, TemporalResolveTuning.GammaMoving * deviation * 1.2f,
                        TemporalResolveTuning.GammaStill * deviation * 0.9f);
                    float clipped = mean + TemporalResolveTuning.GammaMoving * deviation + 1.0e-5f;
                    float c = Weighted(Ramp(x));
                    Assert.Equal(Unweighted(clipped + (c - clipped) / (accumulated + 1f)), color[At(x, y) * 4], 1e-3);
                    Assert.Equal((accumulated + 1f) / Max, state[At(x, y) * 2], 1e-3);
                }
        }
    }
}
