#if CONTACT_SHORE_ATTRIBUTION
using System;
using System.Collections.Generic;
using System.Diagnostics;
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

// Report-only, with no latency assertions. Each invocation runs one finite count or timing pass.
internal static class ShoreStepAttributionBenchmark
{
    const int WarmupSteps = 8;
    const int FixedSteps = 32;
    const int ControlAdvanceSteps = 36;
    const int BankAdvanceSteps = 48;
    static readonly SupportQueryCounter[] SupportCounters = Enum.GetValues<SupportQueryCounter>();

    internal static void Run(string[] args)
    {
        var mode = args switch
        {
            ["--shore-step-attribution", "--counts"] => ShoreProbeMode.Counts,
            ["--shore-step-attribution", "--timings"] => ShoreProbeMode.Timings,
            _ => throw new ArgumentException("Select exactly one pass with --counts or --timings.")
        };
        if (Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") != "0")
            throw new InvalidOperationException("Run with DOTNET_TieredCompilation=0.");
        if (TileMediumSampler.BypassTraceMemoForTests || TileWaterSharedProjections.BypassForTests)
            throw new InvalidOperationException("Use the released water result tiers and shared projections.");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            kind = "metadata", mode = mode.ToString(),
            utc = DateTimeOffset.UtcNow,
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            tileWater = typeof(TileMediumSampler).Assembly.GetName().Version?.ToString(),
            hz = mode == ShoreProbeMode.Timings ? Stopwatch.Frequency : (long?)null,
            dt = WaterPrismScenario.Dt,
            waterPolicy = WaterPrismScenario.Tuning.Water.Mode.ToString(),
            walkSpeed = WaterPrismScenario.Tuning.WalkSpeed,
            warmup = WarmupSteps,
            phases = ShoreStepProbe.Phases,
            events = ShoreStepProbe.Events
        }));
        if (mode == ShoreProbeMode.Timings) ReportClock();
        Case[] cases =
        [
            new("dry-walk", "dry-control", Vector2.UnitX),
            new("flat-wade", "flat-wade", Vector2.UnitX),
            new("bank-up", "bank-stand", Vector2.UnitY, 237.55f),
            new("bank-down", "bank-stand", -Vector2.UnitY, 235.45f),
            new("bank-stand", "bank-stand", Vector2.Zero),
            new("surface-swim", "flat-control", Vector2.UnitX),
        ];
        foreach (var @case in cases)
        {
            Measure(@case, mode, -1);
            Measure(@case, mode, 0);
            Measure(@case, mode, 1);
        }
    }

    static void Measure(Case @case, ShoreProbeMode mode, int path)
    {
        using var scene = new WaterPrismScenario(@case.Fixture);
        var command = new MoveCommand(@case.Axis, false, 0);
        var basis = @case.BankStart is { } bankStart && path >= 0
            ? BankBasis(scene, 110.375f + path * 0.25f, bankStart)
            : scene.Basis;
        float surface = scene.Colliders.Medium.Extent.Maximum.Y;
        if (MathF.Abs(surface + 0.37f) > 0.001f)
            throw new InvalidOperationException($"Unexpected fixture surface for {@case.Name}: {surface}.");
        for (int i = 0; i < WarmupSteps; i++) _ = scene.Step(basis, command);
        int samples = path < 0 ? FixedSteps : @case.BankStart.HasValue ? BankAdvanceSteps : ControlAdvanceSteps;
        var rows = new List<object>(samples);
        var state = basis;
        for (int i = 0; i < samples; i++)
        {
            var (next, clocks, work) = Sample(scene, state, command, mode);
            var input = Describe(scene, state, surface);
            var output = Describe(scene, next, surface);
            CheckFixture(@case, input, output);
            rows.Add(new { kind = "step", name = @case.Name, path, sample = i, input, output, clocks, work });
            state = path < 0 ? basis : next;
        }
        foreach (var row in rows) Console.WriteLine(JsonSerializer.Serialize(row));
    }

    static PlayerMoveState BankBasis(WaterPrismScenario scene, float x, float depth)
    {
        float y = scene.Colliders.MovementGroundHeight(x, -depth) + WaterPrismScenario.Tuning.CapsuleHalfHeight;
        var state = new PlayerMoveState { Move = new() { Position = new(x, y, -depth), Grounded = true, SpeedScale = 1 } };
        for (int i = 0; i < 4; i++) state = scene.Step(state, MoveCommand.Idle);
        return state;
    }

    static StepState Describe(WaterPrismScenario scene, PlayerMoveState value, float surface)
    {
        var move = value.Move;
        var pos = move.Position;
        float bed = scene.Colliders.MovementGroundHeight(pos.X, pos.Z);
        return new(pos.X, pos.Y, pos.Z, bed, surface, move.Grounded, move.Swimming);
    }

    static void CheckFixture(Case @case, StepState input, StepState output)
    {
        bool valid = @case.Name switch
        {
            "dry-walk" => input.Grounded && output.Grounded && input.Bed > input.Surface && output.X > input.X,
            "flat-wade" => input.Grounded && output.Grounded && !input.Swimming && output.X > input.X &&
                MathF.Abs(input.Surface - input.Bed - 0.35f) < 0.001f,
            "bank-stand" => input.Grounded && output.Grounded && input.X == output.X && input.Z == output.Z,
            "surface-swim" => input.Swimming && output.Swimming && output.X > input.X,
            _ => input.Grounded || input.Swimming || output.Grounded
        };
        if (!valid) throw new InvalidOperationException($"Fixture {@case.Name} strayed from its intended motion: {input} -> {output}.");
    }

    static (PlayerMoveState Next, ShoreStepProbe.Reading Clocks, object? Work) Sample(
        WaterPrismScenario scene, PlayerMoveState state, MoveCommand command, ShoreProbeMode mode)
    {
        bool count = mode == ShoreProbeMode.Counts;
        var capture = count ? SupportQueryCapture.Begin(scene.Name) : null;
        if (count)
        {
            TileWaterQueryWork.Reset();
            MovementWaterBufferWork.Reset();
            MovementPlacementDiagnostics.Validations = MovementPlacementDiagnostics.Reuses = 0;
            MovementPlacementDiagnostics.Enabled = true;
        }
        PlayerMoveState next;
        ShoreStepProbe.Start(mode);
        try
        {
            using (ShoreStepProbe.Begin(ShorePhase.Step)) next = scene.Step(state, command);
        }
        finally
        {
            ShoreStepProbe.Stop();
            capture?.Dispose();
            TileWaterQueryWork.Enabled = MovementWaterBufferWork.Enabled = MovementPlacementDiagnostics.Enabled = false;
        }
        var reading = ShoreStepProbe.Read();
        if (!count)
        {
            // The timestamp partition must close, independent of how long the measured step took.
            if (reading.InclusiveTicks[(int)ShorePhase.Step] != reading.ExclusiveTicks.Sum())
                throw new InvalidOperationException("The shore attribution clocks do not partition the step.");
            return (next, reading, null);
        }
        var total = capture!.Snapshot().Total;
        if (reading.InclusiveTicks.Any(t => t != 0) || reading.ExclusiveTicks.Any(t => t != 0) ||
            reading.Calls[(int)ShorePhase.WaterSampler] != reading.Events[(int)ShoreEvent.FlatTrace] + TileWaterQueryWork.PrismTraces ||
            TileWaterQueryWork.ResultHits != reading.Events[(int)ShoreEvent.FlatResultHit] + reading.Events[(int)ShoreEvent.PrismResultHit] ||
            reading.Calls[(int)ShorePhase.Find] != total[SupportQueryCounter.Find])
            throw new InvalidOperationException("The untimed shore attribution counters do not reconcile.");
        return (next, reading, new
        {
            support = SupportCounters.ToDictionary(counter => counter.ToString(), counter => total[counter]),
            tile = new
            {
                TileWaterQueryWork.Intersections, TileWaterQueryWork.NonuniformVisits, TileWaterQueryWork.IndexNodes,
                TileWaterQueryWork.ConvexIntersections, TileWaterQueryWork.ShapePreparations, TileWaterQueryWork.LazyCoefficientFills,
                TileWaterQueryWork.BedSeamProofs, TileWaterQueryWork.BedSeamCertificates, TileWaterQueryWork.PrismTraces,
                TileWaterQueryWork.MemoHits, TileWaterQueryWork.MemoMisses, TileWaterQueryWork.ResultHits,
                TileWaterQueryWork.ResultMisses, TileWaterQueryWork.ResultStores, TileWaterQueryWork.ResultBypasses
            },
            placement = new { MovementPlacementDiagnostics.Validations, MovementPlacementDiagnostics.Reuses },
            buffers = new { MovementWaterBufferWork.Rents, MovementWaterBufferWork.ClearedReferences }
        });
    }

    static void ReportClock()
    {
        const int blocks = 16;
        const int scopesPerBlock = 128;
        var readPairTicks = new long[blocks];
        var scopeTicks = new long[blocks];
        ShoreStepProbe.Start(ShoreProbeMode.Timings);
        try
        {
            using (ShoreStepProbe.Begin(ShorePhase.Step))
            {
                using var warm = ShoreStepProbe.Begin(ShorePhase.WaterPoint);
            }
            for (int block = 0; block < blocks; block++)
            {
                for (int i = 0; i < scopesPerBlock; i++)
                {
                    long start = Stopwatch.GetTimestamp();
                    readPairTicks[block] += Stopwatch.GetTimestamp() - start;
                }
                long begin = Stopwatch.GetTimestamp();
                using (ShoreStepProbe.Begin(ShorePhase.Step))
                    for (int i = 0; i < scopesPerBlock; i++)
                    {
                        using var empty = ShoreStepProbe.Begin(ShorePhase.WaterPoint);
                    }
                scopeTicks[block] = Stopwatch.GetTimestamp() - begin;
            }
        }
        finally { ShoreStepProbe.Stop(); }
        Console.WriteLine(JsonSerializer.Serialize(new { kind = "clock", scopesPerBlock, readPairTicks, scopeTicks }));
    }

    readonly record struct Case(string Name, string Fixture, Vector2 Axis, float? BankStart = null);
    internal readonly record struct StepState(float X, float Y, float Z, float Bed, float Surface, bool Grounded, bool Swimming);
}
#endif
