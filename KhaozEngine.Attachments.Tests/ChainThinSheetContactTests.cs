using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Attachments;
using Xunit;
using static KhaozEngine.Attachments.ChainCapsuleCollision;
using static KhaozEngine.Tests.Attachments.ChainSimulationTests;

namespace KhaozEngine.Tests.Attachments;

public sealed class ChainThinSheetContactTests
{
    private const float StepSeconds = 1f / 120;
    private static readonly Matrix4x4 Identity = Matrix4x4.Identity;

    [Fact]
    public void An_axis_beyond_a_nearly_collinear_sheet_leaves_the_joint_frames_unchanged()
    {
        // Regression for #1902.
        var settings = Settings with { Gravity = 0, Radius = 1 };
        var tip = new Vector3(8388108, 8388107, 0);
        var left = Chain(tip, settings, Joint(Vector3.Zero));
        var right = Chain(tip, settings, Joint(new(-1, -1, 0)));
        var rig = new ChainRig(1, [left, right], [new(0, 1) { Stretch = 1 }]);
        var capsule = new ColliderCapsule(new(8388236, 8388234, -8387980), new(8388236, 8388235, 128), 1);
        var reference = new ChainSimulation(rig, 0);
        var tested = new ChainSimulation(rig, 1);
        reference.Step([Identity], [], Identity, 0);
        tested.Step([Identity], [capsule], Identity, 0);

        for (int step = 1; step <= 4; step++)
        {
            reference.Step([Identity], [], Identity, StepSeconds);
            tested.Step([Identity], [capsule], Identity, StepSeconds);
            var a = reference.JointsFromAnchor[0];
            var b = reference.JointsFromAnchor[1];

            // X is affine over each face and over the capsule axis, so these extrema give a lower bound on distance.
            float maximum = MathF.Max(MathF.Max(a.Translation.X, b.Translation.X),
                MathF.Max(Vector3.Transform(tip, a).X, Vector3.Transform(tip, b).X));
            Assert.True(MathF.Min(capsule.Start.X, capsule.End.X) - maximum > settings.Radius + capsule.Radius);
            Assert.True(MemoryMarshal.AsBytes(reference.JointsFromAnchor)
                .SequenceEqual(MemoryMarshal.AsBytes(tested.JointsFromAnchor)), $"Joint frames differ at step {step}.");
        }
    }

    [Fact]
    public void A_long_axis_preserves_the_separation_from_a_translated_thin_face()
    {
        const float firstX = 4096;
        const float length = 65536;
        float axisExtent = MathF.ScaleB(1, 80);
        var triangle = new ChainContactTriangle(new(firstX, 0, 0), new(firstX + length, 0, 0),
            new(firstX + length, 4, 0));
        var axis = new Axis(new ColliderCapsule(new(axisExtent, 0, -axisExtent),
            new(-axisExtent, 0, axisExtent), 1));

        var contact = triangle.Closest(axis, out _);

        // The axis lies on X + Z = 0 and all triangle vertices have X + Z >= firstX.
        // The projection gap is attained by the first vertex and its projection onto the axis.
        double squaredGap = firstX * firstX / 2d;
        Assert.InRange(contact.Residual.LengthSquared, squaredGap - 1, squaredGap + 1);
    }

    [Fact]
    public void A_capsule_piercing_a_thin_face_can_transfer_velocity_to_its_guides()
    {
        const float length = 65536;
        const float width = 4;
        const float radius = 0.25f;
        var settings = Settings with { Gravity = 0, Stiffness = 0, Radius = radius, AngleLimit = 0.0008f };
        var tip = length * Vector3.UnitX;
        var left = Chain(tip, settings, Joint(Vector3.Zero));
        var right = Chain(tip, settings, Joint(width * Vector3.UnitY));
        var rig = new ChainRig(1, [left, right], [new(0, 1) { Stretch = 1 }]);

        // At three quarters of the length, the first triangle spans Y = 0 to Y = 3.
        // The axis crosses its interior at Y = 1 and clears both individual guides.
        var intersection = new Vector3(0.75f * length, 1, 0);
        var previous = new ColliderCapsule(intersection - 128 * Vector3.UnitZ, intersection - 5 * Vector3.UnitZ, radius);
        var current = new ColliderCapsule(previous.Start + 64 * Vector3.UnitZ, previous.End + 133 * Vector3.UnitZ, radius);
        var particles = new ChainParticles(rig, colliderCount: 1, placementRows: 2);
        particles.SetRest([Identity], reset: true);
        particles.SnapshotOpening();
        particles.SetCapsuleGeometry(0, current, previous);
        for (int chain = 0; chain < rig.Chains.Count; chain++)
            particles.Predict(chain, settings, Vector3.Zero, StepSeconds, Vector3.Zero, Vector3.Zero, Quaternion.Identity);

        new ChainLinks(rig, rig.Links, 1).FinishStep(particles, [Identity], [current], [previous],
            [settings, settings], StepSeconds);

        // The bend limit cannot move the face beyond the axis, so contact still transfers velocity.
        Assert.InRange(particles.Positions[1].Z, 0, current.End.Z);
        Assert.InRange(particles.Positions[3].Z, 0, current.End.Z);
        float speed = MathF.Max(particles.Velocities[1].Z, particles.Velocities[3].Z);
        float maximumSurfaceSpeed = MathF.Max(current.Start.Z - previous.Start.Z,
            current.End.Z - previous.End.Z) / StepSeconds;
        Assert.True(speed > 0 && speed <= maximumSurfaceSpeed);
    }
}
