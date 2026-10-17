using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Physics;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public sealed class PacedCrateDescentTests(ITestOutputHelper output)
{
    static readonly MoveTuning Tuning = MoveTuning.Default;

    [Theory]
    [InlineData("GroundTickBorrowed")]
    [InlineData("DryTick")]
    public void HiddenCrateClimbKeepsTheTopAlreadyReachedInsideItsFootprint(string operation)
    {
        SupportCostRow row = SupportGateCostScenes.Rows().Select(r => (SupportCostRow)r[0])
            .Single(r => r.Name == "hidden crate");
        using var execution = new SupportCostExecution(row, operation);
        var steps = new List<(SupportGroundObservation Start, SupportGroundObservation End)>();
        SupportGroundObservation start = default;
        SupportQueryDiagnostics.ThreadState prior = SupportQueryDiagnostics.Exchange(
            SupportQueryDiagnostics.ThreadState.Fresh(true));
        SupportCostOutput result;
        try
        {
            SupportQueryDiagnostics.SetGroundSubstepObserver(
                () => start = SupportQueryDiagnostics.Ground,
                () => steps.Add((start, SupportQueryDiagnostics.Ground)));
            result = execution.Invoke();
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }

        Assert.Equal(2, steps.Count);
        foreach (var (before, after) in steps)
        {
            output.WriteLine($"{operation}: {before.Feet} support {before.Height:R} -> " +
                $"{after.Feet} support {after.Height:R}, delta {after.Feet.Y - before.Feet.Y:R}");
        }
        // The small crate spans X [0.05, 0.12], Z [-0.1, 0.1], with its top at 0.1.
        // Both accepted axes keep its centre inside the radius 0.2 footprint.
        Assert.InRange(Math.Abs(steps[0].Start.Feet.Y - 0.1), 0, 0.001);
        foreach (var (before, after) in steps)
        {
            Assert.True(after.Supported);
            double dx = after.Feet.X - 0.085, dz = after.Feet.Z;
            Assert.InRange(Math.Sqrt(dx * dx + dz * dz), 0, 0.2);
            Assert.True(after.Feet.Y >= 0.1f - after.HeightError,
                $"Feet {after.Feet.Y:R} descended below the crate top 0.1 inside the footprint.");
            Assert.True(after.Feet.Y >= before.Feet.Y,
                $"An owed climb from {before.Feet} to support {after.Height:R} lowered feet to {after.Feet}.");
        }
        Assert.True(result.GeometryPassed);
        Assert.InRange((double)steps[^1].End.Feet.Y - row.Feet.Y, 0, (double)Tuning.MaxStepClimbSpeed * row.Dt);
    }

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(0.02, true)]
    [InlineData(0.0, false)]
    [InlineData(0.02, false)]
    public void OverhungTiltedBoxEdgeDoesNotChargeAFalseRise(double budget, bool unpaidClimb)
    {
        using var scene = new SupportFootprintScene();
        Quaternion tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 3);
        scene.Install(new BoxShape(new(0.05f / MathF.Cos(MathF.PI / 3), 0.1f, 0.2f)),
            new(new Vector3(0.2f, 0.25f, 0) + Vector3.Transform(new Vector3(0, -0.1f, 0), tilt), tilt));
        using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
        ContactQueryContext queries = DryQueries.For(null, null, scene.World, lease, Tuning);
        float cosine = MathF.Cos(Tuning.MaxSlopeRadians);
        var startAxis = new Vector2(0.1f, 0);
        var axis = new Vector2(0.2f, 0);
        SupportSample support = queries.FindSupport(new(startAxis, 0.1f, 0.2f, 0.4f, 0.4f, cosine));
        float feetY = unpaidClimb ? 0.1f : support.Height;
        GroundSeatResult seat = GroundSeat.Resolve(queries, support, startAxis, feetY,
            axis, Vector2.UnitX, 0.2f, Tuning, cosine);
        Assert.Equal(SupportStatus.Walkable, support.Status);
        Assert.Equal(SeatOutcome.Seated, seat.Outcome);
        // The box's highest edge is X 0.25, Y 0.25 + 0.05 * tan(60 degrees).
        // Both axes overhang that edge within the disc, so its height field is level despite the face normal.
        double edgeHeight = 0.25 + 0.05 * Math.Sqrt(3);
        Assert.InRange(Math.Abs(support.Height - edgeHeight), 0, support.HeightError);
        Assert.InRange(Math.Abs(seat.FeetY - edgeHeight), 0, seat.Support.HeightError);
        Assert.Equal(support.Static, seat.Support.Static);
        Assert.Equal(support.FeatureId, seat.Support.FeatureId);

        GroundPlacement placed = GroundPlacement.Try(queries, support, feetY, startAxis, axis - startAxis,
            seat, 0.2f, Tuning, cosine, budget);

        output.WriteLine($"Box: start support {support.Height:R}, seat {seat.FeetY:R}, " +
            $"normal {support.Normal}, witness {support.Witness}, step part {seat.StepPart:R}, " +
            $"budget {budget:R}, placed {placed.Feet}, left {placed.Budget:R}");
        Assert.True(placed.Valid);
        Assert.Equal(seat.Support, placed.Support);
        Assert.InRange(Math.Abs(seat.StepPart), 0, support.HeightError + seat.Support.HeightError);
        Assert.True(placed.Feet.Y >= feetY,
            $"Unpaid climb lowered feet from {feetY:R} to {placed.Feet.Y:R} while support remains {seat.FeetY:R}.");
        Assert.True(placed.Feet.Y <= feetY + budget);
        double paid = unpaidClimb ? budget : 0;
        Assert.InRange(Math.Abs(placed.Feet.Y - (feetY + paid)), 0, seat.Support.HeightError);
        Assert.Equal(budget - paid, placed.Budget);
    }
}
