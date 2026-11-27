using System;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public sealed class ClothNumericsTests
{
    private const float StepSeconds = 1f / 120;
    private static readonly ClothMaterial Material = ClothDrapeTests.Woven with { NormalAirDrag = 0 };

    [Fact]
    public void A_refused_direct_factor_still_charges_its_full_bound()
    {
        Vector3[] rest = [Vector3.Zero, .1f * Vector3.UnitX, .1f * Vector3.UnitY];
        var rig = ClothRig.Create(rest, [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [new(0, 1, 2)],
            [0, 1], [], Material with { DampingSeconds = float.MaxValue });
        var simulation = new ClothSimulation(rig, new ClothWorkLimits(4, 0, 0, 0));
        simulation.Reset(rest);

        ClothStepResult step = simulation.Advance(new ClothSubstep(rest.AsSpan(0, 2), [], [], StepSeconds, Vector3.Zero));

        Assert.Equal(ClothStepStatus.NonfiniteState, step.Status);
        Assert.Equal(4, step.MaterialFactorProducts);
        Assert.Equal(0, simulation.LastAdvanceMaterialWork.FactorProducts);
        Assert.Equal(rest, simulation.Positions.ToArray());
    }

    [Fact]
    public void A_contact_margin_does_not_launch_a_stationary_sheet()
    {
        Vector3[] rest = [new(0, Material.HalfThickness, 0), new(.01f, Material.HalfThickness, 0),
            new(0, Material.HalfThickness, .01f)];
        ClothRig rig = MakeTriangle(rest);
        var simulation = new ClothSimulation(rig, new ClothWorkLimits(4, 1, 12, 64));
        simulation.Reset(rest);
        ClothSupport floor = ClothSupport.Plane(Vector3.UnitY, Quaternion.Identity, Vector3.Zero);
        var step = new ClothSubstep([], [floor], [floor], StepSeconds, Vector3.Zero);

        Assert.Equal(ClothStepStatus.Accepted, simulation.Advance(step).Status);
        Vector3[] touched = simulation.Positions.ToArray();
        Assert.Equal(ClothStepStatus.Accepted, simulation.Advance(step).Status);

        for (int i = 0; i < touched.Length; i++)
            Assert.InRange(Vector3.Distance(touched[i], simulation.Positions[i]), 0, .000001f);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Loading_contact_removes_inward_velocity_relative_to_the_support(bool capsule, bool moving)
    {
        const float margin = .00025f, openingGap = .00025f;
        const float radius = .025f, rise = .0004f;
        float height = Material.HalfThickness + margin + openingGap + (capsule ? radius : 0);
        Vector3[] rest = [new(0, height, 0), new(.1f, height, 0), new(0, height, .1f)];
        var simulation = new ClothSimulation(MakeTriangle(rest), new ClothWorkLimits(4, 1, 12, 64));
        simulation.Reset(rest);
        float carried = moving ? rise : 0;
        ClothSupport first = Support(Vector3.Zero), last = Support(carried * Vector3.UnitY);

        Assert.Equal(ClothStepStatus.Accepted,
            simulation.Advance(new ClothSubstep([], [first], [last], StepSeconds, -9.81f * Vector3.UnitY)).Status);
        Vector3 bearingPoint = simulation.Positions[0];
        Assert.InRange(bearingPoint.Y, height - openingGap + carried - .00001f, height - openingGap + carried + .00001f);
        Assert.Equal(ClothStepStatus.Accepted,
            simulation.Advance(new ClothSubstep([], [], [], StepSeconds, Vector3.Zero)).Status);

        Assert.InRange(simulation.Positions[0].Y - bearingPoint.Y, carried - .00001f, carried + .00001f);

        ClothSupport Support(Vector3 translation) => capsule
            ? ClothSupport.Capsule(new ColliderCapsule(Vector3.Zero, Vector3.Zero, radius), Quaternion.Identity, translation)
            : ClothSupport.Plane(Vector3.UnitY, Quaternion.Identity, translation);
    }

    [Fact]
    public void Touching_contact_preserves_outward_normal_velocity()
    {
        const float travel = .000125f;
        float height = Material.HalfThickness + .00025f - travel;
        Vector3[] rest = [new(0, height, 0), new(.01f, height, 0), new(0, height, .01f)];
        var simulation = new ClothSimulation(MakeTriangle(rest), new ClothWorkLimits(4, 1, 12, 64));
        simulation.Reset(rest);
        ClothSupport floor = ClothSupport.Plane(Vector3.UnitY, Quaternion.Identity, Vector3.Zero);
        Vector3 acceleration = (travel / (StepSeconds * StepSeconds)) * Vector3.UnitY;

        Assert.Equal(ClothStepStatus.Accepted,
            simulation.Advance(new ClothSubstep([], [floor], [floor], StepSeconds, acceleration)).Status);
        Assert.Equal(ClothStepStatus.Accepted,
            simulation.Advance(new ClothSubstep([], [], [], StepSeconds, Vector3.Zero)).Status);

        Assert.InRange(simulation.Positions[0].Y - height, 2 * travel - .000001f, 2 * travel + .000001f);
    }

    [Fact]
    public void Reset_rejects_a_collapsed_material_frame_without_replacing_the_fit()
    {
        Vector3[] rest = [Vector3.Zero, .01f * Vector3.UnitX, .01f * Vector3.UnitY];
        Vector3[] collapsed = [Vector3.Zero, .01f * Vector3.UnitX, .02f * Vector3.UnitX];
        ClothRig rig = MakeTriangle(rest, [0, 1, 2]);
        var simulation = new ClothSimulation(rig, new ClothWorkLimits(4, 0, 0, 0));
        simulation.Reset(rest);

        Assert.Throws<ArgumentException>(() => simulation.Reset(collapsed));

        Assert.Equal(rest, simulation.Positions.ToArray());
    }

    [Fact]
    public void An_infeasible_pin_step_cannot_publish_a_collapsed_triangle()
    {
        Vector3[] rest = [Vector3.Zero, .01f * Vector3.UnitX, .01f * Vector3.UnitY];
        Vector3[] collapsed = [Vector3.Zero, .01f * Vector3.UnitX, .02f * Vector3.UnitX];
        ClothRig rig = MakeTriangle(rest, [0, 2]);
        var simulation = new ClothSimulation(rig, new ClothWorkLimits(4, 0, 0, 0));
        simulation.Reset(rest);

        ClothStepResult step = simulation.Advance(new ClothSubstep([collapsed[0], collapsed[2]], [], [], StepSeconds, Vector3.Zero));

        Assert.Equal(ClothStepStatus.CollapsedMaterial, step.Status);
        Assert.Equal(0, step.MaterialFactorProducts);
        Assert.Equal(rest, simulation.Positions.ToArray());
    }

    [Fact]
    public void Two_faces_joined_only_by_a_vertex_do_not_form_a_manifold_fan()
    {
        Vector3[] rest = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY, -Vector3.UnitX, -Vector3.UnitY];
        Vector2[] coordinates = [Vector2.Zero, Vector2.UnitX, Vector2.UnitY, -Vector2.UnitX, -Vector2.UnitY];
        ClothTriangle[] triangles = [new(0, 1, 2), new(0, 3, 4)];

        ClothRigException refusal = Assert.Throws<ClothRigException>(
            () => ClothRig.Create(rest, coordinates, triangles, [], [], Material));

        Assert.Equal(ClothRigRefusal.NonManifoldVertex, refusal.Reason);
        Assert.Equal(0, refusal.ElementIndex);
    }

    [Fact]
    public void An_edge_can_slip_after_the_prior_vertex_contact_unloads()
    {
        const float envelope = .005f;
        Vector3[] opening = [envelope * new Vector3(.8f, 0, 0), envelope * new Vector3(.9f, .4f, 0),
            envelope * new Vector3(1.2f, .3f, 0)];
        ClothRig rig = MakeTriangle(opening);
        var limits = new ClothWorkLimits(4, 1, 12, 64);
        var contact = new ClothContact(rig, limits);
        var newEdge = new ClothContact(rig, limits);
        Vector3[] working = opening.AsSpan().ToArray();
        var sphere = new ColliderCapsule(Vector3.Zero, Vector3.Zero, envelope - Material.HalfThickness - .00025f);
        ClothSupport first = ClothSupport.Capsule(sphere, Quaternion.Identity, -.5f * envelope * Vector3.UnitX);
        ClothSupport last = ClothSupport.Capsule(sphere, Quaternion.Identity, Vector3.Zero);
        var step = new ClothSubstep([], [first], [last], StepSeconds, Vector3.Zero);

        contact.Begin();
        Assert.Equal(ClothStepStatus.Accepted, contact.Project(opening, working, step));
        Assert.InRange(working[0].X, envelope - .000001f, envelope + .000001f);
        Vector3[] edgeOnly = working.AsSpan().ToArray();

        newEdge.Begin();
        Assert.Equal(ClothStepStatus.Accepted, newEdge.Project(opening, edgeOnly, step));
        Assert.Equal(ClothStepStatus.Accepted, contact.Project(opening, working, step));

        for (int i = 0; i < working.Length; i++)
            Assert.InRange(Vector3.Distance(edgeOnly[i], working[i]), 0, .000001f);
    }

    private static ClothRig MakeTriangle(Vector3[] rest, ReadOnlySpan<int> pins = default) =>
        ClothRig.Create(rest, [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [new(0, 1, 2)], pins, [], Material);
}
