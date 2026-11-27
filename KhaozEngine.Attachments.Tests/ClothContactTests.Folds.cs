using System;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public sealed partial class ClothContactTests
{
    [Fact]
    public void Distant_parts_of_one_fold_clear_on_both_skins_without_launching()
    {
        float overlap = .0005f;
        Vector3[] rest =
        [
            new(0, 0, 0), new(.04f, 0, 0),
            new(0, .04f, 0), new(.04f, .04f, 0),
            new(0, .07f, .03f), new(.04f, .07f, .03f),
            new(0, .04f, 2 * Material.HalfThickness - overlap), new(.04f, .04f, 2 * Material.HalfThickness - overlap),
            new(0, 0, 2 * Material.HalfThickness - overlap), new(.04f, 0, 2 * Material.HalfThickness - overlap),
        ];
        Vector2[] uv = new Vector2[rest.Length];
        ClothTriangle[] triangles = new ClothTriangle[8];
        for (int i = 0; i < rest.Length; i++)
            uv[i] = new Vector2(.04f * (i % 2), .04f * (i / 2));
        for (int row = 0; row < 4; row++)
        {
            int node = row * 2;
            triangles[node] = new ClothTriangle(node, node + 1, node + 3);
            triangles[node + 1] = new ClothTriangle(node, node + 3, node + 2);
        }
        ClothRig rig = ClothRig.Create(rest, uv, triangles, [], [], Material);
        var contact = new ClothContact(rig, ClothWorkLimits.LayeredContact);
        var step = new ClothSubstep([], [], [], Seconds, Vector3.Zero);
        Vector3[] working = rest.AsSpan().ToArray();

        contact.Begin();
        for (int iteration = 0; iteration < 4; iteration++)
            Assert.Equal(ClothStepStatus.Accepted, contact.Project(rest, working, step));
        Vector3[] velocities = new Vector3[working.Length];
        for (int i = 0; i < working.Length; i++)
            velocities[i] = (working[i] - rest[i] - contact.PositionBias[i]) / Seconds;
        Assert.Equal(ClothStepStatus.Accepted, contact.Complete(rest, working, velocities, step));

        Assert.True(working[0].Z < 0);
        Assert.True(working[8].Z > rest[8].Z);
        Assert.True(DistanceToTriangle(working[8], working[0], working[1], working[3]) >= 2 * Material.HalfThickness);
        Assert.All(velocities, velocity => Assert.InRange(velocity.Length(), 0, .000001f));
    }
    [Fact]
    public void A_swept_fold_uses_the_free_witness_instead_of_distant_pinned_vertices()
    {
        Vector3[] opening =
        [
            new(.004f, -.036f, -.022f), new(.004f, .036f, -.022f), new(-.003f, 0, .022f),
            new(.002f, -.0015f, .012f), new(.002f, .0015f, .012f), new(.002f, 0, .016f),
        ];
        Vector3[] working = opening.AsSpan().ToArray();
        working[2].X = .004f;
        Vector2[] uv = [Vector2.Zero, Vector2.UnitX, Vector2.UnitY, Vector2.Zero, Vector2.UnitX, Vector2.UnitY];
        ClothRig rig = ClothRig.Create(opening, uv, [new(0, 1, 2), new(3, 4, 5)], [0, 1, 3, 4, 5], [], Material);
        var contact = new ClothContact(rig, ClothWorkLimits.LayeredContact);
        var step = new ClothSubstep(opening.AsSpan(0, 2), [], [], Seconds, Vector3.Zero);
        Assert.True(DistanceToTriangle(opening[3], opening[0], opening[1], opening[2]) > 2 * Material.HalfThickness);
        Assert.True(DistanceToTriangle(opening[3], working[0], working[1], working[2]) > 2 * Material.HalfThickness);

        contact.Begin();
        for (int iteration = 0; iteration < 4; iteration++)
            Assert.Equal(ClothStepStatus.Accepted, contact.Project(opening, working, step));
        Assert.Equal(ClothStepStatus.Accepted, contact.Complete(opening, working, new Vector3[working.Length], step));

        foreach (int pin in rig.Pins)
            Assert.Equal(opening[pin], working[pin]);
        for (int sample = 0; sample <= 32; sample++)
        {
            Vector3 apex = Vector3.Lerp(opening[2], working[2], sample / 32f);
            for (int node = 3; node < 6; node++)
                Assert.True(DistanceToTriangle(opening[node], opening[0], opening[1], apex) >=
                    2 * Material.HalfThickness - .00025f);
        }
    }

    [Theory]
    [InlineData(.001f, false, false)]
    [InlineData(.001f, false, true)]
    [InlineData(.00165f, true, false)]
    public void Display_checks_the_proposed_fold_separately_from_the_projected_surface(float proposedHeight,
        bool clear, bool useSupport)
    {
        (ClothRig rig, Vector3[] opening, Vector3[] pins) = DisplayFold();
        Vector3[] proposed = (Vector3[])opening.Clone();
        proposed[3].Z = proposedHeight;
        var simulation = new ClothSimulation(rig, ClothWorkLimits.LayeredContact);
        simulation.Reset(opening);
        ClothSupport[] supports = useSupport
            ? [ClothSupport.Capsule(new ColliderCapsule(Vector3.Zero, Vector3.Zero, .0001f), Quaternion.Identity, .0007f * Vector3.UnitZ)]
            : [];
        var pose = new ClothSubstep(pins, supports, supports, Seconds, Vector3.Zero);
        var displayed = new Vector3[proposed.Length];

        ClothStepResult result = simulation.ProjectDisplay(opening, proposed, displayed, pose,
            checkProposedSelf: true, out bool proposedSelfClear);

        Assert.Equal(ClothStepStatus.Accepted, result.Status);
        Assert.Equal(clear, proposedSelfClear);
        Assert.True(DistanceToTriangle(displayed[3], displayed[0], displayed[1], displayed[2]) >=
            2 * Material.HalfThickness - .00025f);
        Assert.Equal(proposedHeight, proposed[3].Z);
        Assert.Equal(opening, simulation.Positions.ToArray());
    }

    [Fact]
    public void Optional_self_check_preserves_the_projected_path_when_no_candidate_work_remains()
    {
        (ClothRig rig, Vector3[] opening, Vector3[] pins) = DisplayFold();
        Vector3[] proposed = (Vector3[])opening.Clone();
        proposed[3].Z = .00165f;
        var pose = new ClothSubstep(pins, [], [], Seconds, Vector3.Zero);
        var expected = new Vector3[opening.Length];
        var baseline = new ClothSimulation(rig, ClothWorkLimits.LayeredContact);
        ClothStepResult ordinary = baseline.ProjectDisplay(opening, proposed, expected, pose);
        Assert.Equal(ClothStepStatus.Accepted, ordinary.Status);
        Assert.True(ordinary.ContactCandidates > 0);
        var limited = new ClothSimulation(rig, ClothWorkLimits.LayeredContact with
        {
            MaxContactCandidates = ordinary.ContactCandidates,
        });
        var displayed = new Vector3[opening.Length];

        ClothStepResult result = limited.ProjectDisplay(opening, proposed, displayed, pose,
            checkProposedSelf: true, out bool proposedSelfClear);

        Assert.Equal(ClothStepStatus.Accepted, result.Status);
        Assert.False(proposedSelfClear);
        Assert.Equal(ordinary.ContactCandidates, result.ContactCandidates);
        Assert.Equal(expected, displayed);
    }

    [Fact]
    public void A_clear_proposed_endpoint_can_still_sweep_through_another_facet()
    {
        Vector3[] opening =
        [
            new(-.001f, -.002f, 0), new(.001f, -.002f, 0), new(0, .002f, 0),
            new(-.01f, 0, .002f), new(.02f, -.004f, .002f), new(.02f, .004f, .002f),
        ];
        Vector3[] proposed = (Vector3[])opening.Clone();
        proposed[3] = new Vector3(.01f, 0, -.002f);
        Vector2[] uv = [Vector2.Zero, Vector2.UnitX, Vector2.UnitY, Vector2.Zero, Vector2.UnitX, Vector2.UnitY];
        ClothRig rig = ClothRig.Create(opening, uv, [new(0, 1, 2), new(3, 4, 5)], [0, 1, 2, 4, 5], [], Material);
        var simulation = new ClothSimulation(rig, ClothWorkLimits.LayeredContact);
        var pose = new ClothSubstep([opening[0], opening[1], opening[2], opening[4], opening[5]], [], [], Seconds, Vector3.Zero);
        Vector3[] displayed = new Vector3[opening.Length];
        Assert.True(DistanceToTriangle(Vector3.Zero, opening[3], opening[4], opening[5]) > 2 * Material.HalfThickness);
        Assert.True(DistanceToTriangle(Vector3.Zero, proposed[3], proposed[4], proposed[5]) > 2 * Material.HalfThickness);
        Assert.Equal(Vector3.Zero, (opening[3] + proposed[3]) * .5f);

        ClothStepResult result = simulation.ProjectDisplay(opening, proposed, displayed, pose,
            checkProposedSelf: true, out bool proposedSelfClear);

        Assert.Equal(ClothStepStatus.Accepted, result.Status);
        Assert.False(proposedSelfClear);
        for (int sample = 0; sample <= 32; sample++)
        {
            Vector3 apex = Vector3.Lerp(opening[3], displayed[3], sample / 32f);
            Assert.True(DistanceToTriangle(Vector3.Zero, apex, displayed[4], displayed[5]) >=
                2 * Material.HalfThickness - .00025f);
        }
    }

    [Fact]
    public void Thin_cloth_does_not_certify_coincident_faces_at_the_middle_of_a_proposed_sweep()
    {
        const float height = 1f / 1024;
        Vector3[] opening =
        [
            new(-.005f, -.005f, 0), new(.005f, -.005f, 0), new(0, .005f, 0),
            new(-.04f, -.04f, height), new(.04f, -.04f, height), new(0, .04f, height),
            new(0, -.09f, height),
        ];
        Vector3[] proposed = (Vector3[])opening.Clone();
        for (int i = 3; i <= 5; i++) proposed[i].Z = -height;
        var coordinates = Array.ConvertAll(opening, point => new Vector2(point.X, point.Y));
        ClothTriangle[] triangles = [new(0, 1, 2), new(3, 4, 5), new(4, 3, 6)];
        ClothRig rig = ClothRig.Create(opening, coordinates, triangles, [0, 1, 2, 6], [],
            Material with { HalfThickness = .0001f });
        var simulation = new ClothSimulation(rig, ClothWorkLimits.LayeredContact);
        var support = ClothSupport.Plane(Vector3.UnitZ, Quaternion.Identity, -.00015f * Vector3.UnitZ);
        var pose = new ClothSubstep([opening[0], opening[1], opening[2], opening[6]], [support], [support], Seconds, Vector3.Zero);
        var displayed = new Vector3[opening.Length];
        var fixedFace = new ClothFacet(triangles[0], opening);
        var sweep = new ClothSelfSweep(fixedFace, fixedFace, new ClothFacet(triangles[1], opening),
            new ClothFacet(triangles[1], proposed));
        Assert.Equal(height, sweep.Sample(0).Distance);
        Assert.Equal(0, sweep.Sample(.5f).Distance);
        Assert.Equal(height, sweep.Sample(1).Distance);

        ClothStepResult result = simulation.ProjectDisplay(opening, proposed, displayed, pose,
            checkProposedSelf: true, out bool proposedSelfClear);

        Assert.Equal(ClothStepStatus.Accepted, result.Status);
        Assert.False(proposedSelfClear);
        for (int i = 3; i <= 5; i++)
            Assert.True(displayed[i].Z >= 2 * rig.Material.HalfThickness);
    }

    [Fact]
    public void A_mismatched_proposed_pin_declines_optional_checks_without_expanding_primary_work()
    {
        (ClothRig rig, Vector3[] opening, Vector3[] pins) = DisplayFold();
        Vector3[] proposed = (Vector3[])opening.Clone();
        proposed[0].X -= .003f;
        var simulation = new ClothSimulation(rig, ClothWorkLimits.LayeredContact);
        var pose = new ClothSubstep(pins, [], [], Seconds, Vector3.Zero);
        var ordinary = new Vector3[opening.Length];
        var displayed = new Vector3[opening.Length];

        ClothStepResult baseline = simulation.ProjectDisplay(opening, proposed, ordinary, pose);
        ClothStepResult checkedProposal = simulation.ProjectDisplay(opening, proposed, displayed, pose,
            checkProposedSelf: true, out bool proposedSelfClear);

        Assert.Equal(ClothStepStatus.Accepted, baseline.Status);
        Assert.Equal(baseline, checkedProposal);
        Assert.False(proposedSelfClear);
        Assert.Equal(ordinary, displayed);
        Assert.Equal(pins[0], displayed[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Adjacent_faces_only_certify_optional_contact_when_self_contact_is_enabled(bool enableSelfContact)
    {
        Vector3[] positions = [Vector3.Zero, .01f * Vector3.UnitX, .01f * Vector3.UnitY, new(.01f, .01f, 0)];
        Vector2[] coordinates = [Vector2.Zero, Vector2.UnitX, Vector2.UnitY, Vector2.One];
        ClothRig rig = ClothRig.Create(positions, coordinates, [new(0, 1, 2), new(1, 3, 2)], [], [], Material);
        var simulation = new ClothSimulation(rig, ClothWorkLimits.LayeredContact with { EnableSelfContact = enableSelfContact });
        var pose = new ClothSubstep([], [], [], Seconds, Vector3.Zero);
        Vector3[] displayed = new Vector3[positions.Length];

        ClothStepResult result = simulation.ProjectDisplay(positions, positions, displayed, pose,
            checkProposedSelf: true, out bool proposedSelfClear);

        Assert.Equal(ClothStepStatus.Accepted, result.Status);
        Assert.Equal(enableSelfContact, proposedSelfClear);
        Assert.Equal(positions, displayed);
    }

    private static (ClothRig Rig, Vector3[] Opening, Vector3[] Pins) DisplayFold()
    {
        Vector3[] opening =
        [
            new(-.02f, -.02f, 0), new(.02f, -.02f, 0), new(0, .02f, 0),
            new(0, 0, .002f), new(.006f, .006f, .013f), new(-.006f, .006f, .013f),
        ];
        Vector2[] uv = [Vector2.Zero, Vector2.UnitX, Vector2.UnitY, Vector2.Zero, Vector2.UnitX, Vector2.UnitY];
        ClothRig rig = ClothRig.Create(opening, uv, [new(0, 1, 2), new(3, 4, 5)], [0, 1, 2, 4, 5], [], Material);
        return (rig, opening, [opening[0], opening[1], opening[2], opening[4], opening[5]]);
    }
}
