using System;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public sealed partial class ClothContactTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Affine_recovery_checks_the_actual_hull_and_keeps_the_previous_state_on_failure(int transformCase)
    {
        Vector3[] rest = [new(.06f, 0, 0), new(.06f, .01f, 0), new(.06f, 0, .01f)];
        Vector3[] clipped = Array.ConvertAll(rest, point => point - .04f * Vector3.UnitX);
        var simulation = new ClothSimulation(Triangle(rest), ClothWorkLimits.LayeredContact);
        simulation.Reset(rest);
        var shell = Box(Vector3.Zero, new Vector3(.01f, .02f, .02f));
        ClothSupport coarse = Ball(Vector3.Zero, Identity);
        Matrix4x4 closing = Matrix4x4.CreateScale(4, transformCase == 1 ? 0 : 1, 1);
        if (transformCase == 2) closing.M11 = float.NaN;
        var hull = new ClothHullSupport(Identity, shell, Matrix4x4.Identity, closing);
        var step = new ClothSubstep([], [coarse], [coarse], Seconds, Vector3.Zero, [hull]);

        ClothStepResult refused = simulation.Advance(step, clipped);

        Assert.Equal(ClothStepStatus.UnsupportedHullTransform, refused.Status);
        Assert.Equal(Identity.PartId, refused.PartId);
        Assert.Equal(transformCase == 0 ? ClothStepStatus.UnresolvedContact : ClothStepStatus.UnsupportedHullTransform, refused.FallbackStatus);
        Assert.Equal(Identity.PartId, refused.FallbackPartId);
        Assert.Equal(rest, simulation.Positions.ToArray());

        ClothStepResult clear = simulation.Advance(step, rest);

        Assert.Equal(transformCase == 0, clear.Recovered);
        Assert.Equal(rest, simulation.Positions.ToArray());
    }

    [Fact]
    public void A_plane_move_into_a_later_support_refreshes_the_display_candidates()
    {
        Vector3[] rest = [new(0, 0, -.01f), new(0, .01f, -.01f), new(0, .01f, .01f), new(0, 0, .01f)];
        ClothRig rig = ClothRig.Create(rest, [Vector2.Zero, Vector2.UnitY, Vector2.One, Vector2.UnitX],
            [new(0, 1, 2), new(0, 2, 3)], [], [], Material);
        var simulation = new ClothSimulation(rig, ClothWorkLimits.LayeredContact);
        simulation.Reset(rest);
        var center = new Vector3(.00725f, -.000433f, 0);
        const float radius = .0002f;
        ClothSupport[] supports = [ClothSupport.Plane(Vector3.UnitX, Quaternion.Identity, .006f * Vector3.UnitX),
            ClothSupport.Capsule(new ColliderCapsule(Vector3.Zero, Vector3.Zero, radius), Quaternion.Identity, center, Identity)];
        var pose = new ClothSubstep([], supports, supports, Seconds, Vector3.Zero);
        Vector3[] displayed = new Vector3[rest.Length];

        ClothStepResult result = simulation.ProjectDisplay(rest, displayed, pose);

        Assert.Equal(ClothStepStatus.Accepted, result.Status);
        Assert.True(DistanceToTriangle(center, displayed[0], displayed[2], displayed[3]) >=
            radius + Material.HalfThickness - .00025f);
        Assert.All(displayed, point => Assert.True(point.X >= .006f + Material.HalfThickness - .00025f));
        Assert.Equal(rest, simulation.Positions.ToArray());
    }

    [Fact]
    public void Spinning_a_centred_sphere_does_not_launch_stationary_cloth()
    {
        const float radius = .003f;
        float x = radius + Material.HalfThickness + .00025f;
        Vector3[] rest = [new(x, 0, 0), new(x, .01f, 0), new(x, 0, .01f)];
        var simulation = new ClothSimulation(Triangle(rest), ClothWorkLimits.LayeredContact);
        simulation.Reset(rest);
        var sphere = new ColliderCapsule(Vector3.Zero, Vector3.Zero, radius);
        ClothSupport first = ClothSupport.Capsule(sphere, Quaternion.Identity, Vector3.Zero);
        ClothSupport last = ClothSupport.Capsule(sphere, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), Vector3.Zero);

        Assert.Equal(ClothStepStatus.Accepted,
            simulation.Advance(new ClothSubstep([], [first], [last], Seconds, Vector3.Zero)).Status);
        Assert.Equal(ClothStepStatus.Accepted,
            simulation.Advance(new ClothSubstep([], [], [], Seconds, Vector3.Zero)).Status);
        Assert.InRange(Vector3.Distance(rest[0], simulation.Positions[0]), 0, .000001f);
    }

    [Fact]
    public void An_offset_rotating_support_contributes_its_closing_normal_speed()
    {
        const float radius = .003f, angle = .25f, lever = .02f, translation = .0001f;
        float x = radius + Material.HalfThickness + .00025f;
        Vector3[] rest = [new(x, lever, 0), new(x, lever + .01f, 0), new(x, lever, .01f)];
        ClothRig rig = Triangle(rest);
        var contact = new ClothContact(rig, ClothWorkLimits.LayeredContact);
        Vector3[] velocities = new Vector3[3];
        Vector3 tip = lever * Vector3.UnitY;
        var sphere = new ColliderCapsule(tip, tip, radius);
        ClothSupport first = ClothSupport.Capsule(sphere, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle), -translation * Vector3.UnitX);
        ClothSupport last = ClothSupport.Capsule(sphere, Quaternion.Identity, Vector3.Zero);
        var step = new ClothSubstep([], [first], [last], Seconds, Vector3.Zero);

        contact.Begin();
        Assert.Equal(ClothStepStatus.Accepted, contact.Prepare(step));
        Assert.Equal(ClothStepStatus.Accepted, contact.Complete(rest, rest, velocities, step));

        float expected = (translation + angle * lever) / Seconds;
        Assert.InRange(velocities[0].X, expected - .000002f, expected + .000002f);
    }

    [Theory]
    [InlineData(64, ClothStepStatus.Accepted)]
    [InlineData(65, ClothStepStatus.SupportCapacity)]
    public void A_replaced_capsule_still_counts_towards_the_authored_rigid_limit(int count, ClothStepStatus expected)
    {
        Vector3[] rest = [Vector3.Zero, .01f * Vector3.UnitY, .01f * Vector3.UnitZ];
        var simulation = new ClothSimulation(Triangle(rest), ClothWorkLimits.LayeredContact);
        simulation.Reset(rest);
        var supports = new ClothSupport[count];
        supports[0] = Ball(-Vector3.UnitX, Identity);
        for (int i = 1; i < supports.Length; i++)
            supports[i] = Ball(-Vector3.UnitX, Identity with { PartId = (uint)i });
        var hull = new ClothHullSupport(Identity, Box(-Vector3.UnitX, new Vector3(.01f)), Matrix4x4.Identity, Matrix4x4.Identity);

        ClothStepResult refused = simulation.Advance(new ClothSubstep([], supports, supports, Seconds, Vector3.Zero, [hull]));

        Assert.Equal(expected, refused.Status);
        Assert.Equal(expected == ClothStepStatus.Accepted ? null : 64u, refused.PartId);
        Assert.Equal(rest, simulation.Positions.ToArray());
    }
}
