using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The temporal resolve against synthetic inputs, one fact per step of TEMPORAL-RESOLVE-UPSCALING-DESIGN section 3,
    /// on an 8 by 8 target at native resolution unless a fact says otherwise, over what
    /// <see cref="TemporalResolveGpuFacts"/> shares. Step 6, the thin feature lock, has its facts in
    /// <see cref="TemporalResolveLockGpuTests"/>, and the resolve under a fast camera step, a glint, an infinite colour
    /// and a moving clip has its facts in <see cref="TemporalResolveEdgeCaseGpuTests"/>.
    /// </summary>
    public sealed class TemporalResolveRendererGpuTests : TemporalResolveGpuFacts
    {
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
            // Last frame the camera looked about one pixel left and one pixel up of where it looks now, a yaw and a pitch
            // of atan(0.25 / M11) at 8 pixels over a 1 radian field of view. The current frame and the history are
            // planes that slope on both axes, and the history plane sits low by about the rotation's step, so the
            // history at each pixel's rotated position lands inside its variance box and the blend is exact. That
            // position is computed here through BackgroundToPrevious as the shader applies it. A resolve that drops
            // or transposes the rotation, or flips y in its UV, reads history a pixel or more away, and misses by far
            // more than the tolerance.
            float yaw = MathF.Atan(0.25f / Projection.M11), pitch = MathF.Atan(0.25f / Projection.M22);
            Matrix4x4 turn = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateRotationX(pitch);
            Matrix4x4 viewThen = Matrix4x4.CreateLookAt(Eye, Eye + Vector3.TransformNormal(-Vector3.UnitZ, turn),
                Vector3.TransformNormal(Vector3.UnitY, turn));
            var then = new TemporalViewInput(viewThen, Projection);
            TemporalResolveUniforms u = TemporalResolveMath.BuildUniforms(Still, then, Vector2.Zero, N, N, N, N,
                historyValid: true);
            static float Plane(float x, float y) => 0.2f + 0.03f * x + 0.025f * y;   // over texel indices
            const float HistoryBelow = 0.05f;

            using var rig = new Rig();
            rig.BeginFrame();
            rig.Fill(Grey((x, y) => Q(Plane(x, y))), Grey((x, y) => Q(Plane(x, y))), Motion((_, _) => new Vector2(65504f)));
            rig.FillHistory(Grey((x, y) => Q(Plane(x, y) - HistoryBelow)), State((_, _) => new Vector2(Q(3f / Max), 0f)),
                TemporalResolveTuning.BackgroundLinearDepth);
            rig.Resolve(u);

            float[] color = rig.ReadColor(), state = rig.ReadState();
            float accumulated = Q(3f / Max) * Max;
            for (int y = 2; y < N - 2; y++)
                for (int x = 2; x < N - 2; x++)
                {
                    var uv = new Vector2(x + 0.5f, y + 0.5f) / N;
                    Vector4 clip = Vector4.Transform(new Vector4(Ndc(uv), 1f, 1f), u.BackgroundToPrevious);
                    Vector2 previous = new Vector2(clip.X / clip.W * 0.5f + 0.5f, 0.5f - clip.Y / clip.W * 0.5f) * N
                        - new Vector2(0.5f);
                    Assert.InRange(previous.X - x, 0.9f, 1.2f);   // about a pixel right and a pixel down
                    Assert.InRange(previous.Y - y, 0.9f, 1.2f);
                    float h = Weighted(Plane(previous.X, previous.Y) - HistoryBelow), c = Weighted(Q(Plane(x, y)));
                    Assert.Equal(Unweighted(h + (c - h) / (accumulated + 1f)), color[At(x, y) * 4], 1e-3);
                    Assert.Equal((accumulated + 1f) / Max, state[At(x, y) * 2], 1e-3);
                }
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
        public void New_history_targets_rebuild_the_sets_the_resolve_reads()
        {
            // The scene inputs keep their textures across a display resize, so only the history's target generation
            // tells BindInputs that the targets its sets were built over are retired. The first resolve reads a history
            // of accumulated weight 3. The display then grows to 16 by 16 over the same 8 by 8 internal frame, and the new
            // targets hold weight 5. Sets left over the retired targets would read the old history, or a freed one.
            const int D = 2 * N;
            float[] flat = Grey((_, _) => Q(0.3f)), zero = Motion((_, _) => Vector2.Zero);
            using var rig = new Rig();
            rig.BeginFrame();
            rig.Fill(flat, flat, zero);
            rig.FillHistory(flat, State((_, _) => new Vector2(Q(3f / Max), 0f)), SceneLinear);
            rig.Resolve(Uniforms(historyValid: true));
            Assert.Equal((Q(3f / Max) * Max + 1f) / Max, rig.ReadState()[At(4, 4) * 2], 1e-3);

            Assert.True(rig.ResizeDisplay(D, D));
            rig.BeginFrame();
            rig.Fill(flat, flat, zero);
            rig.FillHistory(Grey(D, D, (_, _) => Q(0.3f)), Pairs(D, D, (_, _) => new Vector2(Q(5f / Max), 0f)), SceneLinear);
            rig.Resolve(TemporalResolveMath.BuildUniforms(Still, Still, Vector2.Zero, N, N, D, D, historyValid: true));

            // Upscaled by two with no jitter, every display pixel centre sits a quarter texel from its nearest sample on
            // each axis, half a display pixel, so each frame is worth Lanczos2(0.5) squared.
            float sampleWeight = TemporalResolveMath.Lanczos2(0.5f) * TemporalResolveMath.Lanczos2(0.5f);
            float[] state = rig.ReadState();
            Assert.Equal(D * D * 2, state.Length);
            for (int i = 0; i < D * D; i++)
                Assert.Equal((Q(5f / Max) * Max + sampleWeight) / Max, state[i * 2], 1e-3);
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
    }
}
