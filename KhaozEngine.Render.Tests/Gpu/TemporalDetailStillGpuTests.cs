using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class TemporalDetailStillGpuTests(ITestOutputHelper output) : TemporalResolveGpuFacts
{
    public static IEnumerable<object[]> Scenarios()
    {
        foreach (TemporalUpscale preset in new[] { TemporalUpscale.Native, TemporalUpscale.Quality })
            foreach (string scene in new[] { "step", "trim", "once", "sparse", "ramp", "sparse-ramp", "ramp-wall", "light", "tripped" })
                yield return [preset, scene];
    }

    [GpuTheory]
    [MemberData(nameof(Scenarios))]
    public void Every_cold_still_prefix_has_identical_stored_colour_and_state(TemporalUpscale preset, string scene)
    {
        Compare(preset, scene, true);
        if (preset == TemporalUpscale.Quality) Compare(TemporalUpscale.Balanced, scene, false);
    }

    void Compare(TemporalUpscale preset, string scene, bool assertIdentity)
    {
        const int width = TemporalStillDetailGpuTests.DisplayW, height = TemporalStillDetailGpuTests.DisplayH;
        float factor = TemporalSettings.DisplayOverInternal(preset);
        int iw = (int)(width / factor), ih = (int)(height / factor), phases = TemporalJitter.PhaseCount(factor);
        Matrix4x4 projection = Perspective((float)width / height);
        TemporalViewInput still = View(Eye, projection);
        using var display = new TemporalResolveRig(iw, ih, width, height, NdcDepth(projection, SceneMetres));
        using var fine = new TemporalResolveRig(iw, ih, width, height, NdcDepth(projection, SceneMetres),
            detail: TemporalHistoryDetail.Fine);
        float[] motion = Pairs(iw, ih, (_, _) => Vector2.Zero);
        int different = 0;
        const int frames = TemporalStillDetailGpuTests.Converge + TemporalStillDetailGpuTests.Measured;
        for (int n = 0; n < frames; n++)
        {
            Vector2 jitter = TemporalJitter.Offset(n, phases);
            float[] input = scene switch
            {
                "step" => TemporalStillDetailGpuTests.StillStepScene(iw, ih, jitter, false),
                "trim" => TemporalStillDetailGpuTests.StillStepScene(iw, ih, jitter, true),
                "once" => TemporalStillDetailGpuTests.OncePerCycleLineScene(iw, ih, jitter, n),
                "sparse" => TemporalStillDetailGpuTests.SparseHitLineScene(iw, ih, jitter, n),
                "ramp" => TemporalStillDetailGpuTests.RampUnderLineScene(iw, ih, jitter, n, true),
                "sparse-ramp" => TemporalStillDetailGpuTests.RampUnderLineScene(iw, ih, jitter, n, true,
                    TemporalStillDetailGpuTests.SparseHitInterval),
                "light" => TemporalStillDetailGpuTests.LightStepScene(iw, ih, jitter, n),
                "ramp-wall" => TemporalStillDetailGpuTests.RampUnderLineScene(iw, ih, jitter, n, false),
                "tripped" => TemporalStillDetailGpuTests.TrippedFeatureScene(iw, ih, jitter),
                _ => throw new ArgumentOutOfRangeException(nameof(scene)),
            };
            TemporalResolveUniforms uniforms = TemporalResolveMath.BuildUniforms(still, still, jitter,
                iw, ih, width, height, n > 0);
            foreach (TemporalResolveRig rig in new[] { display, fine })
            {
                rig.BeginFrame();
                rig.Fill(input, input, motion);
                rig.Resolve(uniforms);
            }
            float[] expected = display.ReadColor(), actual = fine.ReadColor();
            Assert.NotEmpty(actual);
            for (int i = 0; i < actual.Length; i++) if (actual[i] != expected[i]) different++;
            if (!assertIdentity) continue;
            Assert.Equal(HalfBits(expected), HalfBits(actual));
            Assert.Equal(HalfBits(display.ReadState()), HalfBits(fine.ReadState()));
        }
        output.WriteLine($"{preset}, {scene}: {frames} cold frames, differing colour components={different}");
    }

    static short[] HalfBits(float[] values) => Array.ConvertAll(values, v => BitConverter.HalfToInt16Bits((Half)v));
}
