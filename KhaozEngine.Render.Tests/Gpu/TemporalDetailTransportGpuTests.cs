using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class TemporalDetailTransportGpuTests(TemporalDetailSceneFixture fixture, ITestOutputHelper output)
    : IClassFixture<TemporalDetailSceneFixture>
{
    [GpuTheory]
    [InlineData(TemporalUpscale.Native, false)]
    [InlineData(TemporalUpscale.Native, true)]
    [InlineData(TemporalUpscale.Quality, false)]
    [InlineData(TemporalUpscale.Quality, true)]
    public void Moving_history_keeps_its_mean_and_differs_from_display_then_replicates_when_stopped(TemporalUpscale preset, bool nearest)
    {
        Run(preset, nearest, true);
        if (preset == TemporalUpscale.Quality) Run(TemporalUpscale.Balanced, nearest, false);
    }

    [GpuTheory]
    [InlineData(TemporalUpscale.Native, false)]
    [InlineData(TemporalUpscale.Native, true)]
    [InlineData(TemporalUpscale.Quality, false)]
    [InlineData(TemporalUpscale.Quality, true)]
    public void Moving_detail_remains_in_history_space_through_hdr_bloom_and_distortion(TemporalUpscale preset, bool hdr)
    {
        PostChain(preset, hdr, true);
        if (preset == TemporalUpscale.Quality) PostChain(TemporalUpscale.Balanced, hdr, false);
    }

    void PostChain(TemporalUpscale preset, bool hdr, bool assertContract)
    {
        fixture.Reset(preset, hdr);
        var view = fixture.Fine;
        view.Frame.Scene.Post.Bloom.Enabled = true;
        long transported = 0;
        double largest = 0;
        for (int n = 0; n < 12; n++)
        {
            view.Frame.Frame((scene, _) =>
            {
                view.Fence.DrawOverWall(scene, n * 1.37f);
                scene.DrawDistortion(new DistortionSprite
                {
                    Position = new Vector3(0f, 0f, 1.2f), Size = 2.2f,
                    Shape = DistortionShape.Ripple, Strength = 2.5f, Seed = 0.31f,
                });
            });
            var measured = CheckMean(view.Frame, false, assertContract);
            largest = Math.Max(largest, measured.Deviation);
            transported += measured.Transported;
        }
        output.WriteLine($"{preset} hdr={hdr}: transported components={transported}, largest stored mean deviation={largest:R}");
        if (assertContract) Assert.True(transported > 0, "The post-chain proof must include transported fine detail.");
    }

    void Run(TemporalUpscale preset, bool nearest, bool assertContract)
    {
        fixture.Reset(preset, nearest: nearest);
        var view = fixture.FineFor(nearest);
        long different = 0;
        long transported = 0;
        double largest = 0;
        for (int n = 0; n < 32; n++)
        {
            float pan = Math.Min(n, 23) * 1.37f;
            byte[] display = fixture.Display.Draw(pan), fine = view.Draw(pan);
            for (int i = 0; i < fine.Length; i++) if (fine[i] != display[i]) different++;
            var measured = CheckMean(view.Frame, n >= 25, assertContract);
            largest = Math.Max(largest, measured.Deviation);
            transported += measured.Transported;
        }
        output.WriteLine($"{preset}: differing presented bytes={different}, transported components={transported}, largest stored mean deviation={largest:R}");
        if (assertContract) Assert.True(different > 0, "A moving Fine history must exercise the fine path.");
        if (assertContract) Assert.True(transported > 0, "Moving history must carry deviations into the fine store.");
    }

    static (double Deviation, long Transported) CheckMean(TemporalFixture view, bool replicate, bool assertContract)
    {
        var history = view.Scene.TemporalHistory;
        float[] r = TemporalTextureIo.Read(view.Device, history.Color(history.WriteIndex));
        float[] stored = TemporalTextureIo.Read(view.Device, history.DetailStore!.Color(history.WriteIndex));
        Assert.NotEmpty(r);
        int width = history.DisplayWidth, height = history.DisplayHeight;
        Assert.Equal(width * height * 16, stored.Length);
        double largest = 0;
        long transported = 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                for (int ch = 0; ch < 4; ch++)
                {
                    float expected = r[(y * width + x) * 4 + ch];
                    double sum = 0;
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                        {
                            float child = stored[((y * 2 + dy) * width * 2 + x * 2 + dx) * 4 + ch];
                            if (assertContract) Assert.InRange(child, 0f, 65504f);
                            if (child != expected) transported++;
                            if (assertContract && replicate) Assert.Equal(BitConverter.HalfToInt16Bits((Half)expected),
                                BitConverter.HalfToInt16Bits((Half)child));
                            sum += child;
                        }
                    double deviation = Math.Abs(sum * 0.25 - expected);
                    largest = Math.Max(largest, deviation);
                    double up = expected == 65504f ? 32.0 : (double)(float)Half.BitIncrement((Half)expected) - expected;
                    double step = Math.Max(up, expected - (double)(float)Half.BitDecrement((Half)expected));
                    if (assertContract) Assert.True(deviation <= step,
                        $"({x},{y}) channel {ch}: mean={sum * 0.25:R}, R={expected:R}, step={step:R}");
                }
        return (largest, transported);
    }
}
