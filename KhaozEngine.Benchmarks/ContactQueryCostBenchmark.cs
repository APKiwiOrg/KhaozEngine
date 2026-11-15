using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
#if WATER_PRISM_CENSUS
using KhaozEngine.TileWorld.Physics;
#endif
using KhaozEngine.Tests.WaterPrismCensus;

namespace KhaozEngine.Benchmarks;

// Diagnostic only. No production clocks, cache prototype, elapsed-time verdict or budget gate.
internal static class ContactQueryCostBenchmark
{
    internal static void Run(string[] args)
    {
        bool timers = Array.IndexOf(args, "--timers") >= 0;
        bool traceTimers = Array.IndexOf(args, "--trace-timers") >= 0;
        bool counters = Array.IndexOf(args, "--counters") >= 0;
        bool quick = Array.IndexOf(args, "--quick") >= 0;
        if (traceTimers && !timers) throw new ArgumentException("Trace timers require --timers.");
        if (timers && Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") != "0")
            throw new InvalidOperationException("Run timed diagnostics with DOTNET_TieredCompilation=0.");
#if WATER_PRISM_CENSUS
        if (timers) throw new InvalidOperationException("Use a normal build for timed diagnostics.");
#endif
        int warmup = quick ? 1 : 8, fixedSamples = quick ? 2 : 96, trajectorySamples = quick ? 2 : 32;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            mode = "query-cost",
            timers,
            traceTimers,
            counters,
            warmup,
            fixedSamples,
            trajectorySamples,
            utc = DateTimeOffset.UtcNow,
            framework = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString()
        }));
        foreach (string name in ContactQueryCostCases.Names)
        {
            using var contact = ContactQueryCostCases.Create(name);
            using var legacy = ContactQueryCostCases.Create(name, MovementControllerKind.Legacy);
            var command = ContactQueryCostCases.Command(name);
            for (int i = 0; i < warmup; i++)
            {
                _ = contact.Step(contact.Basis, command);
                _ = legacy.Step(legacy.Basis, command);
            }
            foreach (bool trajectory in new[] { false, true })
            {
                var c = contact.Basis;
                var l = legacy.Basis;
                int samples = trajectory ? trajectorySamples : fixedSamples;
                var ct = timers ? new List<double>(samples) : null;
                var lt = timers ? new List<double>(samples) : null;
                contact.Field.TraceMicroseconds.Clear();
                legacy.Field.TraceMicroseconds.Clear();
                contact.Field.MeasureTime = legacy.Field.MeasureTime = traceTimers;
                if (counters) SupportProposalAdmission.ResetForTests();
                using var capture = counters ? SupportQueryCapture.Begin(name) : null;
                using var admission = counters ? new SupportProposalAdmissionCapture() : null;
#if WATER_PRISM_CENSUS
                if (counters) TileWaterQueryWork.Reset();
#endif
                for (int i = 0; i < samples; i++)
                {
                    if ((i & 1) == 0)
                    {
                        c = Step(contact, c, command, ct);
                        l = Step(legacy, l, command, lt);
                    }
                    else
                    {
                        l = Step(legacy, l, command, lt);
                        c = Step(contact, c, command, ct);
                    }
                    if (!trajectory) { c = contact.Basis; l = legacy.Basis; }
                }
                Dictionary<string, long>? work = null;
                if (capture is not null)
                {
                    work = new();
                    var total = capture.Snapshot().Total;
                    foreach (SupportQueryCounter counter in Enum.GetValues<SupportQueryCounter>())
                        work[counter.ToString()] = total[counter];
                }
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    name,
                    trajectory,
                    samples,
                    contactUs = ct,
                    legacyUs = lt,
                    contactTraceUs = traceTimers ? contact.Field.TraceMicroseconds : null,
                    legacyTraceUs = traceTimers ? legacy.Field.TraceMicroseconds : null,
                    contactPosition = new[] { c.Position.X, c.Position.Y, c.Position.Z },
                    legacyPosition = new[] { l.Position.X, l.Position.Y, l.Position.Z },
                    work,
#if WATER_PRISM_CENSUS
                    exactResultTier = counters ? new
                    {
                        TileWaterQueryWork.ResultHits,
                        TileWaterQueryWork.ResultMisses,
                        TileWaterQueryWork.ResultStores,
                        TileWaterQueryWork.ResultBypasses,
                        TileWaterQueryWork.PrismTraces,
                        TileWaterQueryWork.MemoHits,
                        TileWaterQueryWork.MemoMisses,
                        TileWaterQueryWork.ShapePreparations
                    } : null,
#endif
                    proposalAdmission = admission is null ? null : new
                    {
                        admission.Hits,
                        admission.Misses,
                        admission.Bypasses
                    }
                }));
#if WATER_PRISM_CENSUS
                TileWaterQueryWork.Enabled = false;
#endif
            }
        }
    }

    static PlayerMoveState Step(WaterPrismScenario scene, PlayerMoveState state, MoveCommand command,
        List<double>? samples)
    {
        if (samples is null) return scene.Step(state, command);
        long start = Stopwatch.GetTimestamp();
        var next = scene.Step(state, command);
        samples.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);
        return next;
    }
}
