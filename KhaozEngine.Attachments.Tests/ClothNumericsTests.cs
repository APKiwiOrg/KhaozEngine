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
        ClothRig rig = MakeTriangle(rest, [0, 1, 2]);
        var simulation = new ClothSimulation(rig, new ClothWorkLimits(4, 0, 0, 0));
        simulation.Reset(rest);

        ClothStepResult step = simulation.Advance(new ClothSubstep(collapsed, [], [], StepSeconds, Vector3.Zero));

        Assert.Equal(ClothStepStatus.CollapsedMaterial, step.Status);
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
        Assert.Equal(ClothStepStatus.Accepted, contact.Project(opening, working, step, verify: false));
        Assert.InRange(working[0].X, envelope - .000001f, envelope + .000001f);
        Vector3[] edgeOnly = working.AsSpan().ToArray();

        newEdge.Begin();
        Assert.Equal(ClothStepStatus.Accepted, newEdge.Project(opening, edgeOnly, step, verify: false));
        Assert.Equal(ClothStepStatus.Accepted, contact.Project(opening, working, step, verify: false));

        for (int i = 0; i < working.Length; i++)
            Assert.InRange(Vector3.Distance(edgeOnly[i], working[i]), 0, .000001f);
    }

    private static ClothRig MakeTriangle(Vector3[] rest, ReadOnlySpan<int> pins = default) =>
        ClothRig.Create(rest, [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [new(0, 1, 2)], pins, [], Material);
}
