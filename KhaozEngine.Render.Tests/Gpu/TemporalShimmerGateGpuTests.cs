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
        const int SteppedColumn = W / 4, ControlColumn = W * 3 / 4;
        // Three frames after a hit, a frame the jitter misses both lines, where each line's score holds.
        const int StepFrame = TemporalStillDetailGpuTests.Converge + 3;
        // 64 of 255 at the centre texel is past the gate. The stepped column puts three of the nine texels of a 3x3 on
        // it, so the mean moves by 3 x 64 / 255 / 9, about 0.084, under the release threshold. A full shadow moves it
        // by a third, past the threshold.
        const byte Lit = 255, Stepped = 191, Shadowed = 0;
        // The view's tint over the grey dimmed scene: blue less red is 0.85 of the score in 8-bit units
        // (TemporalDebugMainGlsl), and the grey context alone reads 0.
        const int Tinted = 64, Untinted = 2;

        // The production call site of the gate, and the text a control without the gate puts in its place.
        const string GateCall = "temporalShimmerVisibilityChanged(uv, centreTexel, previousUv)";

        [GpuFact]
        public void A_one_texel_shadow_step_the_visibility_release_misses_clears_the_shimmer_tint()
        {
            (int[] stepped, int[] control) = RunToTheStep(null, Stepped);
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
            (int[] ungated, _) = RunToTheStep(WithoutGate, Stepped);
            (int[] released, _) = RunToTheStep(WithoutGate, Shadowed);
            output.WriteLine($"stepped column without the gate [{string.Join(", ", ungated)}]");
            output.WriteLine($"shadowed column without the gate [{string.Join(", ", released)}]");
            Assert.True(viewRewritten, "the control must remove the gate from the debug view's program");
            Assert.True(ungated.Min() >= Tinted,
                $"without the gate the stepped line must keep its tint, min {ungated.Min()}");
            Assert.True(released.Max() <= Untinted,
                $"without the gate a full shadow must still clear the tint through the release, max {released.Max()}");
        }

        // Converge the two lines to the step frame, step the visibility of one line's column to stepTo there, and draw
        // the Shimmer view over the step frame's resolve. Returns the blue excess of each line's column at every
        // interior row.
        static (int[] Stepped, int[] Control) RunToTheStep(Func<string, string>? rewrite, byte stepTo)
        {
            int phases = TemporalJitter.PhaseCount(1f);
            Matrix4x4 projection = Perspective((float)W / H);
            TemporalViewInput still = View(Eye, projection);
            using var rig = new TemporalResolveRig(W, H, W, H, NdcDepth(projection, SceneMetres),
                fragmentSource: rewrite);
            IGpuDevice gd = rig.Device;
            using IGpuTexture visibility = gd.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8UNorm, GpuTextureUsage.Sampled));
            using IGpuTexture view = gd.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer viewTarget = gd.Factory.CreateFramebuffer(null, view);
            float[] motion = Pairs(W, H, (_, _) => Vector2.Zero);
            byte[] lit = Enumerable.Repeat(Lit, W * H).ToArray();
            gd.UpdateTexture(visibility, lit, 0, 0, W, H);
            for (int n = 0; n <= StepFrame; n++)
            {
                Vector2 jitter = TemporalJitter.Offset(n, phases);
                float[] input = Grey(W, H, (x, _) => n % 8 == 0 && (x == SteppedColumn || x == ControlColumn)
                    ? TemporalStillDetailGpuTests.Line : TemporalStillDetailGpuTests.Wall);
                if (n == StepFrame)
                {
                    byte[] step = (byte[])lit.Clone();
                    for (int y = 0; y < H; y++) step[y * W + SteppedColumn] = stepTo;
                    gd.UpdateTexture(visibility, step, 0, 0, W, H);
                }
                rig.BeginFrame();
                rig.Fill(input, input, motion);
                rig.Resolve(TemporalResolveMath.BuildUniforms(still, still, jitter, W, H, W, H, n > 0), visibility,
                    n == StepFrame
                        ? cl => rig.Renderer.DrawDebugView(cl, SceneDebugView.Shimmer, viewTarget, viewTarget.Outputs)
                        : null);
                // A completed frame, as the scene marks it, so the next frame reads this one's visibility.
                rig.History.MarkValidAfterFrame();
            }
            byte[] rgba = GpuReadback.ToRgba(gd, view, W, H);
            return (BlueExcess(rgba, SteppedColumn), BlueExcess(rgba, ControlColumn));
        }

        static int[] BlueExcess(byte[] rgba, int column)
        {
            var excess = new int[H - 2];
            for (int y = 1; y < H - 1; y++)
            {
                int i = (y * W + column) * 4;
                excess[y - 1] = rgba[i + 2] - rgba[i];
            }
            return excess;
        }
    }
}
