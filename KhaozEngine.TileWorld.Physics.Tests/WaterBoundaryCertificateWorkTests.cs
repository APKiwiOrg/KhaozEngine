using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Tests.Locomotion.Contacts;
using KhaozEngine.Tests.WaterPrismCensus;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileWorld.Physics;

/// <summary>The work the capture certificate removes, counted with no clock. On the census bank a SurfaceSwimmer walks
/// up and down the steep part, along the flat shelf and along dry ground. Through a field that hides the certificate,
/// each clean grounded step makes its seven boundary proofs as full solves. Through the certificate the same steps
/// publish the same bits and issue no water coverage trace, every boundary proof answered from the capture.</summary>
public class WaterBoundaryCertificateWorkTests(ITestOutputHelper output)
{
    const float Lane = 110.375f;

    public static TheoryData<string> Walks => ["bank-up", "bank-down", "flat-shelf", "dry"];

    [Theory]
    [MemberData(nameof(Walks))]
    public void ACleanGroundedStepTracesNoWaterThroughTheCertificate(string walk)
    {
        // Document Z, so world Z is its negation. The bank falls from row 236 to the -1.30 m shelf at row 237.
        (float start, Vector2 direction, float stop) = walk switch
        {
            "bank-up" => (237.55f, -Vector2.UnitY, 234.88597f),
            "bank-down" => (235.45f, Vector2.UnitY, 237.92163f),
            "flat-shelf" => (239.5f, Vector2.UnitX, float.NaN),
            _ => (234.5f, Vector2.UnitX, float.NaN),
        };
        using var hidden = new WaterPrismScenario("bank-stand", water: WaterTraversalMode.SurfaceSwimmer);
        using var certified = new WaterPrismScenario("bank-stand", water: WaterTraversalMode.SurfaceSwimmer,
            exposeCertificate: true);
        int hiddenTraces = 0, certifiedTraces = 0;
        var answers = new Dictionary<MovementWaterReachKind, int>();
        hidden.Field.Recorded = (_, _, _, _) => hiddenTraces++;
        certified.Field.Recorded = (_, _, _, _) => certifiedTraces++;
        certified.Certificate!.Answered = reach => answers[reach.Kind] = answers.GetValueOrDefault(reach.Kind) + 1;

        PlayerMoveState a = Standing(hidden, -start), b = Standing(certified, -start);
        for (int tick = 0; tick < 4; tick++)
        {
            a = hidden.Step(a, MoveCommand.Idle, out _);
            b = certified.Step(b, MoveCommand.Idle, out _);
        }
        LocomotionStateBits.AssertEqual(a, b, LocomotionStateBits.Policy.ExcludeContinuityHandle);

        var command = new MoveCommand(direction, false, 0);
        var rows = new List<(float Z, int Full, int Certified, Dictionary<MovementWaterReachKind, int> Answers)>();
        for (int tick = 0; tick < 60; tick++)
        {
            (int full, int traced) = (hiddenTraces, certifiedTraces);
            answers.Clear();
            MoveState before = a.Move;
            a = hidden.Step(a, command, out MovementStepOutcome? hiddenOutcome);
            b = certified.Step(b, command, out MovementStepOutcome? certifiedOutcome);
            Assert.Equal(hiddenOutcome, certifiedOutcome);
            LocomotionStateBits.AssertEqual(a, b, LocomotionStateBits.Policy.ExcludeContinuityHandle);
            bool clean = hiddenOutcome == MovementStepOutcome.Advanced && before.Grounded && a.Move.Grounded &&
                !before.Swimming && !a.Move.Swimming;
            Assert.True(clean, $"{walk} tick {tick} at {before.Position}: {hiddenOutcome}");
            rows.Add((-a.Move.Position.Z, hiddenTraces - full, certifiedTraces - traced, new(answers)));
            if (!float.IsNaN(stop) && (direction.Y < 0 ? -a.Move.Position.Z <= stop : -a.Move.Position.Z >= stop)) break;
        }
        foreach (var row in rows)
            output.WriteLine($"{walk} z={row.Z:R} full-solve-traces={row.Full} certified-traces={row.Certified} " +
                $"answers=[{string.Join(", ", row.Answers.Select(p => $"{p.Key}={p.Value}"))}]");
        Assert.NotEmpty(rows);
        bool bank = walk.StartsWith("bank-", System.StringComparison.Ordinal);
        if (bank) Assert.Contains(rows, row => row.Z > 236 && row.Z < 237);
        foreach (var row in rows)
        {
            // Seven boundary proofs per clean grounded step: the placement proof and the three that repeat it, then
            // the lift, the traverse and the seat. Hidden, each is a coverage trace. Certified, each is a capture answer.
            Assert.Equal(7, row.Full);
            Assert.Equal(0, row.Certified);
            Assert.Equal(7, row.Answers.Values.Sum());
            Assert.Equal(0, row.Answers.GetValueOrDefault(MovementWaterReachKind.Declined));
            // Dry ground has no water column within any proof's reach. The shelf and the steep bank have one level.
            if (walk == "dry") Assert.Equal(7, row.Answers.GetValueOrDefault(MovementWaterReachKind.Empty));
            if (walk == "flat-shelf" || bank && row.Z > 236 && row.Z < 237)
                Assert.Equal(7, row.Answers.GetValueOrDefault(MovementWaterReachKind.OneFreeSurface));
        }
    }

    static PlayerMoveState Standing(WaterPrismScenario scene, float z) => new()
    {
        Move = new()
        {
            Position = new(Lane, scene.Colliders.MovementGroundHeight(Lane, z) + WaterPrismScenario.Tuning.CapsuleHalfHeight, z),
            Grounded = true,
            SpeedScale = 1,
        },
    };
}
