using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class OrdinaryMovementMalformedFieldTests
{
    [Theory]
    [InlineData("space", MovementStepOutcome.EnvironmentInvalid)]
    [InlineData("identity", MovementStepOutcome.EnvironmentInvalid)]
    [InlineData("gap", MovementStepOutcome.EnvironmentInvalid)]
    [InlineData("unordered", MovementStepOutcome.EnvironmentInvalid)]
    [InlineData("error", MovementStepOutcome.EnvironmentUnresolved)]
    public void MalformedFieldEvidenceRetainsExistingLeaseRefusal(string fault, MovementStepOutcome expected)
    {
        using var scene = new OrdinaryContactScene(water: new MalformedField(fault));
        using var lease = scene.Acquire();
        var input = scene.State(new(0, 0.75f, 0));
        var result = scene.Binding.StepTowards(input, Vector2.UnitX, false, 0.1f, lease);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(input.State.Position, result.State.State.Position);
    }

    sealed class MalformedField(string fault) : IMovementWaterField
    {
        public string SemanticIdentity => "malformed-fixture";
        public long Generation => 0;
        public MovementWaterExtent Extent => new(new(-20, -4, -20), new(20, 0, 20));
        public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body) =>
            new(MovementAvailability.Known, body.CurrentSpace, null, false, 1, null);
        public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, in MovementQueryIdentity identity,
            Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
        {
            if (fault == "space")
            {
                spans[0] = new(0, 1, 0, 1, true);
                contacts[0] = new(new("world", "bad"), new("world", "other"),
                    new(-4, 0, 0, true, "bed", "surface"), new(query.Body.Centre.X, query.Body.Centre.Z),
                    Vector3.UnitY, 0, "surface", 0, MovementContactOverlap.Tangent, MovementContactOverlap.Tangent);
                return new(MovementAvailability.Known, 1, 1, 1, 1, 0, identity);
            }
            if (fault == "gap")
            {
                spans[0] = new(0, 0.25f, 0, 0, true);
                spans[1] = new(0.5f, 1, 0, 0, true);
                return new(MovementAvailability.Known, 2, 2, 0, 0, 0, identity);
            }
            if (fault == "unordered")
            {
                spans[0] = new(0.5f, 1, 0, 0, true);
                spans[1] = new(0, 0.5f, 0, 0, true);
                return new(MovementAvailability.Known, 2, 2, 0, 0, 0, identity);
            }
            spans[0] = new(0, 1, 0, 0, true);
            return new(MovementAvailability.Known, 1, 1, 0, 0, fault == "error" ? 0.0011f : 0,
                fault == "identity" ? new("wrong", 1, "wrong") : identity);
        }
    }
}
