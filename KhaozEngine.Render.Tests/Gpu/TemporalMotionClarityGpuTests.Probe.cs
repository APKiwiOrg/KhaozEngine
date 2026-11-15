using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class TemporalMotionClarityGpuTests
{
    [GpuTheory]
    [InlineData(TemporalUpscale.Native)]
    [InlineData(TemporalUpscale.Quality)]
    public void The_fine_decision_probe_matches_the_live_path_flags(TemporalUpscale preset)
    {
        Run(preset, true);
        if (preset == TemporalUpscale.Quality) Run(TemporalUpscale.Balanced, false);
    }

    void Run(TemporalUpscale preset, bool assertContract)
    {
        int fineFrames = 0;
        double largestError = 0;
        scene.Run(preset, MotionPath.Translation, 1.37f, TemporalHistoryDetail.Fine,
            collectDecisions: true, observe: (fx, diagnostics) =>
            {
                float[] transport = TemporalTextureIo.Read(fx.Device,
                    fx.Scene.TemporalHistory.DetailStore!.TransportForTests);
                int selected = 0;
                for (int p = 3; p < transport.Length; p += 4) if (transport[p] > 0.5f) selected++;
                double expected = selected / (double)(transport.Length / 4);
                largestError = System.Math.Max(largestError, System.Math.Abs(expected - diagnostics.FineShare));
                if (selected > 0) fineFrames++;
                if (assertContract) Assert.Equal(expected, diagnostics.FineShare);
            });
        output.WriteLine($"PROBE|{preset}|moving fine frames={fineFrames}|largest share error={largestError:R}");
        if (assertContract) Assert.True(fineFrames > 0, "The path probe must observe moving Fine pixels.");
    }
}
