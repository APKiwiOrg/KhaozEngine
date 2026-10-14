using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class OrdinaryMovementExactWaterPointTests
{
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void TwoThinSlabsClassifyTheExactFeetAtALossyOrigin(int neighbour, bool upperWet)
    {
        var upper = new WaterBox("upper", -10, 10, -10, 10, -3e-8f, -2e-8f);
        var lower = new WaterBox("lower", -10, 10, -10, 10, -7e-8f, -5e-8f);
        var field = new BoxWaterField(upper, lower);
        using var scene = new OrdinaryContactScene(height: (_, _) => -4, origin: new(0, 0.1f, 0),
            extent: new(new(-20, -5, -20), new(20, 4, 20), -4, -4), water: field);
        using var lease = scene.Acquire();
        float centreY = neighbour switch { -1 => MathF.BitDecrement(0.65f), 1 => MathF.BitIncrement(0.65f), _ => 0.65f };
        double feet = (double)centreY + 0.1f - 0.75;
        Assert.Equal((double)centreY, feet + 0.75 - 0.1f);
        Assert.Equal(upperWet, feet >= upper.LowerY && feet < upper.UpperY);
        Assert.False(feet >= lower.LowerY && feet < lower.UpperY);
        // The binary32 sum of the review centre lands on the dry feet 0, so a rounded sample misses the upper slab.
        if (neighbour == 0) Assert.Equal(0f, (centreY + 0.1f) - 0.75f);
        Vector3 centre = new(0, centreY, 0);
        Assert.Equal(MovementAvailability.Known, lease.RebuildSelection(scene.State(centre), out var selection));
        var point = lease.SampleCentreWater(new(centre, 0.25f, 0.75f, selection.Space, null));
        Assert.Equal(MovementAvailability.Known, point.Availability);
        Assert.Equal(upperWet, point.InWater);
        if (!upperWet)
        {
            Assert.Null(point.Interval);
            return;
        }
        Assert.Equal(new MovementDomainKey("world", "upper"), point.Domain);
        MovementWaterInterval interval = point.Interval!.Value;
        Assert.Equal("upper/bed", interval.LowerBoundaryId);
        double localFeet = (double)centreY - 0.75;
        Assert.True(interval.LowerY <= (double)upper.LowerY - 0.1f && interval.LowerY <= localFeet);
        Assert.True(interval.UpperY >= (double)upper.UpperY - 0.1f && interval.UpperY > localFeet);
    }

    [Fact]
    public void DryColumnOutsideAFiniteFieldIsKnownDryAtALossyOrigin()
    {
        var field = new BoxWaterField(new WaterBox("pond", 10, 11, -1, 0, -4, 0));
        using var scene = new OrdinaryContactScene(height: (_, _) => -4, origin: new(0, 0.3f, 0),
            extent: new(new(-20, -5, -20), new(20, 4, 20), -4, -4), water: field);
        using var lease = scene.Acquire();
        Vector3 centre = new(0, -2.1f, -0.5f);
        Assert.NotEqual((double)centre.Y + 0.3f, (double)(centre.Y + 0.3f));
        Assert.Equal(MovementAvailability.Known, lease.RebuildSelection(scene.State(centre), out var selection));
        var point = lease.SampleCentreWater(new(centre, 0.25f, 0.75f, selection.Space, null));
        Assert.Equal(MovementAvailability.Known, point.Availability);
        Assert.False(point.InWater);
        Assert.Null(point.Domain);
        Assert.Null(point.Interval);
    }

    internal readonly record struct WaterBox(string Name, float MinX, float MaxX, float MinZ, float MaxZ,
        float LowerY, float UpperY);

    /// <summary>Half-open boxes that classify the exact absolute centre feet independently of the pin.</summary>
    internal sealed class BoxWaterField(params WaterBox[] boxes) : IMovementWaterField
    {
        public string SemanticIdentity => "test-boxes";
        public long Generation => 0;
        public MovementWaterExtent Extent
        {
            get
            {
                Vector3 min = new(float.MaxValue), max = new(float.MinValue);
                foreach (WaterBox box in boxes)
                {
                    min = Vector3.Min(min, new(box.MinX, box.LowerY, box.MinZ));
                    max = Vector3.Max(max, new(box.MaxX, box.UpperY, box.MaxZ));
                }
                return new(min, max);
            }
        }

        public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body, Vector3 physicsOrigin)
        {
            foreach (WaterBox box in boxes)
                if (Inside(box, body.Centre, physicsOrigin, body.HalfHeight))
                    return new(MovementAvailability.Known, body.CurrentSpace, new("world", box.Name), true, 1,
                        new(box.LowerY, box.UpperY, box.UpperY, true, box.Name + "/bed", box.Name + "/surface"));
            return new(MovementAvailability.Known, body.CurrentSpace, null, false, 1, null);
        }

        public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Vector3 physicsOrigin,
            in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts) =>
            new(MovementAvailability.Unresolved, 0, 0, 0, 0, 0, identity);

        static bool Inside(WaterBox box, Vector3 local, Vector3 origin, float halfHeight) =>
            OrdinaryMovementEnvironment.ExactSign(local.X, origin.X, -box.MinX) >= 0 &&
            OrdinaryMovementEnvironment.ExactSign(local.X, origin.X, -box.MaxX) < 0 &&
            OrdinaryMovementEnvironment.ExactSign(local.Z, origin.Z, -box.MinZ) >= 0 &&
            OrdinaryMovementEnvironment.ExactSign(local.Z, origin.Z, -box.MaxZ) < 0 &&
            OrdinaryMovementEnvironment.ExactSign(local.Y, origin.Y, -halfHeight, -box.LowerY) >= 0 &&
            OrdinaryMovementEnvironment.ExactSign(local.Y, origin.Y, -halfHeight, -box.UpperY) < 0;
    }
}
