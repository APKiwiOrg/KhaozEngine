using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using KhaozEngine.Gpu.Core.Frame;
using KhaozEngine.Gpu.Core.Interop;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.GpuCore;

[Collection("CoreBackend")]
public sealed class FrameSubmitBoundaryDiagnostics(ITestOutputHelper output)
{
    private const int WarmupFrames = 200;
    private const int MeasuredFrames = 2000;

    private delegate void Reorder(Span<khaoz_draw_record> records, Random rng);

    private readonly record struct Timing(double MedianMicroseconds, double P95Microseconds);

    [NativeFact]
    [Trait("Category", "Diagnostic")]
    public void Reports_frame_submit_cost()
    {
        if (Environment.GetEnvironmentVariable("KE_NATIVE_FRAME_TIMING") != "1")
            return;

        Timing random = Measure(FrameWorkload.ShuffleRandom);
        Timing coherent = Measure(FrameWorkload.MoveCoherent);

        output.WriteLine(FormattableString.Invariant(
            $"random median {random.MedianMicroseconds:F3} us, p95 {random.P95Microseconds:F3} us"));
        output.WriteLine(FormattableString.Invariant(
            $"coherent median {coherent.MedianMicroseconds:F3} us, p95 {coherent.P95Microseconds:F3} us"));

        string? path = Environment.GetEnvironmentVariable("KE_NATIVE_FRAME_RECEIPT");
        if (!string.IsNullOrEmpty(path))
        {
            var receipt = new
            {
                FrameWorkload.RecordCount,
                FrameWorkload.DistinctKeys,
                FrameWorkload.Seed,
                WarmupFrames,
                MeasuredFrames,
                Random = random,
                Coherent = coherent,
            };
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(receipt, options) + Environment.NewLine);
        }
    }

    private static Timing Measure(Reorder reorder)
    {
        var records = FrameWorkload.Create();
        var rng = new Random(FrameWorkload.Seed);
        using var boundary = new FrameSubmitBoundary(records.Length);
        for (int i = 0; i < WarmupFrames; i++)
        {
            reorder(records, rng);
            boundary.Submit(records);
        }

        var samples = new long[MeasuredFrames];
        for (int i = 0; i < samples.Length; i++)
        {
            reorder(records, rng);
            long start = Stopwatch.GetTimestamp();
            boundary.Submit(records);
            samples[i] = Stopwatch.GetTimestamp() - start;
        }

        Array.Sort(samples);
        int middle = samples.Length / 2;
        double median = (samples[middle - 1] + samples[middle]) / 2.0;
        int p95Index = (int)Math.Ceiling(samples.Length * 0.95) - 1;
        double microsecondsPerTick = 1_000_000.0 / Stopwatch.Frequency;
        return new Timing(median * microsecondsPerTick, samples[p95Index] * microsecondsPerTick);
    }
}
