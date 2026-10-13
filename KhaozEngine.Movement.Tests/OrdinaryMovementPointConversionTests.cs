using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class OrdinaryMovementPointConversionTests
{
    [Theory]
    [InlineData(0f, 12.75f, -1, MovementAvailability.Known, true)]
    [InlineData(0f, 12.75f, 0, MovementAvailability.Known, false)]
    [InlineData(0f, 12.75f, 1, MovementAvailability.Known, false)]
    [InlineData(10f, 2.75f, -1, MovementAvailability.Unresolved, true)]
    [InlineData(10f, 2.75f, 0, MovementAvailability.Known, false)]
    [InlineData(10f, 2.75f, 1, MovementAvailability.Unresolved, false)]
    [InlineData(12.75f, 0f, -1, MovementAvailability.Unresolved, true)]
    [InlineData(12.75f, 0f, 0, MovementAvailability.Known, false)]
    [InlineData(12.75f, 0f, 1, MovementAvailability.Unresolved, false)]
    [InlineData(0f, 8.75f, -1, MovementAvailability.Known, false)]
    [InlineData(0f, 8.75f, 0, MovementAvailability.Known, true)]
    [InlineData(0f, 8.75f, 1, MovementAvailability.Known, true)]
    [InlineData(10f, -1.25f, -1, MovementAvailability.Unresolved, false)]
    [InlineData(10f, -1.25f, 0, MovementAvailability.Known, true)]
    [InlineData(10f, -1.25f, 1, MovementAvailability.Unresolved, true)]
    public void PointMembershipRequiresExactConversionAtSurfaceAndBed(float originY, float tangentCentreY,
        int neighbour, MovementAvailability expectedAvailability, bool canonicalWet)
    {
        float centreY = neighbour switch
        {
            -1 => MathF.BitDecrement(tangentCentreY),
            1 => MathF.BitIncrement(tangentCentreY),
            _ => tangentCentreY
        };
        Assert.Equal(canonicalWet, (double)centreY >= 8.75d - originY && (double)centreY < 12.75d - originY);
        using var scene = new OrdinaryContactScene(height: (_, _) => 8,
            extent: new(new(-20, 8, -20), new(20, 12, 20), 8, 8),
            water: MovementWaterField.Level("world", 12, 8), origin: new(8, originY, -16));
        using var lease = scene.Acquire();
        Vector3 centre = new(1, centreY, 1);
        Assert.Equal(MovementAvailability.Known, lease.RebuildSelection(scene.State(centre), out var selection));
        var point = lease.SampleCentreWater(new(centre, 0.25f, 0.75f, selection.Space, null));
        Assert.Equal(expectedAvailability, point.Availability);
        if (expectedAvailability == MovementAvailability.Known)
        {
            Assert.Equal(canonicalWet, point.InWater);
            if (canonicalWet)
            {
                Assert.Equal(8f - originY, point.Interval!.Value.LowerY);
                Assert.Equal(12f - originY, point.Interval!.Value.UpperY);
            }
        }
        else
        {
            Assert.Null(point.Domain);
            Assert.Null(point.Interval);
        }
    }

    [Theory]
    [InlineData(-4.0000005f)]
    [InlineData(-float.Epsilon)]
    public void PointIntervalConversionHasNoGeometricErrorAllowance(float bedY)
    {
        using var scene = new OrdinaryContactScene(height: (_, _) => -4,
            extent: new(new(-20, -5, -20), new(20, 12, 20), -4, -4),
            water: MovementWaterField.Level("world", 12, bedY), origin: new(8, 10, -16));
        using var lease = scene.Acquire();
        Vector3 centre = new(1, 0, 1);
        Assert.Equal(MovementAvailability.Known, lease.RebuildSelection(scene.State(centre), out var selection));
        var point = lease.SampleCentreWater(new(centre, 0.25f, 0.75f, selection.Space, null));
        Assert.Equal(MovementAvailability.Unresolved, point.Availability);
        Assert.Null(point.Domain);
        Assert.Null(point.Interval);
    }
}
