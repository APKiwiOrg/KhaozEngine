using System;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;
using static KhaozEngine.Tests.Attachments.ChainSimulationTests;

namespace KhaozEngine.Tests.Attachments;

public sealed class ChainBoundaryTests
{
    private const float StepSeconds = 1f / 64;
    private static readonly ChainSettings Unforced = Settings with { Gravity = 0, Damping = 0 };

    [Fact]
    public void Coincident_tips_separate_to_their_authored_width()
    {
        // #1884: coincident endpoints need opposite sphere tangents to recover their spacing.
        var chain = Chain(Vector3.UnitX, Unforced, Joint(Vector3.Zero));
        var rig = new ChainRig(2, [chain, chain with { Anchor = 1 }], [new(0, 1) { Stretch = 0.1f }]);
        var simulation = new ChainSimulation(rig, 0, new() { MaxStepSeconds = StepSeconds });
        Matrix4x4[] anchors = [Matrix4x4.Identity, Matrix4x4.Identity];
        simulation.Step(anchors, [], Matrix4x4.Identity, 0);

        anchors[0] = Matrix4x4.CreateScale(-1, -1, 1);
        simulation.Step(anchors, [], Matrix4x4.Identity, StepSeconds);
        simulation.Step(anchors, [], Matrix4x4.Identity, StepSeconds);
        var worlds = new Matrix4x4[2];
        simulation.ComposeJointWorlds(anchors, worlds);

        var left = Vector3.Transform(chain.Tip, worlds[0]);
        var right = Vector3.Transform(chain.Tip, worlds[1]);
        Assert.InRange(Vector3.Distance(left, right), 1.7995f, 2.00001f);
        Assert.InRange(left.Length(), 0.99999f, 1.00001f);
        Assert.InRange(right.Length(), 0.99999f, 1.00001f);
    }

    [Fact]
    public void A_capped_frame_keeps_the_capsules_actual_contact_speed()
    {
        // #1883: pose time dropped from the grid must not become a one-step impulse.
        var start = new Vector3(1.03f, -0.49f, 0);
        var end = start + 0.25f * Vector3.UnitY;
        float speed = ReleasedTipSpeed(new(start, start, 0.25f), new(end, end, 0.25f), 0.25f);
        Assert.InRange(speed, 0.98f, 1.02f);
    }

    [Fact]
    public void A_growing_sphere_passes_its_surface_speed_to_the_tip()
    {
        // #1882: an expanding surface moves even when its axis points are still.
        var center = new Vector3(1.03f, -0.25f, 0);
        float speed = ReleasedTipSpeed(new(center, center, 0.25f), new(center, center, 0.25f + StepSeconds), StepSeconds);
        Assert.InRange(speed, 0.98f, 1.02f);
    }

    private static float ReleasedTipSpeed(ColliderCapsule previous, ColliderCapsule current, float elapsed)
    {
        var chain = Chain(Vector3.UnitX, Unforced, Joint(Vector3.Zero));
        var rig = new ChainRig(1, [chain], []);
        var simulation = new ChainSimulation(rig, 1, new() { MaxStepSeconds = StepSeconds, MaxStepsPerFrame = 1 });
        var anchor = Matrix4x4.Identity;
        simulation.Step([anchor], [previous], anchor, 0);
        simulation.Step([anchor], [current], anchor, elapsed);
        var distant = new Vector3(4, 0, 0);
        var far = current with { Start = distant, End = distant };
        simulation.Step([anchor], [far], anchor, StepSeconds);
        var before = Vector3.Transform(Vector3.UnitX, simulation.JointsFromAnchor[0]);
        Assert.InRange(Vector3.Distance(before, current.Start), current.Radius - 0.00001f, current.Radius + 0.00001f);
        simulation.Step([anchor], [far], anchor, StepSeconds);
        var after = Vector3.Transform(Vector3.UnitX, simulation.JointsFromAnchor[0]);
        return Vector3.Distance(before, after) / StepSeconds;
    }

    [Fact]
    public void Pure_anchor_twist_turns_the_second_joints_limit()
    {
        // #1882: a turn around the first segment still moves the cone on the next segment.
        const float limit = 0.25f;
        const float twistRadians = 0.04f;
        var chain = Chain(Vector3.UnitY, Unforced with { AngleLimit = limit }, Joint(Vector3.Zero), Joint(Vector3.UnitX));
        var rig = new ChainRig(1, [chain], []);
        var particles = new ChainParticles(rig);
        particles.SetRest([Matrix4x4.Identity], reset: true);
        particles.Positions[2] = Vector3.UnitX + new Vector3(0, MathF.Cos(limit), -MathF.Sin(limit));
        var anchor = Matrix4x4.CreateRotationX(twistRadians);
        particles.SetRest([anchor], reset: false);
        particles.Predict(0, chain.Settings, Vector3.Zero, StepSeconds,
            Vector3.Zero, Vector3.Zero, Quaternion.Identity);
        ChainConstraints.FollowChain(particles, 0, anchor, [], [], chain.Settings, StepSeconds, pullTowardRest: false);

        var segment = Vector3.Normalize(particles.Positions[2] - particles.Positions[1]);
        var inward = Vector3.Cross(Vector3.UnitX, segment);
        // The moving cone shares the stop equally between the two movable particles.
        float expected = 0.5f * twistRadians / StepSeconds;
        Assert.InRange(Vector3.Dot(particles.Velocities[2], inward), expected - 0.00001f, expected + 0.00001f);
    }

    [Fact]
    public void A_large_live_turn_keeps_segment_lengths_between_steps()
    {
        // #1883: the displayed path follows a swing, so rotating endpoints do not take a chord.
        var chain = Chain(-Vector3.UnitY, Unforced, Joint(Vector3.Zero), Joint(-Vector3.UnitY));
        var simulation = new ChainSimulation(new ChainRig(1, [chain], []), 0, new() { MaxStepSeconds = StepSeconds });
        Step(simulation, 0);
        var turn = Matrix4x4.CreateRotationZ(MathF.PI / 2);
        simulation.Step([turn], [], Matrix4x4.Identity, StepSeconds);
        simulation.Step([turn], [], Matrix4x4.Identity, StepSeconds / 2);

        AssertHalfwayWithLength(simulation);
    }

    [Fact]
    public void Finishing_resume_between_steps_keeps_segment_lengths()
    {
        // #1883: the completed blend sees the same hierarchical target as live display.
        var chain = Chain(-Vector3.UnitY, Unforced, Joint(Vector3.Zero), Joint(-Vector3.UnitY));
        var options = new ChainSimulationOptions { MaxStepSeconds = StepSeconds, HoldBlendSeconds = 1.5f * StepSeconds };
        var simulation = new ChainSimulation(new ChainRig(1, [chain], []), 0, options);
        simulation.Step([Matrix4x4.Identity], [], Matrix4x4.Identity, 0, MotionLevel.Held);
        var turn = Matrix4x4.CreateRotationZ(MathF.PI / 2);
        simulation.Step([turn], [], Matrix4x4.Identity, 0);
        simulation.Step([turn], [], Matrix4x4.Identity, options.HoldBlendSeconds);

        AssertHalfwayWithLength(simulation);
    }

    private static void AssertHalfwayWithLength(ChainSimulation simulation)
    {
        var frames = simulation.JointsFromAnchor;
        var second = frames[1].Translation - frames[0].Translation;
        Assert.InRange(second.Length(), 0.99999f, 1.00001f);
        Assert.InRange(second.X, -0.71f, -0.70f);
        Assert.InRange(second.Y, -0.71f, -0.70f);
        var tip = Vector3.Transform(-Vector3.UnitY, frames[1]);
        Assert.InRange(Vector3.Distance(tip, frames[1].Translation), 0.99999f, 1.00001f);
    }

    [Fact]
    public void The_supported_step_minimum_refuses_smaller_steps()
    {
        var rig = new ChainRig(1, [Chain(Vector3.UnitX, Unforced, Joint(Vector3.Zero))], []);
        _ = new ChainSimulation(rig, 0, new() { MaxStepSeconds = 0.0001f });
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ChainSimulation(rig, 0, new() { MaxStepSeconds = 2e-38f }));
        Assert.Equal(nameof(ChainSimulationOptions.MaxStepSeconds), error.ParamName);
    }

    [Fact]
    public void Zero_length_hold_finishes_on_a_zero_duration_frame()
    {
        // #1883: an immediate level switch reaches rest even with no elapsed frame time.
        var chain = Chain(Vector3.UnitX, Unforced, Joint(Vector3.Zero));
        var options = new ChainSimulationOptions { MaxStepSeconds = StepSeconds, HoldBlendSeconds = 0 };
        var simulation = new ChainSimulation(new ChainRig(1, [chain], []), 0, options);
        Step(simulation, 0);
        var turn = Matrix4x4.CreateRotationZ(MathF.PI / 2);
        simulation.Step([turn], [], Matrix4x4.Identity, StepSeconds);
        simulation.Step([turn], [], Matrix4x4.Identity, StepSeconds);
        Assert.True(Vector3.Distance(Vector3.UnitX, Vector3.Transform(Vector3.UnitX, simulation.JointsFromAnchor[0])) > 0.5f);
        simulation.Step([turn], [], Matrix4x4.Identity, 0, MotionLevel.Held);

        Assert.Equal(Matrix4x4.Identity, simulation.JointsFromAnchor[0]);
    }
}
