using System;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Tests.Attachments.ChainSimulationTests;

namespace KhaozEngine.Tests.Attachments;

public sealed class ChainVelocityTests(ITestOutputHelper output)
{
    [Fact]
    public void Bend_and_sphere_projections_do_not_add_energy_to_a_single_tip()
    {
        // #1882: a sideways kick crosses a grazing sphere before reaching the bend limit.
        const float step = 1f / 120;
        var settings = new ChainSettings(0, 0, 1, 1, 0.23f, 0);
        var definition = Chain(-Vector3.UnitY, settings, Joint(Vector3.Zero));
        var particles = new ChainParticles(new ChainRig(1, [definition], []));
        particles.SetRest([Matrix4x4.Identity], reset: true);
        var center = new Vector3(0.1f, -0.8f, 0);
        ColliderCapsule sphere = new(center, center, 0.2f);

        Predict(Vector3.UnitX * (13 / step));
        Project();
        Predict(Vector3.Zero);
        float previousEnergy = SquaredSpeed();
        Assert.True(previousEnergy > 0);
        float largestGain = 0;
        bool touched = false;
        bool reachedBendLimit = false;
        for (int frame = 0; frame < 8; frame++)
        {
            Project();
            var tip = particles.Positions[1];
            reachedBendLimit |= MathF.Abs(Vector3.Dot(-Vector3.UnitY, tip) - MathF.Cos(settings.AngleLimit)) < 0.0001f;
            touched |= MathF.Abs(Vector3.Distance(tip, center) - sphere.Radius) < 0.0001f;
            Predict(Vector3.Zero);
            float energy = SquaredSpeed();
            largestGain = MathF.Max(largestGain, energy - previousEnergy);
            previousEnergy = energy;
        }

        Assert.True(touched, "The kick must reach the sphere.");
        Assert.True(reachedBendLimit, "The kick must reach the bend limit.");
        output.WriteLine($"Largest increase in squared tip speed: {largestGain:F6} m²/s²");
        Assert.InRange(largestGain, 0, 0.002f);

        void Project() => ChainConstraints.FollowChain(particles, 0, Matrix4x4.Identity, [sphere], [sphere], settings, step,
            pullTowardRest: true);

        void Predict(Vector3 gravity) => particles.Predict(0, 1, settings, gravity, step,
            Vector3.Zero, Vector3.Zero, Quaternion.Identity);

        // A force-free prediction reads velocity without counting position corrections as motion.
        float SquaredSpeed() => ((particles.Positions[1] - particles.SubstepStarts[1]) / step).LengthSquared();
    }

    [Fact]
    public void Tip_released_from_sphere_contact_falls_from_rest()
    {
        // #1882: the gravity held back by a sphere must leave no stored speed on release.
        const float step = 1f / 120;
        var simulation = Create(Chain(Vector3.UnitX, Settings with { Stiffness = 0, Damping = 0 },
            Joint(Vector3.Zero)), colliderCount: 1);
        var anchor = Matrix4x4.Identity;
        var center = new Vector3(1, -0.25f, 0);
        ColliderCapsule sphere = new(center, center, 0.25f);
        simulation.Step([anchor], [sphere], anchor, 0);
        for (int frame = 0; frame < 30; frame++)
            simulation.Step([anchor], [sphere], anchor, step);

        var previous = Tip();
        simulation.Step([anchor], [sphere], anchor, step);
        var before = Tip();
        Assert.InRange(Vector3.Distance(before, previous), 0, 0.00001f);
        Assert.InRange(MathF.Abs(Vector3.Distance(before, center) - sphere.Radius), 0, 0.00001f);

        var expected = Vector3.Normalize(before + simulation.Options.Gravity * (0.5f * step * step));
        var distant = new Vector3(3, -2, 0);
        simulation.Step([anchor], [sphere with { Start = distant, End = distant }], anchor, step);
        var after = Tip();
        output.WriteLine($"Gravity-only travel: {Vector3.Distance(before, expected):F9} m, actual: {Vector3.Distance(before, after):F9} m");
        Assert.InRange(Vector3.Distance(expected, after), 0, 0.00001f);

        Vector3 Tip() => Vector3.Transform(Vector3.UnitX, simulation.JointsFromAnchor[0]);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(240)]
    public void Linked_cape_tips_do_not_snap_when_the_anchor_stops(int fps)
    {
        // #1882: the parent must share the cone's stop or the tip can whip after the wearer stops.
        var rig = ChainSwingFixture.CreateRig();
        var simulation = new ChainSimulation(rig, 0);
        var previousTips = new Vector3[ChainSwingFixture.CapeChains];
        float runningPeak = 0, stoppedPeak = 0;
        int stopFrame = ChainSwingFixture.StopTime * fps;
        for (int frame = 0; frame <= stopFrame + fps; frame++)
        {
            var (anchor, wearer) = ChainSwingFixture.Pose((float)frame / fps);
            simulation.Step([anchor], [], wearer, frame == 0 ? 0 : 1f / fps);
            for (int chain = 0; chain < previousTips.Length; chain++)
            {
                var definition = rig.Chains[chain];
                var tip = Vector3.Transform(definition.Tip,
                    simulation.JointsFromAnchor[rig.FirstJoint(chain) + definition.Joints.Count - 1]);
                float speed = Vector3.Distance(tip, previousTips[chain]) * fps;
                if (frame > fps && frame <= stopFrame)
                    runningPeak = MathF.Max(runningPeak, speed);
                if (frame > stopFrame)
                    stoppedPeak = MathF.Max(stoppedPeak, speed);
                previousTips[chain] = tip;
            }
        }

        output.WriteLine($"{fps} Hz: running {runningPeak:F6} m/s, after stop {stoppedPeak:F6} m/s");
        Assert.InRange(stoppedPeak, 0, runningPeak);
    }

    [Fact]
    public void A_moving_capsule_does_not_throw_the_struck_tip_faster_than_it_moved()
    {
        const float step = 1f / 120;
        const float speed = 1.2f;
        var simulation = Create(Chain(-Vector3.UnitY, new ChainSettings(0, 0, 0, 1, MathF.PI, 0),
            Joint(Vector3.Zero)), colliderCount: 1);
        var anchor = Matrix4x4.Identity;
        float afterStopPeak = 0;
        bool touched = false;
        Vector3 previous = -Vector3.UnitY;

        for (int frame = 0; frame <= 25; frame++)
        {
            const int stopFrame = 14;
            var center = new Vector3(-0.13f + Math.Min(frame, stopFrame) * (speed * step), -0.81f, 0);
            ColliderCapsule capsule = new(center, center, 0.2f);
            simulation.Step([anchor], [capsule], anchor, frame == 0 ? 0 : step);
            var tip = Vector3.Transform(-Vector3.UnitY, simulation.JointsFromAnchor[0]);
            touched |= MathF.Abs(Vector3.Distance(tip, capsule.Start) - capsule.Radius) < 0.0001f;
            if (frame > stopFrame)
                afterStopPeak = MathF.Max(afterStopPeak, Vector3.Distance(tip, previous) / step);
            previous = tip;
        }

        Assert.True(touched, "The capsule must strike the tip.");
        output.WriteLine($"Capsule {speed:F6} m/s, tip after stop {afterStopPeak:F6} m/s");
        Assert.InRange(afterStopPeak, 0, speed + 0.005f);
    }
}
