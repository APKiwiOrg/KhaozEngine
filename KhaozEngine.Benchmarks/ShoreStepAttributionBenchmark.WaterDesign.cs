#if CONTACT_SHORE_ATTRIBUTION
using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using KhaozEngine.Locomotion;

using KhaozEngine.NetWorld;
using KhaozEngine.Physics;
using KhaozEngine.Tests.WaterPrismCensus;
using KhaozEngine.TileWorld.Physics;

namespace KhaozEngine.Benchmarks;

internal static partial class ShoreStepAttributionBenchmark
{
    static void RunWaterDesign(string population)
    {
        int seats = population switch
        {
            "1" => 1,
            "16" => 16,
            "32" => 32,
            _ => throw new ArgumentException("Select one bounded count fixture: 1, 16 or 32.")
        };
        if (Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") != "0" ||
            TileMediumSampler.BypassTraceMemoForTests || TileWaterSharedProjections.BypassForTests)
            throw new InvalidOperationException("Use the released water tiers with DOTNET_TieredCompilation=0.");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            kind = "metadata", mode = "Counts", hz = (long?)null, seats,
            runtime = RuntimeInformation.FrameworkDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            metric = Enum.GetNames<ShoreWork>(), repeats = TileWaterShoreCounts.RepeatNames,
            memory = TileWaterShoreCounts.Session.MemoMemory()
        }));
        CountWaterDesign(new("bank-up", "bank-stand", -Vector2.UnitY, 237.55f), seats, BankAdvanceSteps, fixedBasis: false);
        CountWaterDesign(new("bank-down", "bank-stand", Vector2.UnitY, 235.45f), seats, BankAdvanceSteps, fixedBasis: false);
        CountWaterDesign(new("bank-up", "bank-stand", -Vector2.UnitY, 236.365f), seats, 6, fixedBasis: true);
        if (seats == 1)
            CountWaterDesign(new("flat-wade", "flat-wade", Vector2.UnitX), seats, ControlAdvanceSteps, fixedBasis: false);
    }

    static void CountWaterDesign(Case trajectory, int seats, int ticks, bool fixedBasis)
    {
        using var scene = new WaterPrismScenario(trajectory.Fixture);
        float surface = scene.Colliders.Medium.Extent.Maximum.Y;
        var states = new PlayerMoveState[seats];
        var onSlope = new bool[seats];
        for (int seat = 0; seat < seats; seat++)
            states[seat] = trajectory.BankStart is { } depth
                ? BankBasis(scene, 110.375f + (seat - seats / 2) * 0.25f, depth) : scene.Basis;
        scene.Colliders.Medium.ResetTraceMemoForTests();

        var command = new MoveCommand(trajectory.Axis, false, 0);
        var water = new TileWaterShoreCounts.Session();
        TileWaterShoreCounts.Current = water;
        ShoreStepProbe.TraceLabelsEnabled = true;
        try
        {
            for (int tick = 0; tick < ticks; tick++)
                for (int seat = 0; seat < seats; seat++)
                {
                    var input = Describe(scene, states[seat], surface);
                    water.Start();
                    var (next, clocks, _) = Sample(scene, states[seat], command, ShoreProbeMode.Counts);
                    var traces = water.Stop();
                    var output = Describe(scene, next, surface);
                    CheckFixture(trajectory, input, output);
                    onSlope[seat] |= input.Z is <= -236 and >= -237 && input.Grounded && output.Grounded &&
                        !input.Swimming && !output.Swimming && input.Z != output.Z;
                    if (traces.Length != clocks.Calls[(int)ShorePhase.WaterSampler] ||
                        traces.Sum(t => t.Fills) != TileWaterQueryWork.LazyCoefficientFills ||
                        traces.Sum(t => t.Work[(int)ShoreWork.CopiedProjection]) !=
                            clocks.Events[(int)ShoreEvent.SharedProjectionCopies] ||
                        traces.Count(t => t.Outcome == "small-hit") != TileWaterQueryWork.MemoHits ||
                        traces.Count(t => t.Outcome == "result-hit") != TileWaterQueryWork.ResultHits ||
                        traces.Sum(t => t.Work[(int)ShoreWork.ResultStore]) != TileWaterQueryWork.ResultStores)
                        throw new InvalidOperationException("Water design counts did not reconcile.");
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        kind = "step", trajectory.Name, fixedBasis, seats, tick, seat, input, output, traces
                    }));
                    if (!fixedBasis) states[seat] = next;
                }
            if (trajectory.BankStart.HasValue && onSlope.Any(hit => !hit))
                throw new InvalidOperationException("Every bank seat must advance while grounded on the slope.");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "scene", trajectory.Name, fixedBasis, seats, ticks, water.UniqueShapes
            }));
        }
        finally
        {
            TileWaterShoreCounts.Current = null;
            ShoreStepProbe.TraceLabelsEnabled = false;
        }
    }
}
#endif
