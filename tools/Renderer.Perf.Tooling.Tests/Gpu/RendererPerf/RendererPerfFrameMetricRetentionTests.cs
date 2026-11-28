using System;
using System.Linq;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// Live bytes are what a forced full blocking collection promotes, read with the collection running alone, so no
// timing gates the bound. GC.GetTotalMemory counts whole region allocations and overstates small heaps severalfold.
[Collection("AllocSensitive")]
public sealed class RendererPerfFrameMetricRetentionTests
{
    // A full unit holds 16,944 frames until its report is written and must stay within 2 GiB (#1912).
    const long UnitBudgetBytes = 2L << 30;
    const int UnitFrames = 16_944;

    [Fact]
    public void Held_per_pass_frames_fit_the_full_unit_average_budget()
    {
        const int passes = 200, frames = 200;
        long before = LiveBytes();
        var samples = Harvest(passes, frames);
        long after = LiveBytes();
        Assert.Equal(frames, samples.Select(s => s.Metrics["logical.total.Vertices"].Value).Distinct().Count());
        GC.KeepAlive(samples);
        Assert.InRange((after - before) / frames, 0, UnitBudgetBytes / UnitFrames);
    }

    [Fact]
    public void Frames_with_one_key_set_share_a_layout_and_equal_values_share_a_metric()
    {
        var samples = Harvest(4, 2);
        Assert.Same(samples[0].Metrics.Layout, samples[1].Metrics.Layout);
        Assert.Equal(samples[0].Metrics["logical.pass.pass0.0.Draws"], samples[0].Metrics["logical.pass.pass1.0.Draws"]);
        Assert.Same(samples[0].Metrics["logical.pass.pass0.0.Draws"], samples[0].Metrics["logical.pass.pass1.0.Draws"]);
        Assert.Same(samples[0].Metrics["logical.pass.pass0.0.Draws"], samples[1].Metrics["logical.pass.pass0.0.Draws"]);
        Assert.NotEqual(samples[0].Metrics["logical.total.Vertices"], samples[1].Metrics["logical.total.Vertices"]);
    }

    static long LiveBytes()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetGCMemoryInfo(GCKind.FullBlocking).PromotedBytes;
    }

    internal static RendererPerfFrameSample[] Harvest(int passCount, int frameCount)
    {
        var passes = Enumerable.Range(0, passCount).Select(i => new RendererPerfPassDefinition(
            "pass" + i % 20, i / 20, -1, (uint)(i * 2 + 1), (uint)(i * 2 + 2))).ToArray();
        long tick = 0;
        var recorder = new RendererPerfRecorder(frameCount, RendererPerfLayout.PerPass, passes, () => tick += 1_001, 1_000_000_000);
        using var commands = RendererPerfCommandList.Wrap(new NullGpuCommandList(), recorder);
        using var graphics = new FakePipeline();
        using var target = new FakeFramebuffer(new(GpuPixelFormat.D24UNormS8UInt, GpuPixelFormat.R16G16B16A16Float), 1920, 1080);
        recorder.Register(graphics, new(true, Topology: GpuPrimitiveTopology.TriangleList));
        for (int frame = 0; frame < frameCount; frame++)
        {
            recorder.BeginFrame(frame, frame, frame % 256);
            commands.Begin();
            for (int pass = 0; pass < passCount; pass++)
            {
                recorder.BeginPass(commands, pass);
                commands.SetFramebuffer(target);
                commands.SetPipeline(graphics);
                commands.Draw((uint)(6 + frame), 1, 0, 0);
                recorder.EndPass(commands, pass);
            }
            commands.End();
            recorder.EndFrame();
            Assert.Null(recorder.FrameFailure(frame));
        }
        return [.. Enumerable.Range(0, frameCount).Select(frame => recorder.Harvest(frame))];
    }
}
