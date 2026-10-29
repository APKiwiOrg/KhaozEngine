using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class SupportedCapsulePoseTests
{
    public static IEnumerable<object[]> Dimensions()
    {
        foreach (short centimetres in new short[] { -160, -80, -40, -17, -10, -3, 1, 33 })
            foreach (float half in new[] { 0.6f, 0.75f, 0.9f, 1.3f })
                foreach (bool settle in new[] { false, true })
                    yield return new object[] { centimetres, half, settle };
    }

    [Theory]
    [MemberData(nameof(Dimensions))]
    public void GeneratedGroundedPoseNeverPutsExactFeetBelowSupport(short centimetres, float half, bool settle)
    {
        float bed = centimetres * 0.01f;
        var tuning = OrdinaryContactScene.DefaultTuning with
        {
            CapsuleHalfHeight = half,
            Water = new(WaterTraversalMode.WadeOnly),
        };
        using var scene = new OrdinaryContactScene(tuning, (_, _) => bed,
            extent: new(new(-20, -4, -20), new(20, 4, 20), bed, bed),
            water: MovementWaterField.Level("world", bed + 0.1f, bed));
        using var lease = scene.Acquire();
        var state = scene.State(new(0, bed + half + 0.01f, 0));

        var result = settle ? scene.Binding.SettlePlacement(state, lease)
            : scene.Binding.StepTowards(state, Vector2.UnitX, false, 1f / 60, lease);

        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.True(result.State.State.Grounded);
        float centre = result.State.State.Position.Y;
        double feet = (double)centre - half;
        Assert.True(feet >= bed, $"Bed {bed:R}, half {half:R}, centre {centre:R}, exact feet {feet:R}.");
        // The first representable centre above the exact sum is sufficient. No arbitrary lift is permitted.
        Assert.True((double)System.MathF.BitDecrement(centre) - half < bed);
        var body = new MovementBodyQuery(result.State.State.Position, tuning.CapsuleRadius, half,
            result.State.Selection!.Value.Space, result.State.Selection.Value.Support);
        var point = lease.SampleCentreWater(body);
        Assert.Equal(MovementAvailability.Known, point.Availability);
        Assert.True(point.InWater);
        Assert.Equal(bed, point.Interval!.Value.LowerY);
    }
}
