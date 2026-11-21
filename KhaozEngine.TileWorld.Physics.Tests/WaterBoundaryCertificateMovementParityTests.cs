using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Tests.Locomotion.Contacts;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using KhaozEngine.Tests.WaterPrismCensus;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileWorld.Physics;

/// <summary>Contact player movement and its replay publish the same outcome and state bits whether each water boundary
/// proof is answered by the capture certificate or by the full solve, wherever no listed refusal occurs. Each pair of
/// scenes is built twice from one authored world. One field hides the certificate and the other forwards it, and both
/// record every trace that still runs.</summary>
public class WaterBoundaryCertificateMovementParityTests(ITestOutputHelper output)
{
    static readonly WaterTraversalMode[] Bodies = [WaterTraversalMode.SurfaceSwimmer, WaterTraversalMode.WadeOnly];

    public static TheoryData<string, WaterTraversalMode> Scenarios
    {
        get
        {
            var data = new TheoryData<string, WaterTraversalMode>();
            foreach (string name in WaterPrismScenario.Names.Append("shore-traverse"))
                foreach (WaterTraversalMode body in Bodies) data.Add(name, body);
            return data;
        }
    }

    public static TheoryData<bool, WaterTraversalMode> DrawnBeds
    {
        get
        {
            var data = new TheoryData<bool, WaterTraversalMode>();
            foreach (bool level in new[] { false, true })
                foreach (WaterTraversalMode body in Bodies) data.Add(level, body);
            return data;
        }
    }

    // The bank, shelf, deep water and dry scenarios, settled and then driven by each scenario's own command. The shore
    // traverse walks down the bank into swimming water and back out.
    [Theory]
    [MemberData(nameof(Scenarios))]
    public void ScenarioAuthorityAndReplayMatchTheFullSolveBitForBit(string name, WaterTraversalMode body)
    {
        using var hidden = new WaterPrismScenario(name, water: body);
        using var certified = new WaterPrismScenario(name, water: body, exposeCertificate: true);
        var run = new ParityRun(hidden.Field, certified.Field);
        run.Settled(hidden.Basis, certified.Basis);
        bool outward = false;
        int ticks = name == "shore-traverse" ? 400 : 30;
        for (int tick = 0; tick < ticks; tick++)
        {
            MoveCommand command = hidden.Command;
            if (name == "shore-traverse")
            {
                if (run.Hidden.Move.Position.Z < -244) outward = true;
                command = new(outward ? -Vector2.UnitY : Vector2.UnitY, false, 0);
            }
            run.Tick(hidden.Step, certified.Step, command);
            if (name == "shore-traverse" && outward && run.Hidden.Move.Position.Z >= -235.8f) break;
        }
        Report(run, $"{name} {body}");
    }

    // The drawn-bed pool, sloped and with a level triangle at the free surface, crossed from dry ground through the
    // pool and out again.
    [Theory]
    [MemberData(nameof(DrawnBeds))]
    public void DrawnBedCrossingMatchesTheFullSolveBitForBit(bool levelTriangle, WaterTraversalMode body)
    {
        RecordingWaterField? hiddenField = null, certifiedField = null;
        var boundary = MovementBoundary.Rectangle(0, -64, 64, 0);
        using var hidden = new TileWaterMovementScene(DrawnWaterBedTests.World(levelTriangle), body, boundary,
            sampler => hiddenField = new RecordingWaterField(sampler));
        using var certified = new TileWaterMovementScene(DrawnWaterBedTests.World(levelTriangle), body, boundary,
            sampler => new CertificateForwardingWaterField(certifiedField = new RecordingWaterField(sampler), sampler));
        var run = new ParityRun(hiddenField!, certifiedField!);
        PlayerMoveState a = hidden.Standing(6.5f, -8.75f), b = certified.Standing(6.5f, -8.75f);
        for (int tick = 0; tick < 4; tick++)
        {
            a = hidden.Step(a, MoveCommand.Idle, out _);
            b = certified.Step(b, MoveCommand.Idle, out _);
        }
        run.Settled(a, b);
        for (int tick = 0; tick < 90; tick++) run.Tick(hidden.Step, certified.Step, new(Vector2.UnitX, false, 0));
        Report(run, $"drawn-bed level={levelTriangle} {body}");
    }

    void Report(ParityRun run, string label)
    {
        output.WriteLine($"{label}: ticks={run.Ticks} outcomes=[{string.Join(", ", run.Outcomes.Select(p => $"{p.Key}={p.Value}"))}] " +
            $"full-solve-traces={run.HiddenTraces} certified-traces={run.CertifiedTraces}");
        Assert.True(run.Ticks > 0);
        // The certificate answered at least one proof of every run, so no row compares the full solve with itself.
        Assert.True(run.CertifiedTraces < run.HiddenTraces);
    }

    delegate PlayerMoveState Stepper(PlayerMoveState state, MoveCommand command, out MovementStepOutcome? outcome);

    sealed class ParityRun
    {
        internal int HiddenTraces, CertifiedTraces, Ticks;
        internal readonly Dictionary<MovementStepOutcome, int> Outcomes = [];
        internal PlayerMoveState Hidden, Certified;

        internal ParityRun(RecordingWaterField hidden, RecordingWaterField certified)
        {
            hidden.Recorded = (_, _, _, _) => HiddenTraces++;
            certified.Recorded = (_, _, _, _) => CertifiedTraces++;
        }

        internal void Settled(PlayerMoveState hidden, PlayerMoveState certified)
        {
            Same(hidden, certified);
            (Hidden, Certified) = (hidden, certified);
        }

        // One authority tick on each side, then the certified side's replay of the same tick from the same basis
        // under a fresh read.
        internal void Tick(Stepper hidden, Stepper certified, MoveCommand command)
        {
            PlayerMoveState fullSolve = hidden(Hidden, command, out MovementStepOutcome? fullOutcome);
            PlayerMoveState answered = certified(Certified, command, out MovementStepOutcome? answeredOutcome);
            int traces = HiddenTraces, certifiedTraces = CertifiedTraces;
            PlayerMoveState replay = certified(Certified, command, out MovementStepOutcome? replayOutcome);
            (HiddenTraces, CertifiedTraces) = (traces, certifiedTraces);
            Assert.Equal(fullOutcome, answeredOutcome);
            Assert.Equal(answeredOutcome, replayOutcome);
            Same(fullSolve, answered);
            Same(answered, replay);
            Outcomes[fullOutcome!.Value] = Outcomes.GetValueOrDefault(fullOutcome.Value) + 1;
            (Hidden, Certified) = (fullSolve, answered);
            Ticks++;
        }

        // Each scene owns its support intervals, so geometric parity excludes the continuity handle.
        static void Same(PlayerMoveState expected, PlayerMoveState actual) =>
            LocomotionStateBits.AssertEqual(expected, actual, LocomotionStateBits.Policy.ExcludeContinuityHandle);
    }
}
