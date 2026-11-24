using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Attachments;
using Xunit;
using static KhaozEngine.Tests.Attachments.ChainSimulationTests;

namespace KhaozEngine.Tests.Attachments;

public sealed class ChainLayerContactTests
{
    private const float StepSeconds = 1f / 120;
    private const float ClearanceTolerance = 0.0002f;
    private const float UpperRadius = 0.01f;
    private const float LowerRadius = 0.05f;
    private const float ContactDistance = UpperRadius + LowerRadius;
    private static readonly Matrix4x4 UpperAnchor = Matrix4x4.Identity;
    private static readonly ChainRigChain Upper = Chain(-Vector3.UnitY,
        Settings with { Stiffness = 2, Damping = 4, Gravity = 0, Radius = UpperRadius },
        Joint(Vector3.Zero), Joint(-Vector3.UnitY)) with { Layer = AttachmentLayer.Outerwear };
    private static readonly ChainRigChain Lower = Chain(Vector3.UnitZ,
        Settings with { Gravity = 0, Trail = 0, Radius = LowerRadius }, Joint(-0.5f * Vector3.UnitZ))
        with { Anchor = 1, Layer = AttachmentLayer.Armour };

    [Theory]
    [InlineData(AttachmentLayer.Outerwear, AttachmentLayer.Armour, true)]
    [InlineData(AttachmentLayer.Outerwear, AttachmentLayer.Outerwear, false)]
    [InlineData(null, AttachmentLayer.Armour, false)]
    [InlineData(AttachmentLayer.Outerwear, null, false)]
    public void A_sweeping_chain_pushes_only_strictly_higher_layers(
        AttachmentLayer? upperLayer, AttachmentLayer? lowerLayer, bool mustClear)
    {
        // #1894: a soft outer chain used to let the inner chain sweep through between joints.
        var simulation = new ChainSimulation(new ChainRig(2, [Upper with { Layer = upperLayer }, Lower with { Layer = lowerLayer }], []), 0);
        const int sweepSteps = 96;
        var previous = SweepAnchor(0, sweepSteps);
        simulation.Step([UpperAnchor, previous], [], previous, 0);
        Assert.Equal(3, simulation.JointsFromAnchor.Length);
        Span<Matrix4x4> worlds = stackalloc Matrix4x4[3];
        float minimum = float.PositiveInfinity;

        for (int step = 1; step <= sweepSteps + 1; step++)
        {
            var anchor = SweepAnchor(Math.Min(step, sweepSteps), sweepSteps);
            simulation.Step([UpperAnchor, anchor], [], anchor, StepSeconds);
            // The grid boundary displays the previous solved sample. Read both chains on those anchors.
            simulation.ComposeJointWorlds([UpperAnchor, previous], worlds);
            float clearance = Clearance(worlds, Upper);
            if (mustClear)
                Assert.True(clearance >= ContactDistance - ClearanceTolerance, $"Clearance at step {step}: {clearance} m.");
            minimum = MathF.Min(minimum, clearance);
            previous = anchor;
        }

        // The positive row must reach contact. The controls must penetrate the same sweep.
        if (mustClear)
            Assert.InRange(minimum, ContactDistance - ClearanceTolerance, ContactDistance + ClearanceTolerance);
        else
            Assert.InRange(minimum, 0, ClearanceTolerance);
    }

    [Fact]
    public void An_upper_chain_does_not_change_the_lower_chains_pose()
    {
        var simulation = new ChainSimulation(new ChainRig(2, [Upper, Lower], []), 0);
        var alone = Create(Lower with { Anchor = 0 });
        const int sweepSteps = 96;
        Assert.Equal(3, simulation.JointsFromAnchor.Length);
        Assert.Equal(1, alone.JointsFromAnchor.Length);

        for (int step = 0; step <= sweepSteps; step++)
        {
            var anchor = SweepAnchor(step, sweepSteps);
            float seconds = step == 0 ? 0 : StepSeconds;
            simulation.Step([UpperAnchor, anchor], [], anchor, seconds);
            alone.Step([anchor], [], anchor, seconds);
            Assert.True(MemoryMarshal.AsBytes(alone.JointsFromAnchor)
                .SequenceEqual(MemoryMarshal.AsBytes(simulation.JointsFromAnchor[2..])));
        }
    }

    [Fact]
    public void A_lower_chain_crossing_in_one_step_pushes_the_upper_chain_ahead()
    {
        var simulation = new ChainSimulation(new ChainRig(2, [Upper, Lower], []), 0);
        var before = SweepAnchor(0, 1);
        var after = SweepAnchor(1, 1);
        simulation.Step([UpperAnchor, before], [], before, 0);
        simulation.Step([UpperAnchor, after], [], after, StepSeconds);
        simulation.Step([UpperAnchor, after], [], after, StepSeconds);

        Span<Matrix4x4> worlds = stackalloc Matrix4x4[3];
        simulation.ComposeJointWorlds([UpperAnchor, after], worlds);
        Assert.True(worlds[1].Translation.X > worlds[2].Translation.X);
        Assert.InRange(Clearance(worlds, Upper), ContactDistance - ClearanceTolerance, ContactDistance + ClearanceTolerance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_soft_upper_chain_rests_on_a_still_lower_chain(bool uneven)
    {
        var upper = Chain(new(0.8f, 0.6f, 0),
            Upper.Settings with { Stiffness = 3, Damping = 5, Gravity = 0.5f },
            Joint(Vector3.Zero), Joint(Vector3.UnitX)) with { Layer = Upper.Layer };
        var simulation = new ChainSimulation(new ChainRig(2, [upper, Lower], []), 0);
        var support = Matrix4x4.CreateTranslation(0.55f, -0.23f, 0);
        simulation.Step([UpperAnchor, support], [], UpperAnchor, 0);
        Span<Matrix4x4> worlds = stackalloc Matrix4x4[3];
        var previousJoint = Vector3.UnitX;
        var previousTip = previousJoint + upper.Tip;
        float settledTravel = 0;
        const int settleSteps = 8 * 120;

        for (int frame = 1; frame <= settleSteps; frame++)
        {
            float frameSteps = uneven ? (frame % 2 == 0 ? 0.5f : 1.5f) : 1f;
            simulation.Step([UpperAnchor, support], [], UpperAnchor, frameSteps * StepSeconds);
            simulation.ComposeJointWorlds([UpperAnchor, support], worlds);
            var joint = worlds[1].Translation;
            var tip = Vector3.Transform(upper.Tip, worlds[1]);
            if (frame > settleSteps - 120)
                settledTravel += Vector3.Distance(joint, previousJoint) + Vector3.Distance(tip, previousTip);
            previousJoint = joint;
            previousTip = tip;
        }

        Assert.InRange(Clearance(worlds, upper), ContactDistance - ClearanceTolerance, ContactDistance + ClearanceTolerance);
        Assert.InRange(settledTravel, 0, 0.001f);
    }

    private static Matrix4x4 SweepAnchor(int step, int count) =>
        Matrix4x4.CreateTranslation(0.6f * (step / (float)count - 0.5f), -0.5f, 0);

    private static float Clearance(ReadOnlySpan<Matrix4x4> worlds, ChainRigChain upper)
    {
        // The stiff support spans the XY plane at a right angle. Its closest point to each planar
        // upper segment is therefore at Z = 0. Check the fixture geometry before using that slice.
        var start = worlds[2].Translation;
        var end = Vector3.Transform(Lower.Tip, worlds[2]);
        Assert.InRange(start.Z, -0.501f, -0.499f);
        Assert.InRange(end.Z, 0.499f, 0.501f);
        Assert.Equal(new Vector2(start.X, start.Y), new Vector2(end.X, end.Y));
        var root = worlds[0].Translation;
        var joint = worlds[1].Translation;
        var tip = Vector3.Transform(upper.Tip, worlds[1]);
        Assert.InRange(MathF.Max(MathF.Abs(root.Z), MathF.Max(MathF.Abs(joint.Z), MathF.Abs(tip.Z))), 0, 0.000001f);
        var center = new Vector3(start.X, start.Y, 0);
        return MathF.Min(ChainSegmentContactTests.DistanceToSegment(center, root, joint),
            ChainSegmentContactTests.DistanceToSegment(center, joint, tip));
    }
}
