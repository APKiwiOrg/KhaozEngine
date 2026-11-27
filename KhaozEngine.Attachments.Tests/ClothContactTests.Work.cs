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
    [Fact]
    public void A_support_query_needs_only_the_coverage_tree_before_self_contact()
    {
        (ClothRig rig, Vector3[] rest) = IndependentContactFaces();
        // Six particle bounds, three tree nodes and one rejected support query.
        const int visits = 10;
        var work = new ClothContactWorkspace(ClothWorkLimits.LayeredContact with { MaxContactChecks = visits });
        var tree = new ClothSelfBroadPhase(rig, visits, 1);

        Assert.True(tree.EnsureCoverage(rest, rest, Material.HalfThickness, work));
        Assert.True(tree.CollectSupport(0, new ClothBounds(2 * Vector3.One, 3 * Vector3.One), work, out var faces));

        Assert.True(faces.IsEmpty);
        Assert.Equal(visits, work.Checks);
    }

    [Fact]
    public void A_self_query_dispatches_only_pairs_with_eligible_faces()
    {
        (ClothRig rig, Vector3[] rest) = IndependentContactFaces();
        // Six particle bounds, three tree nodes and one spatial pair.
        const int visits = 10;
        var work = new ClothContactWorkspace(ClothWorkLimits.LayeredContact with { MaxContactChecks = visits });
        var tree = new ClothSelfBroadPhase(rig, visits, 0);

        Assert.True(tree.EnsureCoverage(rest, rest, Material.HalfThickness, work));
        Assert.True(tree.EnsurePairs(work));

        Assert.True(tree.Pairs.IsEmpty);
        Assert.Equal(visits, work.Checks);
    }

    [Fact]
    public void Moving_one_corner_refits_its_support_bounds_within_the_remaining_budget()
    {
        var fixture = new ClothDrapeFixture();
        Vector3[] opening = fixture.Fitted;
        Vector3[] closing = (Vector3[])opening.Clone();
        int node = closing.Length - 1;
        int initialRefit = opening.Length + 2 * fixture.Rig.Triangles.Length - 1;
        int maxVisits = initialRefit + 64;
        var work = new ClothContactWorkspace(ClothWorkLimits.LayeredContact with { MaxContactChecks = maxVisits });
        var tree = new ClothSelfBroadPhase(fixture.Rig, maxVisits, 1);
        Assert.True(tree.EnsureCoverage(opening, closing, Material.HalfThickness, work));

        Vector3 escaped = closing[node] + .2f * Vector3.UnitZ;
        var probe = new ClothBounds(escaped, escaped);
        Assert.True(tree.CollectSupport(0, probe, work, out var original));
        Assert.True(original.IsEmpty);
        closing[node] = escaped;
        tree.RecordMove(node, escaped, work);

        Assert.True(tree.EnsureCoverage(opening, closing, Material.HalfThickness, work));
        Assert.True(tree.CollectSupport(0, probe, work, out var moved));
        int[] observed = moved.ToArray();
        for (int face = 0; face < fixture.Rig.Triangles.Length; face++)
        {
            ClothTriangle triangle = fixture.Rig.Triangles[face];
            if (triangle.A == node || triangle.B == node || triangle.C == node)
                Assert.Contains(face, observed);
        }
    }

    [Fact]
    public void A_refit_updates_only_changed_faces_in_an_existing_support_query()
    {
        var fixture = new ClothDrapeFixture();
        Vector3[] opening = fixture.Fitted;
        Vector3[] closing = (Vector3[])opening.Clone();
        int node = closing.Length - 1;
        int faceCount = fixture.Rig.Triangles.Length;
        int treeNodes = 2 * faceCount - 1;
        int maxVisits = opening.Length + 2 * treeNodes + 64;
        var work = new ClothContactWorkspace(ClothWorkLimits.LayeredContact with { MaxContactChecks = maxVisits });
        var tree = new ClothSelfBroadPhase(fixture.Rig, maxVisits, 1);
        var bounds = new ClothBounds(-2 * Vector3.One, 2 * Vector3.One);
        Assert.True(tree.EnsureCoverage(opening, closing, Material.HalfThickness, work));
        Assert.True(tree.CollectSupport(0, bounds, work, out var initial));
        Assert.Equal(faceCount, initial.Length);

        closing[node] += .2f * Vector3.UnitZ;
        tree.RecordMove(node, closing[node], work);
        Assert.True(tree.EnsureCoverage(opening, closing, Material.HalfThickness, work));

        Assert.True(tree.CollectSupport(0, bounds, work, out var updated));
        Assert.Equal(faceCount, updated.Length);
    }

    private static (ClothRig Rig, Vector3[] Positions) IndependentContactFaces()
    {
        Vector3[] rest =
        [
            Vector3.Zero, .01f * Vector3.UnitX, .01f * Vector3.UnitY,
            Vector3.UnitZ, Vector3.UnitZ + .01f * Vector3.UnitX, Vector3.UnitZ + .01f * Vector3.UnitY,
        ];
        ClothRig rig = ClothRig.Create(rest,
            [Vector2.Zero, Vector2.UnitX, Vector2.UnitY, Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
            [new(0, 1, 2), new(3, 4, 5)], [], [], Material);
        return (rig, rest);
    }
}
