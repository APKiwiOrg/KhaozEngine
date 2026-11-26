using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public sealed partial class ClothContactTests
{
    [Theory]
    [InlineData(4, ClothStepStatus.ContactWork)]
    [InlineData(5, ClothStepStatus.Accepted)]
    public void Rejected_broad_pairs_are_charged_on_every_pass(int limit, ClothStepStatus expected)
    {
        Vector3[] rest = [Vector3.Zero, .06f * Vector3.UnitY, .06f * Vector3.UnitZ];
        var simulation = new ClothSimulation(Triangle(rest), ClothWorkLimits.LayeredContact with { MaxContactChecks = limit });
        simulation.Reset(rest);
        ClothSupport support = Ball(-Vector3.UnitX, Identity);

        ClothStepResult step = simulation.Advance(new ClothSubstep([], [support], [support], Seconds, Vector3.Zero));

        Assert.Equal(expected, step.Status);
        Assert.Equal(limit, step.ContactChecks);
        Assert.Equal(0, step.ContactCandidates);
        Assert.Equal(rest, simulation.Positions.ToArray());
        if (expected != ClothStepStatus.Accepted)
            Assert.Equal(Identity.PartId, step.PartId);
    }

    [Theory]
    [InlineData(4, 32, ClothStepStatus.ContactCapacity)]
    [InlineData(5, 1, ClothStepStatus.NormalVelocityWork)]
    [InlineData(5, 2, ClothStepStatus.Accepted)]
    public void Repeated_narrow_and_velocity_evaluations_have_separate_limits(int narrow, int velocity, ClothStepStatus expected)
    {
        Vector3[] rest = [Vector3.Zero, .06f * Vector3.UnitY, .06f * Vector3.UnitZ];
        var simulation = new ClothSimulation(Triangle(rest), ClothWorkLimits.LayeredContact with
        {
            MaxContactCandidates = narrow,
            MaxNormalVelocityChecks = velocity,
        });
        simulation.Reset(rest);
        ClothSupport support = Ball(-(.003f + Material.HalfThickness + .000125f) * Vector3.UnitX, Identity);

        ClothStepResult step = simulation.Advance(new ClothSubstep([], [support], [support], Seconds, Vector3.Zero));

        Assert.Equal(expected, step.Status);
        Assert.Equal(narrow, step.ContactCandidates);
        if (expected != ClothStepStatus.ContactCapacity)
            Assert.Equal(velocity, step.NormalVelocityChecks);
        if (expected != ClothStepStatus.Accepted)
            Assert.Equal(rest, simulation.Positions.ToArray());
    }

    [Fact]
    public void Display_and_recovery_have_independent_checked_envelopes()
    {
        Vector3[] rest = [Vector3.Zero, .06f * Vector3.UnitY, .06f * Vector3.UnitZ];
        Vector3[] display = [Vector3.One, Vector3.One, Vector3.One];
        var simulation = new ClothSimulation(Triangle(rest), ClothWorkLimits.LayeredContact with { MaxContactChecks = 1 });
        simulation.Reset(rest);
        ClothSupport support = Ball(-Vector3.UnitX, Identity);
        var poised = new ClothSubstep([], [support], [support], Seconds, 500 * Vector3.UnitX);

        ClothStepResult projection = simulation.ProjectDisplay(rest, display, poised);

        Assert.Equal(ClothStepStatus.ContactWork, projection.Status);
        Assert.Equal(1, projection.ContactChecks);
        Assert.All(display, node => Assert.Equal(Vector3.One, node));
        Assert.Equal(rest, simulation.Positions.ToArray());

        ClothStepResult recovered = simulation.Advance(poised, rest);

        Assert.Equal(ClothStepStatus.NodeTravel, recovered.Status);
        Assert.True(recovered.Recovered);
        Assert.Equal(1, recovered.FallbackWork.BroadPhaseVisits);
        Assert.Equal(rest, simulation.Positions.ToArray());
    }
}
