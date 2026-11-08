using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class RebasedSupportedCapsulePoseTests
{
    public static IEnumerable<object[]> Dimensions()
    {
        foreach (short centimetres in new short[] { -80, -40, -17, -10 })
            foreach (float half in new[] { 0.6f, 0.75f, 1.3f })
                foreach (float origin in new[] { 0.1f, 1.1f, 3.3f, -3.3f })
                    foreach (bool settle in new[] { false, true })
                        yield return new object[] { centimetres, half, origin, settle };
    }

    [Theory]
    [MemberData(nameof(Dimensions))]
    public void WorldSupportToFrameConversionKeepsGeneratedGroundedPosesWet(short centimetres, float half,
        float origin, bool settle)
    {
        float bed = centimetres * 0.01f;
        var tuning = OrdinaryContactScene.DefaultTuning with { CapsuleHalfHeight = half, Water = new(WaterTraversalMode.WadeOnly) };
        using var scene = new OrdinaryContactScene(tuning, (_, _) => bed,
            extent: new(new(-20, -4, -20), new(20, 4, 20), bed, bed),
            water: MovementWaterField.Level("world", bed + 0.1f, bed), origin: new(0, origin, 0));
        using var lease = scene.Acquire();
        var state = scene.State(new(0, bed - origin + half + 0.01f, 0));
        var result = settle ? scene.Binding.SettlePlacement(state, lease)
            : scene.Binding.StepTowards(state, Vector2.UnitX, false, 1f / 60, lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.True(result.State.State.Grounded);
        double feet = (double)result.State.State.Position.Y + origin - half;
        Assert.True(feet >= bed, $"Bed {bed:R}, origin {origin:R}, half {half:R}, exact world feet {feet:R}.");
        var body = new MovementBodyQuery(result.State.State.Position, tuning.CapsuleRadius, half,
            result.State.Selection!.Value.Space, result.State.Selection.Value.Support);
        var point = lease.SampleCentreWater(body);
        Assert.Equal(MovementAvailability.Known, point.Availability);
        Assert.True(point.InWater);
    }
}
