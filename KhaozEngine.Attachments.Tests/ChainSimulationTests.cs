using System;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public class ChainSimulationTests
{
    [Fact]
    public void Unforced_joints_keep_their_rest_hang_and_can_draw_on_a_new_anchor()
    {
        var chain = Chain(Vector3.UnitY, Settings with { Gravity = 0 },
            new ChainJointRest(new(0, 1, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), new(2, 1, 1)),
            Joint(Vector3.UnitX));
        var simulation = Create(chain);
        var anchor = Matrix4x4.CreateRotationY(0.4f) * Matrix4x4.CreateTranslation(3, 4, 5);
        Vector3 expectedJoint = new(0, 3, 0);
        AssertNear(expectedJoint, simulation.JointsFromAnchor[1].Translation);

        simulation.Step([anchor], [], anchor, 0);
        simulation.Step([anchor], [], anchor, 1f / 60);
        AssertNear(expectedJoint, simulation.JointsFromAnchor[1].Translation);

        var newerAnchor = Matrix4x4.CreateTranslation(-4, 2, 0);
        var worlds = new Matrix4x4[2];
        simulation.ComposeJointWorlds([newerAnchor], worlds);
        AssertNear(new(-4, 5, 0), worlds[1].Translation);
        AssertNear(new(-5, 5, 0), Vector3.Transform(chain.Tip, worlds[1]));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(240)]
    public void Limp_level_chain_settles_and_reaches_the_same_first_swing_depth(int fps)
    {
        var baseline = LimpSwing(60);
        var candidate = LimpSwing(fps);
        AssertNear(new(0, -2, 0), candidate.Settled, 0.01f);
        AssertNear(baseline.Settled, candidate.Settled, 0.01f);
        Assert.InRange(candidate.Lowest, -2.001f, -1.5f);
        Assert.InRange(MathF.Abs(baseline.Lowest - candidate.Lowest), 0, 0.1f * MathF.Abs(baseline.Lowest));
    }

    [Fact]
    public void Gravity_starts_at_rest_and_carries_velocity_through_the_next_grid_step()
    {
        var simulation = Create(Chain(Vector3.UnitX, Settings with { Damping = 0 }, Joint(Vector3.Zero)));
        const float seconds = 1f / 120;
        Step(simulation, 0);
        Step(simulation, seconds);
        AssertNear(Vector3.UnitX, Tip(simulation));

        var expected = Vector3.Normalize(new(1, -0.5f * 9.81f * seconds * seconds, 0));
        Step(simulation, seconds / 2);
        AssertNear(Vector3.Normalize(Vector3.UnitX + expected), Tip(simulation), 0.00002f);
        Step(simulation, seconds / 2);
        AssertNear(expected, Tip(simulation), 0.00002f);

        Step(simulation, seconds);
        const float elapsed = seconds * 2;
        // The next displayed grid sample carries the first step's full gravity impulse.
        Assert.InRange(Tip(simulation).Y, -0.5f * 9.81f * elapsed * elapsed - 0.00002f,
            -0.5f * 9.81f * elapsed * elapsed + 0.00002f);
    }

    [Theory]
    [InlineData(30, 0.2f)]
    [InlineData(144, 0.2f)]
    [InlineData(240, 0.2f)]
    [InlineData(30, MathF.PI / 2)]
    [InlineData(144, MathF.PI / 2)]
    [InlineData(240, MathF.PI / 2)]
    public void Stiff_linked_chains_follow_the_same_trajectory_across_frame_rates(int fps, float releaseAngle)
    {
        float releaseGravity = releaseAngle == 0.2f ? 0 : 9.81f;
        var baseline = LinkedTrajectory(60, releaseAngle, releaseGravity);
        var candidate = LinkedTrajectory(fps, releaseAngle, releaseGravity);
        Assert.Equal(8, candidate.Length);
        for (int index = 0; index < candidate.Length; index++)
            AssertNear(baseline[index], candidate[index], 0.01f);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(144)]
    [InlineData(240)]
    public void A_release_along_the_link_axis_agrees_at_the_same_times(int fps)
    {
        var baseline = LinkedTrajectory(60, -0.2f, 0, releaseAlongLink: true);
        var candidate = LinkedTrajectory(fps, -0.2f, 0, releaseAlongLink: true);
        Assert.Equal(8, candidate.Length);
        for (int index = 0; index < candidate.Length; index++)
            AssertNear(baseline[index], candidate[index], 0.01f);
    }

    [Fact]
    public void A_triangle_of_links_is_stable_and_order_independent_at_the_largest_substep()
    {
        var chain = Chain(-Vector3.UnitY, Settings with { Gravity = 0, Damping = 0 }, Joint(Vector3.Zero));
        ChainRigChain[] chains = [chain, chain with { Anchor = 1 }, chain with { Anchor = 2 }];
        var options = new ChainSimulationOptions { MaxStepSeconds = 1f / 30, MaxStepsPerFrame = 64 };
        Matrix4x4[] anchors = [Matrix4x4.CreateTranslation(-1, 0, 0), Matrix4x4.Identity, Matrix4x4.CreateTranslation(1, 0, 0)];

        ChainSimulation Release(ChainLink[] links, float angle)
        {
            var sim = new ChainSimulation(new ChainRig(3, chains, links), 0, options);
            sim.Step([Matrix4x4.CreateRotationZ(angle) * anchors[0], Matrix4x4.CreateRotationZ(-angle), anchors[2]],
                [], Matrix4x4.Identity, 0);
            sim.Step(anchors, [], Matrix4x4.Identity, options.MaxStepSeconds);
            sim.Step(anchors, [], Matrix4x4.Identity, options.MaxStepSeconds);
            return sim;
        }

        const float releaseAngle = 0.01f;
        var forward = Release([new(0, 1), new(1, 2), new(0, 2)], releaseAngle);
        var reversed = Release([new(0, 2), new(1, 2), new(0, 1)], releaseAngle);
        var control = Release([new(0, 1), new(1, 2), new(0, 2)], 0);
        float Disturbance(ChainSimulation sim)
        {
            float sum = 0;
            for (int chainIndex = 0; chainIndex < chains.Length; chainIndex++)
                sum += Vector3.DistanceSquared(Vector3.Transform(chain.Tip, sim.JointsFromAnchor[chainIndex]), chain.Tip);
            return MathF.Sqrt(sum);
        }

        // The first displayed sample has already integrated one step from the two tilted tips.
        float initial = MathF.Sqrt(2) * (2 * MathF.Sin(releaseAngle / 2));
        Assert.InRange(Disturbance(forward), initial * 0.95f, initial * 1.02f);
        for (int frame = 0; frame < 240; frame++)
        {
            forward.Step(anchors, [], Matrix4x4.Identity, options.MaxStepSeconds);
            reversed.Step(anchors, [], Matrix4x4.Identity, options.MaxStepSeconds);
            control.Step(anchors, [], Matrix4x4.Identity, options.MaxStepSeconds);
            Assert.InRange(Disturbance(forward), 0, initial * 1.02f);
            Assert.InRange(Disturbance(control), 0, 0.00001f);
            for (int chainIndex = 0; chainIndex < chains.Length; chainIndex++)
            {
                var a = Vector3.Transform(chain.Tip, forward.JointsFromAnchor[chainIndex]);
                var b = Vector3.Transform(chain.Tip, reversed.JointsFromAnchor[chainIndex]);
                AssertNear(a, b, 0.00005f);
            }
        }
    }

    [Fact]
    public void Variable_frames_finish_on_the_current_anchor_and_keep_scaled_lengths()
    {
        var chain = Chain(-Vector3.UnitY, Settings with { Stiffness = 5 },
            Joint(Vector3.UnitX), Joint(-Vector3.UnitY), Joint(-Vector3.UnitY));
        var simulation = Create(chain);
        var worlds = new Matrix4x4[3];
        var random = new Random(789);
        for (var frame = 0; frame < 90; frame++)
        {
            float time = (float)(0.001 + 0.499 * random.NextDouble());
            var anchor = Matrix4x4.CreateScale(1.1f, 1.25f, 0.9f) *
                Matrix4x4.CreateRotationZ(frame * 0.07f) * Matrix4x4.CreateTranslation(MathF.Sin(frame * 0.3f), 0, 0);
            simulation.Step([anchor], [], anchor, time);
            simulation.ComposeJointWorlds([anchor], worlds);

            AssertNear(Vector3.Transform(Vector3.UnitX, anchor), worlds[0].Translation);
            for (var joint = 0; joint < worlds.Length; joint++)
            {
                var end = joint + 1 < worlds.Length ? worlds[joint + 1].Translation : Vector3.Transform(chain.Tip, worlds[joint]);
                Assert.InRange(Vector3.Distance(worlds[joint].Translation, end), 1.25f / 1.05f, 1.25f * 1.05f);
                Assert.InRange(MathF.Abs(worlds[joint].GetDeterminant()), 0.01f, 100);
            }
        }
    }

    [Theory]
    [InlineData(4, 0.5f)]
    [InlineData(29, 0.25f)]
    public void A_frame_matches_the_retained_grid_steps_inside_it(int completeSteps, float remainder)
    {
        const float stepSeconds = 1f / 64;
        var chain = Chain(-Vector3.UnitY, Settings with { Trail = 0.4f, Stiffness = 4, Radius = 0.02f },
            Joint(Vector3.Zero), Joint(-Vector3.UnitY));
        var options = new ChainSimulationOptions { MaxStepSeconds = stepSeconds };
        var rig = new ChainRig(1, [chain], []);
        var wholeFrame = new ChainSimulation(rig, 1, options);
        var sampled = new ChainSimulation(rig, 1, options);
        void At(ChainSimulation simulation, float fraction, float seconds)
        {
            var wearer = Matrix4x4.CreateRotationY(0.8f * fraction) * Matrix4x4.CreateTranslation(fraction, 0, 0);
            var anchor = Matrix4x4.CreateRotationZ(fraction) * Matrix4x4.CreateTranslation(fraction, 0, 0);
            var collider = new ColliderCapsule(new(1.5f - fraction, -1, 0), new(1.5f - fraction, -2, 0), 0.3f);
            simulation.Step([anchor], [collider], wearer, seconds);
        }

        float frameSteps = completeSteps + remainder;
        At(wholeFrame, 0, 0);
        At(sampled, 0, 0);
        At(wholeFrame, 1, frameSteps * stepSeconds);
        int first = Math.Max(1, completeSteps - options.MaxStepsPerFrame + 1);
        for (int step = first; step <= completeSteps; step++)
            At(sampled, step / frameSteps, stepSeconds);
        At(sampled, 1, remainder * stepSeconds);

        AssertNear(sampled.JointsFromAnchor[1].Translation, wholeFrame.JointsFromAnchor[1].Translation);
        AssertNear(Tip(sampled), Tip(wholeFrame));

        // The remaining fraction belongs to both paths after a capped frame.
        At(wholeFrame, 1, (1 - remainder) * stepSeconds);
        At(sampled, 1, (1 - remainder) * stepSeconds);
        AssertNear(Tip(sampled), Tip(wholeFrame));
    }

    [Theory]
    [InlineData("teleport")]
    [InlineData("gap")]
    [InlineData("explicit")]
    public void Reset_discards_the_previous_pose_and_velocity(string trigger)
    {
        var simulation = Create(Chain(-Vector3.UnitY, Settings with { Gravity = 0 }, Joint(Vector3.Zero)));
        Step(simulation, 0);
        var moving = Matrix4x4.CreateTranslation(1, 0, 0);
        simulation.Step([moving], [], moving, 1f / 60);
        Assert.True(Vector3.Distance(-Vector3.UnitY, Tip(simulation)) > 0.1f);

        var pose = trigger == "teleport" ? Matrix4x4.CreateTranslation(11, 0, 0) : moving;
        if (trigger == "explicit")
            simulation.Reset();
        simulation.Step([pose], [], pose, trigger == "gap" ? 1 : 1f / 60);
        AssertNear(-Vector3.UnitY, Tip(simulation));
        simulation.Step([pose], [], pose, 1f / 60);
        AssertNear(-Vector3.UnitY, Tip(simulation));
    }

    [Theory]
    [InlineData(-1, -2)]
    [InlineData(-1, -1)]
    [InlineData(2e20f, -2e20f)]
    [InlineData(-147573952589676412928f, 295147905179352825856f)]
    public void Posed_capsule_clears_joints_and_virtual_tip_even_when_it_moves(float axisStart, float axisEnd)
    {
        var chain = Chain(-Vector3.UnitY, Settings with { Gravity = 0, Radius = 0.025f },
            Joint(Vector3.Zero), Joint(-Vector3.UnitY));
        var simulation = Create(chain, colliderCount: 1);
        ColliderCapsule CapsuleAt(float x) =>
            new(new(x, axisStart, 0), new(x, axisEnd, 0), 0.35f);
        bool longAxis = MathF.Abs(axisStart) > 3;
        float Clearance(Vector3 point, ColliderCapsule capsule) => longAxis
            ? new Vector2(point.X - capsule.Start.X, point.Z).Length()
            : DistanceToCapsuleAxis(point, capsule);

        simulation.Step([Matrix4x4.Identity], [CapsuleAt(1)], Matrix4x4.Identity, 0);
        const int movementSteps = 12;
        for (int step = 1; step <= movementSteps + 1; step++)
        {
            var current = CapsuleAt(1 - Math.Min(step, movementSteps) / (float)movementSteps);
            simulation.Step([Matrix4x4.Identity], [current], Matrix4x4.Identity, 1f / 120);
            // At a grid boundary presentation shows the preceding simulated step.
            var capsule = CapsuleAt(1 - (step - 1) / (float)movementSteps);
            Vector3 joint = simulation.JointsFromAnchor[1].Translation;
            Vector3 tip = Tip(simulation);
            Assert.InRange(Clearance(joint, capsule), 0.374f, 4);
            Assert.InRange(Clearance(tip, capsule), 0.374f, 4);
            Assert.InRange(Vector3.Distance(joint, tip), 0.999f, 1.001f);
        }
    }

    [Fact]
    public void Capsule_clearance_can_exceed_the_bend_limit_at_fixed_length()
    {
        var settings = Settings with { Gravity = 0, AngleLimit = 0.1f };
        var simulation = Create(Chain(-Vector3.UnitY, settings, Joint(Vector3.Zero)), 1);
        var capsule = new ColliderCapsule(-Vector3.UnitY, -Vector3.UnitY, 0.35f);
        simulation.Step([Matrix4x4.Identity], [capsule], Matrix4x4.Identity, 0);
        simulation.Step([Matrix4x4.Identity], [capsule], Matrix4x4.Identity, 1f / 60);

        AssertNear(Vector3.Zero, simulation.JointsFromAnchor[0].Translation);
        Assert.InRange(Tip(simulation).Length(), 0.9999f, 1.0001f);
        Assert.InRange(Vector3.Distance(Tip(simulation), capsule.Start), 0.349f, 2);
    }

    [Fact]
    public void Fast_turn_clamps_each_bend_relative_to_its_swung_parent()
    {
        const float limit = 0.25f;
        var chain = Chain(-Vector3.UnitY, Settings with { Gravity = 0, AngleLimit = limit },
            Joint(Vector3.Zero), Joint(-Vector3.UnitY));
        var simulation = Create(chain);
        Step(simulation, 0);
        var turn = Matrix4x4.CreateRotationZ(2.4f);
        simulation.Step([turn], [], Matrix4x4.Identity, 1f / 120);
        simulation.Step([turn], [], Matrix4x4.Identity, 1f / 120);
        var worlds = new Matrix4x4[2];
        simulation.ComposeJointWorlds([turn], worlds);

        var first = Direction(worlds[1].Translation - worlds[0].Translation);
        var next = Direction(Vector3.Transform(chain.Tip, worlds[1]) - worlds[1].Translation);
        Assert.InRange(MathF.Acos(Math.Clamp(Vector3.Dot(Direction(Vector3.TransformNormal(-Vector3.UnitY, turn)), first), -1, 1)),
            limit - 0.001f, limit + 0.001f);
        Assert.InRange(Vector3.Dot(first, next), MathF.Cos(limit) - 0.00001f, 1.00001f);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Trail_zero_follows_wearer_movement_and_full_trail_lags(int framesPerStep)
    {
        // #1883: carry across a grid boundary starts at the previous step, including half-step frames.
        var settings = Settings with { Gravity = 0 };
        var carried = Create(Chain(-Vector3.UnitY, settings with { Trail = 0 }, Joint(Vector3.Zero)));
        var lagging = Create(Chain(-Vector3.UnitY, settings, Joint(Vector3.Zero)));
        for (int frame = 0; frame <= 12 * framesPerStep; frame++)
        {
            float steps = (float)frame / framesPerStep;
            var pose = Matrix4x4.CreateRotationZ(steps * 0.025f) * Matrix4x4.CreateTranslation(steps * 0.03f, 0, 0);
            float seconds = frame == 0 ? 0 : carried.Options.MaxStepSeconds / framesPerStep;
            carried.Step([pose], [], pose, seconds);
            lagging.Step([pose], [], pose, seconds);
            AssertNear(-Vector3.UnitY, Tip(carried));
        }

        Assert.True(Tip(lagging).X < -0.2f);
    }

    [Fact]
    public void Hold_blends_from_the_shown_shape_and_resume_starts_without_a_jump()
    {
        var simulation = Create(Chain(-Vector3.UnitY, Settings with { Gravity = 0 }, Joint(Vector3.Zero)));
        Step(simulation, 0);
        var pose = Matrix4x4.CreateTranslation(1, 0, 0);
        simulation.Step([pose], [], pose, 1f / 120);
        simulation.Step([pose], [], pose, 1f / 120);
        var before = Tip(simulation);
        float distance = Vector3.Distance(before, -Vector3.UnitY);
        Assert.True(distance > 0.1f);

        var previous = before;
        for (var frame = 0; frame < 4; frame++)
        {
            simulation.Step([pose], [], pose, 1f / 16, MotionLevel.Held);
            var shown = Tip(simulation);
            Assert.InRange(Vector3.Distance(previous, shown), 0.001f, distance / 4 * 1.05f);
            previous = shown;
        }

        AssertNear(-Vector3.UnitY, previous);
        simulation.Step([pose], [], pose, 1f / 16, MotionLevel.Held);
        simulation.Step([pose], [], pose, 1f / 120);
        AssertNear(previous, Tip(simulation));
    }

    [Fact]
    public void Resuming_during_the_hold_starts_from_the_blended_shape_with_no_velocity()
    {
        const float step = 1f / 64;
        var options = new ChainSimulationOptions { MaxStepSeconds = step, HoldBlendSeconds = 2 * step };
        var settings = Settings with { Stiffness = 0, Damping = 0 };
        var chain = Chain(Vector3.UnitX, settings, Joint(Vector3.Zero));
        var simulation = new ChainSimulation(new ChainRig(1, [chain], []), 0, options);

        // Give the tip speed before holding, then remove gravity to isolate the resume state.
        Step(simulation, 0);
        for (int frame = 0; frame < 18; frame++)
            Step(simulation, step);
        var simulated = Tip(simulation);
        simulation.Tune(0, settings with { Gravity = 0 });
        simulation.Step([Matrix4x4.Identity], [], Matrix4x4.Identity, step, MotionLevel.Held);
        var held = Tip(simulation);
        Assert.True(Vector3.Distance(simulated, held) > 0.1f);
        Assert.True(Vector3.Distance(Vector3.UnitX, held) > 0.1f);

        Step(simulation, step);
        // The second boundary ends the blend and displays the first simulated sample.
        Step(simulation, step);
        AssertNear(held, Tip(simulation));
    }

    [Fact]
    public void Resuming_from_rest_inside_a_capsule_blends_the_presented_tip_to_clearance()
    {
        // Strong damping makes the contact pose stationary after the first correction.
        var settings = Settings with { Gravity = 0, Damping = 10_000 };
        var simulation = Create(Chain(-Vector3.UnitY, settings, Joint(Vector3.Zero)), 1);
        var capsule = new ColliderCapsule(-Vector3.UnitY, -Vector3.UnitY, 0.35f);
        simulation.Step([Matrix4x4.Identity], [capsule], Matrix4x4.Identity, 0, MotionLevel.Held);

        Vector3 previous = Tip(simulation);
        AssertNear(capsule.Start, previous);
        const int frames = 4;
        float clearanceAngle = 2 * MathF.Asin(capsule.Radius / 2);
        float maximumTravel = 2 * MathF.Sin(clearanceAngle / (2 * frames));
        for (int frame = 0; frame < frames; frame++)
        {
            simulation.Step([Matrix4x4.Identity], [capsule], Matrix4x4.Identity, 0.25f / frames);
            Vector3 shown = Tip(simulation);
            AssertNear(Vector3.Zero, simulation.JointsFromAnchor[0].Translation);
            Assert.InRange(shown.Length(), 0.9999f, 1.0001f);
            Assert.InRange(Vector3.Distance(previous, shown), 0.001f, maximumTravel + 0.0001f);
            previous = shown;
        }

        Assert.InRange(Vector3.Distance(previous, capsule.Start), 0.349f, 0.351f);
    }

    [Fact]
    public void Turning_an_anchor_through_half_a_turn_during_resume_keeps_the_blend_continuous()
    {
        var chain = Chain(-Vector3.UnitY, Settings with { Gravity = 0 }, Joint(Vector3.Zero));
        var simulation = Create(chain);
        var worlds = new Matrix4x4[1];
        Vector3 At(float degrees, float elapsed, MotionLevel level = MotionLevel.Simulated)
        {
            var anchor = Matrix4x4.CreateRotationZ(degrees * MathF.PI / 180);
            simulation.Step([anchor], [], Matrix4x4.Identity, elapsed, level);
            simulation.ComposeJointWorlds([anchor], worlds);
            return Vector3.Transform(chain.Tip, worlds[0]);
        }

        AssertNear(-Vector3.UnitY, At(0, 0, MotionLevel.Held));
        // Move the presentation anchor before starting the timed blend.
        At(179, 0);
        float elapsed = 0.125f;
        var previous = At(179, elapsed);
        Assert.InRange(Vector3.Distance(-Vector3.UnitY, previous), 1.39f, 1.43f);
        const float frameSeconds = 0.01f;
        for (int frame = 1; frame <= 3; frame++)
        {
            var shown = At(179 + 2 * frame, frameSeconds);
            float maxAngle = 2 * MathF.PI / 180 + MathF.PI * frameSeconds / (0.25f - elapsed);
            Assert.InRange(Vector3.Distance(previous, shown), 0, 2 * MathF.Sin(maxAngle / 2) + 0.001f);
            previous = shown;
            elapsed += frameSeconds;
        }

        AssertNear(-Vector3.UnitY, At(185, 0.25f - elapsed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Links_keep_corresponding_movable_joints_and_tips_closer_to_rest_spacing(bool hasMovableJoint)
    {
        var joints = hasMovableJoint ? new[] { Joint(Vector3.Zero), Joint(-Vector3.UnitY) } : [Joint(Vector3.Zero)];
        var left = Chain(-Vector3.UnitY, Settings with { Gravity = 0, Damping = 12 }, joints);
        var right = left with { Anchor = 1 };
        var linked = new ChainSimulation(new ChainRig(2, [left, right], [new(0, 1)]), 1);
        var loose = new ChainSimulation(new ChainRig(2, [left, right], []), 1);
        Matrix4x4[] anchors = [Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, 0, 0.65f)];
        ColliderCapsule[] capsules = [new(new(0, -1, 0.14f), new(0, -joints.Length, 0.14f), 0.35f)];
        foreach (var simulation in new[] { linked, loose })
        {
            simulation.Step(anchors, capsules, Matrix4x4.Identity, 0);
            for (int frame = 0; frame < 12; frame++)
                simulation.Step(anchors, capsules, Matrix4x4.Identity, 1f / 60);
        }

        float Error(ChainSimulation simulation, bool tip)
        {
            int count = joints.Length;
            var worlds = new Matrix4x4[2 * count];
            simulation.ComposeJointWorlds(anchors, worlds);
            var first = tip ? Vector3.Transform(left.Tip, worlds[count - 1]) : worlds[count - 1].Translation;
            var second = tip ? Vector3.Transform(right.Tip, worlds[^1]) : worlds[^1].Translation;
            return MathF.Abs(Vector3.Distance(first, second) - 0.65f);
        }

        AssertImprovement(tip: true);
        if (hasMovableJoint)
            AssertImprovement(tip: false);

        void AssertImprovement(bool tip)
        {
            float looseError = Error(loose, tip);
            float linkedError = Error(linked, tip);
            Assert.InRange(looseError, 0.05f, 2);
            Assert.True(linkedError < looseError, $"Expected error below {looseError}, got {linkedError}.");
        }
    }

    [Fact]
    public void Tune_changes_the_next_substep()
    {
        var settings = Settings with { Gravity = 0 };
        var simulation = Create(Chain(Vector3.UnitX, settings, Joint(Vector3.Zero)));
        Step(simulation, 0);
        Step(simulation, 1f / 60);
        AssertNear(Vector3.UnitX, Tip(simulation));
        simulation.Tune(0, settings with { Gravity = 4 });
        Step(simulation, 1f / 60);
        Assert.InRange(Tip(simulation).Y, -0.05f, -0.001f);
    }

    [Fact]
    public void Identical_inputs_produce_identical_joint_frames()
    {
        var chain = Chain(Vector3.UnitX, Settings, Joint(Vector3.Zero), Joint(Vector3.UnitX));
        var first = Create(chain, 1);
        var second = Create(chain, 1);
        for (var frame = 0; frame < 20; frame++)
        {
            var pose = Matrix4x4.CreateTranslation(frame * 0.03f, 0, 0);
            ColliderCapsule[] colliders = [new(new(0.5f, -0.3f, 0), new(0.5f, -1.3f, 0), 0.3f)];
            var level = frame % 7 == 0 ? MotionLevel.Held : MotionLevel.Simulated;
            float delta = (1 + frame % 4) / 180f;
            first.Step([pose], colliders, pose, delta, level);
            second.Step([pose], colliders, pose, delta, level);
            Assert.True(first.JointsFromAnchor.SequenceEqual(second.JointsFromAnchor));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Warm_step_and_compose_do_not_allocate(bool bounded)
    {
        var simulation = bounded
            ? new ChainSimulation(ChainSwingFixture.CreateRig(0.12f, wideLimits: true), 2)
            : Create(Chain(Vector3.UnitX, Settings, Joint(Vector3.Zero), Joint(Vector3.UnitX)), 2);
        Matrix4x4[] anchors = [Matrix4x4.Identity];
        ColliderCapsule[] capsules = [new(new(8, 0, 0), new(8, -1, 0), 0.3f), new(new(2, -0.1f, 0), new(2, -1, 0), 0.3f)];
        var output = new Matrix4x4[simulation.Rig.JointCount];
        int frame = 0;
        void AdvanceAndCompose()
        {
            var wearer = Matrix4x4.Identity;
            if (bounded)
                (anchors[0], wearer) = ChainSwingFixture.Pose(frame++ / 60f);
            simulation.Step(anchors, capsules, wearer, 1f / 60);
            simulation.ComposeJointWorlds(anchors, output);
        }

        int warmFrames = bounded ? 20 : 2;
        for (int call = 0; call < warmFrames; call++)
            AdvanceAndCompose();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (var call = 0; call < 100; call++)
            AdvanceAndCompose();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    internal static readonly ChainSettings Settings = new(0, 3, 1, 1, MathF.PI, 0);

    internal static ChainJointRest Joint(Vector3 translation) => new(translation, Quaternion.Identity, Vector3.One);

    internal static ChainRigChain Chain(Vector3 tip, ChainSettings settings, params ChainJointRest[] joints) =>
        new(0, joints, tip, settings);

    internal static ChainSimulation Create(ChainRigChain chain, int colliderCount = 0) =>
        new(new ChainRig(1, [chain], []), colliderCount);

    internal static void Step(ChainSimulation simulation, float delta) =>
        simulation.Step([Matrix4x4.Identity], [], Matrix4x4.Identity, delta);

    private static void Advance(ChainSimulation simulation, int fps, int seconds)
    {
        for (var frame = 0; frame < fps * seconds; frame++)
            Step(simulation, 1f / fps);
    }

    private static (Vector3 Settled, float Lowest) LimpSwing(int fps)
    {
        var simulation = Create(Chain(Vector3.UnitX, Settings, Joint(Vector3.Zero), Joint(Vector3.UnitX)));
        AssertNear(new(2, 0, 0), Tip(simulation));
        Step(simulation, 0);
        float lowest = 0;
        for (int frame = 0; frame < fps; frame++)
        {
            Step(simulation, 1f / fps);
            lowest = MathF.Min(lowest, Tip(simulation).Y);
        }

        Advance(simulation, fps, 7);
        return (Tip(simulation), lowest);
    }

    private static readonly float[] TrajectoryTimes = [0.1f, 0.25f, 0.5f, 1];

    private static Vector3[] LinkedTrajectory(int fps, float releaseAngle, float gravity, bool releaseAlongLink = false)
    {
        var settings = Settings with { Stiffness = releaseAlongLink ? 0 : 5 };
        var left = Chain(-Vector3.UnitY, settings, Joint(Vector3.Zero));
        var right = left with { Anchor = 1 };
        var options = new ChainSimulationOptions { Gravity = new(0, -gravity, 0) };
        var simulation = new ChainSimulation(new ChainRig(2, [left, right], [new(0, 1)]), 0, options);
        Matrix4x4[] poses = [Matrix4x4.Identity, Matrix4x4.CreateTranslation(releaseAlongLink ? Vector3.UnitX : 0.5f * Vector3.UnitZ)];
        var rightRelease = releaseAlongLink ? Matrix4x4.CreateRotationZ(-releaseAngle) * poses[1] : poses[1];
        simulation.Step([Matrix4x4.CreateRotationZ(releaseAngle), rightRelease], [], Matrix4x4.Identity, 0);
        simulation.Step(poses, [], Matrix4x4.Identity, options.MaxStepSeconds);
        simulation.Step(poses, [], Matrix4x4.Identity, options.MaxStepSeconds);

        var samples = new Vector3[2 * TrajectoryTimes.Length];
        var worlds = new Matrix4x4[2];
        simulation.ComposeJointWorlds(poses, worlds);
        var previousLeft = Vector3.Transform(left.Tip, worlds[0]);
        var previousRight = Vector3.Transform(right.Tip, worlds[1]);
        if (releaseAlongLink)
            Assert.InRange(Vector3.Distance(previousLeft, previousRight), 1.39f, 1.41f);

        int sample = 0;
        for (int frame = 1; frame <= fps; frame++)
        {
            simulation.Step(poses, [], Matrix4x4.Identity, 1f / fps);
            simulation.ComposeJointWorlds(poses, worlds);
            var currentLeft = Vector3.Transform(left.Tip, worlds[0]);
            var currentRight = Vector3.Transform(right.Tip, worlds[1]);
            if (sample < TrajectoryTimes.Length && TrajectoryTimes[sample] * fps <= frame)
            {
                float fraction = TrajectoryTimes[sample] * fps - (frame - 1);
                samples[2 * sample] = Vector3.Lerp(previousLeft, currentLeft, fraction);
                samples[2 * sample + 1] = Vector3.Lerp(previousRight, currentRight, fraction);
                sample++;
            }

            previousLeft = currentLeft;
            previousRight = currentRight;
        }

        Assert.Equal(TrajectoryTimes.Length, sample);
        return samples;
    }

    private static Vector3 Tip(ChainSimulation simulation) =>
        Vector3.Transform(simulation.Rig.Chains[0].Tip, simulation.JointsFromAnchor[^1]);

    private static Vector3 Direction(Vector3 value) => Vector3.Normalize(value);

    private static void AssertNear(Vector3 expected, Vector3 actual, float tolerance = 0.0001f) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"Expected {expected}, got {actual}.");

    private static float DistanceToCapsuleAxis(Vector3 point, ColliderCapsule capsule)
    {
        var axis = capsule.End - capsule.Start;
        float amount = axis == Vector3.Zero ? 0 : Math.Clamp(Vector3.Dot(point - capsule.Start, axis) / axis.LengthSquared(), 0, 1);
        return Vector3.Distance(point, capsule.Start + amount * axis);
    }
}
