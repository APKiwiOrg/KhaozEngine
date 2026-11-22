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

    [Fact]
    public void Limp_level_chain_falls_and_settles_below_its_anchor()
    {
        var simulation = Create(Chain(Vector3.UnitX, Settings, Joint(Vector3.Zero), Joint(Vector3.UnitX)));
        AssertNear(new(2, 0, 0), Tip(simulation));
        Advance(simulation, 120, 8);
        AssertNear(new(0, -2, 0), Tip(simulation), 0.01f);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(144)]
    [InlineData(240)]
    public void Settled_tip_and_first_swing_agree_across_frame_rates(int fps)
    {
        var baseline = SwingAtRate(60);
        var candidate = SwingAtRate(fps);
        AssertNear(new(0, -1, 0), candidate.Settled, 0.01f);
        AssertNear(baseline.Settled, candidate.Settled, 0.01f);
        Assert.InRange(MathF.Abs(candidate.Lowest - baseline.Lowest), 0, 0.1f * MathF.Abs(baseline.Lowest));
        Assert.InRange(candidate.Lowest, -1.001f, -0.9f);
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
    [InlineData(1f / 30, 4)]
    [InlineData(0.4f, 8)]
    public void A_frame_matches_advancing_the_interpolated_poses_at_each_substep(float delta, int substeps)
    {
        var chain = Chain(-Vector3.UnitY, Settings with { Trail = 0.4f, Stiffness = 4, Radius = 0.02f },
            Joint(Vector3.Zero), Joint(-Vector3.UnitY));
        var wholeFrame = Create(chain, 1);
        var sampled = Create(chain, 1);
        void At(ChainSimulation simulation, float fraction, float seconds)
        {
            var wearer = Matrix4x4.CreateRotationY(0.8f * fraction) * Matrix4x4.CreateTranslation(fraction, 0, 0);
            var anchor = Matrix4x4.CreateRotationZ(fraction) * Matrix4x4.CreateTranslation(fraction, 0, 0);
            var collider = new ColliderCapsule(new(1.5f - fraction, -1, 0), new(1.5f - fraction, -2, 0), 0.3f);
            simulation.Step([anchor], [collider], wearer, seconds);
        }

        At(wholeFrame, 0, 0);
        At(sampled, 0, 0);
        At(wholeFrame, 1, delta);
        for (int index = 1; index <= substeps; index++)
            At(sampled, (float)index / substeps, 1f / 120);

        AssertNear(sampled.JointsFromAnchor[1].Translation, wholeFrame.JointsFromAnchor[1].Translation);
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
    [InlineData(false)]
    [InlineData(true)]
    public void Posed_capsule_clears_joints_and_virtual_tip_even_when_it_moves(bool sphere)
    {
        var chain = Chain(-Vector3.UnitY, Settings with { Gravity = 0, Radius = 0.025f },
            Joint(Vector3.Zero), Joint(-Vector3.UnitY));
        var simulation = Create(chain, colliderCount: 1);
        var end = new Vector3(0, sphere ? -1 : -2, 0);
        ColliderCapsule CapsuleAt(float x) => new(new(x, -1, 0), end + new Vector3(x, 0, 0), 0.35f);

        simulation.Step([Matrix4x4.Identity], [CapsuleAt(1)], Matrix4x4.Identity, 0);
        for (var frame = 1; frame <= 6; frame++)
        {
            var capsule = CapsuleAt(1 - frame / 6f);
            simulation.Step([Matrix4x4.Identity], [capsule], Matrix4x4.Identity, 1f / 60);
            Vector3 joint = simulation.JointsFromAnchor[1].Translation;
            Vector3 tip = Tip(simulation);
            Assert.InRange(DistanceToCapsuleAxis(joint, capsule), 0.374f, 4);
            Assert.InRange(DistanceToCapsuleAxis(tip, capsule), 0.374f, 4);
            Assert.InRange(Vector3.Distance(joint, tip), 0.999f, 1.001f);
        }
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
        simulation.Step([turn], [], Matrix4x4.Identity, 1f / 240);
        var worlds = new Matrix4x4[2];
        simulation.ComposeJointWorlds([turn], worlds);

        var first = Direction(worlds[1].Translation - worlds[0].Translation);
        var next = Direction(Vector3.Transform(chain.Tip, worlds[1]) - worlds[1].Translation);
        Assert.InRange(MathF.Acos(Math.Clamp(Vector3.Dot(Direction(Vector3.TransformNormal(-Vector3.UnitY, turn)), first), -1, 1)),
            limit - 0.001f, limit + 0.001f);
        Assert.InRange(Vector3.Dot(first, next), MathF.Cos(limit) - 0.00001f, 1.00001f);
    }

    [Fact]
    public void Trail_zero_follows_wearer_movement_and_full_trail_lags()
    {
        var settings = Settings with { Gravity = 0 };
        var carried = Create(Chain(-Vector3.UnitY, settings with { Trail = 0 }, Joint(Vector3.Zero)));
        var lagging = Create(Chain(-Vector3.UnitY, settings, Joint(Vector3.Zero)));
        for (var frame = 0; frame <= 12; frame++)
        {
            var pose = Matrix4x4.CreateRotationZ(frame * 0.025f) * Matrix4x4.CreateTranslation(frame * 0.03f, 0, 0);
            carried.Step([pose], [], pose, 1f / 120);
            lagging.Step([pose], [], pose, 1f / 120);
        }

        AssertNear(-Vector3.UnitY, Tip(carried));
        Assert.True(Tip(lagging).X < -0.2f);
    }

    [Fact]
    public void Hold_blends_from_the_shown_shape_and_resume_starts_without_a_jump()
    {
        var simulation = Create(Chain(-Vector3.UnitY, Settings with { Gravity = 0 }, Joint(Vector3.Zero)));
        Step(simulation, 0);
        var pose = Matrix4x4.CreateTranslation(1, 0, 0);
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
        var simulation = Create(Chain(-Vector3.UnitY, Settings with { Gravity = 0 },
            Joint(Vector3.Zero), Joint(-Vector3.UnitY)));
        Step(simulation, 0);
        var pose = Matrix4x4.CreateTranslation(1, 0, 0);
        simulation.Step([pose], [], pose, 1f / 120);
        var simulated = Tip(simulation);
        simulation.Step([pose], [], pose, 1f / 8, MotionLevel.Held);
        var held = Tip(simulation);
        Assert.True(Vector3.Distance(simulated, held) > 0.1f);
        Assert.True(Vector3.Distance(-2 * Vector3.UnitY, held) > 0.1f);

        simulation.Step([pose], [], pose, 1f / 120);
        AssertNear(held, Tip(simulation));
    }

    [Fact]
    public void Links_keep_corresponding_joints_closer_to_rest_spacing()
    {
        var left = Chain(-Vector3.UnitY, Settings with { Gravity = 0 }, Joint(Vector3.Zero), Joint(-Vector3.UnitY));
        var right = left with { Anchor = 1 };
        var linked = new ChainSimulation(new ChainRig(2, [left, right], [new(0, 1)]), 1);
        var loose = new ChainSimulation(new ChainRig(2, [left, right], []), 1);
        Matrix4x4[] anchors = [Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, 0, 0.65f)];
        ColliderCapsule[] capsules = [new(new(0, -1, 0.14f), new(0, -1, 0.14f), 0.35f)];
        foreach (var simulation in new[] { linked, loose })
        {
            simulation.Step(anchors, capsules, Matrix4x4.Identity, 0);
            simulation.Step(anchors, capsules, Matrix4x4.Identity, 1f / 60);
        }

        float Error(ChainSimulation simulation)
        {
            var worlds = new Matrix4x4[4];
            simulation.ComposeJointWorlds(anchors, worlds);
            return MathF.Abs(Vector3.Distance(worlds[1].Translation, worlds[3].Translation) - 0.65f);
        }

        Assert.True(Error(loose) > 0.05f);
        Assert.True(Error(linked) < Error(loose));
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

    [Fact]
    public void Warm_step_and_compose_do_not_allocate()
    {
        var simulation = Create(Chain(Vector3.UnitX, Settings, Joint(Vector3.Zero), Joint(Vector3.UnitX)), 1);
        Matrix4x4[] anchors = [Matrix4x4.Identity];
        ColliderCapsule[] capsules = [new(new(8, 0, 0), new(8, -1, 0), 0.3f)];
        var output = new Matrix4x4[2];
        void AdvanceAndCompose()
        {
            simulation.Step(anchors, capsules, Matrix4x4.Identity, 1f / 60);
            simulation.ComposeJointWorlds(anchors, output);
        }

        AdvanceAndCompose();
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

    private static (Vector3 Settled, float Lowest) SwingAtRate(int fps)
    {
        var simulation = Create(Chain(Vector3.UnitX, Settings, Joint(Vector3.Zero)));
        Step(simulation, 0);
        float lowest = 0;
        for (var frame = 0; frame < fps; frame++)
        {
            Step(simulation, 1f / fps);
            lowest = MathF.Min(lowest, Tip(simulation).Y);
        }

        Advance(simulation, fps, 7);
        return (Tip(simulation), lowest);
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
