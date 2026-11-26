using System;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public sealed partial class ClothContactTests
{
    private const float Seconds = 1f / 120;
    private static readonly ClothMaterial Material = ClothDrapeTests.Woven with
    {
        StaticFriction = 0, KineticFriction = 0, NormalAirDrag = 0,
    };

    [Theory]
    [InlineData(.6f, false)]
    [InlineData(.6f, true)]
    [InlineData(-.6f, false)]
    [InlineData(-.6f, true)]
    public void Two_advancing_normals_balance_compression_and_relative_speed(float dot, bool reverse)
    {
        const float supportSpeed = .125f;
        Vector3 first = Vector3.UnitX, second = new(dot, .8f, 0);
        var contact = new ClothContactVelocity(2);
        float[] inverseMasses = [1];
        var node = new ClothTriangle(0, 0, 0);
        Vector3[] velocities = [Vector3.UnitZ];
        contact.Add(node, Vector3.UnitX, reverse ? second : first, supportSpeed, inverseMasses);
        contact.Add(node, Vector3.UnitX, reverse ? first : second, supportSpeed, inverseMasses);

        Assert.Equal(ClothStepStatus.Accepted, contact.ProjectAndCheck(velocities));

        Assert.InRange(Vector3.Dot(velocities[0], first) - supportSpeed, -.000001f, .000001f);
        Assert.InRange(Vector3.Dot(velocities[0], second) - supportSpeed, -.000001f, .000001f);
        Assert.Equal(1, velocities[0].Z);
    }

    [Fact]
    public void A_separating_normal_remains_unloaded_when_another_support_pushes()
    {
        var contact = new ClothContactVelocity(2);
        float[] inverseMasses = [1];
        var node = new ClothTriangle(0, 0, 0);
        Vector3[] velocities = [Vector3.UnitX];
        contact.Add(node, Vector3.UnitX, Vector3.UnitX, 0, inverseMasses);
        contact.Add(node, Vector3.UnitX, new Vector3(.6f, .8f, 0), 1, inverseMasses);

        Assert.Equal(ClothStepStatus.Accepted, contact.ProjectAndCheck(velocities));

        Assert.InRange(Vector3.Distance(velocities[0], new Vector3(1.24f, .32f, 0)), 0, .000001f);
    }

    [Fact]
    public void Contradictory_normal_speeds_leave_the_last_admissible_state_available()
    {
        float envelope = Material.HalfThickness + .00025f;
        Vector3[] rest = [Vector3.Zero, .1f * Vector3.UnitY, .1f * Vector3.UnitZ];
        ClothRig rig = Triangle(rest);
        var simulation = new ClothSimulation(rig, new ClothWorkLimits(4, 2, 16, 128));
        simulation.Reset(rest);
        ClothSupport[] first = [Plane(Vector3.UnitX, envelope + .0001f), Plane(-Vector3.UnitX, envelope + .0001f)];
        ClothSupport[] last = [Plane(Vector3.UnitX, envelope), Plane(-Vector3.UnitX, envelope)];

        ClothStepResult refusal = simulation.Advance(new ClothSubstep([], first, last, Seconds, Vector3.Zero));

        Assert.Equal(ClothStepStatus.UnresolvedContact, refusal.Status);
        Assert.Equal(rest, simulation.Positions.ToArray());
        Assert.Equal(ClothStepStatus.Accepted,
            simulation.Advance(new ClothSubstep([], [], [], Seconds, Vector3.Zero)).Status);
        Assert.Equal(rest, simulation.Positions.ToArray());

        static ClothSupport Plane(Vector3 normal, float distance) =>
            ClothSupport.Plane(normal, Quaternion.Identity, -distance * normal);
    }

    private static ClothRig Triangle(Vector3[] rest, ReadOnlySpan<int> pins = default) =>
        ClothRig.Create(rest, [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [new(0, 1, 2)], pins, [], Material);
}
