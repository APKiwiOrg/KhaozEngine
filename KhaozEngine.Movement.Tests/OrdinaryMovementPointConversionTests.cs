using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class OrdinaryMovementPointConversionTests
{
    [Theory]
    [InlineData(0f, 12.75f, -1, true)]
    [InlineData(0f, 12.75f, 0, false)]
    [InlineData(0f, 12.75f, 1, false)]
    [InlineData(10f, 2.75f, -1, true)]
    [InlineData(10f, 2.75f, 0, false)]
    [InlineData(10f, 2.75f, 1, false)]
    [InlineData(12.75f, 0f, -1, true)]
    [InlineData(12.75f, 0f, 0, false)]
    [InlineData(12.75f, 0f, 1, false)]
    [InlineData(0f, 8.75f, -1, false)]
    [InlineData(0f, 8.75f, 0, true)]
    [InlineData(0f, 8.75f, 1, true)]
    [InlineData(10f, -1.25f, -1, false)]
    [InlineData(10f, -1.25f, 0, true)]
    [InlineData(10f, -1.25f, 1, true)]
    public void PointMembershipIsExactAtTheSurfaceAndBed(float originY, float tangentCentreY, int neighbour,
        bool canonicalWet)
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
        Assert.Equal(MovementAvailability.Known, point.Availability);
        Assert.Equal(canonicalWet, point.InWater);
        if (!canonicalWet)
        {
            Assert.Null(point.Domain);
            Assert.Null(point.Interval);
            return;
        }
        Assert.Equal(8f - originY, point.Interval!.Value.LowerY);
        Assert.Equal(12f - originY, point.Interval!.Value.UpperY);
    }

    [Theory]
    [InlineData(-4.0000005f, 0f, 0.75f, -14.000001f)]
    [InlineData(-float.Epsilon, 0f, 0.75f, -10.000001f)]
    [InlineData(-4.0000005f, -2.3841858E-07f, 14f, -14.000001f)]
    public void PointIntervalConvertsOutwardAroundTheLocalFeet(float bedY, float centreY, float halfHeight,
        float expectedLower)
    {
        using var scene = new OrdinaryContactScene(height: (_, _) => -4,
            extent: new(new(-20, -5, -20), new(20, 40, 20), -4, -4),
            water: MovementWaterField.Level("world", 12, bedY), origin: new(8, 10, -16));
        using var lease = scene.Acquire();
        Vector3 centre = new(1, centreY, 1);
        Assert.Equal(MovementAvailability.Known, lease.RebuildSelection(scene.State(centre), out var selection));
        // The absolute feet are wet in every row. The expected local bed is the next binary32 value at or
        // below the exact local bed, so the last row's local feet stay inside the reported interval.
        Assert.True((double)centreY + 10 - halfHeight >= bedY);
        Assert.True((double)expectedLower < (double)bedY - 10);
        var point = lease.SampleCentreWater(new(centre, 0.25f, halfHeight, selection.Space, null));
        Assert.Equal(MovementAvailability.Known, point.Availability);
        Assert.True(point.InWater);
        Assert.Equal(expectedLower, point.Interval!.Value.LowerY);
        Assert.Equal(2f, point.Interval!.Value.UpperY);
        Assert.True((double)centreY - halfHeight >= point.Interval!.Value.LowerY);
    }

    [Theory]
    [InlineData(256f, 0f, 0f)]
    [InlineData(256f, 0.3f, -128f)]
    [InlineData(4096f, 0f, 4096f)]
    public void BodyWellUnderALevelSurfaceAtAFramedOriginIsKnownWet(float originX, float originY, float originZ)
    {
        using var scene = new OrdinaryContactScene(height: (_, _) => -4, origin: new(originX, originY, originZ),
            extent: new(new(originX - 20, -5, originZ - 20), new(originX + 20, 4, originZ + 20), -4, -4),
            water: MovementWaterField.Level("world", 0, -4),
            boundary: MovementBoundary.Rectangle(originX - 20, originZ - 20, originX + 20, originZ + 20));
        using var lease = scene.Acquire();
        Vector3 centre = new(0.4f, -2.1f, 0.3f);
        Assert.Equal(MovementAvailability.Known, lease.RebuildSelection(scene.State(centre), out var selection));
        var point = lease.SampleCentreWater(new(centre, 0.25f, 0.75f, selection.Space, null));
        Assert.Equal(MovementAvailability.Known, point.Availability);
        Assert.True(point.InWater);
        Assert.Equal(new MovementDomainKey("world", "level"), point.Domain);
        Assert.Equal(-4f - originY, point.Interval!.Value.LowerY);
        Assert.Equal(0f - originY, point.Interval!.Value.UpperY);
    }
}
