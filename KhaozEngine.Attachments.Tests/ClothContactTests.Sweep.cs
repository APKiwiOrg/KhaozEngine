using System;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public sealed partial class ClothContactTests
{
    [Fact]
    public void A_rotating_capsule_cannot_pass_through_the_interior_between_clear_poses()
    {
        const float angle = MathF.PI / 6, radius = .001f, reach = .025f, plane = .0255f;
        Vector3[] rest = [new(plane, -.036f, -.022f), new(plane, .036f, -.022f), new(plane, 0, .022f)];
        var simulation = new ClothSimulation(Triangle(rest), new ClothWorkLimits(4, 2, 64, 256));
        simulation.Reset(rest);
        var tip = reach * Vector3.UnitX;
        var capsule = new ColliderCapsule(tip, tip, radius);
        ClothSupport first = ClothSupport.Capsule(capsule, Pose(0), Vector3.Zero);
        ClothSupport last = ClothSupport.Capsule(capsule, Pose(1), Vector3.Zero);

        Assert.True(WorldTip(0).X + radius + Material.HalfThickness < plane);
        Assert.True(WorldTip(1).X + radius + Material.HalfThickness < plane);
        Assert.True(WorldTip(.5f).X + radius > plane);
        ClothStepResult step = simulation.Advance(new ClothSubstep([], [first], [last], Seconds, Vector3.Zero));

        Assert.Equal(ClothStepStatus.Accepted, step.Status);
        for (int sample = 0; sample <= 32; sample++)
        {
            float time = sample / 32f;
            float hardware = WorldTip(time).X + radius;
            for (int i = 0; i < rest.Length; i++)
            {
                float clearance = float.Lerp(rest[i].X, simulation.Positions[i].X, time) - Material.HalfThickness - hardware;
                Assert.True(clearance >= 0, $"Sample {sample}, node {i}, clearance {clearance:R} m.");
            }
        }

        static Quaternion Pose(float t) => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, float.Lerp(-angle, angle, t));
        Vector3 WorldTip(float t) => Vector3.Transform(tip, Pose(t));
    }
}
