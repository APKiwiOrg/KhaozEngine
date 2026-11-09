using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>The shimmer score's shadow gate (<c>temporalShimmerVisibilityChanged</c>) as the Shimmer debug view
    /// shows it, on the resolve-only rig with an exported key-shadow visibility texture. Two still lines the jitter
    /// hits once a cycle, <see cref="TemporalStillDetailGpuTests"/>' line twice, converge under full visibility. On
    /// one frame the visibility of one line's column alone steps down by more than
    /// <see cref="TemporalResolveTuning.VisibilityShimmerGate"/>, a one-texel-wide shadow step whose 3x3 mean moves
    /// less than <see cref="TemporalResolveTuning.VisibilityReleaseThreshold"/>, so the gate sees it and the
    /// visibility release does not. Lines are columns, so no assertion depends on which way a backend stores
    /// rows.</summary>
    public sealed class TemporalShimmerGateGpuTests(ITestOutputHelper output) : TemporalResolveGpuFacts
    {
        const int W = TemporalStillDetailGpuTests.DisplayW, H = TemporalStillDetailGpuTests.DisplayH;
        // Three frames after a hit, a frame the jitter misses both lines, where each line's score holds.
        const int StepFrame = TemporalStillDetailGpuTests.Converge + 3;
        // 64 of 255 at the centre texel is past the gate. The stepped column puts three of the nine texels of a 3x3 on
        // it, so the mean moves by 3 x 64 / 255 / 9, about 0.084, under the release threshold. A full shadow moves it
        // by a third, past the threshold.
        const byte Lit = 255, Stepped = 191, Shadowed = 0;
        const int Tinted = ShimmerViewTint.Tinted, Untinted = ShimmerViewTint.Untinted;

        // The production call site of the gate, and the text a control without the gate puts in its place.
        const string GateCall = "temporalShimmerVisibilityChanged(uv, centreTexel, previousUv)";

        [GpuFact]
        public void A_one_texel_shadow_step_the_visibility_release_misses_clears_the_shimmer_tint()
        {
            (int[] stepped, int[] control) = RunToTheStep(null, Stepped, out _);
            output.WriteLine($"stepped column blue excess [{string.Join(", ", stepped)}]");
            output.WriteLine($"control column blue excess [{string.Join(", ", control)}]");
            Assert.True(control.Min() >= Tinted, $"the unstepped line must keep its shimmer tint, min {control.Min()}");
            Assert.True(stepped.Max() <= Untinted,
                $"the line whose visibility stepped must read no shimmer tint, max {stepped.Max()}");

            // The same runs with the gate's call removed. The stepped line keeps its tint, so the visibility release
            // did not catch the step and the gate alone cleared it. A full shadow on the column still clears it, so the
            // release runs on these pixels and declined the smaller step by its threshold.
            bool viewRewritten = false;
            string WithoutGate(string source)
            {
                if (!source.Contains(GateCall, StringComparison.Ordinal)) return source;
                viewRewritten |= source.Contains("uniform DebugView", StringComparison.Ordinal);
                return source.Replace(GateCall, "false", StringComparison.Ordinal);
            }
            (int[] ungated, _) = RunToTheStep(WithoutGate, Stepped, out _);
            (int[] released, _) = RunToTheStep(WithoutGate, Shadowed, out _);
            output.WriteLine($"stepped column without the gate [{string.Join(", ", ungated)}]");
            output.WriteLine($"shadowed column without the gate [{string.Join(", ", released)}]");
            Assert.True(viewRewritten, "the control must remove the gate from the debug view's program");
            Assert.True(ungated.Min() >= Tinted,
                $"without the gate the stepped line must keep its tint, min {ungated.Min()}");
            Assert.True(released.Max() <= Untinted,
                $"without the gate a full shadow must still clear the tint through the release, max {released.Max()}");
        }

        [GpuFact]
        public void A_one_texel_shadow_step_the_visibility_release_misses_keeps_the_lock_decay()
        {
            float factor = TemporalSettings.DisplayOverInternal(TemporalUpscale.Quality);
            RunToTheStep(null, Stepped, out LockStep gated, TemporalUpscale.Quality);
            bool resolveRewritten = false;
            string WithoutGate(string source)
            {
                if (!source.Contains(GateCall, StringComparison.Ordinal)) return source;
                resolveRewritten |= !source.Contains("uniform DebugView", StringComparison.Ordinal);
                return source.Replace(GateCall, "false", StringComparison.Ordinal);
            }
            RunToTheStep(WithoutGate, Stepped, out LockStep ungated, TemporalUpscale.Quality);
            output.WriteLine($"gated {gated}, stepped loss {gated.SteppedLoss}, control loss {gated.ControlLoss}");
            output.WriteLine($"ungated {ungated}, stepped loss {ungated.SteppedLoss}, control loss {ungated.ControlLoss}");
            Assert.True(resolveRewritten, "the control must remove the visibility gate from the resolve program");
            Assert.True(gated.SteppedBefore > TemporalResolveTuning.LockDecay, $"{gated}");
            Assert.True(gated.ControlBefore > TemporalResolveTuning.LockDecay, $"{gated}");
            Assert.Equal(TemporalResolveTuning.LockDecay, gated.SteppedLoss, 1e-3);
            Assert.True(MathF.Abs(gated.ControlLoss - TemporalShimmer.Rate(factor)) <= 1e-3,
                $"control column loss {gated.ControlLoss}, expected rate {TemporalShimmer.Rate(factor)}");
            Assert.Equal(TemporalShimmer.Rate(factor), ungated.SteppedLoss, 1e-3);
            Assert.Equal(TemporalShimmer.Rate(factor), ungated.ControlLoss, 1e-3);
        }

        [GpuFact]
        public void A_shadow_step_keeps_the_colour_release_available()
        {
            // A stale bright history over a textured ramp releases. Its prior record shows flicker, but a centre
            // visibility step beyond the gate must keep that release available, even below the 3x3 release threshold.
            float stepped = ResolveColourRelease(null);
            bool resolveRewritten = false;
            string WithoutGate(string source)
            {
                if (!source.Contains(GateCall, StringComparison.Ordinal)) return source;
                resolveRewritten |= !source.Contains("uniform DebugView", StringComparison.Ordinal);
                return source.Replace(GateCall, "false", StringComparison.Ordinal);
            }
            float ungated = ResolveColourRelease(WithoutGate);
            output.WriteLine($"shadow step confidence {stepped}, without the visibility gate {ungated}");
            Assert.True(resolveRewritten, "the control must remove the visibility gate from the resolve program");
            Assert.Equal(1f / Max, stepped, 1e-3);
            Assert.True(ungated > stepped + 1f / Max,
                $"without visibility evidence the flickering record must veto the colour release, confidence {ungated}");
        }

        static float ResolveColourRelease(Func<string, string>? rewrite)
        {
            using var rig = new TemporalResolveRig(W, H, W, H, SceneNdc, fragmentSource: rewrite);
            using IGpuTexture visibility = rig.Device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8UNorm, GpuTextureUsage.Sampled));
            byte[] lit = Enumerable.Repeat(Lit, W * H).ToArray();
            rig.Device.UpdateTexture(visibility, lit, 0, 0, W, H);
            float[] ramp = Grey(W, H, (x, _) => Ramp(x % N));
            float[] motion = Pairs(W, H, (_, _) => Vector2.Zero);
            TemporalResolveUniforms uniforms = TemporalResolveMath.BuildUniforms(Still, Still, Vector2.Zero, W, H, W, H,
                historyValid: false);
            rig.BeginFrame();
            rig.Fill(ramp, ramp, motion);
            rig.Resolve(uniforms, visibility);
            rig.History.MarkValidAfterFrame();
            int column = W / 4;
            for (int y = 0; y < H; y++) lit[y * W + column] = Stepped;
            rig.Device.UpdateTexture(visibility, lit, 0, 0, W, H);
            rig.BeginFrame();
            rig.Fill(ramp, ramp, motion);
            rig.FillHistory(Grey(W, H, (_, _) => Q(0.9f)),
                StateWithRecord(W, H, (_, _) => new Vector4(Q(3f / Max), 0f, 0f, 0.5f)), SceneLinear);
            uniforms = TemporalResolveMath.BuildUniforms(Still, Still, Vector2.Zero, W, H, W, H, historyValid: true);
            rig.Resolve(uniforms, visibility);
            return rig.ReadState()[(H / 2 * W + column) * 4];
        }

        [GpuFact]
        public void A_history_reset_takes_todays_lock_decay()
        {
            float factor = TemporalSettings.DisplayOverInternal(TemporalUpscale.Quality);
            int iw = (int)(W / factor), ih = (int)(H / factor), phases = TemporalJitter.PhaseCount(factor);
            int resetFrame = TemporalStillDetailGpuTests.Converge;
            int column = (int)MathF.Floor((iw / 2 + 0.5f) * factor), pixel = (H / 2 * W + column) * 4;
            Matrix4x4 projection = Perspective((float)W / H);
            TemporalViewInput still = View(Eye, projection);
            using var rig = new Rig(iw, ih, W, H, NdcDepth(projection, SceneMetres));
            float[] motion = Pairs(iw, ih, (_, _) => Vector2.Zero);
            float resetLock = 0f;
            for (int n = 0; n <= resetFrame + 1; n++)
            {
                Vector2 jitter = TemporalJitter.Offset(n, phases);
                float[] input = TemporalStillDetailGpuTests.OncePerCycleLineScene(iw, ih, jitter, n);
                rig.BeginFrame();
                rig.Fill(input, input, motion);
                rig.Resolve(TemporalResolveMath.BuildUniforms(still, still, jitter, iw, ih, W, H,
                    historyValid: n > 0 && n != resetFrame));
                if (n < resetFrame - 1) continue;
                float[] state = rig.ReadState();
                if (n == resetFrame - 1)
                    Assert.True(state[pixel + 3] > 0f, "the converged line must have a shimmer record before reset");
                else if (n == resetFrame)
                {
                    resetLock = state[pixel + 1];
                    Assert.Equal(1f, resetLock);
                    Assert.Equal(0f, state[pixel + 2]);
                    Assert.Equal(0f, state[pixel + 3]);
                }
                else
                {
                    float loss = resetLock - state[pixel + 1];
                    output.WriteLine($"reset lock {resetLock}, next lock {state[pixel + 1]}, loss {loss}");
                    Assert.Equal(TemporalResolveTuning.LockDecay, loss, 1e-3);
                }
            }
        }

        readonly record struct LockStep(float SteppedBefore, float SteppedAfter, float ControlBefore, float ControlAfter)
        {
            internal float SteppedLoss => SteppedBefore - SteppedAfter;
            internal float ControlLoss => ControlBefore - ControlAfter;
        }

        // Converge the two lines to the step frame, step the visibility of one line's column to stepTo there, and draw
        // the Shimmer view over the step frame's resolve. Returns the blue excess of each line's column at every
        // interior row.
        static (int[] Stepped, int[] Control) RunToTheStep(Func<string, string>? rewrite, byte stepTo,
            out LockStep locks, TemporalUpscale preset = TemporalUpscale.Native)
        {
            float factor = TemporalSettings.DisplayOverInternal(preset);
            int iw = (int)(W / factor), ih = (int)(H / factor), phases = TemporalJitter.PhaseCount(factor);
            int steppedColumn = iw / 4, controlColumn = iw * 3 / 4;
            int steppedDisplay = (int)MathF.Floor((steppedColumn + 0.5f) * factor);
            int controlDisplay = (int)MathF.Floor((controlColumn + 0.5f) * factor);
            int steppedPixel = (H / 2 * W + steppedDisplay) * 4 + 1;
            int controlPixel = (H / 2 * W + controlDisplay) * 4 + 1;
            float steppedBefore = 0f, controlBefore = 0f;
            Matrix4x4 projection = Perspective((float)W / H);
            TemporalViewInput still = View(Eye, projection);
            using var rig = new TemporalResolveRig(iw, ih, W, H, NdcDepth(projection, SceneMetres),
                fragmentSource: rewrite);
            IGpuDevice gd = rig.Device;
            using IGpuTexture visibility = gd.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                (uint)iw, (uint)ih, GpuPixelFormat.R8UNorm, GpuTextureUsage.Sampled));
            using IGpuTexture view = gd.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer viewTarget = gd.Factory.CreateFramebuffer(null, view);
            float[] motion = Pairs(iw, ih, (_, _) => Vector2.Zero);
            byte[] lit = Enumerable.Repeat(Lit, iw * ih).ToArray();
            gd.UpdateTexture(visibility, lit, 0, 0, (uint)iw, (uint)ih);
            for (int n = 0; n <= StepFrame; n++)
            {
                Vector2 jitter = TemporalJitter.Offset(n, phases);
                float[] input = Grey(iw, ih, (x, _) => n % 8 == 0 && (x == steppedColumn || x == controlColumn)
                    ? TemporalStillDetailGpuTests.Line : TemporalStillDetailGpuTests.Wall);
                if (n == StepFrame)
                {
                    byte[] step = (byte[])lit.Clone();
                    for (int y = 0; y < ih; y++) step[y * iw + steppedColumn] = stepTo;
                    gd.UpdateTexture(visibility, step, 0, 0, (uint)iw, (uint)ih);
                }
                rig.BeginFrame();
                rig.Fill(input, input, motion);
                rig.Resolve(TemporalResolveMath.BuildUniforms(still, still, jitter, iw, ih, W, H, n > 0), visibility,
                    n == StepFrame
                        ? cl => rig.Renderer.DrawDebugView(cl, SceneDebugView.Shimmer, viewTarget, viewTarget.Outputs)
                        : null);
                if (n == StepFrame - 1)
                {
                    float[] state = rig.ReadState();
                    steppedBefore = state[steppedPixel];
                    controlBefore = state[controlPixel];
                }
                // A completed frame, as the scene marks it, so the next frame reads this one's visibility.
                rig.History.MarkValidAfterFrame();
            }
            byte[] rgba = GpuReadback.ToRgba(gd, view, W, H);
            float[] steppedState = rig.ReadState();
            locks = new LockStep(steppedBefore, steppedState[steppedPixel], controlBefore, steppedState[controlPixel]);
            return (BlueExcess(rgba, steppedDisplay), BlueExcess(rgba, controlDisplay));
        }

        static int[] BlueExcess(byte[] rgba, int column)
        {
            var excess = new int[H - 2];
            for (int y = 1; y < H - 1; y++) excess[y - 1] = ShimmerViewTint.BlueExcess(rgba, W, column, y);
            return excess;
        }
    }
}
