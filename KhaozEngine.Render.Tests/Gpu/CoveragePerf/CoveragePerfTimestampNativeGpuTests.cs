using System;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoveragePerfTimestampNativeGpuTests
{
    [GpuFact]
    public void RealSharedSessionCompletesTwoThreeFrameBatchesInBothLayouts()
    {
        var manifest = CoveragePerfManifest.Parse(new CoveragePerfFakeRun("G1").Json);
        foreach (var layout in new[] { CoveragePerfTimerLayout.Phases, CoveragePerfTimerLayout.Total })
        {
            var block = manifest.Blocks.First(b => b.Actors == 1 && b.Condition == CoveragePerfCondition.SharedOn)
                with
            { TimerLayout = layout };
            long ticks = 0;
            using var session = new CoveragePerfNativeSession(manifest, block, CoveragePerfNativeHost.Open,
                new(() => ++ticks, 1000));
            var info = session.ReadInfo();
            Assert.True(info.NativeDevice);
            Assert.False(info.Synthetic);
            Assert.Null(info.TimestampPeriodNanoseconds);
            if (!info.TimestampSupported || info.SoftwareAdapter)
            {
                CoveragePerfFrameSample unsupported = default;
                Assert.Throws<NotSupportedException>(() => session.Frame(0, ref unsupported));
                continue;
            }
            var samples = new CoveragePerfFrameSample[3];
            for (int batch = 0; batch < 2; batch++)
            {
                for (int i = 0; i < 3; i++)
                {
                    session.Frame(batch * 3 + i, ref samples[i]);
                    Assert.Null(samples[i].Gpu);
                }
                session.CompleteBatch(samples);
                for (int i = 0; i < 3; i++)
                {
                    var sample = samples[i];
                    Assert.Equal(batch * 3 + i, sample.FrameIndex);
                    Assert.True(sample.DeviceComplete);
                    Assert.False(sample.Fallback);
                    Assert.Null(sample.Error);
                    var gpu = Assert.IsType<CoveragePerfGpuResult>(sample.Gpu);
                    Assert.True(gpu.Valid, gpu.Reason);
                    Assert.False(gpu.Disjoint);
                    Assert.Equal(layout, gpu.Layout);
                    Assert.Equal(layout == CoveragePerfTimerLayout.Phases ? 5 : 2, gpu.Ticks.Length);
                    Assert.Equal(0ul, gpu.Ticks[0]);
                    for (int marker = 1; marker < gpu.Ticks.Length; marker++)
                        Assert.True(gpu.Ticks[marker] >= gpu.Ticks[marker - 1]);
                    Assert.True(double.IsFinite(gpu.PeriodNanoseconds) && gpu.PeriodNanoseconds > 0);
                    if (batch == 1) Assert.Equal(0, sample.ProductionWaits);
                }
                Assert.Equal(batch + 1, session.HarnessDrains);
                Assert.True(session.ReadInfo().TimestampPeriodNanoseconds is > 0);
            }
        }
    }
}
