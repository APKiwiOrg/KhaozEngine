using System;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Tests.Attachments.ChainSimulationTests;

namespace KhaozEngine.Tests.Attachments;

public sealed class ChainVelocityTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Bend_and_sphere_projections_do_not_add_energy_to_a_single_tip(bool strikesMiddle)
    {
        // #1882: a sideways kick grazes a sphere before reaching the bend limit.
        const float step = 1f / 120;
        var settings = new ChainSettings(0, 0, 1, 1, 0.23f, 0);
        var definition = Chain(-Vector3.UnitY, settings, Joint(Vector3.Zero));
        var particles = new ChainParticles(new ChainRig(1, [definition], []), colliderCount: 1);
        particles.SetRest([Matrix4x4.Identity], reset: true);
        var center = strikesMiddle ? new Vector3(0.09f, -0.58f, 0.116f) : new Vector3(0.16f, -1.15f, 0.10f);
        ColliderCapsule sphere = new(center, center, strikesMiddle ? 0.12f : 0.2f);
        particles.SetCapsuleGeometry(0, sphere, sphere);

        Predict(Vector3.UnitX * (13 / step));
        Project();
        Assert.True(Clearance(particles.Positions[1]) > sphere.Radius);
        bool touched = false;
        Predict(Vector3.Zero);
        float previousEnergy = SquaredSpeed();
        Assert.True(previousEnergy > 0);
        float largestGain = 0;
        bool reachedBendLimit = false;
        for (int frame = 0; frame < 8; frame++)
        {
            bool predictedPenetration = Clearance(Vector3.Normalize(particles.Positions[1])) < sphere.Radius;
            Project();
            var tip = particles.Positions[1];
            reachedBendLimit |= MathF.Abs(Vector3.Dot(-Vector3.UnitY, tip) - MathF.Cos(settings.AngleLimit)) < 0.0001f;
            touched |= predictedPenetration && MathF.Abs(Clearance(tip) - sphere.Radius) < 0.0001f;
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

        void Predict(Vector3 gravity)
        {
            particles.SnapshotOpening();
            particles.Predict(0, settings, gravity, step, Vector3.Zero, Vector3.Zero, Quaternion.Identity);
        }

        float Clearance(Vector3 tip) => strikesMiddle
            ? ChainSegmentContactTests.DistanceToSegment(center, Vector3.Zero, tip)
            : Vector3.Distance(tip, center);

        // A force-free prediction reads velocity without counting position corrections as motion.
        float SquaredSpeed() => ((particles.Positions[1] - particles.SubstepStarts[1]) / step).LengthSquared();
    }

    [Fact]
    public void A_downstream_bend_stop_shares_speed_with_its_free_parent_without_adding_energy()
    {
        const float step = 1f / 120;
        var chain = SharedBendStopChain;
        var anchor = SharedBendStopAnchor;
        var particles = new ChainParticles(new ChainRig(1, [chain], []));
        particles.SetRest([Matrix4x4.Identity], reset: true);
        particles.SetRest([anchor], reset: false);
        particles.Predict(0, chain.Settings, Vector3.Zero, step, Vector3.Zero, Vector3.Zero, Quaternion.Identity);
        var anchorSpeed = anchor.Translation / step;
        float before = SquaredRelativeSpeed();
        Assert.True(before > 0);

        ChainConstraints.FollowChain(particles, 0, anchor, [], [], chain.Settings, step, pullTowardRest: true);

        var first = Vector3.Normalize(particles.Positions[1] - particles.Positions[0]);
        // The first length rule removes this component before the second cone passes its stop
        // back. A measurable component after the pass proves the case reached that shared stop.
        Assert.True(Vector3.Dot(particles.Velocities[1] - anchorSpeed, first) < -1);
        Assert.InRange(SquaredRelativeSpeed(), 0, before);

        // The sum of squared speeds in the anchor frame is proportional to kinetic energy for equal masses.
        float SquaredRelativeSpeed() =>
            (particles.Velocities[1] - anchorSpeed).LengthSquared() + (particles.Velocities[2] - anchorSpeed).LengthSquared();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Endpoint_contact_retains_a_small_approach_beside_large_shared_speed(bool embeddedParent)
    {
        const float step = 1f / 64;
        var definition = Chain(Vector3.UnitX, Settings, Joint(Vector3.Zero));
        var particles = new ChainParticles(new ChainRig(1, [definition], []), colliderCount: 1);
        particles.SetRest([Matrix4x4.Identity], reset: true);
        particles.Velocities[1] = new Vector3(0, 1, 1024);
        var center = new Vector3(embeddedParent ? 0 : 1.125f, -0.25f, -0.25f);
        float radius = Vector3.Distance(Vector3.UnitX, center);
        Assert.Equal(embeddedParent, center.Length() < radius);
        var surface = new Vector3(0, 1 + 1f / (1 << 18), 1024);
        var previousCenter = center - step * surface;
        ColliderCapsule sphere = new(center, center, radius);
        ColliderCapsule previous = new(previousCenter, previousCenter, sphere.Radius);
        particles.SetCapsuleGeometry(0, sphere, previous);

        var carry = new ChainCarry(Vector3.Zero, Vector3.Zero, Quaternion.Identity);
        var constraints = new ChainVelocityConstraints(particles, 0, carry, [previous], 0, step);
        constraints.ApplyLocal(0, 1, Vector3.UnitX, Vector3.UnitX, clamped: false, new ChainCapsuleCandidates([sphere]));

        // The tangent splits the tiny Y approach equally between Y and Z, where the shared
        // speed is large enough to erase that approach when each velocity is dotted separately.
        Assert.Equal(1 + 1f / (1 << 19), particles.Velocities[1].Y);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-0.96f, 0.28f)]
    public void Tip_released_from_sphere_contact_falls_from_rest(float normalX, float normalY)
    {
        // #1882: the gravity held back by a sphere must leave no stored speed on release.
        const float step = 1f / 120;
        var simulation = Create(Chain(Vector3.UnitX, Settings with { Stiffness = 0, Damping = 0 },
            Joint(Vector3.Zero)), colliderCount: 1);
        var anchor = Matrix4x4.Identity;
        const float radius = 0.25f;
        var center = Vector3.UnitX - radius * new Vector3(normalX, normalY, 0);
        ColliderCapsule sphere = new(center, center, radius);
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
        var distantSphere = sphere with { Start = distant, End = distant };
        simulation.Step([anchor], [distantSphere], anchor, step);
        // The next grid boundary displays the first free step.
        simulation.Step([anchor], [distantSphere], anchor, step);
        var after = Tip();
        output.WriteLine($"Gravity-only travel: {Vector3.Distance(before, expected):F9} m, actual: {Vector3.Distance(before, after):F9} m");
        Assert.InRange(Vector3.Distance(expected, after), 0, 0.00001f);

        Vector3 Tip() => Vector3.Transform(Vector3.UnitX, simulation.JointsFromAnchor[0]);
    }

    [Fact]
    public void Trail_zero_tip_stays_put_against_a_sphere_carried_with_the_wearer()
    {
        // #1882: rigid wearer motion leaves a touching sphere stationary in the chain's frame.
        const float step = 1f / 120;
        var settings = new ChainSettings(0, 0, 0, 0, MathF.PI, 0);
        var simulation = Create(Chain(Vector3.UnitX, settings, Joint(Vector3.Zero)), colliderCount: 1);
        var origin = new Vector3(0.75f, -0.45f, 0.2f);
        var localCenter = new Vector3(1, -0.25f, 0);

        float largestDrift = 0;
        for (int frame = 0; frame <= 16; frame++)
        {
            var wearer = Matrix4x4.CreateRotationZ(frame * 0.02f) *
                Matrix4x4.CreateTranslation(origin + frame * 0.015f * Vector3.UnitY);
            var center = Vector3.Transform(localCenter, wearer);
            ColliderCapsule sphere = new(center, center, 0.25f);
            simulation.Step([wearer], [sphere], wearer, frame == 0 ? 0 : step);
            var tip = Vector3.Transform(Vector3.UnitX, simulation.JointsFromAnchor[0]);
            largestDrift = MathF.Max(largestDrift, Vector3.Distance(Vector3.UnitX, tip));
        }

        output.WriteLine($"Largest drift from the wearer-relative sphere contact: {largestDrift:F9} m");
        Assert.InRange(largestDrift, 0, 0.00001f);
    }

    [Fact]
    public void Trail_zero_tip_stays_at_its_bend_limit_when_the_wearer_turns()
    {
        // #1882: wearer rotation carries the cone with the chain so it cannot kick the tip inward.
        const float step = 1f / 120;
        const float angleLimit = 0.16f;
        var settings = new ChainSettings(0, 0, 1, 0, angleLimit, 0);
        var simulation = Create(Chain(Vector3.UnitX, settings, Joint(Vector3.Zero)));

        Step(simulation, 0);
        for (int frame = 0; frame < 40; frame++)
            Step(simulation, step);

        var atLimit = new Vector3(MathF.Cos(angleLimit), -MathF.Sin(angleLimit), 0);
        Assert.InRange(Vector3.Distance(atLimit, Tip()), 0, 0.00001f);

        float largestDrift = 0;
        for (int frame = 1; frame <= 16; frame++)
        {
            var wearer = Matrix4x4.CreateRotationZ(frame * 0.02f);
            simulation.Step([wearer], [], wearer, step);
            largestDrift = MathF.Max(largestDrift, Vector3.Distance(atLimit, Tip()));
        }

        output.WriteLine($"Largest drift from the wearer-relative bend limit: {largestDrift:F9} m");
        Assert.InRange(largestDrift, 0, 0.00001f);

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_moving_capsule_does_not_throw_the_struck_tip_faster_than_it_moved(bool strikesMiddle)
    {
        const float step = 1f / 120;
        const float speed = 1.2f;
        var simulation = Create(Chain(-Vector3.UnitY, new ChainSettings(0, 0, 0, 1, MathF.PI, 0),
            Joint(Vector3.Zero)), colliderCount: 1);
        var anchor = Matrix4x4.Identity;
        float afterStopPeak = 0;
        bool touched = false;
        Vector3 previous = -Vector3.UnitY;

        const int stopFrame = 14;
        const float radius = 0.2f;
        Vector3 CenterAt(int frame) =>
            new(-0.13f + Math.Min(frame, stopFrame) * (speed * step), strikesMiddle ? -0.55f : -1.16f, 0);

        for (int frame = 0; frame <= 26; frame++)
        {
            var center = CenterAt(frame);
            ColliderCapsule capsule = new(center, center, radius);
            simulation.Step([anchor], [capsule], anchor, frame == 0 ? 0 : step);
            var tip = Vector3.Transform(-Vector3.UnitY, simulation.JointsFromAnchor[0]);
            int shownFrame = Math.Max(0, frame - 1);
            var shownCenter = CenterAt(shownFrame);
            float distance = strikesMiddle ? ChainSegmentContactTests.DistanceToSegment(shownCenter, Vector3.Zero, tip)
                : Vector3.Distance(tip, shownCenter);
            touched |= MathF.Abs(distance - radius) < 0.0001f;
            if (shownFrame > stopFrame)
                afterStopPeak = MathF.Max(afterStopPeak, Vector3.Distance(tip, previous) / step);
            previous = tip;
        }

        Assert.True(touched, "The capsule must strike the segment.");
        output.WriteLine($"Capsule {speed:F6} m/s, tip after stop {afterStopPeak:F6} m/s");
        Assert.InRange(afterStopPeak, 0, speed + 0.005f);
    }
}
