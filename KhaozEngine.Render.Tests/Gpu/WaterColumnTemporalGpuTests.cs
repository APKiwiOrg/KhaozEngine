using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Tests.Gpu.WaterColumnGpuTests;

namespace KhaozEngine.Tests.Gpu;

/// <summary>A fixed 8-frame warmup and 16-frame acceptance sequence, with frozen scene and effect time.</summary>
public sealed class WaterColumnTemporalGpuTests(WaterColumnRiverScene scene, ITestOutputHelper output)
    : IClassFixture<WaterColumnRiverScene>
{
    // Removing stratification or proper temporal history would expose noisy column visibility near the floater.
    [GpuFact]
    public void Golden_ColumnNoiseIsBounded()
    {
        foreach (AntiAliasing aa in new[] { AntiAliasing.Off, AntiAliasing.Temporal })
        {
            var settings = new WaterColumnCase { Aa = aa };
            WaterColumnImage warm = scene.Capture(settings, 8);
            Vector2 probe = warm.Project(ShadowColumn);
            const int radius = 1, width = radius * 2 + 1;
            var sum = new double[width * width];
            var squares = new double[sum.Length];
            WaterColumnImage last = warm;
            for (int frame = 0; frame < 16; frame++)
            {
                last = scene.Capture(settings, 1);
                for (int y = -radius; y <= radius; y++)
                    for (int x = -radius; x <= radius; x++)
                    {
                        int i = (y + radius) * width + x + radius;
                        double luma = L(last.ResolvedPixel((int)probe.X + x, (int)probe.Y + y));
                        sum[i] += luma;
                        squares[i] += luma * luma;
                    }
            }
            double maximumVariance = 0d, residualSquares = 0d;
            for (int i = 0; i < sum.Length; i++)
                maximumVariance = Math.Max(maximumVariance, Math.Max(0d, squares[i] / 16d - Math.Pow(sum[i] / 16d, 2d)));
            for (int y = -radius; y <= radius; y++)
                for (int x = -radius; x <= radius; x++)
                {
                    int px = (int)probe.X + x, py = (int)probe.Y + y;
                    double mean = 0d;
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++) mean += L(last.ResolvedPixel(px + dx, py + dy)) / 25d;
                    residualSquares += Math.Pow(L(last.ResolvedPixel(px, py)) - mean, 2d);
                }
            double rms = Math.Sqrt(residualSquares / sum.Length);
            WaterColumnImage control = scene.Capture(settings with { Caster = false }, 8);
            float margin = L(control.ResolvedSample(ShadowColumn) - last.ResolvedSample(ShadowColumn));
            output.WriteLine($"AA={aa}, max temporal variance={maximumVariance:G9}, 5x5 residual RMS={rms:G9}, caster-free margin={margin:F6}");
            Assert.InRange(maximumVariance, 0d, aa.Mode == AntiAliasingMode.Temporal ? 0.000025d : 0d);
            Assert.InRange(rms, 0d, 0.01d);
            Assert.True(margin >= 0.025f);
        }
    }
}
