using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using KhaozEngine.Tests.WaterPrismCensus;

namespace KhaozEngine.Benchmarks;

internal static class WaterPrismBenchmark
{
    internal static void Run()
    {
#if WATER_PRISM_CENSUS
        throw new InvalidOperationException("Build without WaterPrismCensus to time the uninstrumented kernel.");
#else
        const int warmup = 8, measured = 96;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            mode = "water-prism",
            warmup,
            measured,
            timestamp = DateTimeOffset.UtcNow,
            framework = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString()
        }));
        foreach (string name in WaterPrismScenario.Names)
        {
            using var scene = new WaterPrismScenario(name);
            for (int i = 0; i < warmup; i++) _ = scene.Step(scene.Basis, scene.Command);
            var steps = new List<double>(measured);
            var reads = new List<double>(measured);
            var cores = new List<double>(measured);
            var water = new List<double>(measured);
            var other = new List<double>(measured);
            scene.Field.TraceMicroseconds.EnsureCapacity(measured * 32);
            scene.Field.MeasureTime = true;
            for (int i = 0; i < measured; i++)
            {
                int first = scene.Field.TraceMicroseconds.Count;
                long start = Stopwatch.GetTimestamp();
                long acquired, stepped;
                using (var read = scene.Simulator.BeginExplicitRead(scene.Basis))
                {
                    acquired = Stopwatch.GetTimestamp();
                    _ = scene.Simulator.Step(scene.Basis, scene.Command, WaterPrismScenario.Dt);
                    stepped = Stopwatch.GetTimestamp();
                }
                double elapsed = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
                double waterUs = 0;
                for (int t = first; t < scene.Field.TraceMicroseconds.Count; t++) waterUs += scene.Field.TraceMicroseconds[t];
                steps.Add(elapsed);
                reads.Add(Stopwatch.GetElapsedTime(start, acquired).TotalMicroseconds);
                cores.Add(Stopwatch.GetElapsedTime(acquired, stepped).TotalMicroseconds);
                water.Add(waterUs);
                other.Add(elapsed - waterUs);
            }
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                name,
                measured,
                tracesPerStep = scene.Field.TraceMicroseconds.Count / measured,
                medianStepUs = Median(steps),
                medianTraceUs = Median(scene.Field.TraceMicroseconds),
                medianReadUs = Median(reads),
                medianCoreUs = Median(cores),
                medianWaterUs = Median(water),
                medianOtherUs = Median(other)
            }));
        }
#endif
    }

    static double Median(List<double> values)
    {
        values.Sort();
        return values.Count == 0 ? 0 : (values[(values.Count - 1) / 2] + values[values.Count / 2]) * 0.5;
    }
}
